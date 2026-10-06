using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DealOS.Agents.Infrastructure
{
    /// <summary>
    /// Dependency-free JSON for the plug-in sandbox.
    /// Objects parse to Dictionary&lt;string, object&gt;, arrays to List&lt;object&gt;,
    /// numbers to long or double, plus string, bool and null.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var p = new Parser(text);
            p.SkipWs();
            var value = p.ReadValue();
            p.SkipWs();
            if (!p.End) throw new FormatException("Trailing characters after JSON value at " + p.Pos);
            return value;
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            var o = Parse(text) as Dictionary<string, object>;
            if (o == null) throw new FormatException("JSON value is not an object.");
            return o;
        }

        /// <summary>Parses JSON that a model may have wrapped in ```json fences or surrounded with prose.</summary>
        public static object ParseLenient(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text.Trim();
            if (t.StartsWith("```"))
            {
                var nl = t.IndexOf('\n');
                if (nl > 0) t = t.Substring(nl + 1);
                var end = t.LastIndexOf("```", StringComparison.Ordinal);
                if (end >= 0) t = t.Substring(0, end);
                t = t.Trim();
            }
            try { return Parse(t); }
            catch (FormatException)
            {
                int a = t.IndexOf('{'), b = t.LastIndexOf('}');
                if (a >= 0 && b > a) return Parse(t.Substring(a, b - a + 1));
                throw;
            }
        }

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            var s = v as string;
            if (s != null) { WriteString(sb, s); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is Guid) { WriteString(sb, ((Guid)v).ToString()); return; }
            if (v is DateTime) { WriteString(sb, ((DateTime)v).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)); return; }
            if (v is int || v is long || v is short || v is byte) { sb.Append(Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is double || v is float || v is decimal)
            {
                var d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            var dict = v as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                var first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    Write(sb, e.Value);
                }
                sb.Append('}');
                return;
            }
            var en = v as IEnumerable;
            if (en != null)
            {
                sb.Append('[');
                var first = true;
                foreach (var item in en)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item);
                }
                sb.Append(']');
                return;
            }
            WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private sealed class Parser
        {
            private readonly string _s;
            public int Pos;
            public Parser(string s) { _s = s; }
            public bool End { get { return Pos >= _s.Length; } }

            public void SkipWs()
            {
                while (Pos < _s.Length && char.IsWhiteSpace(_s[Pos])) Pos++;
            }

            public object ReadValue()
            {
                SkipWs();
                if (End) throw new FormatException("Unexpected end of JSON.");
                var c = _s[Pos];
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return ReadNumber();
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(_s, Pos, word, 0, word.Length) != 0)
                    throw new FormatException("Invalid token at " + Pos);
                Pos += word.Length;
            }

            private Dictionary<string, object> ReadObject()
            {
                var o = new Dictionary<string, object>(StringComparer.Ordinal);
                Pos++;
                SkipWs();
                if (!End && _s[Pos] == '}') { Pos++; return o; }
                while (true)
                {
                    SkipWs();
                    if (End || _s[Pos] != '"') throw new FormatException("Expected property name at " + Pos);
                    var key = ReadString();
                    SkipWs();
                    if (End || _s[Pos] != ':') throw new FormatException("Expected ':' at " + Pos);
                    Pos++;
                    o[key] = ReadValue();
                    SkipWs();
                    if (End) throw new FormatException("Unterminated object.");
                    if (_s[Pos] == ',') { Pos++; continue; }
                    if (_s[Pos] == '}') { Pos++; return o; }
                    throw new FormatException("Expected ',' or '}' at " + Pos);
                }
            }

            private List<object> ReadArray()
            {
                var a = new List<object>();
                Pos++;
                SkipWs();
                if (!End && _s[Pos] == ']') { Pos++; return a; }
                while (true)
                {
                    a.Add(ReadValue());
                    SkipWs();
                    if (End) throw new FormatException("Unterminated array.");
                    if (_s[Pos] == ',') { Pos++; continue; }
                    if (_s[Pos] == ']') { Pos++; return a; }
                    throw new FormatException("Expected ',' or ']' at " + Pos);
                }
            }

            private string ReadString()
            {
                var sb = new StringBuilder();
                Pos++;
                while (true)
                {
                    if (End) throw new FormatException("Unterminated string.");
                    var c = _s[Pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (End) throw new FormatException("Bad escape.");
                    var e = _s[Pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (Pos + 4 > _s.Length) throw new FormatException("Bad unicode escape.");
                            sb.Append((char)int.Parse(_s.Substring(Pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            Pos += 4;
                            break;
                        default: throw new FormatException("Bad escape \\" + e);
                    }
                }
            }

            private object ReadNumber()
            {
                var start = Pos;
                while (Pos < _s.Length && "+-0123456789.eE".IndexOf(_s[Pos]) >= 0) Pos++;
                var tok = _s.Substring(start, Pos - start);
                if (tok.Length == 0) throw new FormatException("Unexpected character '" + _s[start] + "' at " + start);
                long l;
                if (tok.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 && long.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
                return double.Parse(tok, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>Typed accessors over parsed JSON.</summary>
    public static class J
    {
        public static Dictionary<string, object> Obj(params object[] kv)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        public static object Get(IDictionary<string, object> o, string key)
        {
            object v;
            return o != null && o.TryGetValue(key, out v) ? v : null;
        }

        public static string Str(IDictionary<string, object> o, string key, string fallback = null)
        {
            var v = Get(o, key);
            if (v == null) return fallback;
            var s = v as string;
            return s ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static double? Num(IDictionary<string, object> o, string key)
        {
            var v = Get(o, key);
            if (v == null) return null;
            if (v is long || v is double || v is int) return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            double d;
            return double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : (double?)null;
        }

        public static bool Bool(IDictionary<string, object> o, string key, bool fallback = false)
        {
            var v = Get(o, key);
            if (v is bool) return (bool)v;
            var s = v as string;
            bool b;
            return s != null && bool.TryParse(s, out b) ? b : fallback;
        }

        public static Dictionary<string, object> ObjOf(IDictionary<string, object> o, string key)
        {
            return Get(o, key) as Dictionary<string, object>;
        }

        public static List<object> Arr(IDictionary<string, object> o, string key)
        {
            return Get(o, key) as List<object> ?? new List<object>();
        }

        public static Guid? Id(IDictionary<string, object> o, string key)
        {
            Guid g;
            var s = Str(o, key);
            return s != null && Guid.TryParse(s, out g) ? g : (Guid?)null;
        }
    }
}
