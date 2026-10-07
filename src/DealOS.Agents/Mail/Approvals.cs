using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DealOS.Agents.Infrastructure;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DealOS.Agents.Mail
{
    /// <summary>
    /// Approve by reply: every decision a person must take (Confirm deal, contract terms, e-signature, KYB tier, compliance, signed copy...)
    /// is briefed to the desk owner with a reference code, and the owner answers the briefing with one word:
    ///   APPROVE (or CONFIRM, YES, OK, DONE)   REJECT &lt;reason&gt;   SEND (the drafts listed in the briefing)   PIPELINE (where every deal stands)
    /// Only replies from the owner (desk.owner_email, or the mailbox itself) in the briefing thread are acted on; a reply from another
    /// address, or one that failed SPF/DKIM/DMARC, is ignored and the owner is told. Each Gmail message is handled once (ingest is idempotent).
    /// </summary>
    public static class Approvals
    {
        public const string BriefingThread = "DealOS desk briefing";
        private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        private static readonly Regex RefRx = new Regex(@"\bref[:#\s]+([A-HJ-NP-Z2-9]{6})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly string[] ApproveWords = { "APPROVE", "APPROVED", "CONFIRM", "CONFIRMED", "YES", "OK", "OKAY", "DONE", "GO", "AGREED" };
        private static readonly string[] RejectWords = { "REJECT", "REJECTED", "DECLINE", "DECLINED", "NO", "CANCEL" };
        private static readonly string[] Hidden = { "agent", "run", "approve", "how", "instructions" };

        public enum Command { None, Approve, Reject, Send, ApproveAndSend, Pipeline, Help }

        public sealed class Parsed
        {
            public Command Command;
            public string Reason;
            public string Code;
        }

        // ---------------------------------------------------------------- briefing a decision

        public static string NewCode()
        {
            var bytes = new byte[6];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return new string(bytes.Select(b => Alphabet[b % Alphabet.Length]).ToArray());
        }

        /// <summary>
        /// Briefs an open review task to the owner with a reply code (once; the reminder re-sends the same code).
        /// Returns the briefing message id, or null when the task is not open or was already briefed.
        /// </summary>
        public static Guid? BriefTask(DeskWriter w, Guid taskId, bool reminder = false)
        {
            var dv = w.Dv;
            var t = dv.Retrieve("gc_reviewtask", taskId, "gc_name", "gc_kind", "gc_purpose", "gc_status", "gc_payload", "gc_deal", "gc_account", "gc_replycode", "gc_briefedon");
            if (t == null || Opt(t, "gc_status") != Choice.ReviewStatus.Open) return null;
            if (!reminder && t.GetAttributeValue<DateTime?>("gc_briefedon") != null) return null;
            var code = t.GetAttributeValue<string>("gc_replycode");
            if (string.IsNullOrEmpty(code)) code = NewCode();

            var title = t.GetAttributeValue<string>("gc_name") ?? "Decision";
            var kind = Dv.Label(t, "gc_kind") ?? "Approval";
            var payload = Payload(t.GetAttributeValue<string>("gc_payload"));
            var docs = TaskDocuments(dv, payload);
            var text = new StringBuilder();
            if (reminder) text.Append("Reminder: this is still waiting for you.\n\n");
            text.Append(title).Append("\n\n");
            foreach (var line in Summary(payload)) text.Append(line).Append('\n');
            var approveMeaning = J.Str(payload, "approve");
            text.Append("\nReply to this email with one word:\n");
            text.Append(kind == "Approval" ? "APPROVE" : "DONE").Append("  ").Append(approveMeaning ?? (kind == "Approval" ? "to approve." : "when it is handled.")).Append('\n');
            text.Append("REJECT <reason>  to turn it down.\n");
            if (docs.Count > 0) text.Append("\nThe documents are attached for your check.\n");
            text.Append("\nRef ").Append(code).Append(". Only replies from the desk owner are acted on.");

            var subject = (reminder ? "Reminder: " : "") + (kind == "Approval" ? "Approve? " : "To do: ") + title + " (ref " + code + ")";
            var id = Desk.Brief(w, subject, text.ToString(), docs, J.Obj("code", code, "task", taskId.ToString()));
            var u = new Entity("gc_reviewtask", taskId);
            u["gc_replycode"] = code;
            if (reminder) u["gc_remindedon"] = DateTime.UtcNow;
            else u["gc_briefedon"] = DateTime.UtcNow;
            w.Update(u, "task_briefed", J.Obj("code", code, "reminder", reminder));
            return id;
        }

        /// <summary>Open tasks briefed more than desk.approval_remind_hours ago and not reminded yet get one reminder.</summary>
        public static List<object> Remind(DeskWriter w, DateTime now)
        {
            var hours = w.Dv.SettingNum("desk.approval_remind_hours", 12);
            var done = new List<object>();
            if (hours <= 0) return done;
            foreach (var t in w.Dv.Query("gc_reviewtask", new[] { "gc_reviewtaskid", "gc_name", "gc_briefedon", "gc_remindedon" }, 50,
                                         "gc_status", ConditionOperator.Equal, Choice.ReviewStatus.Open, "gc_briefedon", ConditionOperator.NotNull, null,
                                         "gc_remindedon", ConditionOperator.Null, null))
            {
                var briefed = t.GetAttributeValue<DateTime?>("gc_briefedon");
                if (briefed == null || (now - briefed.Value).TotalHours < hours) continue;
                if (BriefTask(w, t.Id, true) != null) done.Add(J.Obj("task", t.GetAttributeValue<string>("gc_name")));
            }
            return done;
        }

        /// <summary>The payload as lines a person can read: simple values first, long text cut, ids and agent bookkeeping left out.</summary>
        public static List<string> Summary(Dictionary<string, object> payload)
        {
            var lines = new List<string>();
            foreach (var kv in payload)
            {
                if (Hidden.Contains(kv.Key) || kv.Key.EndsWith("Id", StringComparison.Ordinal) || kv.Key.EndsWith("_id", StringComparison.Ordinal) || kv.Value == null) continue;
                var v = Value(kv.Value);
                if (string.IsNullOrWhiteSpace(v)) continue;
                lines.Add("- " + Words(kv.Key) + ": " + Desk.Cut(v, 600));
                if (lines.Count >= 14) break;
            }
            return lines;
        }

        private static string Value(object v)
        {
            if (v is string) return ((string)v).Trim();
            if (v is bool) return (bool)v ? "yes" : "no";
            if (v is double || v is decimal || v is int || v is long)
            {
                var d = Convert.ToDecimal(v, CultureInfo.InvariantCulture);
                return Desk.Num(d);
            }
            if (v is IList<object>)
            {
                var items = ((IList<object>)v).Select(Value).Where(x => !string.IsNullOrWhiteSpace(x)).Take(6).ToList();
                return items.Count == 0 ? null : string.Join("; ", items);
            }
            if (v is IDictionary<string, object>)
                return string.Join(", ", ((IDictionary<string, object>)v).Where(kv => kv.Value != null && !(kv.Value is IDictionary<string, object>))
                                                                            .Take(4).Select(kv => Words(kv.Key) + " " + Value(kv.Value)));
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        /// <summary>"buyerPrice" / "missing_terms" → "Buyer price" / "Missing terms".</summary>
        public static string Words(string key)
        {
            var s = Regex.Replace(key.Replace('_', ' '), "([a-z])([A-Z])", "$1 $2").ToLowerInvariant();
            return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        private static List<Guid> TaskDocuments(Dv dv, Dictionary<string, object> payload)
        {
            var ids = new List<Guid>();
            foreach (var x in J.Arr(payload, "documents").OfType<string>())
            {
                Guid g;
                if (Guid.TryParse(x, out g) && dv.Exists("gc_document", g)) ids.Add(g);
            }
            return ids;
        }

        // ---------------------------------------------------------------- the owner's reply

        /// <summary>The first line the person wrote (above the quoted briefing) → command, reason and the reference code.</summary>
        public static Parsed Parse(string subject, string body)
        {
            var own = new List<string>();
            foreach (var raw in (body ?? "").Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith(">") || Regex.IsMatch(line, @"^On .{4,200} wrote:?$", RegexOptions.IgnoreCase) || line.StartsWith("-----Original Message", StringComparison.OrdinalIgnoreCase)
                    || Regex.IsMatch(line, @"^From:\s", RegexOptions.IgnoreCase)) break;
                own.Add(line);
            }
            var text = string.Join("\n", own).Trim();
            var first = own.FirstOrDefault(l => l.Length > 0) ?? "";
            var words = Regex.Split(first.ToUpperInvariant(), @"[^A-Z]+").Where(x => x.Length > 0).ToList();
            var p = new Parsed { Command = Command.None };
            var m = RefRx.Match((subject ?? "") + "\n" + text);
            if (m.Success) p.Code = m.Groups[1].Value.ToUpperInvariant();
            if (words.Count == 0) return p;
            var head = words[0];
            var approve = ApproveWords.Contains(head);
            var send = head == "SEND" || (approve && words.Take(3).Contains("SEND"));
            if (approve && send) p.Command = Command.ApproveAndSend;
            else if (approve) p.Command = Command.Approve;
            else if (send) p.Command = Command.Send;
            else if (RejectWords.Contains(head)) p.Command = Command.Reject;
            else if (head == "PIPELINE" || head == "STATUS") p.Command = Command.Pipeline;
            else if (head == "HELP") p.Command = Command.Help;
            if (p.Command == Command.Reject)
            {
                var reason = Regex.Replace(text, @"^\W*\w+\W*", "").Trim();
                p.Reason = reason.Length == 0 ? null : Desk.Cut(reason, 500);
            }
            return p;
        }

        /// <summary>
        /// Whether a stored email is a reply to a desk briefing: in the briefing thread, a "Re:" subject (our own briefings never are).
        /// </summary>
        public static bool IsBriefingReply(Dv dv, Guid conversationId, string subject)
        {
            if (!Regex.IsMatch(subject ?? "", @"^\s*(re|aw|sv)\s*:", RegexOptions.IgnoreCase)) return false;
            var conv = dv.Retrieve("gc_conversation", conversationId, "gc_name");
            return conv != null && conv.GetAttributeValue<string>("gc_name") == BriefingThread;
        }

        /// <summary>The owner's addresses: desk.owner_email ({mailbox} = the connected mailbox) and the mailbox itself.</summary>
        public static HashSet<string> Owners(Dv dv, string mailbox)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mb = (mailbox ?? "").Trim().ToLowerInvariant();
            if (mb.Length > 0) set.Add(mb);
            foreach (var a in (dv.Setting("desk.owner_email") ?? "{mailbox}").Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var x = a.Trim().ToLowerInvariant().Replace("{mailbox}", mb);
                if (x.Contains("@") && !x.Contains("{")) set.Add(x);
            }
            return set;
        }

        /// <summary>
        /// Acts on the owner's reply to a briefing. sentByMailbox = the reply was sent from the connected mailbox (Gmail SENT label);
        /// otherwise the sender must be an owner address AND the email must pass authentication (DMARC pass, or SPF and DKIM pass),
        /// so a forged "From: owner" can never approve anything.
        /// </summary>
        public static Dictionary<string, object> FromReply(DeskWriter w, string from, bool sentByMailbox, bool authenticated, string mailbox, string subject, string body)
        {
            var dv = w.Dv;
            var owners = Owners(dv, mailbox);
            var sender = (from ?? "").Trim().ToLowerInvariant();
            if (!sentByMailbox && (!owners.Contains(sender) || !authenticated))
            {
                Desk.Brief(w, "Reply not acted on", "A reply to a desk briefing came from " + (from ?? "an unknown sender") +
                                                    (owners.Contains(sender) ? " but did not pass the email authentication checks (SPF/DKIM/DMARC)" : ", which is not the desk owner") +
                                                    ". Nothing was approved or sent. Only replies from " + string.Join(", ", owners) + " are acted on.");
                return J.Obj("status", "NotOwner", "from", from);
            }

            var p = Parse(subject, body);
            if (p.Command == Command.None || p.Command == Command.Help)
            {
                if (p.Command == Command.Help || p.Code != null)
                    Desk.Brief(w, "How to answer a briefing", HelpText());
                return J.Obj("status", p.Command == Command.Help ? "Help" : "NotACommand");
            }
            if (p.Command == Command.Pipeline)
            {
                Desk.Brief(w, "Pipeline " + DateTime.UtcNow.ToString("d MMM HH:mm", CultureInfo.InvariantCulture) + " UTC", Pipeline(dv));
                return J.Obj("status", "Pipeline");
            }
            if (p.Code == null)
            {
                Desk.Brief(w, "Which item?", "Your reply \"" + Desk.Cut(FirstWords(body), 80) + "\" has no reference code, so nothing was done. Reply to the briefing of the item itself (its subject ends with \"ref XXXXXX\").");
                return J.Obj("status", "NoCode");
            }

            var brief = dv.Query("gc_message", new[] { "gc_messageid", "gc_emailmeta", "gc_subject" }, 5, "gc_subject", ConditionOperator.Like, "%(ref " + p.Code + ")%")
                          .FirstOrDefault(x => J.Str(Meta(x), "code") == p.Code);
            if (brief == null)
            {
                Desk.Brief(w, "Ref " + p.Code + " not found", "No briefing has the reference " + p.Code + ", so nothing was done.");
                return J.Obj("status", "UnknownCode", "code", p.Code);
            }
            var meta = Meta(brief);
            var done = new List<string>();
            var taskId = J.Id(meta, "task");
            var drafts = J.Arr(meta, "drafts").OfType<string>().ToList();

            var wantsApprove = p.Command == Command.Approve || p.Command == Command.ApproveAndSend || (p.Command == Command.Send && drafts.Count == 0 && taskId != null);
            var wantsSend = p.Command == Command.Send || p.Command == Command.ApproveAndSend;
            if (wantsApprove || p.Command == Command.Reject)
            {
                if (taskId == null) done.Add("This briefing has no decision to " + (p.Command == Command.Reject ? "reject" : "approve") + ".");
                else done.Add(Decide(w, taskId.Value, p.Command != Command.Reject, p.Reason, sender));
            }
            if (wantsSend && drafts.Count > 0) done.Add(SendDrafts(w, drafts));
            else if (wantsSend && !wantsApprove) done.Add("This briefing lists no drafts to send.");

            Desk.Brief(w, "Done: ref " + p.Code, string.Join("\n", done));
            return J.Obj("status", "Applied", "code", p.Code, "command", p.Command.ToString(), "result", done);
        }

        private static string Decide(DeskWriter w, Guid taskId, bool approve, string reason, string by)
        {
            var t = w.Dv.Retrieve("gc_reviewtask", taskId, "gc_name", "gc_status");
            if (t == null) return "The task no longer exists.";
            var name = t.GetAttributeValue<string>("gc_name");
            if (Opt(t, "gc_status") != Choice.ReviewStatus.Open)
                return "\"" + name + "\" was already decided (" + (Dv.Label(t, "gc_status") ?? "closed") + "); nothing changed.";
            var u = new Entity("gc_reviewtask", taskId);
            u["gc_status"] = new OptionSetValue(approve ? Choice.ReviewStatus.Open + 1 : Choice.ReviewStatus.Open + 2);
            u["gc_decisionreason"] = (approve ? "Approved" : "Rejected") + " by email reply from " + by + " on " +
                                     DateTime.UtcNow.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC" + (string.IsNullOrEmpty(reason) ? "" : ": " + reason);
            w.Update(u, approve ? "task_approved_by_reply" : "task_rejected_by_reply", J.Obj("by", by, "reason", reason));
            return (approve ? "Approved: " : "Rejected: ") + name + (approve ? ". The next step runs now." : (string.IsNullOrEmpty(reason) ? "." : " (" + reason + ")."));
        }

        /// <summary>Drafts the owner released by replying SEND: still pending ones are flagged, and the Desk drafts flow sends them from Gmail.</summary>
        public static string SendDrafts(DeskWriter w, IEnumerable<string> ids)
        {
            int sent = 0, gone = 0;
            foreach (var x in ids)
            {
                Guid g;
                if (!Guid.TryParse(x, out g)) continue;
                var d = w.Dv.Retrieve("gc_message", g, "gc_draftstatus", "gc_autosend");
                if (d == null || Opt(d, "gc_draftstatus") != DeskChoice.DraftStatus.Pending) { gone++; continue; }
                var u = new Entity("gc_message", g);
                u["gc_autosend"] = true;
                w.Update(u, "send_released_draft");
                sent++;
            }
            return sent + " draft(s) are being sent from Gmail as they stand now" + (gone > 0 ? "; " + gone + " were already sent or replaced" : "") + ".";
        }

        public static string HelpText()
        {
            return "Answer a briefing by replying to it with one word on the first line:\n" +
                   "APPROVE (or CONFIRM / YES / OK / DONE): approve the decision in the briefing.\n" +
                   "REJECT <reason>: turn it down.\n" +
                   "SEND: send the drafts listed in the briefing, as they are in Gmail now.\n" +
                   "APPROVE SEND: both.\n" +
                   "PIPELINE: where every requirement, lot and deal stands.\n" +
                   "Only replies from the desk owner are acted on.";
        }

        // ---------------------------------------------------------------- pipeline

        /// <summary>Where the desk stands: requirements by stage, open lots, decisions and drafts waiting, contracts, inspections and shipments.</summary>
        public static string Pipeline(Dv dv)
        {
            var sb = new StringBuilder();
            var reqs = dv.Query("gc_buyerrequirement", new[] { "gc_name", "gc_commoditytext", "gc_deskstage", "gc_quantity", "gc_ourprice", "modifiedon" }, 200,
                                "gc_source", ConditionOperator.Equal, DeskChoice.RequirementSource.Email)
                         .Where(r => Opt(r, "gc_deskstage") >= 0 && Opt(r, "gc_deskstage") != DeskChoice.Stage.Closed).ToList();
            sb.Append("Buyer requirements open: ").Append(reqs.Count).Append('\n');
            foreach (var g in reqs.GroupBy(r => Opt(r, "gc_deskstage")).OrderBy(g => g.Key))
            {
                sb.Append("  ").Append(Desk.Label(DeskChoice.Stages, new OptionSetValue(g.Key)) ?? "?").Append(": ").Append(g.Count()).Append('\n');
                foreach (var r in g.OrderByDescending(x => x.GetAttributeValue<DateTime>("modifiedon")).Take(5))
                    sb.Append("    - ").Append(Desk.Cut(r.GetAttributeValue<string>("gc_commoditytext") ?? r.GetAttributeValue<string>("gc_name"), 60))
                      .Append(r.GetAttributeValue<decimal?>("gc_quantity") == null ? "" : ", " + Desk.Num(r.GetAttributeValue<decimal>("gc_quantity")))
                      .Append(r.GetAttributeValue<decimal?>("gc_ourprice") == null ? "" : ", our price " + Desk.Num(r.GetAttributeValue<decimal>("gc_ourprice")))
                      .Append(", updated ").Append(Ago(r.GetAttributeValue<DateTime>("modifiedon"))).Append('\n');
            }
            var lots = dv.Query("gc_sellerlot", new[] { "gc_commoditytext", "gc_name", "gc_status", "gc_biddeadline", "gc_window" }, 50)
                         .Where(l => (Dv.Label(l, "gc_status") ?? "Open") == "Open").ToList();
            sb.Append("\nSeller lots open: ").Append(lots.Count).Append('\n');
            foreach (var l in lots.Take(8))
                sb.Append("  - ").Append(Desk.Cut(l.GetAttributeValue<string>("gc_commoditytext") ?? l.GetAttributeValue<string>("gc_name"), 60))
                  .Append(", ").Append(l.GetAttributeValue<string>("gc_window") ?? "window not set").Append('\n');
            var tasks = dv.Query("gc_reviewtask", new[] { "gc_name", "gc_replycode", "createdon" }, 50, "gc_status", ConditionOperator.Equal, Choice.ReviewStatus.Open);
            sb.Append("\nWaiting for you: ").Append(tasks.Count).Append('\n');
            foreach (var t in tasks.Take(12))
                sb.Append("  - ").Append(t.GetAttributeValue<string>("gc_name")).Append(string.IsNullOrEmpty(t.GetAttributeValue<string>("gc_replycode")) ? "" : " (ref " + t.GetAttributeValue<string>("gc_replycode") + ")")
                  .Append(", ").Append(Ago(t.GetAttributeValue<DateTime>("createdon"))).Append('\n');
            var drafts = dv.Query("gc_message", new[] { "gc_messageid" }, 200, "gc_draftstatus", ConditionOperator.Equal, DeskChoice.DraftStatus.Pending,
                                  "gc_autosend", ConditionOperator.Equal, false).Count;
            sb.Append("\nDrafts waiting in Gmail: ").Append(drafts).Append('\n');
            var contracts = dv.Query("gc_contract", new[] { "gc_name", "gc_status" }, 50, "gc_status", ConditionOperator.Equal, DeskChoice.Contract.SentForSignature);
            sb.Append("Contracts out for signature: ").Append(contracts.Count).Append('\n');
            foreach (var c in contracts.Take(5)) sb.Append("  - ").Append(c.GetAttributeValue<string>("gc_name")).Append('\n');
            var inspections = dv.Query("gc_inspection", new[] { "gc_name", "gc_status" }, 50).Where(i => new[] { "Requested", "Booked", "Sampling Done", "Report Received" }.Contains(Dv.Label(i, "gc_status"))).ToList();
            sb.Append("Inspections in progress: ").Append(inspections.Count).Append('\n');
            foreach (var i in inspections.Take(5)) sb.Append("  - ").Append(i.GetAttributeValue<string>("gc_name")).Append(": ").Append(Dv.Label(i, "gc_status")).Append('\n');
            var shipments = dv.Query("gc_shipment", new[] { "gc_name", "gc_status", "gc_eta" }, 50).Where(s => new[] { "Booked", "Loading", "In Transit", "Arrived", "Cleared" }.Contains(Dv.Label(s, "gc_status"))).ToList();
            sb.Append("Shipments under way: ").Append(shipments.Count).Append('\n');
            foreach (var s in shipments.Take(5))
                sb.Append("  - ").Append(s.GetAttributeValue<string>("gc_name")).Append(": ").Append(Dv.Label(s, "gc_status"))
                  .Append(s.GetAttributeValue<DateTime?>("gc_eta") == null ? "" : ", ETA " + s.GetAttributeValue<DateTime>("gc_eta").ToString("d MMM", CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString().TrimEnd();
        }

        private static string Ago(DateTime t)
        {
            var h = (DateTime.UtcNow - t).TotalHours;
            return h < 1 ? "just now" : h < 48 ? Math.Round(h) + " h ago" : Math.Round(h / 24) + " days ago";
        }

        // ---------------------------------------------------------------- helpers

        public static Dictionary<string, object> Meta(Entity m)
        {
            try { return Json.ParseObject(m.GetAttributeValue<string>("gc_emailmeta") ?? "{}"); }
            catch (FormatException) { return new Dictionary<string, object>(); }
        }

        private static Dictionary<string, object> Payload(string raw)
        {
            try { return string.IsNullOrWhiteSpace(raw) ? new Dictionary<string, object>() : Json.ParseObject(raw); }
            catch (FormatException) { return J.Obj("details", raw); }
        }

        private static string FirstWords(string body)
        {
            return ((body ?? "").Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "").Trim();
        }

        private static int Opt(Entity e, string column)
        {
            var v = e == null ? null : e.GetAttributeValue<OptionSetValue>(column);
            return v == null ? -1 : v.Value;
        }
    }
}
