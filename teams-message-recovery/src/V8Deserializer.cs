// Teams Message History - V8 ValueSerializer format reader (the payload of Blink's SerializedScriptValue).
// Reference: https://chromium.googlesource.com/v8/v8/+/main/src/objects/value-serializer.cc
// Blink host objects: third_party/blink/renderer/bindings/core/v8/serialization/v8_script_value_deserializer.cc
// Only decoding is implemented; unsupported tags raise an exception so the record is reported as unreadable.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TeamsMessageHistory
{
    public sealed class V8Undefined
    {
        public static readonly V8Undefined Instance = new V8Undefined();
        private V8Undefined() { }
        public override string ToString() { return "undefined"; }
    }

    public sealed class V8Date
    {
        public double Millis;
        public override string ToString()
        {
            string iso = TimeText.MillisToIso(Millis);
            return iso ?? ("date(" + Millis.ToString("R", CultureInfo.InvariantCulture) + ")");
        }
    }

    public sealed class V8RegExp
    {
        public string Pattern;
        public ulong Flags;
        public override string ToString() { return "/" + Pattern + "/"; }
    }

    public sealed class V8ArrayBuffer
    {
        public byte[] Data;
        public override string ToString() { return "ArrayBuffer(" + Data.Length.ToString(CultureInfo.InvariantCulture) + " bytes)"; }
    }

    public sealed class V8ArrayBufferView
    {
        public char Tag;
        public byte[] Data;
        public override string ToString() { return "ArrayBufferView(" + Tag + ", " + Data.Length.ToString(CultureInfo.InvariantCulture) + " bytes)"; }
    }

    public sealed class V8HostObject
    {
        public string Kind;
        public object Payload;
        public override string ToString() { return "HostObject(" + Kind + ")"; }
    }

    public sealed class V8SharedObject
    {
        public ulong Id;
        public override string ToString() { return "SharedObject(" + Id.ToString(CultureInfo.InvariantCulture) + ")"; }
    }

    /// <summary>JavaScript object with ordered properties.</summary>
    public sealed class JsObject
    {
        public readonly OrderedMap Properties = new OrderedMap();
    }

    public sealed class V8Deserializer
    {
        private readonly byte[] _buf;
        private int _pos;
        private readonly int _end;
        private readonly List<object> _objects = new List<object>();
        private int _depth;
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        public ulong Version;

        private V8Deserializer(byte[] buffer, int offset, int end)
        {
            _buf = buffer;
            _pos = offset;
            _end = end;
        }

        public static object Deserialize(byte[] buffer, int offset, out ulong version)
        {
            V8Deserializer d = new V8Deserializer(buffer, offset, buffer.Length);
            d.ReadHeader();
            version = d.Version;
            return d.ReadObject();
        }

        private void ReadHeader()
        {
            if (_pos < _end && _buf[_pos] == 0xFF)
            {
                _pos++;
                Version = ReadVarint();
            }
            else Version = 0;
        }

        private Exception Fail(string message)
        {
            return new FormatException("V8 data at offset " + _pos.ToString(CultureInfo.InvariantCulture) + ": " + message);
        }

        private byte ReadTag()
        {
            while (true)
            {
                if (_pos >= _end) throw Fail("unexpected end of data");
                byte tag = _buf[_pos++];
                if (tag != 0x00) return tag;
            }
        }

        private int PeekTag()
        {
            int p = _pos;
            while (p < _end)
            {
                byte tag = _buf[p++];
                if (tag != 0x00) return tag;
            }
            return -1;
        }

        private ulong ReadVarint()
        {
            ulong value;
            if (!Varint.TryRead(_buf, ref _pos, _end, out value)) throw Fail("varint truncated");
            return value;
        }

        private int ReadLength()
        {
            ulong value = ReadVarint();
            if (value > (ulong)(_end - _pos)) throw Fail("length " + value.ToString(CultureInfo.InvariantCulture) + " exceeds remaining data");
            return (int)value;
        }

        private long ReadZigZag()
        {
            ulong raw = ReadVarint();
            uint u = (uint)raw;
            return (long)(int)((u >> 1) ^ (uint)(-(int)(u & 1)));
        }

        private double ReadDouble()
        {
            if (_pos + 8 > _end) throw Fail("double truncated");
            double d = BitConverter.ToDouble(_buf, _pos);
            _pos += 8;
            return d;
        }

        private byte[] ReadBytes(int count)
        {
            if (_pos + count > _end) throw Fail("raw data truncated");
            byte[] data = new byte[count];
            Buffer.BlockCopy(_buf, _pos, data, 0, count);
            _pos += count;
            return data;
        }

        private string ReadUtf8String()
        {
            int length = ReadLength();
            string s = Encoding.UTF8.GetString(_buf, _pos, length);
            _pos += length;
            return s;
        }

        private string ReadOneByteString()
        {
            int length = ReadLength();
            string s = Latin1.GetString(_buf, _pos, length);
            _pos += length;
            return s;
        }

        private string ReadTwoByteString()
        {
            int length = ReadLength();
            string s = Encoding.Unicode.GetString(_buf, _pos, length - (length % 2));
            _pos += length;
            return s;
        }

        private string ReadString()
        {
            if (Version < 12) return ReadUtf8String();
            object value = ReadObject();
            string s = value as string;
            if (s == null) throw Fail("expected a string");
            return s;
        }

        private string ReadBigInt()
        {
            ulong bitfield = ReadVarint();
            bool negative = (bitfield & 1) != 0;
            int byteLength = (int)(bitfield >> 1);
            byte[] digits = ReadBytes(byteLength);
            Array.Reverse(digits);
            return (negative ? "-" : "") + "0x" + Hex.ToHex(digits);
        }

        private static string KeyToString(object key)
        {
            if (key is string) return (string)key;
            if (key is double)
            {
                double d = (double)key;
                if (d == Math.Floor(d) && Math.Abs(d) < 9007199254740992.0) return ((long)d).ToString(CultureInfo.InvariantCulture);
                return d.ToString("R", CultureInfo.InvariantCulture);
            }
            if (key is long || key is ulong || key is int) return Convert.ToString(key, CultureInfo.InvariantCulture);
            if (key == null) return "null";
            return Convert.ToString(key, CultureInfo.InvariantCulture);
        }

        private int ReadProperties(OrderedMap target, byte endTag)
        {
            int count = 0;
            while (true)
            {
                int next = PeekTag();
                if (next < 0) throw Fail("object not terminated");
                if (next == endTag)
                {
                    ReadTag();
                    break;
                }
                object key = ReadObject();
                object value = ReadObject();
                target.Set(KeyToString(key), value);
                count++;
            }
            return count;
        }

        public object ReadObject()
        {
            if (++_depth > 512) throw Fail("nesting too deep");
            try
            {
                byte tag = ReadTag();
                object result = ReadObjectInternal(tag);
                if (result is V8ArrayBuffer && PeekTag() == 'V')
                {
                    ReadTag();
                    result = ReadArrayBufferView((V8ArrayBuffer)result);
                }
                return result;
            }
            finally
            {
                _depth--;
            }
        }

        private object ReadObjectInternal(byte tag)
        {
            switch (tag)
            {
                case (byte)'?':
                    ReadVarint();
                    return ReadObject();
                case (byte)'-':
                case (byte)'_':
                    return V8Undefined.Instance;
                case (byte)'0':
                    return null;
                case (byte)'T':
                    return true;
                case (byte)'F':
                    return false;
                case (byte)'I':
                    return ReadZigZag();
                case (byte)'U':
                    return (long)(uint)ReadVarint();
                case (byte)'N':
                    return ReadDouble();
                case (byte)'Z':
                    return ReadBigInt();
                case (byte)'S':
                    return ReadUtf8String();
                case (byte)'"':
                    return ReadOneByteString();
                case (byte)'c':
                    return ReadTwoByteString();
                case (byte)'^':
                    {
                        ulong id = ReadVarint();
                        if (id >= (ulong)_objects.Count) throw Fail("object reference " + id.ToString(CultureInfo.InvariantCulture) + " out of range");
                        return _objects[(int)id];
                    }
                case (byte)'o':
                    {
                        JsObject obj = new JsObject();
                        _objects.Add(obj);
                        int count = ReadProperties(obj.Properties, (byte)'{');
                        ulong expected = ReadVarint();
                        if ((ulong)count != expected) throw Fail("object property count mismatch");
                        return obj;
                    }
                case (byte)'a':
                    {
                        ulong length = ReadVarint();
                        if (length > 50000000) throw Fail("sparse array too long");
                        List<object> list = new List<object>();
                        for (ulong i = 0; i < length; i++) list.Add(V8Undefined.Instance);
                        _objects.Add(list);
                        OrderedMap props = new OrderedMap();
                        int count = ReadProperties(props, (byte)'@');
                        ApplyIndexedProperties(list, props);
                        ulong expectedCount = ReadVarint();
                        ReadVarint();
                        if ((ulong)count != expectedCount) throw Fail("sparse array property count mismatch");
                        return list;
                    }
                case (byte)'A':
                    {
                        ulong length = ReadVarint();
                        if (length > (ulong)(_end - _pos)) throw Fail("dense array longer than data");
                        List<object> list = new List<object>((int)length);
                        _objects.Add(list);
                        for (ulong i = 0; i < length; i++) list.Add(ReadObject());
                        OrderedMap props = new OrderedMap();
                        int count = ReadProperties(props, (byte)'$');
                        ApplyIndexedProperties(list, props);
                        ulong expectedCount = ReadVarint();
                        ReadVarint();
                        if ((ulong)count != expectedCount) throw Fail("dense array property count mismatch");
                        return list;
                    }
                case (byte)'D':
                    {
                        V8Date date = new V8Date();
                        date.Millis = ReadDouble();
                        _objects.Add(date);
                        return date;
                    }
                case (byte)'y':
                    _objects.Add(true);
                    return true;
                case (byte)'x':
                    _objects.Add(false);
                    return false;
                case (byte)'n':
                    {
                        double d = ReadDouble();
                        _objects.Add(d);
                        return d;
                    }
                case (byte)'z':
                    {
                        string big = ReadBigInt();
                        _objects.Add(big);
                        return big;
                    }
                case (byte)'s':
                    {
                        string s = ReadString();
                        _objects.Add(s);
                        return s;
                    }
                case (byte)'R':
                    {
                        V8RegExp re = new V8RegExp();
                        re.Pattern = ReadString();
                        re.Flags = ReadVarint();
                        _objects.Add(re);
                        return re;
                    }
                case (byte)';':
                    {
                        List<object> entries = new List<object>();
                        _objects.Add(entries);
                        int count = 0;
                        while (true)
                        {
                            int next = PeekTag();
                            if (next < 0) throw Fail("map not terminated");
                            if (next == ':') { ReadTag(); break; }
                            OrderedMap entry = new OrderedMap();
                            entry.Set("key", ReadObject());
                            entry.Set("value", ReadObject());
                            entries.Add(entry);
                            count += 2;
                        }
                        ulong expected = ReadVarint();
                        if ((ulong)count != expected) throw Fail("map length mismatch");
                        return entries;
                    }
                case (byte)'\'':
                    {
                        List<object> items = new List<object>();
                        _objects.Add(items);
                        while (true)
                        {
                            int next = PeekTag();
                            if (next < 0) throw Fail("set not terminated");
                            if (next == ',') { ReadTag(); break; }
                            items.Add(ReadObject());
                        }
                        ulong expected = ReadVarint();
                        if ((ulong)items.Count != expected) throw Fail("set length mismatch");
                        return items;
                    }
                case (byte)'B':
                    {
                        V8ArrayBuffer ab = new V8ArrayBuffer();
                        ab.Data = ReadBytes(ReadLength());
                        _objects.Add(ab);
                        return ab;
                    }
                case (byte)'~':
                    {
                        int length = ReadLength();
                        ReadVarint();
                        V8ArrayBuffer ab = new V8ArrayBuffer();
                        ab.Data = ReadBytes(length);
                        _objects.Add(ab);
                        return ab;
                    }
                case (byte)'p':
                    {
                        V8SharedObject shared = new V8SharedObject();
                        shared.Id = ReadVarint();
                        return shared;
                    }
                case (byte)'r':
                    return ReadError();
                case (byte)'\\':
                    return ReadHostObject();
                case (byte)'V':
                    throw Fail("array buffer view without buffer");
                case (byte)'t':
                case (byte)'u':
                case (byte)'w':
                case (byte)'m':
                    throw Fail("transferred object (tag '" + (char)tag + "') cannot be read from storage");
                default:
                    throw Fail("unknown tag 0x" + tag.ToString("x2", CultureInfo.InvariantCulture));
            }
        }

        private static void ApplyIndexedProperties(List<object> list, OrderedMap props)
        {
            foreach (KeyValuePair<string, object> pair in props)
            {
                long index;
                if (long.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) && index >= 0 && index < list.Count)
                {
                    list[(int)index] = pair.Value;
                }
            }
        }

        private V8ArrayBufferView ReadArrayBufferView(V8ArrayBuffer buffer)
        {
            ulong tag = ReadVarint();
            ulong byteOffset = ReadVarint();
            ulong byteLength = ReadVarint();
            if (Version >= 14) ReadVarint();
            if (byteOffset + byteLength > (ulong)buffer.Data.Length) throw Fail("array buffer view outside buffer");
            V8ArrayBufferView view = new V8ArrayBufferView();
            view.Tag = (char)tag;
            view.Data = new byte[(int)byteLength];
            Buffer.BlockCopy(buffer.Data, (int)byteOffset, view.Data, 0, (int)byteLength);
            _objects.Add(view);
            return view;
        }

        private object ReadError()
        {
            JsObject error = new JsObject();
            _objects.Add(error);
            while (true)
            {
                byte tag = ReadTag();
                switch (tag)
                {
                    case (byte)'E': error.Properties.Set("prototype", "EvalError"); break;
                    case (byte)'R': error.Properties.Set("prototype", "RangeError"); break;
                    case (byte)'F': error.Properties.Set("prototype", "ReferenceError"); break;
                    case (byte)'S': error.Properties.Set("prototype", "SyntaxError"); break;
                    case (byte)'T': error.Properties.Set("prototype", "TypeError"); break;
                    case (byte)'U': error.Properties.Set("prototype", "URIError"); break;
                    case (byte)'m': error.Properties.Set("message", ReadString()); break;
                    case (byte)'c': error.Properties.Set("cause", ReadObject()); break;
                    case (byte)'s': error.Properties.Set("stack", ReadString()); break;
                    case (byte)'.': return error;
                    default: throw Fail("unknown error sub-tag");
                }
            }
        }

        private string ReadBlinkString()
        {
            ulong length;
            if (!Varint.TryRead(_buf, ref _pos, _end, out length)) throw Fail("host string length truncated");
            if (length > (ulong)(_end - _pos)) throw Fail("host string truncated");
            string s = Encoding.UTF8.GetString(_buf, _pos, (int)length);
            _pos += (int)length;
            return s;
        }

        private object ReadHostObject()
        {
            if (_pos >= _end) throw Fail("host object tag missing");
            byte tag = _buf[_pos++];
            V8HostObject host = new V8HostObject();
            switch (tag)
            {
                case (byte)'i':
                    host.Kind = "BlobIndex";
                    host.Payload = (long)ReadVarint();
                    break;
                case (byte)'e':
                    host.Kind = "FileIndex";
                    host.Payload = (long)ReadVarint();
                    break;
                case (byte)'L':
                    {
                        host.Kind = "FileListIndex";
                        ulong count = ReadVarint();
                        List<object> indexes = new List<object>();
                        for (ulong i = 0; i < count; i++) indexes.Add((long)ReadVarint());
                        host.Payload = indexes;
                        break;
                    }
                case (byte)'b':
                    {
                        host.Kind = "Blob";
                        OrderedMap blob = new OrderedMap();
                        blob.Set("uuid", ReadBlinkString());
                        blob.Set("type", ReadBlinkString());
                        blob.Set("size", (long)ReadVarint());
                        host.Payload = blob;
                        break;
                    }
                case (byte)'Q':
                case (byte)'W':
                case (byte)'E':
                case (byte)'R':
                    host.Kind = (tag == 'Q' || tag == 'W') ? "DOMPoint" : "DOMRect";
                    host.Payload = ReadDoubles(4);
                    break;
                case (byte)'T':
                    host.Kind = "DOMQuad";
                    host.Payload = ReadDoubles(16);
                    break;
                case (byte)'Y':
                case (byte)'U':
                    host.Kind = "DOMMatrix";
                    host.Payload = ReadDoubles(16);
                    break;
                case (byte)'I':
                case (byte)'O':
                    host.Kind = "DOMMatrix2D";
                    host.Payload = ReadDoubles(6);
                    break;
                case (byte)'x':
                    {
                        host.Kind = "DOMException";
                        OrderedMap ex = new OrderedMap();
                        ex.Set("name", ReadBlinkString());
                        ex.Set("message", ReadBlinkString());
                        ex.Set("stack", ReadBlinkString());
                        host.Payload = ex;
                        break;
                    }
                default:
                    throw Fail("host object type '" + (char)tag + "' is not supported");
            }
            _objects.Add(host);
            return host;
        }

        private List<object> ReadDoubles(int count)
        {
            List<object> values = new List<object>();
            for (int i = 0; i < count; i++) values.Add(ReadDouble());
            return values;
        }
    }
}
