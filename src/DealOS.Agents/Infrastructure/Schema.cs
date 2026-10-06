using System.Collections.Generic;
using System.Linq;

namespace DealOS.Agents.Infrastructure
{
    /// <summary>Builders for Gemini (OpenAPI-subset) schemas used in tool parameters and structured output.</summary>
    public static class S
    {
        public static Dictionary<string, object> Str(string description)
        {
            return J.Obj("type", "STRING", "description", description);
        }

        public static Dictionary<string, object> Enum(string description, params string[] values)
        {
            return J.Obj("type", "STRING", "description", description, "enum", values.Cast<object>().ToList());
        }

        public static Dictionary<string, object> Num(string description)
        {
            return J.Obj("type", "NUMBER", "description", description);
        }

        public static Dictionary<string, object> Int(string description)
        {
            return J.Obj("type", "INTEGER", "description", description);
        }

        public static Dictionary<string, object> Bool(string description)
        {
            return J.Obj("type", "BOOLEAN", "description", description);
        }

        public static Dictionary<string, object> Arr(string description, Dictionary<string, object> items)
        {
            return J.Obj("type", "ARRAY", "description", description, "items", items);
        }

        /// <summary>Object schema. Property names ending in '?' are optional; the '?' is stripped.</summary>
        public static Dictionary<string, object> Obj(string description, params object[] props)
        {
            var properties = new Dictionary<string, object>();
            var required = new List<object>();
            var order = new List<object>();
            for (var i = 0; i + 1 < props.Length; i += 2)
            {
                var name = (string)props[i];
                var optional = name.EndsWith("?");
                if (optional) name = name.TrimEnd('?');
                properties[name] = props[i + 1];
                order.Add(name);
                if (!optional) required.Add(name);
            }
            var o = J.Obj("type", "OBJECT", "properties", properties, "propertyOrdering", order);
            if (!string.IsNullOrEmpty(description)) o["description"] = description;
            if (required.Count > 0) o["required"] = required;
            return o;
        }

        /// <summary>Checks that required properties exist (one level deep, plus arrays of objects).</summary>
        public static List<string> Validate(Dictionary<string, object> schema, Dictionary<string, object> value, string path = "")
        {
            var errors = new List<string>();
            if (value == null) { errors.Add((path == "" ? "result" : path) + " is missing"); return errors; }
            var props = J.ObjOf(schema, "properties") ?? new Dictionary<string, object>();
            foreach (var r in J.Arr(schema, "required"))
            {
                var name = (string)r;
                var v = J.Get(value, name);
                if (v == null || (v is string && ((string)v).Length == 0)) errors.Add(path + name + " is required");
            }
            foreach (var kv in props)
            {
                var ps = kv.Value as Dictionary<string, object>;
                var v = J.Get(value, kv.Key);
                if (ps == null || v == null) continue;
                var en = J.Get(ps, "enum") as List<object>;
                if (en != null && v is string && !en.Contains(v)) errors.Add(path + kv.Key + " must be one of: " + string.Join(", ", en));
                var child = v as Dictionary<string, object>;
                if (child != null && J.Str(ps, "type") == "OBJECT") errors.AddRange(Validate(ps, child, path + kv.Key + "."));
            }
            return errors;
        }
    }
}
