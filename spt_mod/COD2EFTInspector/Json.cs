// Minimal JSON reader (objects -> Dictionary<string, object>, arrays -> List<object>, numbers -> double).
// Own code so the plugin doesn't depend on which Newtonsoft version the game ships. No Unity types: unit-tested with mono.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace COD2EFTInspector
{
    internal static class Json
    {
        public static object Parse(string s)
        {
            int i = 0;
            var v = Value(s, ref i);
            Ws(s, ref i);
            if (i < s.Length) throw new FormatException($"JSON: extra text at {i}");
            return v;
        }

        /// <summary>Walks a path of object keys; null when any step is missing.</summary>
        public static object At(object o, params string[] keys)
        {
            foreach (var k in keys)
            {
                var d = o as Dictionary<string, object>;
                if (d == null || !d.TryGetValue(k, out o)) return null;
            }
            return o;
        }

        public static string Str(object o, params string[] keys) => At(o, keys) as string;

        static void Ws(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '﻿') i++;
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; }   // tolerate // comments (jsonc)
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') { int e = s.IndexOf("*/", i + 2, StringComparison.Ordinal); i = e < 0 ? s.Length : e + 2; }
                else break;
            }
        }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON: unexpected end");
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>();
                i++; Ws(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == '}') { i++; return d; }   // trailing comma
                    string k = Str(s, ref i);
                    Ws(s, ref i);
                    Expect(s, ref i, ':');
                    d[k] = Value(s, ref i);
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, '}');
                    return d;
                }
            }
            if (c == '[')
            {
                var l = new List<object>();
                i++; Ws(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true)
                {
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ']') { i++; return l; }
                    l.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, ']');
                    return l;
                }
            }
            if (c == '"') return Str(s, ref i);
            if (Word(s, ref i, "true")) return true;
            if (Word(s, ref i, "false")) return false;
            if (Word(s, ref i, "null")) return null;
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (st == i) throw new FormatException($"JSON: unexpected '{c}' at {i}");
            return double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static bool Word(string s, ref int i, string w)
        {
            if (string.CompareOrdinal(s, i, w, 0, w.Length) != 0) return false;
            i += w.Length;
            return true;
        }

        static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c) throw new FormatException($"JSON: expected '{c}' at {i}");
            i++;
        }

        static string Str(string s, ref int i)
        {
            Expect(s, ref i, '"');
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
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("JSON: bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("JSON: unterminated string");
        }
    }
}
