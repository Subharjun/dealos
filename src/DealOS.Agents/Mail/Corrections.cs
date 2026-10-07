using System;
using System.Collections.Generic;
using System.Linq;
using DealOS.Agents.Infrastructure;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Mail
{
    /// <summary>
    /// Triage corrections from Gmail (EMAIL_DESK.md 4.4): the owner moves an email to another DealOS label and the desk follows.
    /// Mailbox sync reads Gmail's label history (users.history.list, labelAdded) and passes it here with the label name → id map.
    ///   DealOS/Genuine, DealOS/Buyer, DealOS/Seller → Genuine (the Trade desk flow then works the email)
    ///   DealOS/Review → Review;  DealOS/Ignored → Ignored
    /// Labels the desk itself added match the stored verdict and change nothing. Each correction is kept on the email
    /// (gc_triagecorrection, gc_correctedon); the latest ones are shown to the Mail Triage agent as examples.
    /// </summary>
    public static class Corrections
    {
        private static readonly Dictionary<string, string> Verdicts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "DealOS/Genuine", "Genuine" }, { "DealOS/Buyer", "Genuine" }, { "DealOS/Seller", "Genuine" },
            { "DealOS/Review", "Review" }, { "DealOS/Ignored", "Ignored" }
        };

        public static string VerdictOf(string label)
        {
            string v;
            return label != null && Verdicts.TryGetValue(label.Trim(), out v) ? v : null;
        }

        /// <summary>Gmail history (labelAdded events) → one correction per message: the last triage label added wins.</summary>
        public static Dictionary<string, object> FromHistory(DeskWriter w, string historyJson, string labelMapJson)
        {
            var names = new Dictionary<string, string>();
            foreach (var kv in Json.ParseObject(string.IsNullOrWhiteSpace(labelMapJson) ? "{}" : labelMapJson))
                if (kv.Value is string) names[(string)kv.Value] = kv.Key;
            var latest = new Dictionary<string, string>();
            var history = Json.ParseObject(string.IsNullOrWhiteSpace(historyJson) ? "{}" : historyJson);
            foreach (var h in J.Arr(history, "history").OfType<Dictionary<string, object>>())
                foreach (var added in J.Arr(h, "labelsAdded").OfType<Dictionary<string, object>>())
                {
                    var id = J.Str(J.ObjOf(added, "message"), "id");
                    if (id == null) continue;
                    foreach (var labelId in J.Arr(added, "labelIds").OfType<string>())
                    {
                        string name;
                        if (names.TryGetValue(labelId, out name) && VerdictOf(name) != null) latest[id] = name;
                    }
                }
            var applied = new List<object>();
            foreach (var kv in latest)
            {
                var r = Apply(w, kv.Key, kv.Value);
                if (J.Str(r, "status") == "Corrected") applied.Add(r);
            }
            return J.Obj("events", latest.Count, "corrected", applied.Count, "corrections", applied, "history_id", J.Str(history, "historyId"));
        }

        /// <summary>One Gmail message moved to a DealOS label by a person: the stored verdict follows it.</summary>
        public static Dictionary<string, object> Apply(DeskWriter w, string gmailId, string label)
        {
            var verdict = VerdictOf(label);
            if (verdict == null) return J.Obj("status", "NotATriageLabel", "label", label);
            var msg = w.Dv.Query("gc_message", new[] { "gc_messageid", "gc_triage", "gc_direction", "gc_conversation", "gc_subject" }, 1,
                                 "gc_externalid", ConditionOperator.Equal, gmailId).FirstOrDefault();
            if (msg == null) return J.Obj("status", "Unknown", "gmail_id", gmailId);
            var dir = msg.GetAttributeValue<OptionSetValue>("gc_direction");
            if (dir == null || dir.Value != MailChoice.Direction.Inbound) return J.Obj("status", "NotInbound", "gmail_id", gmailId);
            var current = Dv.Label(msg, "gc_triage");
            if (current == verdict) return J.Obj("status", "Same", "gmail_id", gmailId);

            var note = (current ?? "Not triaged") + " → " + verdict + " (moved to " + label + " in Gmail)";
            var u = new Entity("gc_message", msg.Id);
            u["gc_triage"] = new OptionSetValue(MailChoice.TriageValue(verdict));
            u["gc_triagecorrection"] = Desk.Cut(note, 200);
            u["gc_correctedon"] = DateTime.UtcNow;
            w.Update(u, "triage_corrected", J.Obj("from", current, "to", verdict, "label", label));
            var conv = Desk.H(msg, "gc_conversation");
            if (conv != null)
            {
                var c = new Entity("gc_conversation", conv.Value);
                c["gc_triage"] = new OptionSetValue(MailChoice.TriageValue(verdict));
                w.Update(c, "thread_triage_corrected");
            }
            return J.Obj("status", "Corrected", "gmail_id", gmailId, "subject", msg.GetAttributeValue<string>("gc_subject"), "from", current, "to", verdict);
        }

        /// <summary>The latest corrections as examples for the Mail Triage agent (what was said, what the owner decided).</summary>
        public static List<object> Examples(Dv dv, int top = 8)
        {
            return dv.Query("gc_message", new[] { "gc_subject", "gc_fromaddress", "gc_text", "gc_category", "gc_triagecorrection" }, top,
                            "gc_correctedon", ConditionOperator.NotNull, null)
                     .Select(m => (object)J.Obj(
                         "sender_domain", Domain(m.GetAttributeValue<string>("gc_fromaddress")),
                         "subject", m.GetAttributeValue<string>("gc_subject"),
                         "category", Dv.Label(m, "gc_category"),
                         "owner_decision", m.GetAttributeValue<string>("gc_triagecorrection"),
                         "start_of_email", Desk.Cut((m.GetAttributeValue<string>("gc_text") ?? "").Trim(), 300)))
                     .ToList();
        }

        private static string Domain(string address)
        {
            var at = (address ?? "").IndexOf('@');
            return at < 0 ? null : address.Substring(at + 1).ToLowerInvariant();
        }
    }
}
