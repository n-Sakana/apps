// Teams Message History - minimal JSON reader for the tool's own result.json (C# 5, no dependencies).
// Objects become OrderedMap (keys are case-sensitive, so clientmessageid and clientMessageId may coexist),
// arrays become List<object>, numbers become long when integral or double otherwise.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TeamsMessageHistory
{
    public sealed class JsonReader
    {
        private readonly string _text;
        private int _pos;

        private JsonReader(string text)
        {
            _text = text;
        }

        public static object Parse(string text)
        {
            JsonReader r = new JsonReader(text);
            r.SkipWhitespace();
            object value = r.ReadValue();
            r.SkipWhitespace();
            if (r._pos != r._text.Length) throw r.Fail("trailing characters");
            return value;
        }

        private Exception Fail(string message)
        {
            return new FormatException("JSON at " + _pos.ToString(CultureInfo.InvariantCulture) + ": " + message);
        }

        private void SkipWhitespace()
        {
            while (_pos < _text.Length)
            {
                char c = _text[_pos];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') _pos++;
                else break;
            }
        }

        private object ReadValue()
        {
            if (_pos >= _text.Length) throw Fail("unexpected end");
            char c = _text[_pos];
            if (c == '{') return ReadObject();
            if (c == '[') return ReadArray();
            if (c == '"') return ReadString();
            if (c == 't') { Expect("true"); return true; }
            if (c == 'f') { Expect("false"); return false; }
            if (c == 'n') { Expect("null"); return null; }
            return ReadNumber();
        }

        private void Expect(string word)
        {
            if (string.CompareOrdinal(_text, _pos, word, 0, word.Length) != 0) throw Fail("expected " + word);
            _pos += word.Length;
        }

        private OrderedMap ReadObject()
        {
            OrderedMap map = new OrderedMap();
            _pos++;
            SkipWhitespace();
            if (_pos < _text.Length && _text[_pos] == '}') { _pos++; return map; }
            while (true)
            {
                SkipWhitespace();
                if (_pos >= _text.Length || _text[_pos] != '"') throw Fail("expected property name");
                string name = ReadString();
                SkipWhitespace();
                if (_pos >= _text.Length || _text[_pos] != ':') throw Fail("expected ':'");
                _pos++;
                SkipWhitespace();
                map.Set(name, ReadValue());
                SkipWhitespace();
                if (_pos >= _text.Length) throw Fail("unterminated object");
                char c = _text[_pos++];
                if (c == ',') continue;
                if (c == '}') return map;
                throw Fail("expected ',' or '}'");
            }
        }

        private List<object> ReadArray()
        {
            List<object> list = new List<object>();
            _pos++;
            SkipWhitespace();
            if (_pos < _text.Length && _text[_pos] == ']') { _pos++; return list; }
            while (true)
            {
                SkipWhitespace();
                list.Add(ReadValue());
                SkipWhitespace();
                if (_pos >= _text.Length) throw Fail("unterminated array");
                char c = _text[_pos++];
                if (c == ',') continue;
                if (c == ']') return list;
                throw Fail("expected ',' or ']'");
            }
        }

        private string ReadString()
        {
            StringBuilder sb = new StringBuilder();
            _pos++;
            while (true)
            {
                if (_pos >= _text.Length) throw Fail("unterminated string");
                char c = _text[_pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (_pos >= _text.Length) throw Fail("unterminated escape");
                char e = _text[_pos++];
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
                        if (_pos + 4 > _text.Length) throw Fail("bad unicode escape");
                        sb.Append((char)int.Parse(_text.Substring(_pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        _pos += 4;
                        break;
                    default: throw Fail("bad escape");
                }
            }
        }

        private object ReadNumber()
        {
            int start = _pos;
            while (_pos < _text.Length)
            {
                char c = _text[_pos];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') _pos++;
                else break;
            }
            string s = _text.Substring(start, _pos - start);
            if (s.Length == 0) throw Fail("unexpected character");
            long l;
            if (s.IndexOfAny(new char[] { '.', 'e', 'E' }) < 0 && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
            double d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            throw Fail("bad number");
        }

        // ---------------------------------------------------------------- navigation helpers

        public static OrderedMap AsMap(object value)
        {
            return value as OrderedMap;
        }

        public static List<object> AsList(object value)
        {
            return value as List<object>;
        }

        public static string AsString(object value)
        {
            return value as string;
        }

        public static long AsLong(object value)
        {
            if (value is long) return (long)value;
            if (value is double) return (long)(double)value;
            if (value is int) return (int)value;
            return 0;
        }

        public static object Path(object root, params string[] names)
        {
            object current = root;
            foreach (string name in names)
            {
                OrderedMap map = current as OrderedMap;
                if (map == null) return null;
                current = map.Get(name);
            }
            return current;
        }
    }
}
