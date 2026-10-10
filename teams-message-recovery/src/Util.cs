// Teams Message History - shared helpers.
// Target: C# 5 (Windows PowerShell 5.1 Add-Type) and .NET Framework 4.x.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TeamsMessageHistory
{
    /// <summary>Little-endian base-128 varints as used by LevelDB, Chromium IndexedDB and V8.</summary>
    public static class Varint
    {
        public static bool TryRead(byte[] buffer, ref int position, int end, out ulong value)
        {
            value = 0;
            int shift = 0;
            int pos = position;
            while (pos < end && shift < 64)
            {
                byte b = buffer[pos++];
                value |= ((ulong)(b & 0x7F)) << shift;
                if ((b & 0x80) == 0)
                {
                    position = pos;
                    return true;
                }
                shift += 7;
            }
            return false;
        }

        public static bool TryReadInt(byte[] buffer, ref int position, int end, out int value)
        {
            ulong raw;
            value = 0;
            if (!TryRead(buffer, ref position, end, out raw)) return false;
            if (raw > int.MaxValue) return false;
            value = (int)raw;
            return true;
        }
    }

    public static class Hex
    {
        public static string ToHex(byte[] data)
        {
            if (data == null) return "";
            return ToHex(data, 0, data.Length);
        }

        public static string ToHex(byte[] data, int offset, int count)
        {
            StringBuilder sb = new StringBuilder(count * 2);
            for (int i = 0; i < count; i++) sb.Append(data[offset + i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    public static class Sha256Util
    {
        public static string HexOfBytes(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return Hex.ToHex(sha.ComputeHash(data));
            }
        }

        public static string HexOfString(string text)
        {
            return HexOfBytes(Encoding.UTF8.GetBytes(text ?? ""));
        }

        public static string HexOfFile(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                return Hex.ToHex(sha.ComputeHash(fs));
            }
        }
    }

    public static class ByteSearch
    {
        /// <summary>True when needle occurs in haystack[0..count).</summary>
        public static bool Contains(byte[] haystack, int count, byte[] needle)
        {
            if (haystack == null || needle == null || needle.Length == 0 || count < needle.Length) return false;
            int last = count - needle.Length;
            byte first = needle[0];
            for (int i = 0; i <= last; i++)
            {
                if (haystack[i] != first) continue;
                int j = 1;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return true;
            }
            return false;
        }

        public static int IndexOf(byte[] haystack, int count, byte[] needle)
        {
            if (haystack == null || needle == null || needle.Length == 0 || count < needle.Length) return -1;
            int last = count - needle.Length;
            byte first = needle[0];
            for (int i = 0; i <= last; i++)
            {
                if (haystack[i] != first) continue;
                int j = 1;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }
    }

    /// <summary>Where the run spends its time: named stages, summed (thread-safe). Shown at the end of a run and
    /// written to result.json, so slowness can be located instead of guessed.</summary>
    public static class Timing
    {
        private static readonly object Gate = new object();
        private static readonly List<string> Order = new List<string>();
        private static readonly Dictionary<string, long> Ticks = new Dictionary<string, long>(StringComparer.Ordinal);

        public static void Reset()
        {
            lock (Gate) { Order.Clear(); Ticks.Clear(); }
        }

        /// <summary>Fixes the position of a stage in the listing before its sub-stages report.</summary>
        public static void Declare(string name)
        {
            lock (Gate)
            {
                if (!Ticks.ContainsKey(name)) { Order.Add(name); Ticks[name] = 0; }
            }
        }

        public static long Start()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp();
        }

        public static void Stop(string name, long start)
        {
            long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            lock (Gate)
            {
                long t;
                if (!Ticks.TryGetValue(name, out t)) Order.Add(name);
                Ticks[name] = t + elapsed;
            }
        }

        public static List<KeyValuePair<string, double>> Seconds()
        {
            List<KeyValuePair<string, double>> list = new List<KeyValuePair<string, double>>();
            lock (Gate)
            {
                foreach (string name in Order) list.Add(new KeyValuePair<string, double>(name, Ticks[name] / (double)System.Diagnostics.Stopwatch.Frequency));
            }
            return list;
        }
    }

    /// <summary>Several byte needles searched in one pass (candidates are picked by their first byte).</summary>
    public sealed class MultiNeedle
    {
        /// <summary>Called for every occurrence; return false to stop the search.</summary>
        public delegate bool HitHandler(int needleIndex, int offset);

        private readonly List<byte[]> _needles = new List<byte[]>();
        private readonly List<int>[] _byFirst = new List<int>[256];

        public int Count { get { return _needles.Count; } }

        public int Add(byte[] needle)
        {
            if (needle == null || needle.Length == 0) return -1;
            _needles.Add(needle);
            int index = _needles.Count - 1;
            if (_byFirst[needle[0]] == null) _byFirst[needle[0]] = new List<int>();
            _byFirst[needle[0]].Add(index);
            return index;
        }

        public void FindAll(byte[] data, int count, HitHandler onHit)
        {
            if (data == null || _needles.Count == 0) return;
            for (int i = 0; i < count; i++)
            {
                List<int> candidates = _byFirst[data[i]];
                if (candidates == null) continue;
                for (int c = 0; c < candidates.Count; c++)
                {
                    byte[] needle = _needles[candidates[c]];
                    if (i + needle.Length > count) continue;
                    int j = 1;
                    while (j < needle.Length && data[i + j] == needle[j]) j++;
                    if (j == needle.Length && !onHit(candidates[c], i)) return;
                }
            }
        }

        public bool Any(byte[] data, int count)
        {
            bool found = false;
            FindAll(data, count, delegate(int needleIndex, int offset) { found = true; return false; });
            return found;
        }
    }

    /// <summary>Insertion-ordered string-keyed map (JavaScript objects and report sections).</summary>
    public sealed class OrderedMap : IEnumerable<KeyValuePair<string, object>>
    {
        private readonly List<string> _keys = new List<string>();
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>(StringComparer.Ordinal);

        public int Count { get { return _keys.Count; } }
        public IList<string> Keys { get { return _keys.AsReadOnly(); } }

        public void Set(string key, object value)
        {
            if (!_values.ContainsKey(key)) _keys.Add(key);
            _values[key] = value;
        }

        public bool ContainsKey(string key)
        {
            return _values.ContainsKey(key);
        }

        public object Get(string key)
        {
            object value;
            return _values.TryGetValue(key, out value) ? value : null;
        }

        public bool TryGet(string key, out object value)
        {
            return _values.TryGetValue(key, out value);
        }

        public string GetString(string key)
        {
            object value = Get(key);
            return value as string;
        }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            foreach (string key in _keys) yield return new KeyValuePair<string, object>(key, _values[key]);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    /// <summary>Minimal JSON writer (no external dependency). Values: null, bool, numbers, string, DateTime,
    /// byte[] (hex), OrderedMap, IDictionary&lt;string,object&gt;, IEnumerable, and V8 marker types.</summary>
    public static class JsonWriter
    {
        public static string Serialize(object value)
        {
            StringBuilder sb = new StringBuilder();
            WriteValue(sb, value, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        private static void Indent(StringBuilder sb, int depth)
        {
            sb.Append('\n');
            for (int i = 0; i < depth; i++) sb.Append("  ");
        }

        private static void WriteValue(StringBuilder sb, object value, int depth)
        {
            if (value == null || value is V8Undefined) { sb.Append("null"); return; }
            if (value is string) { WriteString(sb, (string)value); return; }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            if (value is int || value is long || value is uint || value is ulong || value is short || value is byte)
            {
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }
            if (value is double)
            {
                double d = (double)value;
                if (double.IsNaN(d) || double.IsInfinity(d)) { WriteString(sb, d.ToString(CultureInfo.InvariantCulture)); return; }
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is float) { sb.Append(((float)value).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (value is decimal) { sb.Append(((decimal)value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is DateTime) { WriteString(sb, ((DateTime)value).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)); return; }
            if (value is byte[]) { WriteString(sb, "hex:" + Hex.ToHex((byte[])value)); return; }
            if (value is V8Date) { WriteString(sb, ((V8Date)value).ToString()); return; }
            if (value is OrderedMap)
            {
                OrderedMap map = (OrderedMap)value;
                if (map.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> pair in map)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Indent(sb, depth + 1);
                    WriteString(sb, pair.Key);
                    sb.Append(": ");
                    WriteValue(sb, pair.Value, depth + 1);
                }
                Indent(sb, depth);
                sb.Append('}');
                return;
            }
            IDictionary<string, object> dict = value as IDictionary<string, object>;
            if (dict != null)
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> pair in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Indent(sb, depth + 1);
                    WriteString(sb, pair.Key);
                    sb.Append(": ");
                    WriteValue(sb, pair.Value, depth + 1);
                }
                Indent(sb, depth);
                sb.Append('}');
                return;
            }
            IEnumerable list = value as IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                bool any = false;
                foreach (object item in list)
                {
                    any = true;
                    if (!first) sb.Append(',');
                    first = false;
                    Indent(sb, depth + 1);
                    WriteValue(sb, item, depth + 1);
                }
                if (any) Indent(sb, depth);
                sb.Append(']');
                return;
            }
            WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        public static void WriteString(StringBuilder sb, string text)
        {
            sb.Append('"');
            foreach (char c in text)
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
                        if (c < 0x20 || c == 0x2028 || c == 0x2029)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    /// <summary>Best-effort plain text from Teams message HTML.</summary>
    public static class HtmlText
    {
        private static readonly Regex BreakTags = new Regex(@"<\s*(br|/p|/div|/li|/tr|/h[1-6])\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AnyTag = new Regex(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex NumericEntity = new Regex(@"&#(x[0-9a-fA-F]+|[0-9]+);", RegexOptions.Compiled);
        private static readonly Regex ManyBlankLines = new Regex(@"(\r?\n[ \t]*){3,}", RegexOptions.Compiled);

        public static string ToPlainText(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            string text = BreakTags.Replace(html, "\n");
            text = AnyTag.Replace(text, "");
            text = DecodeEntities(text);
            text = ManyBlankLines.Replace(text, "\n\n");
            return text.Trim();
        }

        public static string DecodeEntities(string text)
        {
            text = NumericEntity.Replace(text, delegate(Match m)
            {
                string body = m.Groups[1].Value;
                try
                {
                    int code = body.StartsWith("x", StringComparison.OrdinalIgnoreCase)
                        ? int.Parse(body.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                        : int.Parse(body, CultureInfo.InvariantCulture);
                    if (code >= 0 && code <= 0x10FFFF && !(code >= 0xD800 && code <= 0xDFFF)) return char.ConvertFromUtf32(code);
                }
                catch (Exception)
                {
                }
                return m.Value;
            });
            text = text.Replace("&nbsp;", " ").Replace("&quot;", "\"").Replace("&apos;", "'")
                       .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
            return text;
        }
    }

    public static class TimeText
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Returns an ISO-8601 UTC string when the value looks like milliseconds since 1970 (years 2000-2100); otherwise null.</summary>
        public static string MillisToIso(object value)
        {
            double millis;
            if (value is double) millis = (double)value;
            else if (value is long) millis = (long)value;
            else if (value is int) millis = (int)value;
            else if (value is ulong) millis = (ulong)value;
            else if (value is string)
            {
                string s = (string)value;
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out millis)) return null;
            }
            else return null;
            if (millis < 946684800000.0 || millis > 4102444800000.0) return null;
            DateTime dt = Epoch.AddMilliseconds(millis);
            return dt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        public static string NowIso()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        public static string NowStamp()
        {
            return DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        }
    }
}
