using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DealOS.Agents.Infrastructure;

namespace DealOS.Agents.Mail
{
    public sealed class MailAddress
    {
        public string Name;
        public string Address;

        public string Domain { get { var i = Address == null ? -1 : Address.LastIndexOf('@'); return i < 0 ? null : Address.Substring(i + 1).ToLowerInvariant(); } }

        public override string ToString() { return string.IsNullOrEmpty(Name) ? Address : Name + " <" + Address + ">"; }
    }

    public sealed class MailAttachment
    {
        public string AttachmentId;
        public string FileName;
        public string MimeType;
        public long Size;
        /// <summary>Base64url content when Gmail returned it inline (small parts); otherwise fetch it by AttachmentId.</summary>
        public string InlineData;
    }

    /// <summary>A Gmail API message (users.messages.get, format=full) reduced to what the Email Desk needs.</summary>
    public sealed class GmailMessage
    {
        public string Id;
        public string ThreadId;
        public List<string> Labels = new List<string>();
        public string Snippet;
        public DateTime? InternalDate;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public MailAddress From;
        public MailAddress ReplyTo;
        public List<MailAddress> To = new List<MailAddress>();
        public List<MailAddress> Cc = new List<MailAddress>();
        public string Subject;
        public string Text;
        public string Html;
        public readonly List<MailAttachment> Attachments = new List<MailAttachment>();

        /// <summary>Headers kept on gc_message.gc_emailmeta (and used for the genuineness signals).</summary>
        public static readonly string[] KeptHeaders =
        {
            "Date", "Message-ID", "In-Reply-To", "References", "Reply-To", "Return-Path", "Authentication-Results", "ARC-Authentication-Results",
            "Received-SPF", "List-Unsubscribe", "List-Id", "Precedence", "Auto-Submitted", "X-Mailer"
        };

        public bool IsSent { get { return Labels.Contains("SENT"); } }

        public DateTime? Date
        {
            get
            {
                DateTimeOffset d;
                var raw = Header("Date");
                if (raw != null)
                {
                    raw = Regex.Replace(raw, @"\s*\([^)]*\)\s*$", "");
                    if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out d)) return d.UtcDateTime;
                }
                return InternalDate;
            }
        }

        /// <summary>Plain text of the body: the text/plain part, else the HTML part converted to text.</summary>
        public string BodyText { get { return !string.IsNullOrWhiteSpace(Text) ? Text.Trim() : HtmlToText(Html); } }

        public string Header(string name)
        {
            string v;
            return Headers.TryGetValue(name, out v) ? v : null;
        }

        public static GmailMessage Parse(string json)
        {
            var o = Json.ParseObject(json);
            var m = new GmailMessage
            {
                Id = J.Str(o, "id"),
                ThreadId = J.Str(o, "threadId"),
                Snippet = J.Str(o, "snippet"),
                Labels = J.Arr(o, "labelIds").OfType<string>().ToList()
            };
            long ms;
            if (long.TryParse(J.Str(o, "internalDate") ?? Convert.ToString(J.Get(o, "internalDate"), CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out ms))
                m.InternalDate = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);
            var payload = J.ObjOf(o, "payload");
            if (payload != null)
            {
                foreach (var h in J.Arr(payload, "headers").OfType<Dictionary<string, object>>())
                {
                    var name = J.Str(h, "name");
                    if (name != null && !m.Headers.ContainsKey(name)) m.Headers[name] = J.Str(h, "value") ?? "";
                }
                m.Walk(payload);
            }
            m.Subject = m.Header("Subject");
            m.From = ParseAddresses(m.Header("From")).FirstOrDefault();
            m.ReplyTo = ParseAddresses(m.Header("Reply-To")).FirstOrDefault();
            m.To = ParseAddresses(m.Header("To"));
            m.Cc = ParseAddresses(m.Header("Cc"));
            return m;
        }

        private void Walk(Dictionary<string, object> part)
        {
            var mime = (J.Str(part, "mimeType") ?? "").ToLowerInvariant();
            var fileName = J.Str(part, "filename");
            var body = J.ObjOf(part, "body") ?? new Dictionary<string, object>();
            var data = J.Str(body, "data");
            if (!string.IsNullOrEmpty(fileName))
            {
                Attachments.Add(new MailAttachment
                {
                    AttachmentId = J.Str(body, "attachmentId"),
                    FileName = fileName,
                    MimeType = string.IsNullOrEmpty(mime) ? "application/octet-stream" : mime,
                    Size = (long)(J.Num(body, "size") ?? 0),
                    InlineData = data
                });
            }
            else if (mime == "text/plain" && data != null && Text == null) Text = DecodeText(data);
            else if (mime == "text/html" && data != null && Html == null) Html = DecodeText(data);
            foreach (var child in J.Arr(part, "parts").OfType<Dictionary<string, object>>()) Walk(child);
        }

        // ---------- helpers ----------

        public static byte[] FromBase64Url(string data)
        {
            if (string.IsNullOrEmpty(data)) return new byte[0];
            var s = data.Trim().Replace('-', '+').Replace('_', '/');
            s = s.Replace("\r", "").Replace("\n", "");
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }

        private static string DecodeText(string data)
        {
            try { return Encoding.UTF8.GetString(FromBase64Url(data)); }
            catch (FormatException) { return null; }
        }

        /// <summary>Splits an address header ("A B" &lt;a@b.com&gt;, c@d.com) into name and address pairs.</summary>
        public static List<MailAddress> ParseAddresses(string header)
        {
            var list = new List<MailAddress>();
            if (string.IsNullOrWhiteSpace(header)) return list;
            var parts = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false, angled = false;
            foreach (var c in header)
            {
                if (c == '"') quoted = !quoted;
                else if (c == '<' && !quoted) angled = true;
                else if (c == '>' && !quoted) angled = false;
                if ((c == ',' || c == ';') && !quoted && !angled) { parts.Add(sb.ToString()); sb.Clear(); continue; }
                sb.Append(c);
            }
            parts.Add(sb.ToString());
            foreach (var raw in parts.Select(p => p.Trim()).Where(p => p.Length > 0))
            {
                var m = Regex.Match(raw, @"^(?<name>.*?)\s*<(?<addr>[^>]+)>\s*$");
                string name = null, addr;
                if (m.Success)
                {
                    name = m.Groups["name"].Value.Trim().Trim('"').Trim();
                    addr = m.Groups["addr"].Value.Trim();
                }
                else addr = raw.Trim('"', ' ');
                if (addr.IndexOf('@') < 0) continue;
                list.Add(new MailAddress { Name = string.IsNullOrEmpty(name) ? null : name, Address = addr.ToLowerInvariant() });
            }
            return list;
        }

        public static string HtmlToText(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            var s = Regex.Replace(html, @"<(script|style|head)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<a\s[^>]*href\s*=\s*[""']([^""']+)[""'][^>]*>(.*?)</a>", "$2 ($1)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<(br|/p|/div|/tr|/li|/h\d)[^>]*>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<[^>]+>", " ");
            s = System.Net.WebUtility.HtmlDecode(s);
            s = Regex.Replace(s, @"[ \t ]+", " ");
            s = Regex.Replace(s, @"\s*\n\s*(\n\s*)+", "\n\n");
            return s.Trim();
        }
    }
}
