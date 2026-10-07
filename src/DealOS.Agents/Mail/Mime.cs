using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DealOS.Agents.Mail
{
    /// <summary>Builds the RFC 2822 message Gmail expects in drafts.create (base64url "raw").</summary>
    public static class MimeBuilder
    {
        public sealed class Attachment
        {
            public string FileName;
            public string MimeType;
            public byte[] Data;
        }

        /// <summary>
        /// Plain-text email, optionally with attachments. From is left out: Gmail sets the signed-in mailbox.
        /// inReplyTo/references thread the reply in the recipient's mail client; replyTo routes answers to a desk alias.
        /// </summary>
        public static string Build(string to, string subject, string body, string replyTo, string inReplyTo, string references, IList<Attachment> attachments)
        {
            var sb = new StringBuilder();
            sb.Append("To: ").Append(to).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(replyTo)) sb.Append("Reply-To: ").Append(replyTo).Append("\r\n");
            sb.Append("Subject: ").Append(EncodeHeader(subject ?? "")).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(inReplyTo))
            {
                sb.Append("In-Reply-To: ").Append(inReplyTo.Trim()).Append("\r\n");
                sb.Append("References: ").Append(string.IsNullOrWhiteSpace(references) ? inReplyTo.Trim() : references.Trim() + " " + inReplyTo.Trim()).Append("\r\n");
            }
            sb.Append("MIME-Version: 1.0\r\n");
            var text = Base64Lines(Encoding.UTF8.GetBytes((body ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n")));
            if (attachments == null || attachments.Count == 0)
            {
                sb.Append("Content-Type: text/plain; charset=\"UTF-8\"\r\nContent-Transfer-Encoding: base64\r\n\r\n").Append(text);
            }
            else
            {
                var boundary = "dealos-" + Guid.NewGuid().ToString("N");
                sb.Append("Content-Type: multipart/mixed; boundary=\"").Append(boundary).Append("\"\r\n\r\n");
                sb.Append("--").Append(boundary).Append("\r\nContent-Type: text/plain; charset=\"UTF-8\"\r\nContent-Transfer-Encoding: base64\r\n\r\n").Append(text);
                foreach (var a in attachments)
                {
                    var name = (a.FileName ?? "attachment").Replace("\"", "'");
                    sb.Append("--").Append(boundary).Append("\r\n");
                    sb.Append("Content-Type: ").Append(string.IsNullOrEmpty(a.MimeType) ? "application/octet-stream" : a.MimeType).Append("; name=\"").Append(name).Append("\"\r\n");
                    sb.Append("Content-Disposition: attachment; filename=\"").Append(name).Append("\"\r\nContent-Transfer-Encoding: base64\r\n\r\n");
                    sb.Append(Base64Lines(a.Data ?? new byte[0]));
                }
                sb.Append("--").Append(boundary).Append("--\r\n");
            }
            return ToBase64Url(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        public static string ToBase64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string EncodeHeader(string value)
        {
            return value.All(c => c >= 32 && c < 127) ? value : "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "?=";
        }

        private static string Base64Lines(byte[] data)
        {
            var b64 = Convert.ToBase64String(data);
            var sb = new StringBuilder();
            for (var i = 0; i < b64.Length; i += 76) sb.Append(b64, i, Math.Min(76, b64.Length - i)).Append("\r\n");
            return sb.ToString();
        }
    }

    /// <summary>A minimal text-only PDF (Helvetica, A4, word-wrapped, several pages) for contracts sent by email.</summary>
    public static class PdfWriter
    {
        public static byte[] Write(string title, IEnumerable<string> lines)
        {
            const int perPage = 52, width = 92;
            var wrapped = new List<string>();
            foreach (var line in lines ?? new string[0])
            {
                var rest = (line ?? "").Replace("\t", "    ");
                if (rest.Length == 0) { wrapped.Add(""); continue; }
                while (rest.Length > width)
                {
                    var cut = rest.LastIndexOf(' ', width);
                    if (cut <= 0) cut = width;
                    wrapped.Add(rest.Substring(0, cut));
                    rest = rest.Substring(cut).TrimStart();
                }
                wrapped.Add(rest);
            }
            var pages = new List<List<string>>();
            for (var i = 0; i < Math.Max(1, wrapped.Count); i += perPage) pages.Add(wrapped.Skip(i).Take(perPage).ToList());

            var objects = new List<string>();
            // 1 catalog, 2 pages, 3 font, then per page: page object + content stream
            var kids = string.Join(" ", pages.Select((p, i) => (4 + i * 2) + " 0 R"));
            objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
            objects.Add("<< /Type /Pages /Kids [" + kids + "] /Count " + pages.Count + " >>");
            objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            for (var p = 0; p < pages.Count; p++)
            {
                var content = new StringBuilder("BT /F1 10 Tf 50 800 Td 14 TL\n");
                if (p == 0 && !string.IsNullOrEmpty(title)) content.Append("/F1 14 Tf (").Append(Escape(title)).Append(") Tj T* T* /F1 10 Tf\n");
                foreach (var l in pages[p]) content.Append("(").Append(Escape(l)).Append(") Tj T*\n");
                content.Append("ET");
                var stream = content.ToString();
                objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents " + (5 + p * 2) + " 0 R >>");
                objects.Add("<< /Length " + Latin1(stream).Length + " >>\nstream\n" + stream + "\nendstream");
            }

            using (var ms = new MemoryStream())
            {
                var offsets = new List<long>();
                Action<string> put = s => { var b = Latin1(s); ms.Write(b, 0, b.Length); };
                put("%PDF-1.4\n");
                for (var i = 0; i < objects.Count; i++)
                {
                    offsets.Add(ms.Position);
                    put((i + 1) + " 0 obj\n" + objects[i] + "\nendobj\n");
                }
                var xref = ms.Position;
                put("xref\n0 " + (objects.Count + 1) + "\n0000000000 65535 f \n");
                foreach (var o in offsets) put(o.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
                put("trailer\n<< /Size " + (objects.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
                return ms.ToArray();
            }
        }

        private static string Escape(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s ?? "")
            {
                if (c == '(' || c == ')' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '₂') sb.Append('2');
                else if (c > 255) sb.Append('?');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static byte[] Latin1(string s)
        {
            return s.Select(c => (byte)(c > 255 ? '?' : c)).ToArray();
        }
    }
}
