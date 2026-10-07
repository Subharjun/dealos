using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DealOS.Agents.Infrastructure;

namespace DealOS.Agents.Mail
{
    /// <summary>Email Desk choice values (order = deploy_schema.py TRIAGE / MAIL_CATEGORIES).</summary>
    public static class MailChoice
    {
        public static readonly string[] Triage = { "Genuine", "Review", "Ignored" };
        public static readonly string[] Categories = { "Buyer Requirement", "Offer To Sell", "Thread Reply", "Documents", "Vendor Pitch", "Scam", "Not Trade" };
        public static readonly string[] TradeCategories = { "Buyer Requirement", "Offer To Sell", "Thread Reply", "Documents" };
        public static class Direction { public const int Inbound = Choice.Base, Outbound = Choice.Base + 1, Draft = Choice.Base + 2; }
        public const int ChannelEmail = Choice.Base + 1;
        public const int DocumentQuarantined = Choice.Base + 3;

        public static int TriageValue(string label) { return Choice.ValueOf(Triage, label); }
        public static int CategoryValue(string label) { return Choice.ValueOf(Categories, label); }
    }

    /// <summary>
    /// Hard genuineness signals computed in code from headers, addresses, links and attachments.
    /// The triage model sees them as data; the final verdict (Decide) applies them so the model cannot argue them away.
    /// </summary>
    public sealed class MailSignals
    {
        private static readonly HashSet<string> FreeMail = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "gmail.com", "googlemail.com", "yahoo.com", "yahoo.co.in", "yahoo.in", "yahoo.co.uk", "ymail.com", "rocketmail.com", "outlook.com", "hotmail.com",
            "live.com", "msn.com", "aol.com", "icloud.com", "me.com", "mac.com", "proton.me", "protonmail.com", "gmx.com", "gmx.de", "gmx.net", "web.de",
            "mail.com", "mail.ru", "inbox.ru", "list.ru", "bk.ru", "yandex.ru", "yandex.com", "rambler.ru", "163.com", "126.com", "yeah.net", "qq.com",
            "foxmail.com", "sina.com", "sina.cn", "sohu.com", "aliyun.com", "rediffmail.com", "zoho.com", "zohomail.com", "tutanota.com", "naver.com"
        };
        private static readonly string[] DangerousExt = { ".exe", ".scr", ".js", ".jse", ".vbs", ".vbe", ".bat", ".cmd", ".com", ".msi", ".jar", ".ps1", ".hta", ".html", ".htm", ".shtml", ".svg", ".iso", ".img", ".lnk", ".reg", ".dll", ".apk" };
        private static readonly string[] ArchiveExt = { ".zip", ".rar", ".7z", ".gz", ".tar", ".cab" };
        private static readonly HashSet<string> Shorteners = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "bit.ly", "tinyurl.com", "t.co", "goo.gl", "ow.ly", "is.gd", "buff.ly", "rebrand.ly", "cutt.ly", "shorturl.at", "rb.gy", "tiny.cc" };
        private static readonly HashSet<string> NeutralLinkDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "google.com", "gmail.com", "linkedin.com", "wa.me", "whatsapp.com", "microsoft.com", "outlook.com", "office.com", "w3.org", "apple.com", "youtube.com", "facebook.com", "x.com", "twitter.com" };

        public string Spf, Dkim, Dmarc;
        public string FromDomain;
        public bool FreeMailSender;
        public bool ReplyToMismatch;
        public bool Bulk;
        public bool NoReplySender;
        public bool SenderDomainInBody;
        public readonly List<string> DangerousAttachments = new List<string>();
        public readonly List<string> Archives = new List<string>();
        public readonly List<string> LinkDomains = new List<string>();
        public readonly List<string> ForeignLinkDomains = new List<string>();
        public readonly List<string> ShortLinks = new List<string>();

        /// <summary>Positively authenticated: DMARC pass, or SPF and DKIM both pass (stricter than "not failed").</summary>
        public bool Authenticated
        {
            get { return Dmarc == "pass" || (Spf == "pass" && Dkim == "pass"); }
        }

        public bool AuthFailed
        {
            get { return Dmarc == "fail" || (Spf == "fail" || Spf == "softfail") && Dkim != "pass"; }
        }

        public static MailSignals From(GmailMessage m)
        {
            var s = new MailSignals();
            var auth = m.Header("Authentication-Results") ?? m.Header("ARC-Authentication-Results") ?? "";
            s.Spf = AuthResult(auth, "spf") ?? SpfFromReceived(m.Header("Received-SPF"));
            s.Dkim = AuthResult(auth, "dkim");
            s.Dmarc = AuthResult(auth, "dmarc");
            s.FromDomain = m.From == null ? null : m.From.Domain;
            s.FreeMailSender = s.FromDomain != null && FreeMail.Contains(s.FromDomain);
            s.ReplyToMismatch = m.ReplyTo != null && m.From != null && !string.Equals(m.ReplyTo.Address, m.From.Address, StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(Registrable(m.ReplyTo.Domain), Registrable(s.FromDomain), StringComparison.OrdinalIgnoreCase);
            var precedence = (m.Header("Precedence") ?? "").ToLowerInvariant();
            var auto = (m.Header("Auto-Submitted") ?? "no").ToLowerInvariant();
            s.Bulk = m.Header("List-Unsubscribe") != null || m.Header("List-Id") != null || precedence == "bulk" || precedence == "list" || precedence == "junk" || auto != "no";
            s.NoReplySender = m.From != null && Regex.IsMatch(m.From.Address, @"^(no-?reply|do-?not-?reply|mailer-daemon|notifications?)@", RegexOptions.IgnoreCase);

            foreach (var a in m.Attachments)
            {
                var name = (a.FileName ?? "").ToLowerInvariant();
                if (DangerousExt.Any(e => name.EndsWith(e))) s.DangerousAttachments.Add(a.FileName);
                else if (ArchiveExt.Any(e => name.EndsWith(e))) s.Archives.Add(a.FileName);
            }

            var text = (m.Text ?? "") + "\n" + (m.Html ?? "");
            var sender = Registrable(s.FromDomain);
            foreach (Match link in Regex.Matches(text, @"https?://([a-z0-9.-]+\.[a-z]{2,})", RegexOptions.IgnoreCase))
            {
                var host = link.Groups[1].Value.ToLowerInvariant();
                var reg = Registrable(host);
                if (!s.LinkDomains.Contains(reg)) s.LinkDomains.Add(reg);
                if (Shorteners.Contains(host) && !s.ShortLinks.Contains(host)) s.ShortLinks.Add(host);
                else if (reg != sender && !NeutralLinkDomains.Contains(reg) && !s.ForeignLinkDomains.Contains(reg)) s.ForeignLinkDomains.Add(reg);
            }
            if (sender != null && !s.FreeMailSender)
                s.SenderDomainInBody = Regex.IsMatch(m.BodyText ?? "", @"(www\.|@|https?://)" + Regex.Escape(sender), RegexOptions.IgnoreCase);
            return s;
        }

        public Dictionary<string, object> ToJson()
        {
            return J.Obj("spf", Spf, "dkim", Dkim, "dmarc", Dmarc, "auth_failed", AuthFailed, "from_domain", FromDomain,
                         "free_mail_sender", FreeMailSender, "reply_to_mismatch", ReplyToMismatch, "bulk_or_automated", Bulk,
                         "no_reply_sender", NoReplySender, "sender_domain_in_body", SenderDomainInBody,
                         "dangerous_attachments", DangerousAttachments.Cast<object>().ToList(), "archives", Archives.Cast<object>().ToList(),
                         "link_domains", LinkDomains.Take(20).Cast<object>().ToList(),
                         "foreign_link_domains", ForeignLinkDomains.Take(20).Cast<object>().ToList(),
                         "short_links", ShortLinks.Cast<object>().ToList());
        }

        public static MailSignals FromJson(Dictionary<string, object> o)
        {
            var s = new MailSignals();
            if (o == null) return s;
            s.Spf = J.Str(o, "spf"); s.Dkim = J.Str(o, "dkim"); s.Dmarc = J.Str(o, "dmarc");
            s.FromDomain = J.Str(o, "from_domain");
            s.FreeMailSender = J.Bool(o, "free_mail_sender");
            s.ReplyToMismatch = J.Bool(o, "reply_to_mismatch");
            s.Bulk = J.Bool(o, "bulk_or_automated");
            s.NoReplySender = J.Bool(o, "no_reply_sender");
            s.SenderDomainInBody = J.Bool(o, "sender_domain_in_body");
            s.DangerousAttachments.AddRange(J.Arr(o, "dangerous_attachments").OfType<string>());
            s.Archives.AddRange(J.Arr(o, "archives").OfType<string>());
            s.LinkDomains.AddRange(J.Arr(o, "link_domains").OfType<string>());
            s.ForeignLinkDomains.AddRange(J.Arr(o, "foreign_link_domains").OfType<string>());
            s.ShortLinks.AddRange(J.Arr(o, "short_links").OfType<string>());
            return s;
        }

        private static string AuthResult(string header, string method)
        {
            var results = Regex.Matches(header ?? "", @"\b" + method + @"=([a-z]+)", RegexOptions.IgnoreCase).Cast<Match>()
                               .Select(x => x.Groups[1].Value.ToLowerInvariant()).ToList();
            if (results.Count == 0) return null;
            return results.Contains("pass") ? "pass" : results[0];
        }

        private static string SpfFromReceived(string header)
        {
            var m = Regex.Match(header ?? "", @"^\s*([a-z]+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }

        /// <summary>Naive registrable domain: last two labels, or three for second-level public suffixes (co.in, com.cn, co.uk …).</summary>
        public static string Registrable(string host)
        {
            if (string.IsNullOrEmpty(host)) return null;
            var labels = host.ToLowerInvariant().TrimEnd('.').Split('.');
            if (labels.Length <= 2) return string.Join(".", labels);
            var sld = labels[labels.Length - 2];
            var tld = labels[labels.Length - 1];
            var take = tld.Length == 2 && new[] { "co", "com", "net", "org", "gov", "ac", "edu", "ltd", "plc", "or", "ne", "go" }.Contains(sld) ? 3 : 2;
            return string.Join(".", labels.Skip(labels.Length - take));
        }
    }

    /// <summary>The Email Desk verdict: Genuine / Review / Ignored, with the Gmail label to apply.</summary>
    public sealed class TriageDecision
    {
        public string Verdict;
        public int Score;
        public string Label;
        public readonly List<string> Reasons = new List<string>();

        public const string LabelBuyer = "DealOS/Buyer", LabelSeller = "DealOS/Seller", LabelGenuine = "DealOS/Genuine",
                            LabelReview = "DealOS/Review", LabelIgnored = "DealOS/Ignored", LabelProcessed = "DealOS/Processed", LabelError = "DealOS/Error";

        public static readonly string[] AllLabels = { LabelBuyer, LabelSeller, LabelGenuine, LabelReview, LabelIgnored, LabelProcessed, LabelError };

        /// <summary>
        /// Applies the hard signals to the model's category and score.
        /// knownSender: a contact/account we already have; threadWithUs: we have written in this thread. Neither is ever Ignored.
        /// </summary>
        public static TriageDecision Decide(string category, int modelScore, MailSignals s, bool knownSender, bool threadWithUs, bool injection, int proceed, int ignore)
        {
            var d = new TriageDecision { Score = Math.Max(0, Math.Min(100, modelScore)) };
            if (s.DangerousAttachments.Count > 0) { d.Score = Math.Min(d.Score, 10); d.Reasons.Add("dangerous attachment: " + string.Join(", ", s.DangerousAttachments)); }
            if (s.Dmarc == "fail") { d.Score = Math.Min(d.Score, 20); d.Reasons.Add("DMARC failed: the sender address is probably forged"); }
            else if (s.AuthFailed) { d.Score = Math.Min(d.Score, 25); d.Reasons.Add("SPF failed and no valid DKIM signature"); }
            if (s.ShortLinks.Count > 0) { d.Score = Math.Min(d.Score, 40); d.Reasons.Add("link shortener used: " + string.Join(", ", s.ShortLinks)); }
            if (s.ReplyToMismatch) { d.Score = Math.Max(0, d.Score - 15); d.Reasons.Add("replies are redirected to a different domain"); }

            var trade = MailChoice.TradeCategories.Contains(category);
            if (!trade) { d.Verdict = "Ignored"; d.Reasons.Add("not a trade email (" + category + ")"); }
            else if (injection) { d.Verdict = d.Score >= ignore ? "Review" : "Ignored"; d.Reasons.Add("text tries to instruct the AI"); }
            else if (d.Score >= proceed) d.Verdict = "Genuine";
            else if (d.Score >= ignore) d.Verdict = "Review";
            else d.Verdict = "Ignored";

            if (d.Verdict == "Ignored" && (knownSender || threadWithUs) && s.DangerousAttachments.Count == 0)
            {
                d.Verdict = "Review";
                d.Reasons.Add(threadWithUs ? "we have already written in this thread" : "sender is already known to us");
            }

            if (d.Verdict == "Genuine")
                d.Label = category == "Buyer Requirement" ? LabelBuyer : category == "Offer To Sell" ? LabelSeller : LabelGenuine;
            else d.Label = d.Verdict == "Review" ? LabelReview : LabelIgnored;
            return d;
        }
    }
}
