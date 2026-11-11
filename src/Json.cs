using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Trasgo
{
    /// <summary>Tiny dependency-free JSON parser/serializer.
    /// Objects -> Dictionary&lt;string,object&gt;, arrays -> List&lt;object&gt;, numbers -> long or double.</summary>
    public static class Json
    {
        public static object Parse(string s)
        {
            if (s == null) throw new ArgumentNullException("s");
            int i = 0;
            SkipWs(s, ref i);
            object v = ParseValue(s, ref i);
            SkipWs(s, ref i);
            if (i != s.Length) throw new FormatException("JSON: trailing characters at " + i);
            return v;
        }

        public static bool TryParse(string s, out object value)
        {
            try { value = Parse(s); return true; } catch { value = null; return false; }
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r' || s[i] == '﻿')) i++;
        }

        static object ParseValue(string s, ref int i)
        {
            if (i >= s.Length) throw new FormatException("JSON: unexpected end");
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        static void Expect(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException("JSON: expected " + word + " at " + i);
            i += word.Length;
        }

        static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++; SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("JSON: expected key at " + i);
                string k = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("JSON: expected ':' at " + i);
                i++; SkipWs(s, ref i);
                d[k] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("JSON: unexpected end in object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("JSON: expected ',' or '}' at " + i);
            }
        }

        static List<object> ParseArray(string s, ref int i)
        {
            var l = new List<object>();
            i++; SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (true)
            {
                SkipWs(s, ref i);
                l.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("JSON: unexpected end in array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return l; }
                throw new FormatException("JSON: expected ',' or ']' at " + i);
            }
        }

        static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
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
                        if (i + 4 > s.Length) throw new FormatException("JSON: bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4; break;
                    default: throw new FormatException("JSON: bad escape \\" + e);
                }
            }
            throw new FormatException("JSON: unterminated string");
        }

        static object ParseNumber(string s, ref int i)
        {
            int start = i;
            bool isFloat = false;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length)
            {
                char c = s[i];
                if (c >= '0' && c <= '9') { i++; continue; }
                if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { isFloat = true; i++; continue; }
                break;
            }
            string num = s.Substring(start, i - start);
            if (num.Length == 0) throw new FormatException("JSON: unexpected character '" + s[start] + "' at " + start);
            long l;
            if (!isFloat && long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
            return double.Parse(num, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        // ---------------- serialization ----------------

        public static string Serialize(object o, bool pretty = false)
        {
            var sb = new StringBuilder();
            Write(sb, o, pretty, 0);
            return sb.ToString();
        }

        static void Indent(StringBuilder sb, bool pretty, int level)
        {
            if (!pretty) return;
            sb.Append('\n');
            sb.Append(' ', level * 2);
        }

        static void Write(StringBuilder sb, object o, bool pretty, int level)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is JsonRaw) { sb.Append(((JsonRaw)o).Text); return; }
            if (o is string) { WriteString(sb, (string)o); return; }
            if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
            if (o is int || o is long || o is short || o is byte || o is uint || o is ulong)
            { sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture)); return; }
            if (o is double || o is float || o is decimal)
            {
                double d = Convert.ToDouble(o, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return;
            }
            if (o is Enum) { WriteString(sb, o.ToString()); return; }
            var dict = o as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Indent(sb, pretty, level + 1);
                    WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                    sb.Append(pretty ? ": " : ":");
                    Write(sb, kv.Value, pretty, level + 1);
                }
                if (!first) Indent(sb, pretty, level);
                sb.Append('}');
                return;
            }
            var list = o as IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Indent(sb, pretty, level + 1);
                    Write(sb, item, pretty, level + 1);
                }
                if (!first) Indent(sb, pretty, level);
                sb.Append(']');
                return;
            }
            WriteString(sb, o.ToString());
        }

        public static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
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
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public static string Quote(string s) { var sb = new StringBuilder(); WriteString(sb, s ?? ""); return sb.ToString(); }

        // ---------------- helpers ----------------

        public static Dictionary<string, object> Obj(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        public static List<object> Arr(params object[] items) { return new List<object>(items); }

        /// <summary>Navigate a parsed JSON tree: Get(o, "a", 0, "b").</summary>
        public static object Get(object o, params object[] path)
        {
            foreach (var p in path)
            {
                if (o == null) return null;
                if (p is string)
                {
                    var d = o as Dictionary<string, object>;
                    object v;
                    if (d == null || !d.TryGetValue((string)p, out v)) return null;
                    o = v;
                }
                else
                {
                    var l = o as List<object>;
                    int idx = Convert.ToInt32(p, CultureInfo.InvariantCulture);
                    if (l == null || idx < 0 || idx >= l.Count) return null;
                    o = l[idx];
                }
            }
            return o;
        }

        public static string Str(object o, params object[] path)
        {
            var v = Get(o, path);
            if (v == null) return null;
            if (v is string) return (string)v;
            if (v is bool) return (bool)v ? "true" : "false";
            if (v is double) return ((double)v).ToString(CultureInfo.InvariantCulture);
            if (v is long) return ((long)v).ToString(CultureInfo.InvariantCulture);
            return Serialize(v);
        }

        public static long Long(object o, long def, params object[] path)
        {
            var v = Get(o, path);
            if (v is long) return (long)v;
            if (v is double) return (long)(double)v;
            if (v is string) { long r; if (long.TryParse((string)v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return r; }
            return def;
        }

        public static double Dbl(object o, double def, params object[] path)
        {
            var v = Get(o, path);
            if (v is long) return (long)v;
            if (v is double) return (double)v;
            if (v is string) { double r; if (double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return r; }
            return def;
        }

        public static bool Bool(object o, bool def, params object[] path)
        {
            var v = Get(o, path);
            if (v is bool) return (bool)v;
            if (v is string) { var s = ((string)v).ToLowerInvariant(); if (s == "true" || s == "1" || s == "yes" || s == "si" || s == "sí") return true; if (s == "false" || s == "0" || s == "no") return false; }
            if (v is long) return (long)v != 0;
            return def;
        }

        public static List<object> List(object o, params object[] path) { return Get(o, path) as List<object> ?? new List<object>(); }
        public static Dictionary<string, object> Dict(object o, params object[] path) { return Get(o, path) as Dictionary<string, object>; }
    }

    /// <summary>Pre-serialized JSON fragment, written verbatim.</summary>
    public sealed class JsonRaw
    {
        public readonly string Text;
        public JsonRaw(string text) { Text = text; }
    }
}
