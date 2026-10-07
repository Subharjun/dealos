using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DealOS.Agents.Infrastructure;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Mail
{
    /// <summary>
    /// Finds sellers on the web for a buyer requirement: Gemini with Google Search grounding lists real producers and exporters
    /// (public business contacts only), a second call structures the answer, and the result is saved as leads (source Web Search).
    /// Sourcing then writes to the leads that have an email. LinkedIn and other sites are not scraped; only what search returns.
    /// </summary>
    public static class Discovery
    {
        private static readonly Regex EmailRx = new Regex(@"^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$", RegexOptions.Compiled);
        private static readonly string[] Excluded = { "iran", "north korea", "dprk", "syria", "cuba" };

        public static Dictionary<string, object> Run(Dv dv, Guid requirementId, bool force, Action<string> trace)
        {
            var req = dv.Retrieve("gc_buyerrequirement", requirementId);
            if (req == null) throw new InvalidPluginExecutionException("Requirement " + requirementId + " was not found.");
            if (!force && req.GetAttributeValue<DateTime?>("gc_discoveredon") != null) return J.Obj("status", "AlreadyDone");
            var commodity = req.GetAttributeValue<string>("gc_commoditytext") ?? req.GetAttributeValue<string>("gc_name");
            var max = Math.Max(1, Math.Min(15, dv.SettingInt("email.discovery.max", 8)));
            var spec = req.GetAttributeValue<string>("gc_specification");
            var qty = req.GetAttributeValue<decimal?>("gc_quantity");
            var unit = Desk.UnitLabel(req.GetAttributeValue<OptionSetValue>("gc_quantityunit"));
            var delivery = req.GetAttributeValue<string>("gc_deliverytext");

            var apiKey = dv.Secret("gemini.api_key");
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidPluginExecutionException("Gemini API key is not configured.");
            var gemini = new GeminiClient(apiKey, dv.Setting("agents.gemini_base_url")) { CallTimeout = TimeSpan.FromSeconds(50) };
            var deadline = DateTime.UtcNow.AddSeconds(105);
            var models = new[] { dv.Setting("agents.model.default") ?? "gemini-3.5-flash" }
                .Concat((dv.Setting("agents.model.fallbacks") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(m => m.Trim())).Distinct().ToList();

            var question =
                "Find up to " + max + " real companies that produce or export " + commodity +
                (string.IsNullOrWhiteSpace(spec) ? "" : " (specification: " + spec.Replace("\n", "; ") + ")") +
                (qty == null ? "" : ", able to supply about " + Desk.Num(qty.Value) + " " + unit) +
                (string.IsNullOrWhiteSpace(delivery) ? "" : ", for delivery: " + delivery) + ".\n" +
                "Prefer manufacturers and established exporters with an official website. For each company give: name, country, official website, " +
                "a business contact email exactly as published on their own website or an official trade directory (never guess or construct an email; " +
                "leave it empty if none is published), phone if published, the contact person if published, what they sell, and the source URL. " +
                "Exclude individuals, anonymous marketplace listings and companies in sanctioned countries.";
            GeminiResponse search;
            try
            {
                search = Call(gemini, models, J.Obj(
                    "contents", new List<object> { J.Obj("role", "user", "parts", new List<object> { J.Obj("text", question) }) },
                    "tools", new List<object> { J.Obj("google_search", new Dictionary<string, object>()) }), deadline, trace);
            }
            catch (InvalidPluginExecutionException ex)
            {
                // Google Search grounding is not in the Gemini free tier: sourcing continues with the imported leads.
                return J.Obj("status", "Unavailable", "found", 0, "reason", ex.Message.Contains("429")
                    ? "Web search is not available on the current Gemini key (free tier has no Google Search quota). Enable billing on the key to use it."
                    : ex.Message);
            }
            if (string.IsNullOrWhiteSpace(search.Text)) return J.Obj("status", "NoResult", "found", 0);

            var schema = S.Obj("Companies found",
                "companies", S.Arr("Companies.", S.Obj(null,
                    "name", S.Str("Company name."),
                    "country?", S.Str("Country."),
                    "website?", S.Str("Official website URL."),
                    "email?", S.Str("Published business email, exactly as in the text; empty if none."),
                    "phone?", S.Str("Published phone."),
                    "contact_name?", S.Str("Published contact person."),
                    "products?", S.Str("What they sell."),
                    "source_url?", S.Str("Source URL."))));
            var structured = Call(gemini, models, J.Obj(
                "contents", new List<object> { J.Obj("role", "user", "parts", new List<object> { J.Obj("text",
                    "Extract the companies from this research note. Copy values exactly; do not add companies, emails or websites that are not in the note.\n\nNOTE:\n" +
                    GeminiClient.Truncate(search.Text, 30000)) }) },
                "generationConfig", J.Obj("responseMimeType", "application/json", "responseSchema", schema)), deadline, trace);
            var parsed = Json.ParseLenient(structured.Text ?? "{}") as Dictionary<string, object> ?? new Dictionary<string, object>();

            var created = new List<object>();
            var skipped = new List<object>();
            foreach (var c in J.Arr(parsed, "companies").OfType<Dictionary<string, object>>().Take(max))
            {
                var name = (J.Str(c, "name") ?? "").Trim();
                var country = (J.Str(c, "country") ?? "").Trim();
                if (name.Length < 3) continue;
                if (Excluded.Any(x => country.ToLowerInvariant().Contains(x))) { skipped.Add(J.Obj("name", name, "why", "sanctioned country")); continue; }
                if (!search.Text.ToLowerInvariant().Contains(name.ToLowerInvariant().Split(' ')[0])) { skipped.Add(J.Obj("name", name, "why", "not in the search answer")); continue; }
                var email = (J.Str(c, "email") ?? "").Trim().ToLowerInvariant();
                var note = new List<string> { "Found by AI web search on " + DateTime.UtcNow.ToString("yyyy-MM-dd") + " for: " + commodity + ". Check before relying on it." };
                if (email.Length > 0 && (!EmailRx.IsMatch(email) || !search.Text.ToLowerInvariant().Contains(email)))
                {
                    note.Add("Email '" + email + "' dropped: not literally in the search answer.");
                    email = "";
                }
                var website = (J.Str(c, "website") ?? "").Trim();
                if (email.Length > 0 && website.Length > 0)
                {
                    var host = Regex.Replace(website.ToLowerInvariant(), @"^https?://(www\.)?", "").Split('/')[0];
                    if (MailSignals.Registrable(host) != MailSignals.Registrable(email.Substring(email.IndexOf('@') + 1))) note.Add("Email domain differs from the website.");
                }
                var existing = dv.Svc.RetrieveMultiple(new QueryExpression("gc_lead")
                {
                    ColumnSet = new ColumnSet("gc_leadid", "gc_commodities", "gc_email"), TopCount = 1,
                    Criteria = { Conditions = { new ConditionExpression("gc_name", ConditionOperator.Equal, name) } }
                }).Entities.FirstOrDefault();
                var products = (J.Str(c, "products") ?? "").Trim();
                var line = commodity + (products.Length > 0 ? " — " + products : "");
                if (existing != null)
                {
                    var u = new Entity("gc_lead", existing.Id);
                    var had = existing.GetAttributeValue<string>("gc_commodities") ?? "";
                    if (had.IndexOf(commodity, StringComparison.OrdinalIgnoreCase) < 0) u["gc_commodities"] = Desk.Cut(had + "\n" + line, 100000);
                    if (string.IsNullOrEmpty(existing.GetAttributeValue<string>("gc_email")) && email.Length > 0) u["gc_email"] = email;
                    u["gc_lastseen"] = DateTime.UtcNow;
                    dv.Svc.Update(u);
                    created.Add(J.Obj("name", name, "email", email, "existing", true));
                    continue;
                }
                var l = new Entity("gc_lead");
                l["gc_name"] = Desk.Cut(name, 200);
                l["gc_role"] = new OptionSetValue(DeskChoice.LeadRole.Seller);
                l["gc_commodities"] = line;
                if (email.Length > 0) l["gc_email"] = email;
                l["gc_country"] = Desk.Cut(country, 100);
                l["gc_website"] = Desk.Cut(website, 400);
                l["gc_phone"] = Desk.Cut(J.Str(c, "phone"), 100);
                l["gc_contactname"] = Desk.Cut(J.Str(c, "contact_name"), 200);
                l["gc_source"] = new OptionSetValue(DeskChoice.LeadSource.WebSearch);
                l["gc_sourceref"] = Desk.Cut(J.Str(c, "source_url") ?? "Web search", 400);
                l["gc_lastseen"] = DateTime.UtcNow;
                l["gc_status"] = new OptionSetValue(DeskChoice.LeadStatus.New);
                l["gc_notes"] = string.Join("\n", note);
                dv.Svc.Create(l);
                created.Add(J.Obj("name", name, "country", country, "email", email, "website", website));
            }
            var stamp = new Entity("gc_buyerrequirement", requirementId);
            stamp["gc_discoveredon"] = DateTime.UtcNow;
            dv.Svc.Update(stamp);
            return J.Obj("status", "Done", "found", created.Count, "with_email", created.Count(x => !string.IsNullOrEmpty(J.Str((Dictionary<string, object>)x, "email"))),
                         "leads", created, "skipped", skipped, "model", search.Model);
        }

        private static GeminiResponse Call(GeminiClient gemini, List<string> models, Dictionary<string, object> request, DateTime deadline, Action<string> trace)
        {
            GeminiException last = null;
            foreach (var model in models)
            {
                try { return gemini.Generate(model, request, deadline); }
                catch (GeminiException ex)
                {
                    last = ex;
                    trace("discovery: " + model + " failed: " + ex.Message);
                    if (ex.StatusCode != 429 && ex.StatusCode != 503 && ex.StatusCode != 404 && ex.StatusCode != 0 && ex.StatusCode != 500) break;
                }
            }
            throw new InvalidPluginExecutionException("Seller discovery could not reach Gemini: " + (last == null ? "no model" : last.Message));
        }
    }
}
