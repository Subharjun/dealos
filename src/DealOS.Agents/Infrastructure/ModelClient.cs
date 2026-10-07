using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using Microsoft.Xrm.Sdk;

namespace DealOS.Agents.Infrastructure
{
    public sealed class FunctionCall
    {
        public string Name;
        public string Id;
        public Dictionary<string, object> Args;
    }

    public sealed class ModelResponse
    {
        /// <summary>The model's turn in Gemini's content format (role "model", parts), ready to append to the conversation.</summary>
        public Dictionary<string, object> Content;
        public List<FunctionCall> Calls = new List<FunctionCall>();
        public string Text;
        public string FinishReason;
        public string BlockReason;
        public int TokensIn;
        public int TokensOut;
        public long LatencyMs;
        public string Model;
    }

    public sealed class ModelException : Exception
    {
        public int StatusCode;
        public ModelException(string message, int statusCode) : base(message) { StatusCode = statusCode; }
    }

    /// <summary>
    /// A model provider. The runtime builds every request in Gemini's generateContent format (systemInstruction, contents with
    /// parts, functionDeclarations, toolConfig, generationConfig); each client translates as needed.
    /// </summary>
    public interface IModelClient
    {
        string Provider { get; }
        TimeSpan CallTimeout { get; set; }
        ModelResponse Generate(string model, Dictionary<string, object> request, DateTime deadline);
    }

    /// <summary>
    /// Which provider and models the agents use. Setting agents.provider = openai (gc_secret openai.api_key, models in
    /// agents.openai.model.*) or gemini (gc_secret gemini.api_key, models in agents.model.*).
    /// </summary>
    public static class Models
    {
        public static bool OpenAI(Dv dv) { return (dv.Setting("agents.provider") ?? "gemini").Trim().ToLowerInvariant() == "openai"; }

