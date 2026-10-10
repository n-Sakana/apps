// Teams Message History - Chromium disk cache formats, read-only:
//   * blockfile: "index" (table of entry addresses) + "data_N" block files + "f_xxxxxx" external files
//   * simple:    one "<hash>_0" file per entry (header, key, stream 1 = body, stream 0 = response info)
// plus the HttpResponseInfo header pickle (status line and headers, Content-Encoding among them) and the
// body decoding per Content-Encoding: gzip / deflate with System.IO.Compression, br with the Google Brotli
// decoder kept verbatim in src\brotli (MIT). zstd and unknown encodings are counted as unsupported.
// Layouts: net/disk_cache/blockfile/disk_format.h, net/disk_cache/simple/simple_entry_format.h,
// net/http/http_response_info.cc; cross-checked with ccl_chromium_reader/ccl_chromium_cache.py (MIT).
// Blockfile entries that are no longer reachable from the index (evicted / deleted, blocks not yet reused)
// are also read ("索引外") when their stored key hash matches the key, and are always marked as such.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace TeamsMessageHistory
{
    /// <summary>One cache entry as read from disk. Key, locations and headers are private details.</summary>
    public sealed class CacheEntry
    {
        public string Format;          // blockfile / simple
        public string Directory;       // relative to the source folder (private)
        public string Location;        // private: where the entry record is
        public string Key;             // private: raw cache key (URL with cache-key prefixes)
        public bool FromIndex;         // blockfile: reached from the index table; simple: always true
        public bool Allocated;         // blockfile: the record block is marked allocated in the file bitmap
        public int State;              // blockfile: 0 normal, 1 evicted, 2 doomed
        public bool Child;             // sparse child entry (partial body)
        public bool KeyHashChecked;
        public bool KeyHashMatches;
        public string HeaderKind;      // http / cachestorage / none / unknown
        public string StatusLine;      // private
        public string ContentType;     // private
        public string ContentEncoding; // private: raw header value
        public string EncodingClass;   // identity / gzip / br / deflate / zstd / other / unknown
        public string ResponseTime;    // ISO-8601 from the header pickle, if any
        public string BodyLocation;    // private
        public long BodyBytes;
        public long DecodedBytes;
        public string BodyStatus;      // ok / unreadable / empty / media / toolarge
        public string DecodeStatus;    // none / ok / failed / unsupported / truncated / mismatch / skipped
        public string Error;           // private
        public readonly Dictionary<string, long> KeyHits = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly Dictionary<string, long> BodyHits = new Dictionary<string, long>(StringComparer.Ordinal);
        public object Record;          // transient: the blockfile EntryStore
        public byte[] Stream0;         // transient
        public byte[] Body;            // transient
    }

    /// <summary>Counts of the structural cache pass for one source (screen) and the entry list (private).</summary>
    public sealed class CacheStats
    {
        public int Directories;
        public int BlockfileDirs;
        public int SimpleDirs;
        public long Entries;           // entries parsed (blockfile index + orphan, simple files)
        public long FromIndex;
        public long Orphan;            // blockfile records not reachable from the index
        public long Unreadable;        // entry records / files that could not be parsed
        public long KeyHashMismatch;   // stored key hash differs from the hash of the key read (format or damage)
        public long OtherFiles;        // simple "_1" / "_s" files (side data, sparse): not parsed
        public long BodiesRead, BodiesUnreadable, BodiesEmpty, BodiesMedia, BodiesTooLarge;
        public long DecodeOk, DecodeFailed, DecodeUnsupported, DecodeTruncated, DecodeAsIs, DecodeSkippedCap;
        public long DecodedBytes;
        public readonly Dictionary<string, long> Encodings = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly Dictionary<string, long> HeaderKinds = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly Dictionary<string, long> KeyHitsByNeedle = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly Dictionary<string, long> BodyHitsByNeedle = new Dictionary<string, long>(StringComparer.Ordinal);
        public long EntriesWithKeyHit;
        public readonly List<CacheEntry> EntryList = new List<CacheEntry>();
        public readonly List<string> Warnings = new List<string>();   // private

        public string FormatLabel
        {
            get
            {
                if (BlockfileDirs > 0 && SimpleDirs > 0) return "混在";
                if (BlockfileDirs > 0) return "blockfile";
                if (SimpleDirs > 0) return "simple";
                return "なし";
            }
        }
    }

    /// <summary>Paul Hsieh's SuperFastHash as used by Chromium's base::PersistentHash (the blockfile entry hash
    /// and the simple-cache header key hash are PersistentHash(key)).</summary>
    public static class SuperFastHash
    {
        public static uint Compute(byte[] data, int offset, int length)
        {
            unchecked
            {
                if (data == null || length <= 0) return 0;
                uint hash = (uint)length;
                uint tmp;
                int rem = length & 3;
                int len = length >> 2;
                int p = offset;
                for (; len > 0; len--)
                {
                    hash += Get16(data, p);
                    tmp = (Get16(data, p + 2) << 11) ^ hash;
                    hash = (hash << 16) ^ tmp;
                    p += 4;
                    hash += hash >> 11;
                }
                switch (rem)
                {
                    case 3:
                        hash += Get16(data, p);
                        hash ^= hash << 16;
                        hash ^= (uint)(((int)(sbyte)data[p + 2]) << 18);
                        hash += hash >> 11;
                        break;
                    case 2:
                        hash += Get16(data, p);
                        hash ^= hash << 11;
                        hash += hash >> 17;
                        break;
                    case 1:
                        hash += (uint)(int)(sbyte)data[p];
                        hash ^= hash << 10;
                        hash += hash >> 1;
                        break;
                }
                hash ^= hash << 3;
                hash += hash >> 5;
                hash ^= hash << 4;
                hash += hash >> 17;
                hash ^= hash << 25;
                hash += hash >> 6;
                return hash;
            }
        }

        public static uint Compute(byte[] data)
        {
            return Compute(data, 0, data == null ? 0 : data.Length);
        }

        private static uint Get16(byte[] d, int p)
        {
            return (uint)(d[p] | (d[p + 1] << 8));
        }
    }

    /// <summary>Little-endian field access and Chrome time conversion.</summary>
    public static class CacheBytes
    {
        public static uint U32(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        public static int I32(byte[] b, int o)
        {
            return (int)U32(b, o);
        }

        public static ulong U64(byte[] b, int o)
        {
            return (ulong)U32(b, o) | ((ulong)U32(b, o + 4) << 32);
        }

        public static short I16(byte[] b, int o)
        {
            return (short)(b[o] | (b[o + 1] << 8));
        }

        /// <summary>Microseconds since 1601-01-01 UTC to ISO-8601; null when zero or out of range.</summary>
        public static string ChromeTimeToIso(ulong microseconds)
        {
            if (microseconds == 0) return null;
            DateTime baseTime = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            if (microseconds > 20000000000000000UL) return null; // beyond year 2234: not a time
            try
            {
                DateTime t = baseTime.AddTicks((long)(microseconds * 10));
                return t.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            }
            catch (Exception) { return null; }
        }

        public static bool IsMedia(string contentType)
        {
            if (string.IsNullOrEmpty(contentType)) return false;
            string t = contentType.Trim().ToLowerInvariant();
            return t.StartsWith("image/") || t.StartsWith("font/") || t.StartsWith("audio/") || t.StartsWith("video/");
        }
    }

    /// <summary>The blockfile EntryStore record (256 bytes, key may continue into up to 3 following blocks).</summary>
    public sealed class EntryStore
    {
        public const int Size = 256;
        public const int KeyOffset = 96;

        public uint Hash;
        public uint Next;
        public uint RankingsNode;
        public int ReuseCount;
        public int RefetchCount;
        public int State;
        public ulong CreationTime;
        public int KeyLength;
        public uint LongKey;
        public readonly int[] DataSize = new int[4];
        public readonly uint[] DataAddr = new uint[4];
        public uint Flags;
        public uint SelfHash;
        public byte[] Raw;   // the record block(s) as read

        public static EntryStore Parse(byte[] b)
        {
            if (b == null || b.Length < Size) return null;
            EntryStore e = new EntryStore();
            e.Hash = CacheBytes.U32(b, 0);
            e.Next = CacheBytes.U32(b, 4);
            e.RankingsNode = CacheBytes.U32(b, 8);
            e.ReuseCount = CacheBytes.I32(b, 12);
            e.RefetchCount = CacheBytes.I32(b, 16);
            e.State = CacheBytes.I32(b, 20);
            e.CreationTime = CacheBytes.U64(b, 24);
            e.KeyLength = CacheBytes.I32(b, 32);
            e.LongKey = CacheBytes.U32(b, 36);
            for (int i = 0; i < 4; i++) e.DataSize[i] = CacheBytes.I32(b, 40 + i * 4);
            for (int i = 0; i < 4; i++) e.DataAddr[i] = CacheBytes.U32(b, 56 + i * 4);
            e.Flags = CacheBytes.U32(b, 72);
            e.SelfHash = CacheBytes.U32(b, 92);
            e.Raw = b;
            return e;
        }
    }

    /// <summary>Reader for one blockfile cache folder (index + data_N + f_xxxxxx). Read-only, shared access.</summary>
    public sealed class BlockfileReader : IDisposable
    {
        public const uint IndexMagic = 0xC103CAC3;
        public const uint BlockMagic = 0xC104CAC3;
        public const int BlockHeaderSize = 8192;
        public const int IndexHeaderSize = 368;
        public const int MaxContiguousBlocks = 4;

        private sealed class DataFile
        {
            public int Number;
            public string Path;
            public FileStream Stream;
            public long Length;
            public int EntrySize;
            public byte[] Bitmap;
            public bool Bad;
            public string Error;
        }

        private readonly string _folder;
        private readonly Dictionary<int, DataFile> _files = new Dictionary<int, DataFile>();
        public readonly List<string> Warnings = new List<string>();
        public int TableLength;
        public int NumEntriesDeclared;
        public uint IndexVersion;

        public BlockfileReader(string folder)
        {
            _folder = folder;
        }

        public static bool HasIndex(string folder)
        {
            string index = Path.Combine(folder, "index");
            try
            {
                if (!File.Exists(index)) return false;
                using (FileStream fs = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    byte[] b = new byte[4];
                    int n = fs.Read(b, 0, 4);
                    return n == 4 && CacheBytes.U32(b, 0) == IndexMagic;
                }
            }
            catch (Exception) { return false; }
        }

        // ---------------------------------------------------------------- addresses (addr.h)

        public static bool Initialized(uint a) { return (a & 0x80000000u) != 0; }
        public static int FileType(uint a) { return (int)((a & 0x70000000u) >> 28); }
        public static int ContiguousBlocks(uint a) { return (int)((a & 0x03000000u) >> 24) + 1; }
        public static int FileSelector(uint a) { return (int)((a & 0x00ff0000u) >> 16); }
        public static int StartBlock(uint a) { return (int)(a & 0x0000ffffu); }
        public static int ExternalNumber(uint a) { return (int)(a & 0x0fffffffu); }
        public static bool ReservedBitsClear(uint a) { return (a & 0x0c000000u) == 0; }

        public static string Describe(uint a)
        {
            if (!Initialized(a)) return "(未設定)";
            if (FileType(a) == 0) return "f_" + ExternalNumber(a).ToString("x6", CultureInfo.InvariantCulture);
            return "data_" + FileSelector(a).ToString(CultureInfo.InvariantCulture) + " ブロック " + StartBlock(a).ToString(CultureInfo.InvariantCulture)
                + " (" + ContiguousBlocks(a).ToString(CultureInfo.InvariantCulture) + " ブロック)";
        }

        // ---------------------------------------------------------------- index

        public uint[] ReadIndexTable()
        {
            string index = Path.Combine(_folder, "index");
            byte[] header = new byte[IndexHeaderSize];
            using (FileStream fs = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (ReadFully(fs, header, 0, header.Length) != header.Length) throw new InvalidDataException("index header too short");
                if (CacheBytes.U32(header, 0) != IndexMagic) throw new InvalidDataException("index magic mismatch");
                IndexVersion = CacheBytes.U32(header, 4);
                NumEntriesDeclared = CacheBytes.I32(header, 8);
                int tableLength = CacheBytes.I32(header, 28);
                if (tableLength <= 0) tableLength = 0x10000;
                if (tableLength > 0x1000000) throw new InvalidDataException("index table length implausible");
                TableLength = tableLength;
                byte[] table = new byte[(long)tableLength * 4 > fs.Length - IndexHeaderSize ? (int)(fs.Length - IndexHeaderSize) : tableLength * 4];
                int got = ReadFully(fs, table, 0, table.Length);
                uint[] addrs = new uint[got / 4];
                for (int i = 0; i < addrs.Length; i++) addrs[i] = CacheBytes.U32(table, i * 4);
                if (addrs.Length < tableLength) Warnings.Add("index table shorter than declared: " + addrs.Length.ToString(CultureInfo.InvariantCulture) + " of " + tableLength.ToString(CultureInfo.InvariantCulture));
                return addrs;
            }
        }

        // ---------------------------------------------------------------- data files

        private DataFile GetFile(int number)
        {
            DataFile f;
            if (_files.TryGetValue(number, out f)) return f;
            f = new DataFile();
            f.Number = number;
            f.Path = Path.Combine(_folder, "data_" + number.ToString(CultureInfo.InvariantCulture));
            _files[number] = f;
            try
            {
                f.Stream = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                f.Length = f.Stream.Length;
                byte[] header = new byte[80];
                if (ReadFully(f.Stream, header, 0, header.Length) != header.Length) { f.Bad = true; f.Error = "ヘッダーが短い"; return f; }
                if (CacheBytes.U32(header, 0) != BlockMagic) { f.Bad = true; f.Error = "ブロックファイルの magic 不一致"; return f; }
                f.EntrySize = CacheBytes.I32(header, 12);
                if (f.EntrySize <= 0 || f.EntrySize > 65536) { f.Bad = true; f.Error = "ブロック長が不正"; return f; }
                f.Bitmap = new byte[BlockHeaderSize - 80];
                ReadFully(f.Stream, f.Bitmap, 0, f.Bitmap.Length);
            }
            catch (Exception ex)
            {
                f.Bad = true;
                f.Error = ex.GetType().Name;
            }
            return f;
        }

        public bool DataFileExists(int number)
        {
            return File.Exists(Path.Combine(_folder, "data_" + number.ToString(CultureInfo.InvariantCulture)));
        }

        public int EntrySizeOf(int number)
        {
            DataFile f = GetFile(number);
            return f.Bad ? 0 : f.EntrySize;
        }

        public long BlockCountOf(int number)
        {
            DataFile f = GetFile(number);
            if (f.Bad || f.EntrySize <= 0) return 0;
            return (f.Length - BlockHeaderSize) / f.EntrySize;
        }

        public bool IsAllocated(int number, int block)
        {
            DataFile f = GetFile(number);
            if (f.Bad || f.Bitmap == null) return false;
            int byteIndex = block >> 3;
            if (byteIndex < 0 || byteIndex >= f.Bitmap.Length) return false;
            return (f.Bitmap[byteIndex] & (1 << (block & 7))) != 0;
        }

        /// <summary>Reads the bytes an address points at: the contiguous blocks, or the whole external file.</summary>
        public bool ReadAddress(uint addr, long maxBytes, out byte[] data, out string location, out string error)
        {
            data = null;
            location = Describe(addr);
            error = null;
            if (!Initialized(addr)) { error = "アドレス未設定"; return false; }
            int type = FileType(addr);
            if (type == 0)
            {
                string path = Path.Combine(_folder, location);
                try
                {
                    FileInfo fi = new FileInfo(path);
                    if (!fi.Exists) { error = "外部ファイルなし"; return false; }
                    if (fi.Length > maxBytes) { error = "上限超過"; return false; }
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        data = new byte[fs.Length];
                        ReadFully(fs, data, 0, data.Length);
                    }
                    return true;
                }
                catch (Exception ex) { error = "読取失敗: " + ex.GetType().Name; return false; }
            }
            if (type < 2 || type > 4 || !ReservedBitsClear(addr)) { error = "アドレス種別が不正"; return false; }
            DataFile f = GetFile(FileSelector(addr));
            if (f.Bad) { error = "ブロックファイル読取不可: " + f.Error; return false; }
            int expected = type == 2 ? 256 : (type == 3 ? 1024 : 4096);
            if (f.EntrySize != expected) Warn("data_" + f.Number.ToString(CultureInfo.InvariantCulture) + " のブロック長 " + f.EntrySize.ToString(CultureInfo.InvariantCulture) + " がアドレス種別と異なる");
            return ReadBlocks(f, StartBlock(addr), ContiguousBlocks(addr), out data, out error);
        }

        public bool ReadBlocksOf(int number, int start, int count, out byte[] data, out string error)
        {
            DataFile f = GetFile(number);
            if (f.Bad) { data = null; error = "ブロックファイル読取不可: " + f.Error; return false; }
            return ReadBlocks(f, start, count, out data, out error);
        }

        private static bool ReadBlocks(DataFile f, int start, int count, out byte[] data, out string error)
        {
            data = null;
            error = null;
            long offset = BlockHeaderSize + (long)start * f.EntrySize;
            long length = (long)count * f.EntrySize;
            if (start < 0 || count <= 0 || offset + length > f.Length) { error = "ブロックが範囲外"; return false; }
            try
            {
                f.Stream.Seek(offset, SeekOrigin.Begin);
                data = new byte[length];
                if (ReadFully(f.Stream, data, 0, data.Length) != data.Length) { error = "ブロック読取が短い"; data = null; return false; }
                return true;
            }
            catch (Exception ex) { error = "読取失敗: " + ex.GetType().Name; data = null; return false; }
        }

        private void Warn(string text)
        {
            if (Warnings.Count < 50 && !Warnings.Contains(text)) Warnings.Add(text);
        }

        private static int ReadFully(Stream s, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buffer, offset + total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        public List<int> DataFileNumbers()
        {
            List<int> numbers = new List<int>();
            string[] files;
            try { files = Directory.GetFiles(_folder, "data_*"); } catch (Exception) { return numbers; }
            foreach (string f in files)
            {
                string name = Path.GetFileName(f);
                int n;
                if (name.Length > 5 && int.TryParse(name.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0 && n < 256) numbers.Add(n);
            }
            numbers.Sort();
            return numbers;
        }

        public void Dispose()
        {
            foreach (DataFile f in _files.Values)
            {
                if (f.Stream != null) { try { f.Stream.Dispose(); } catch (Exception) { } }
            }
            _files.Clear();
        }
    }

    /// <summary>The simple cache entry file "<hash>_0": header + key + stream 1 (body) + EOF + stream 0 (headers) [+ sha256] + EOF.</summary>
    public static class SimpleCacheFormat
    {
        public const ulong InitialMagic = 0xfcfb6d1ba7725c30UL;
        public const ulong FinalMagic = 0xf4fa6f45970d41d8UL;
        public static readonly Regex EntryFileName = new Regex("^[0-9a-f]{16}_0$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex OtherFileName = new Regex("^[0-9a-f]{16}_(1|s)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Returns null (with error) when the file is not a readable simple-cache entry.</summary>
        public static CacheEntry Parse(byte[] data, string fileName, out string error)
        {
            error = null;
            CacheEntry e = TryLayout(data, fileName, 24, 24, out error);
            if (e != null) return e;
            string firstError = error;
            e = TryLayout(data, fileName, 20, 20, out error);
            if (e != null) return e;
            error = firstError;
            return null;
        }

        private static CacheEntry TryLayout(byte[] data, string fileName, int headerSize, int eofSize, out string error)
        {
            error = null;
            if (data == null || data.Length < headerSize) { error = "ヘッダーが短い"; return null; }
            if (CacheBytes.U64(data, 0) != InitialMagic) { error = "magic 不一致"; return null; }
            uint version = CacheBytes.U32(data, 8);
            uint keyLength = CacheBytes.U32(data, 12);
            uint keyHash = CacheBytes.U32(data, 16);
            if (keyLength == 0 || keyLength > 65536 || headerSize + keyLength > data.Length) { error = "キー長が不正"; return null; }
            CacheEntry e = new CacheEntry();
            e.Format = "simple";
            e.Location = fileName;
            e.FromIndex = true;
            e.Allocated = true;
            e.Key = Encoding.UTF8.GetString(data, headerSize, (int)keyLength);
            e.KeyHashChecked = true;
            e.KeyHashMatches = SuperFastHash.Compute(data, headerSize, (int)keyLength) == keyHash;
            int bodyStart = headerSize + (int)keyLength;
            if (data.Length < bodyStart + eofSize + eofSize)
            {
                error = "キーのみ（ストリームなし）";
                return null;
            }
            int eof0 = data.Length - eofSize;
            if (CacheBytes.U64(data, eof0) != FinalMagic) { error = "終端 magic 不一致"; return null; }
            uint flags0 = CacheBytes.U32(data, eof0 + 8);
            uint size0 = CacheBytes.U32(data, eof0 + 16);
            long stream0Start = (long)eof0 - size0 - (((flags0 & 2) != 0) ? 32 : 0);
            long eof1 = stream0Start - eofSize;
            if (eof1 < bodyStart) { error = "ストリーム 0 の長さが不正"; return null; }
            if (CacheBytes.U64(data, (int)eof1) != FinalMagic) { error = "ストリーム 1 の終端 magic 不一致"; return null; }
            int bodyLength = (int)(eof1 - bodyStart);
            e.Body = new byte[bodyLength];
            Array.Copy(data, bodyStart, e.Body, 0, bodyLength);
            e.Stream0 = new byte[size0];
            Array.Copy(data, (int)stream0Start, e.Stream0, 0, (int)size0);
            e.BodyLocation = fileName + " 位置 " + bodyStart.ToString(CultureInfo.InvariantCulture);
            e.BodyBytes = bodyLength;
            e.Error = version == 5 ? null : "版 " + version.ToString(CultureInfo.InvariantCulture);
            return e;
        }
    }

    /// <summary>The headers part of an HttpResponseInfo pickle (stream 0 of HTTP cache entries).</summary>
    public sealed class ResponseHeaders
    {
        public string StatusLine;
        public readonly Dictionary<string, string> First = new Dictionary<string, string>(StringComparer.Ordinal); // lower-case name -> first value
        public string RequestTime;
        public string ResponseTime;

        public string Get(string lowerName)
        {
            string v;
            return First.TryGetValue(lowerName, out v) ? v : null;
        }
    }

    public static class HttpResponseInfoPickle
    {
        private const uint HasExtraFlags = 0x80000000u;
        private const uint ExtraHasOriginalResponseTime = 4;

        /// <summary>Null when the bytes are not a response-info pickle.</summary>
        public static ResponseHeaders Parse(byte[] s0)
        {
            try
            {
                if (s0 == null || s0.Length < 4 + 4 + 8 + 8 + 4) return null;
                uint payload = CacheBytes.U32(s0, 0);
                if (payload != (uint)(s0.Length - 4)) return null;
                int p = 4;
                uint flags = CacheBytes.U32(s0, p); p += 4;
                int version = (int)(flags & 0xFF);
                if (version < 1 || version > 10) return null;
                uint extra = 0;
                if ((flags & HasExtraFlags) != 0) { extra = CacheBytes.U32(s0, p); p += 4; }
                ulong requestTime = CacheBytes.U64(s0, p); p += 8;
                ulong responseTime = CacheBytes.U64(s0, p); p += 8;
                if ((extra & ExtraHasOriginalResponseTime) != 0) p += 8;
                if (p + 4 > s0.Length) return null;
                uint headersLength = CacheBytes.U32(s0, p); p += 4;
                if (headersLength > (uint)(s0.Length - p)) return null;
                ResponseHeaders h = new ResponseHeaders();
                h.RequestTime = CacheBytes.ChromeTimeToIso(requestTime);
                h.ResponseTime = CacheBytes.ChromeTimeToIso(responseTime);
                string raw = Encoding.GetEncoding("iso-8859-1").GetString(s0, p, (int)headersLength);
                string[] lines = raw.Split('\0');
                bool first = true;
                foreach (string line in lines)
                {
                    if (line.Length == 0) continue;
                    if (first)
                    {
                        h.StatusLine = line;
                        first = false;
                        if (!line.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)) return null;
                        continue;
                    }
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    string name = line.Substring(0, colon).Trim().ToLowerInvariant();
                    string value = line.Substring(colon + 1).Trim();
                    if (!h.First.ContainsKey(name)) h.First[name] = value;
                }
                return h.StatusLine == null ? null : h;
            }
            catch (Exception) { return null; }
        }

        /// <summary>CacheStorage entries keep a protobuf (CacheMetadata) in stream 0: header name/value pairs are
        /// length-prefixed strings (field 1 name, field 2 value). This finds one header value by name.</summary>
        public static string FindProtobufHeader(byte[] s0, string lowerName)
        {
            if (s0 == null || s0.Length == 0) return null;
            byte[] needle = Encoding.ASCII.GetBytes(lowerName);
            for (int i = 0; i + needle.Length + 2 <= s0.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    byte c = s0[i + j];
                    if (c >= (byte)'A' && c <= (byte)'Z') c = (byte)(c + 32);
                    if (c != needle[j]) { match = false; break; }
                }
                if (!match) continue;
                int p = i + needle.Length;
                if (s0[p] != 0x12) continue;      // field 2, length-delimited
                p++;
                int len = 0, shift = 0;
                while (p < s0.Length)
                {
                    byte b = s0[p++];
                    len |= (b & 0x7F) << shift;
                    if ((b & 0x80) == 0) break;
                    shift += 7;
                    if (shift > 28) return null;
                }
                if (len < 0 || p + len > s0.Length) return null;
                return Encoding.ASCII.GetString(s0, p, len);
            }
            return null;
        }
    }

    /// <summary>Content-Encoding handling. Decoded output is capped; the cap is reported, never hidden.</summary>
    public static class BodyDecoder
    {
        public const int MaxDecodedBytes = 32 * 1024 * 1024;

        public static string Classify(string contentEncoding)
        {
            if (contentEncoding == null) return "unknown";
            string v = contentEncoding.Trim().ToLowerInvariant();
            if (v.Length == 0 || v == "identity" || v == "none") return "identity";
            if (v == "gzip" || v == "x-gzip") return "gzip";
            if (v == "br") return "br";
            if (v == "deflate") return "deflate";
            if (v == "zstd") return "zstd";
            return "other";
        }

        public static bool LooksGzip(byte[] b)
        {
            return b != null && b.Length >= 3 && b[0] == 0x1F && b[1] == 0x8B && b[2] == 0x08;
        }

        /// <summary>Returns the bytes to search, or null when nothing can be searched (failed / unsupported).
        /// status: none (not compressed), ok, truncated, failed, unsupported, mismatch (declared compressed but not).</summary>
        public static byte[] Decode(byte[] body, string encodingClass, out string status)
        {
            status = "none";
            if (body == null) return null;
            if (encodingClass == "identity") return body;
            if (encodingClass == "unknown")
            {
                if (LooksGzip(body)) return Gzip(body, out status);
                return body;
            }
            if (encodingClass == "gzip")
            {
                if (!LooksGzip(body)) { status = "mismatch"; return body; }
                return Gzip(body, out status);
            }
            if (encodingClass == "deflate") return Deflate(body, out status);
            if (encodingClass == "br") return Brotli(body, out status);
            status = "unsupported";
            return null;
        }

        private static byte[] Gzip(byte[] body, out string status)
        {
            try
            {
                using (MemoryStream input = new MemoryStream(body, 0, body.Length, false))
                using (GZipStream gz = new GZipStream(input, CompressionMode.Decompress))
                {
                    return ReadAll(gz, out status);
                }
            }
            catch (Exception) { status = "failed"; return null; }
        }

        private static byte[] Deflate(byte[] body, out string status)
        {
            status = "failed";
            if (body.Length < 2) return null;
            bool zlib = (body[0] & 0x0F) == 8 && (((body[0] << 8) | body[1]) % 31) == 0;
            int[] offsets = zlib ? new int[] { 2, 0 } : new int[] { 0, 2 };
            foreach (int offset in offsets)
            {
                if (offset >= body.Length) continue;
                try
                {
                    using (MemoryStream input = new MemoryStream(body, offset, body.Length - offset, false))
                    using (DeflateStream ds = new DeflateStream(input, CompressionMode.Decompress))
                    {
                        string s;
                        byte[] output = ReadAll(ds, out s);
                        if (output != null && output.Length > 0) { status = s; return output; }
                    }
                }
                catch (Exception) { }
            }
            status = "failed";
            return null;
        }

        private static byte[] Brotli(byte[] body, out string status)
        {
            try
            {
                using (MemoryStream input = new MemoryStream(body, 0, body.Length, false))
                using (Org.Brotli.Dec.BrotliInputStream br = new Org.Brotli.Dec.BrotliInputStream(input))
                {
                    byte[] output = ReadAll(br, out status);
                    if (output == null || output.Length == 0) { status = "failed"; return null; }
                    return output;
                }
            }
            catch (Exception) { status = "failed"; return null; }
        }

        /// <summary>Reads until the stream reports no more data (0 or, for the Brotli decoder, -1), with the output cap.</summary>
        public static byte[] ReadAll(Stream s, out string status)
        {
            status = "ok";
            using (MemoryStream outStream = new MemoryStream())
            {
                byte[] buffer = new byte[65536];
                while (true)
                {
                    int n = s.Read(buffer, 0, buffer.Length);
                    if (n <= 0) break;
                    outStream.Write(buffer, 0, n);
                    if (outStream.Length > MaxDecodedBytes) { status = "truncated"; break; }
                }
                return outStream.ToArray();
            }
        }
    }
}
