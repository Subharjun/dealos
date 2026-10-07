using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace DealOS.Agents.Infrastructure
{
    /// <summary>Thin client for the Gemini generateContent REST endpoint. Requests and responses are in Gemini's format, which the runtime speaks.</summary>
    public sealed class GeminiClient : IModelClient
    {
        private static readonly HttpClient Http = CreateHttp();
        private readonly string _apiKey;
        private readonly string _baseUrl;

        public GeminiClient(string apiKey, string baseUrl = null)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("Gemini API key is not configured.");
            _apiKey = apiKey.Trim();
            _baseUrl = (baseUrl ?? "https://generativelanguage.googleapis.com/v1beta").TrimEnd('/');
        }

        private static HttpClient CreateHttp()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            return new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        }

        /// <param name="deadline">Absolute time after which no request (or retry) may start.</param>
        /// <summary>Upper bound for a single HTTP call, so a hung model leaves time for fallbacks.</summary>
        public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(45);

        public string Provider { get { return "Gemini"; } }

        public ModelResponse Generate(string model, Dictionary<string, object> request, DateTime deadline)
        {
            var body = Json.Serialize(request);
            var attempt = 0;
            while (true)
            {
                attempt++;
                var remaining = deadline - DateTime.UtcNow;
                if (remaining.TotalSeconds < 5) throw new ModelException("Time budget exhausted before model call.", 0);
                var sw = Stopwatch.StartNew();
                using (var cts = new CancellationTokenSource(remaining < CallTimeout ? remaining : CallTimeout))
                using (var msg = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/models/" + Uri.EscapeDataString(model) + ":generateContent"))
                {
                    msg.Headers.Add("x-goog-api-key", _apiKey);
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
                        throw new ModelException("Gemini HTTP " + code + ": " + Truncate(text, 600), code);
                    var parsed = Json.ParseObject(text);
                    var r = ReadResponse(parsed);
                    r.LatencyMs = sw.ElapsedMilliseconds;
                    r.Model = J.Str(parsed, "modelVersion", model);
                    return r;
                }
            }
        }

        private static ModelResponse ReadResponse(Dictionary<string, object> parsed)
        {
            var r = new ModelResponse();
            var usage = J.ObjOf(parsed, "usageMetadata");
            r.TokensIn = (int)(J.Num(usage, "promptTokenCount") ?? 0);
            r.TokensOut = (int)((J.Num(usage, "candidatesTokenCount") ?? 0) + (J.Num(usage, "thoughtsTokenCount") ?? 0));
            r.BlockReason = J.Str(J.ObjOf(parsed, "promptFeedback"), "blockReason");
            var candidates = J.Arr(parsed, "candidates");
            if (candidates.Count == 0) return r;
            var cand = candidates[0] as Dictionary<string, object>;
            r.FinishReason = J.Str(cand, "finishReason");
            r.Content = J.ObjOf(cand, "content");
            var sb = new StringBuilder();
            foreach (var p in J.Arr(r.Content, "parts"))
            {
                var part = p as Dictionary<string, object>;
                if (part == null) continue;
                var fc = J.ObjOf(part, "functionCall");
                if (fc != null)
                {
                    r.Calls.Add(new FunctionCall
                    {
                        Name = J.Str(fc, "name"),
                        Id = J.Str(fc, "id"),
                        Args = J.ObjOf(fc, "args") ?? new Dictionary<string, object>()
                    });
                    continue;
                }
                if (J.Bool(part, "thought")) continue;
                var t = J.Str(part, "text");
                if (t != null) sb.Append(t);
            }
            r.Text = sb.Length > 0 ? sb.ToString() : null;
            return r;
        }

        public static string Truncate(string s, int max)
        {
            if (s == null) return null;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