        public static IModelClient Client(Dv dv)
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(10, dv.SettingInt("agents.call_timeout_seconds", 45)));
            if (OpenAI(dv))
            {
                var key = dv.Secret("openai.api_key");
                if (string.IsNullOrWhiteSpace(key)) throw new InvalidPluginExecutionException("OpenAI API key is not configured (gc_secret 'openai.api_key').");
                return new OpenAIClient(key, dv.Setting("agents.openai_base_url"), dv.Setting("agents.openai.reasoning_effort") ?? "low") { CallTimeout = timeout };
            }
            var gkey = dv.Secret("gemini.api_key");
            if (string.IsNullOrWhiteSpace(gkey)) throw new InvalidPluginExecutionException("Gemini API key is not configured (gc_secret 'gemini.api_key').");
            return new GeminiClient(gkey, dv.Setting("agents.gemini_base_url")) { CallTimeout = timeout };
        }

        /// <summary>The model for a setting key such as agents.model.default (agents.openai.model.default under OpenAI).</summary>
        public static string Model(Dv dv, string key)
        {
            if (OpenAI(dv))
                return dv.Setting(key.Replace("agents.model.", "agents.openai.model.")) ?? dv.Setting("agents.openai.model.default") ?? "gpt-5.4-mini";
            return dv.Setting(key) ?? dv.Setting("agents.model.default") ?? "gemini-3.5-flash";
        }

        /// <summary>The first model, then the fallbacks tried on quota, overload, retired model or timeout.</summary>
        public static List<string> Chain(Dv dv, string first)
        {
            var chain = new List<string> { first };
            var fallbacks = OpenAI(dv) ? dv.Setting("agents.openai.model.fallbacks", "gpt-4.1-mini") : dv.Setting("agents.model.fallbacks", "gemini-3.5-flash,gemini-3.1-flash-lite");
            foreach (var m in fallbacks.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (!chain.Contains(m.Trim())) chain.Add(m.Trim());
            return chain;
        }
    }

    /// <summary>
    /// OpenAI through the Responses API (function tools, reasoning, structured output and web search on one endpoint).
    /// Translates the runtime's Gemini-format request and returns the turn in Gemini format; the raw output items ride along
    /// in the content (openai_items) so reasoning carries over between tool calls. Nothing is stored at OpenAI (store=false).
    /// </summary>
    public sealed class OpenAIClient : IModelClient
    {
        private static readonly HttpClient Http = CreateHttp();
        private static readonly string[] ImageTypes = { "image/png", "image/jpeg", "image/jpg", "image/webp", "image/gif" };
        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly string _effort;

        public OpenAIClient(string apiKey, string baseUrl = null, string reasoningEffort = "low")
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("OpenAI API key is not configured.");
            _apiKey = apiKey.Trim();
            _baseUrl = (baseUrl ?? "https://api.openai.com/v1").TrimEnd('/');
            _effort = reasoningEffort;
        }

        private static HttpClient CreateHttp()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            return new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        }

        public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(45);

        public string Provider { get { return "OpenAI"; } }

        /// <summary>gpt-5 and o-series models reason (no temperature); gpt-4.x does not.</summary>
        public static bool Reasons(string model)
        {
            var m = (model ?? "").ToLowerInvariant();
            return m.StartsWith("gpt-5") || m.StartsWith("o1") || m.StartsWith("o3") || m.StartsWith("o4");
        }

        public ModelResponse Generate(string model, Dictionary<string, object> request, DateTime deadline)
        {
            var body = Json.Serialize(Translate(model, request, _effort));
            var attempt = 0;
            while (true)
            {
                attempt++;
                var remaining = deadline - DateTime.UtcNow;
                if (remaining.TotalSeconds < 5) throw new ModelException("Time budget exhausted before model call.", 0);
                var sw = Stopwatch.StartNew();
                using (var cts = new CancellationTokenSource(remaining < CallTimeout ? remaining : CallTimeout))
                using (var msg = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/responses"))
                {
                    msg.Headers.Add("Authorization", "Bearer " + _apiKey);
                    msg.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    HttpResponseMessage resp;
                    try
                    {
                        resp = Http.SendAsync(msg, cts.Token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        throw new ModelException("Model call timed out.", 0);
                    }
                    var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var code = (int)resp.StatusCode;
                    // 500: one quick retry on the same model. 429/503/404 are left to the runtime's model fallback.
                    if (code == 500 && attempt == 1 && deadline - DateTime.UtcNow > TimeSpan.FromSeconds(20))
                    {
                        Thread.Sleep(1500);
                        continue;
                    }
                    if (code < 200 || code >= 300)
                        throw new ModelException("OpenAI HTTP " + code + ": " + GeminiClient.Truncate(text, 600), code);
                    var r = Read(Json.ParseObject(text), model);
                    r.LatencyMs = sw.ElapsedMilliseconds;
                    return r;
                }
            }
        }

        // ---------------------------------------------------------------- request

        /// <summary>Gemini-format request → Responses API request. Public for the offline tests.</summary>
        public static Dictionary<string, object> Translate(string model, Dictionary<string, object> g, string effort)
        {
            var o = J.Obj("model", model, "store", false);
            var system = string.Join("\n", J.Arr(J.ObjOf(g, "systemInstruction"), "parts").OfType<Dictionary<string, object>>().Select(p => J.Str(p, "text")).Where(t => t != null));
            if (system.Length > 0) o["instructions"] = system;

            var input = new List<object>();
            foreach (var c in J.Arr(g, "contents").OfType<Dictionary<string, object>>())
            {
                if (J.Str(c, "role") == "model") { input.AddRange(ModelTurn(c, model)); continue; }
                var content = new List<object>();
                foreach (var p in J.Arr(c, "parts").OfType<Dictionary<string, object>>())
                {
                    var fr = J.ObjOf(p, "functionResponse");
                    if (fr != null)
                    {
                        input.Add(J.Obj("type", "function_call_output", "call_id", J.Str(fr, "id") ?? J.Str(fr, "name"), "output", Json.Serialize(J.Get(fr, "response"))));
                        continue;
                    }
                    var inline = J.ObjOf(p, "inlineData") ?? J.ObjOf(p, "inline_data");
                    if (inline != null) { content.Add(FilePart(J.Str(inline, "mimeType") ?? J.Str(inline, "mime_type"), J.Str(inline, "data"))); continue; }
                    var t = J.Str(p, "text");
                    if (t != null) content.Add(J.Obj("type", "input_text", "text", t));
                }
                if (content.Count > 0) input.Add(J.Obj("role", "user", "content", content));
            }
            o["input"] = input;

            var tools = new List<object>();
            foreach (var t in J.Arr(g, "tools").OfType<Dictionary<string, object>>())
            {
                if (t.ContainsKey("google_search") || t.ContainsKey("googleSearch")) tools.Add(J.Obj("type", "web_search"));
                foreach (var d in J.Arr(t, "functionDeclarations").OfType<Dictionary<string, object>>())
                    tools.Add(J.Obj("type", "function", "name", J.Str(d, "name"), "description", J.Str(d, "description") ?? "",
                                    "parameters", JsonSchema(J.ObjOf(d, "parameters") ?? J.Obj("type", "OBJECT", "properties", new Dictionary<string, object>())), "strict", false));
            }
            if (tools.Count > 0) o["tools"] = tools;
            var fc = J.ObjOf(J.ObjOf(g, "toolConfig"), "functionCallingConfig");
            if (fc != null && J.Str(fc, "mode") == "ANY")
            {
                var allowed = J.Arr(fc, "allowedFunctionNames");
                o["tool_choice"] = allowed.Count == 1 ? (object)J.Obj("type", "function", "name", (string)allowed[0]) : "required";
            }

            var gen = J.ObjOf(g, "generationConfig") ?? new Dictionary<string, object>();
            if (Reasons(model)) { if (!string.IsNullOrWhiteSpace(effort)) o["reasoning"] = J.Obj("effort", effort); }
            else if (J.Num(gen, "temperature") != null) o["temperature"] = J.Num(gen, "temperature");
            if (J.Num(gen, "maxOutputTokens") != null) o["max_output_tokens"] = (int)J.Num(gen, "maxOutputTokens").Value;
            var schema = J.ObjOf(gen, "responseSchema");
            if (schema != null) o["text"] = J.Obj("format", J.Obj("type", "json_schema", "name", "result", "schema", JsonSchema(schema), "strict", false));
            else if (J.Str(gen, "responseMimeType") == "application/json") o["text"] = J.Obj("format", J.Obj("type", "json_object"));
            // Reasoning comes back encrypted so it can be passed on with the next turn without storing anything at OpenAI.
            if (Reasons(model)) o["include"] = new List<object> { "reasoning.encrypted_content" };
            return o;
        }

        /// <summary>A model turn: the raw output items when this model produced them (reasoning kept), otherwise rebuilt from the parts.</summary>
        private static IEnumerable<object> ModelTurn(Dictionary<string, object> c, string model)
        {
            var raw = J.Arr(c, "openai_items");
            if (raw.Count > 0)
            {
                // Encrypted reasoning only works for the model that wrote it; after a fallback it is dropped.
                var same = J.Str(c, "openai_model") == model;
                return raw.OfType<Dictionary<string, object>>().Where(i => same || J.Str(i, "type") != "reasoning")
                          .Select(i => (object)Clean(i));
            }
            var items = new List<object>();
            foreach (var p in J.Arr(c, "parts").OfType<Dictionary<string, object>>())
            {
                var call = J.ObjOf(p, "functionCall");
                if (call != null)
                    items.Add(J.Obj("type", "function_call", "call_id", J.Str(call, "id") ?? J.Str(call, "name"), "name", J.Str(call, "name"),
                                    "arguments", Json.Serialize(J.ObjOf(call, "args") ?? new Dictionary<string, object>())));
                else if (J.Str(p, "text") != null && !J.Bool(p, "thought"))
                    items.Add(J.Obj("role", "assistant", "content", J.Str(p, "text")));
            }
            return items;
        }

        /// <summary>Output items go back as input without the fields only valid in output (status) and without web-search results.</summary>
        private static object Clean(Dictionary<string, object> item)
        {
            var copy = new Dictionary<string, object>(item);
            copy.Remove("status");
            return copy;
        }

        private static object FilePart(string mime, string base64)
        {
            mime = (mime ?? "application/octet-stream").ToLowerInvariant();
            if (ImageTypes.Contains(mime)) return J.Obj("type", "input_image", "image_url", "data:" + mime + ";base64," + base64);
            if (mime == "application/pdf") return J.Obj("type", "input_file", "filename", "document.pdf", "file_data", "data:application/pdf;base64," + base64);
            return J.Obj("type", "input_text", "text", "[An attachment of type " + mime + " was not shown to the model: the format is not supported.]");
        }

        /// <summary>The runtime's schema (Gemini OpenAPI subset: upper-case types, propertyOrdering) → JSON Schema.</summary>
        public static Dictionary<string, object> JsonSchema(Dictionary<string, object> s)
        {
            var o = new Dictionary<string, object>();
            foreach (var kv in s)
            {
                switch (kv.Key)
                {
                    case "propertyOrdering":
                        break;
                    case "type":
                        o["type"] = ((kv.Value as string) ?? "string").ToLowerInvariant();
                        break;
                    case "properties":
                        var props = new Dictionary<string, object>();
                        foreach (var p in (kv.Value as Dictionary<string, object>) ?? new Dictionary<string, object>())
                            props[p.Key] = p.Value is Dictionary<string, object> ? JsonSchema((Dictionary<string, object>)p.Value) : p.Value;
                        o["properties"] = props;
                        break;
                    case "items":
                        o["items"] = kv.Value is Dictionary<string, object> ? JsonSchema((Dictionary<string, object>)kv.Value) : kv.Value;
                        break;
                    default:
                        o[kv.Key] = kv.Value;
                        break;
                }
            }
            if (J.Str(o, "type") == "object" && !o.ContainsKey("properties")) o["properties"] = new Dictionary<string, object>();
            return o;
        }

        // ---------------------------------------------------------------- response

        public static ModelResponse Read(Dictionary<string, object> parsed, string model)
        {
            var r = new ModelResponse { Model = J.Str(parsed, "model", model) };
            var usage = J.ObjOf(parsed, "usage");
            r.TokensIn = (int)(J.Num(usage, "input_tokens") ?? 0);
            r.TokensOut = (int)(J.Num(usage, "output_tokens") ?? 0);
            var status = J.Str(parsed, "status");
            r.FinishReason = status == "incomplete" ? "incomplete: " + J.Str(J.ObjOf(parsed, "incomplete_details"), "reason") : status;
            var items = J.Arr(parsed, "output").OfType<Dictionary<string, object>>().ToList();
            var parts = new List<object>();
            var sb = new StringBuilder();
            foreach (var item in items)
            {
                switch (J.Str(item, "type"))
                {
                    case "function_call":
                        Dictionary<string, object> args;
                        try { args = Json.ParseLenient(J.Str(item, "arguments") ?? "{}") as Dictionary<string, object>; }
                        catch (FormatException) { args = null; }
                        var call = new FunctionCall { Name = J.Str(item, "name"), Id = J.Str(item, "call_id"), Args = args ?? new Dictionary<string, object>() };
                        r.Calls.Add(call);
                        parts.Add(J.Obj("functionCall", J.Obj("name", call.Name, "id", call.Id, "args", call.Args)));
                        break;
                    case "message":
                        foreach (var c in J.Arr(item, "content").OfType<Dictionary<string, object>>())
                        {
                            if (J.Str(c, "type") == "output_text") sb.Append(J.Str(c, "text"));
                            else if (J.Str(c, "type") == "refusal") r.BlockReason = "refusal: " + GeminiClient.Truncate(J.Str(c, "refusal"), 300);
                        }
                        break;
                }
            }
            r.Text = sb.Length > 0 ? sb.ToString() : null;
            if (r.Text != null) parts.Insert(0, J.Obj("text", r.Text));
            // Web-search calls are not passed back (they are server-side); everything else is, in order.
            var keep = items.Where(i => J.Str(i, "type") != "web_search_call").Cast<object>().ToList();
            r.Content = J.Obj("role", "model", "parts", parts, "openai_items", keep, "openai_model", model);
            return r;
        }
    }
}
