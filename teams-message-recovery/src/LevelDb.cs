// Teams Message History - LevelDB file-level reader (read-only, no database engine).
// Reads every record version that is physically present in .log (write-ahead log) and
// .ldb/.sst (sorted table) files, including overwritten versions and deletion markers.
// Formats: https://github.com/google/leveldb/blob/main/doc/log_format.md and table_format.md.
// Structure follows the approach of ccl_chromium_reader (CCL Forensics, MIT), re-implemented in C#.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace TeamsMessageHistory
{
    public sealed class LevelDbRecord
    {
        public byte[] UserKey;
        public byte[] Value;
        public ulong Sequence;
        public bool IsDeletion;
        public string FileName;
        public int FileNumber;
        public string FileKind; // "log" or "ldb"
        public long Offset;
        public bool FromCompressedBlock;
    }

    public sealed class LevelDbWarning
    {
        public string FileName;
        public long Offset;
        public string Message;

        public override string ToString()
        {
            return FileName + " @" + Offset.ToString(CultureInfo.InvariantCulture) + ": " + Message;
        }
    }

    public sealed class LevelDbFileInfo
    {
        public string FileName;
        public string Kind;
        public long Bytes;
        public long Records;
        public long DeletionMarkers;
        public long Blocks;
        public long UnsupportedBlocks;
    }

    /// <summary>One LevelDB directory. Files are read in file-number order; records are yielded in file order.</summary>
    public sealed class LevelDbFolder
    {
        // LevelDB names data files "%06llu.ext": at least six digits, more once the file number passes 999999
        // (db/filename.cc MakeFileName / ParseFileName), so the number is parsed with any length.
        private static readonly Regex DataFilePattern = new Regex(@"^([0-9]{6,})\.(log|ldb|sst)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex OtherLevelDbFilePattern = new Regex(@"^(CURRENT|LOCK|LOG|LOG\.old|MANIFEST-[0-9]{6,})$", RegexOptions.Compiled);

        public readonly string Path;
        public readonly List<string> DataFiles = new List<string>();
        /// <summary>Files in the folder that follow none of LevelDB's naming rules; they are not read and are reported.</summary>
        public readonly List<string> UnrecognizedFiles = new List<string>();
        public readonly List<LevelDbWarning> Warnings = new List<LevelDbWarning>();
        public readonly Dictionary<string, LevelDbFileInfo> Files = new Dictionary<string, LevelDbFileInfo>(StringComparer.OrdinalIgnoreCase);

        public LevelDbFolder(string path)
        {
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
            Path = path;
            List<KeyValuePair<long, string>> found = new List<KeyValuePair<long, string>>();
            foreach (string file in Directory.GetFiles(path))
            {
                string name = System.IO.Path.GetFileName(file);
                Match m = DataFilePattern.Match(name);
                if (!m.Success)
                {
                    if (!OtherLevelDbFilePattern.IsMatch(name)) UnrecognizedFiles.Add(name);
                    continue;
                }
                found.Add(new KeyValuePair<long, string>(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), file));
            }
            found.Sort(delegate(KeyValuePair<long, string> a, KeyValuePair<long, string> b) { return a.Key.CompareTo(b.Key); });
            foreach (KeyValuePair<long, string> pair in found) DataFiles.Add(pair.Value);
            foreach (string name in UnrecognizedFiles) Warn(name, 0, "file name follows no LevelDB naming rule; not read");
        }

        /// <summary>The file number encoded in a LevelDB data file name, or -1.</summary>
        public static long FileNumber(string fileName)
        {
            Match m = DataFilePattern.Match(fileName ?? "");
            long n;
            if (m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            return -1;
        }

        public IEnumerable<LevelDbRecord> ReadRecords()
        {
            foreach (string file in DataFiles)
            {
                string name = System.IO.Path.GetFileName(file);
                long numberLong = FileNumber(name);
                int number = numberLong > int.MaxValue ? int.MaxValue : (int)numberLong;
                string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
                LevelDbFileInfo info;
                if (!Files.TryGetValue(name, out info))
                {
                    info = new LevelDbFileInfo();
                    info.FileName = name;
                    info.Kind = ext == ".log" ? "log" : "ldb";
                    info.Bytes = new FileInfo(file).Length;
                    Files[name] = info;
                }
                else
                {
                    info.Records = 0;
                    info.DeletionMarkers = 0;
                    info.Blocks = 0;
                    info.UnsupportedBlocks = 0;
                }
                IEnumerable<LevelDbRecord> records = ext == ".log"
                    ? ReadLogFile(file, name, number, info)
                    : ReadTableFile(file, name, number, info);
                foreach (LevelDbRecord record in records)
                {
                    info.Records++;
                    if (record.IsDeletion) info.DeletionMarkers++;
                    yield return record;
                }
            }
        }

        private void Warn(string file, long offset, string message)
        {
            LevelDbWarning w = new LevelDbWarning();
            w.FileName = file;
            w.Offset = offset;
            w.Message = message;
            Warnings.Add(w);
        }

        // ---------------------------------------------------------------- log files

        private IEnumerable<LevelDbRecord> ReadLogFile(string path, string name, int number, LevelDbFileInfo info)
        {
            byte[] data = File.ReadAllBytes(path);
            const int BlockSize = 32768;
            const int HeaderSize = 7;
            List<byte> pending = null;
            long pendingStart = 0;
            for (int blockStart = 0; blockStart < data.Length; blockStart += BlockSize)
            {
                int blockEnd = Math.Min(blockStart + BlockSize, data.Length);
                int pos = blockStart;
                info.Blocks++;
                while (blockEnd - pos >= HeaderSize)
                {
                    int length = data[pos + 4] | (data[pos + 5] << 8);
                    int type = data[pos + 6];
                    int payloadStart = pos + HeaderSize;
                    if (type == 0 && length == 0) break; // zero padding at the end of a block
                    if (payloadStart + length > blockEnd)
                    {
                        Warn(name, pos, "log record length exceeds block; rest of block skipped");
                        pending = null;
                        break;
                    }
                    if (type == 1)
                    {
                        if (pending != null) Warn(name, pos, "FULL record while a fragmented record was open; fragment dropped");
                        pending = null;
                        foreach (LevelDbRecord r in ParseBatch(data, payloadStart, length, name, number, pos)) yield return r;
                    }
                    else if (type == 2)
                    {
                        if (pending != null) Warn(name, pos, "FIRST record while a fragmented record was open; fragment dropped");
                        pending = new List<byte>(length);
                        pendingStart = pos;
                        for (int i = 0; i < length; i++) pending.Add(data[payloadStart + i]);
                    }
                    else if (type == 3)
                    {
                        if (pending == null) Warn(name, pos, "MIDDLE record without FIRST; ignored");
                        else for (int i = 0; i < length; i++) pending.Add(data[payloadStart + i]);
                    }
                    else if (type == 4)
                    {
                        if (pending == null) Warn(name, pos, "LAST record without FIRST; ignored");
                        else
                        {
                            for (int i = 0; i < length; i++) pending.Add(data[payloadStart + i]);
                            byte[] batch = pending.ToArray();
                            pending = null;
                            foreach (LevelDbRecord r in ParseBatch(batch, 0, batch.Length, name, number, pendingStart)) yield return r;
                        }
                    }
                    else
                    {
                        Warn(name, pos, "unknown log record type " + type.ToString(CultureInfo.InvariantCulture) + "; rest of block skipped");
                        pending = null;
                        break;
                    }
                    pos = payloadStart + length;
                }
            }
            if (pending != null) Warn(name, pendingStart, "fragmented record not terminated before end of file");
        }

        private List<LevelDbRecord> ParseBatch(byte[] data, int start, int length, string name, int number, long batchOffset)
        {
            List<LevelDbRecord> result = new List<LevelDbRecord>();
            int end = start + length;
            if (length < 12)
            {
                Warn(name, batchOffset, "write batch shorter than header");
                return result;
            }
            ulong sequence = BitConverter.ToUInt64(data, start);
            uint count = BitConverter.ToUInt32(data, start + 8);
            int pos = start + 12;
            for (uint i = 0; i < count; i++)
            {
                if (pos >= end)
                {
                    Warn(name, batchOffset, "write batch truncated (entry " + i.ToString(CultureInfo.InvariantCulture) + " of " + count.ToString(CultureInfo.InvariantCulture) + ")");
                    break;
                }
                long entryOffset = batchOffset + (pos - start);
                int type = data[pos++];
                int keyLength;
                if (!Varint.TryReadInt(data, ref pos, end, out keyLength) || pos + keyLength > end)
                {
                    Warn(name, entryOffset, "write batch key truncated");
                    break;
                }
                byte[] key = new byte[keyLength];
                Buffer.BlockCopy(data, pos, key, 0, keyLength);
                pos += keyLength;
                byte[] value = null;
                if (type == 1)
                {
                    int valueLength;
                    if (!Varint.TryReadInt(data, ref pos, end, out valueLength) || pos + valueLength > end)
                    {
                        Warn(name, entryOffset, "write batch value truncated");
                        break;
                    }
                    value = new byte[valueLength];
                    Buffer.BlockCopy(data, pos, value, 0, valueLength);
                    pos += valueLength;
                }
                else if (type != 0)
                {
                    Warn(name, entryOffset, "unknown write batch entry type " + type.ToString(CultureInfo.InvariantCulture));
                    break;
                }
                LevelDbRecord record = new LevelDbRecord();
                record.UserKey = key;
                record.Value = value;
                record.Sequence = sequence + i;
                record.IsDeletion = type == 0;
                record.FileName = name;
                record.FileNumber = number;
                record.FileKind = "log";
                record.Offset = entryOffset;
                record.FromCompressedBlock = false;
                result.Add(record);
            }
            return result;
        }

        // ---------------------------------------------------------------- table files

        private struct BlockHandle
        {
            public long Offset;
            public long Size;
        }

        private struct BlockEntry
        {
            public byte[] Key;
            public byte[] Value;
            public int EntryOffset;
        }

        private IEnumerable<LevelDbRecord> ReadTableFile(string path, string name, int number, LevelDbFileInfo info)
        {
            byte[] data = File.ReadAllBytes(path);
            const int FooterSize = 48;
            if (data.Length < FooterSize)
            {
                Warn(name, 0, "table file shorter than footer");
                yield break;
            }
            ulong magic = BitConverter.ToUInt64(data, data.Length - 8);
            if (magic != 0xdb4775248b80fb57UL)
            {
                Warn(name, data.Length - 8, "table magic mismatch; file skipped");
                yield break;
            }
            int fpos = data.Length - FooterSize;
            BlockHandle metaIndex;
            BlockHandle index;
            if (!ReadHandle(data, ref fpos, data.Length, out metaIndex) || !ReadHandle(data, ref fpos, data.Length, out index))
            {
                Warn(name, data.Length - FooterSize, "footer handles unreadable; file skipped");
                yield break;
            }
            bool indexCompressed;
            byte[] indexBlock = ReadBlock(data, index, name, out indexCompressed, info);
            if (indexBlock == null)
            {
                Warn(name, index.Offset, "index block unreadable; file skipped");
                yield break;
            }
            List<BlockEntry> indexEntries = ParseBlock(indexBlock, name, index.Offset);
            foreach (BlockEntry indexEntry in indexEntries)
            {
                int hpos = 0;
                BlockHandle handle;
                if (!ReadHandle(indexEntry.Value, ref hpos, indexEntry.Value.Length, out handle))
                {
                    Warn(name, index.Offset, "index entry handle unreadable; data block skipped");
                    continue;
                }
                bool compressed;
                byte[] block = ReadBlock(data, handle, name, out compressed, info);
                if (block == null) continue;
                info.Blocks++;
                foreach (BlockEntry entry in ParseBlock(block, name, handle.Offset))
                {
                    LevelDbRecord record = new LevelDbRecord();
                    if (entry.Key.Length >= 8)
                    {
                        ulong tail = BitConverter.ToUInt64(entry.Key, entry.Key.Length - 8);
                        record.Sequence = tail >> 8;
                        record.IsDeletion = (tail & 0xFF) == 0;
                        byte[] userKey = new byte[entry.Key.Length - 8];
                        Buffer.BlockCopy(entry.Key, 0, userKey, 0, userKey.Length);
                        record.UserKey = userKey;
                    }
                    else
                    {
                        record.UserKey = entry.Key;
                        record.Sequence = 0;
                        record.IsDeletion = false;
                        Warn(name, handle.Offset + entry.EntryOffset, "internal key shorter than 8 bytes");
                    }
                    record.Value = record.IsDeletion ? null : entry.Value;
                    record.FileName = name;
                    record.FileNumber = number;
                    record.FileKind = "ldb";
                    record.Offset = compressed ? handle.Offset : handle.Offset + entry.EntryOffset;
                    record.FromCompressedBlock = compressed;
                    yield return record;
                }
            }
        }

        private static bool ReadHandle(byte[] data, ref int pos, int end, out BlockHandle handle)
        {
            handle = new BlockHandle();
            ulong offset;
            ulong size;
            if (!Varint.TryRead(data, ref pos, end, out offset)) return false;
            if (!Varint.TryRead(data, ref pos, end, out size)) return false;
            if (offset > long.MaxValue || size > int.MaxValue) return false;
            handle.Offset = (long)offset;
            handle.Size = (long)size;
            return true;
        }

        private byte[] ReadBlock(byte[] data, BlockHandle handle, string name, out bool compressed, LevelDbFileInfo info)
        {
            compressed = false;
            const int TrailerSize = 5;
            if (handle.Offset < 0 || handle.Size < 0 || handle.Offset + handle.Size + TrailerSize > data.Length)
            {
                Warn(name, handle.Offset, "block handle outside file");
                return null;
            }
            int offset = (int)handle.Offset;
            int size = (int)handle.Size;
            int compression = data[offset + size];
            if (compression == 0)
            {
                byte[] raw = new byte[size];
                Buffer.BlockCopy(data, offset, raw, 0, size);
                return raw;
            }
            if (compression == 1)
            {
                try
                {
                    compressed = true;
                    return Snappy.Decompress(data, offset, size);
                }
                catch (Exception ex)
                {
                    Warn(name, handle.Offset, "snappy block could not be decompressed: " + ex.Message);
                    info.UnsupportedBlocks++;
                    return null;
                }
            }
            Warn(name, handle.Offset, "block compression type " + compression.ToString(CultureInfo.InvariantCulture) + " is not supported (only none/snappy); block skipped");
            info.UnsupportedBlocks++;
            return null;
        }

        private List<BlockEntry> ParseBlock(byte[] block, string name, long blockOffset)
        {
            List<BlockEntry> entries = new List<BlockEntry>();
            if (block.Length < 4)
            {
                Warn(name, blockOffset, "block shorter than restart count");
                return entries;
            }
            uint restartCount = BitConverter.ToUInt32(block, block.Length - 4);
            long restartOffsetLong = block.Length - ((long)restartCount + 1) * 4;
            if (restartOffsetLong < 0 || restartOffsetLong > block.Length - 4)
            {
                Warn(name, blockOffset, "restart array outside block");
                return entries;
            }
            int restartOffset = (int)restartOffsetLong;
            int pos = 0;
            byte[] previousKey = new byte[0];
            while (pos < restartOffset)
            {
                int entryOffset = pos;
                int shared;
                int nonShared;
                int valueLength;
                if (!Varint.TryReadInt(block, ref pos, restartOffset, out shared) ||
                    !Varint.TryReadInt(block, ref pos, restartOffset, out nonShared) ||
                    !Varint.TryReadInt(block, ref pos, restartOffset, out valueLength))
                {
                    Warn(name, blockOffset + entryOffset, "block entry header unreadable; rest of block skipped");
                    break;
                }
                if (shared > previousKey.Length || pos + nonShared + valueLength > restartOffset)
                {
                    Warn(name, blockOffset + entryOffset, "block entry lengths inconsistent; rest of block skipped");
                    break;
                }
                byte[] key = new byte[shared + nonShared];
                Buffer.BlockCopy(previousKey, 0, key, 0, shared);
                Buffer.BlockCopy(block, pos, key, shared, nonShared);
                pos += nonShared;
                byte[] value = new byte[valueLength];
                Buffer.BlockCopy(block, pos, value, 0, valueLength);
                pos += valueLength;
                BlockEntry entry = new BlockEntry();
                entry.Key = key;
                entry.Value = value;
                entry.EntryOffset = entryOffset;
                entries.Add(entry);
                previousKey = key;
            }
            return entries;
        }
    }

    /// <summary>Raw (non-framed) Snappy decompression, as used by LevelDB blocks and Chromium IndexedDB values.</summary>
    public static class Snappy
    {
        public static byte[] Decompress(byte[] src, int offset, int count)
        {
            int pos = offset;
            int end = offset + count;
            ulong expected;
            if (!Varint.TryRead(src, ref pos, end, out expected)) throw new FormatException("snappy: missing length");
            if (expected > 512UL * 1024 * 1024) throw new FormatException("snappy: implausible length");
            byte[] dst = new byte[(int)expected];
            int dp = 0;
            while (pos < end)
            {
                byte tag = src[pos++];
                int kind = tag & 3;
                if (kind == 0)
                {
                    int len = tag >> 2;
                    if (len < 60)
                    {
                        len += 1;
                    }
                    else
                    {
                        int extra = len - 59;
                        if (pos + extra > end) throw new FormatException("snappy: literal length truncated");
                        len = 0;
                        for (int i = 0; i < extra; i++) len |= src[pos++] << (8 * i);
                        len += 1;
                    }
                    if (len < 0 || pos + len > end || dp + len > dst.Length) throw new FormatException("snappy: literal overflow");
                    Buffer.BlockCopy(src, pos, dst, dp, len);
                    pos += len;
                    dp += len;
                }
                else
                {
                    int length;
                    int back;
                    if (kind == 1)
                    {
                        if (pos + 1 > end) throw new FormatException("snappy: copy truncated");
                        length = ((tag >> 2) & 7) + 4;
                        back = ((tag >> 5) << 8) | src[pos++];
                    }
                    else if (kind == 2)
                    {
                        if (pos + 2 > end) throw new FormatException("snappy: copy truncated");
                        length = (tag >> 2) + 1;
                        back = src[pos] | (src[pos + 1] << 8);
                        pos += 2;
                    }
                    else
                    {
                        if (pos + 4 > end) throw new FormatException("snappy: copy truncated");
                        length = (tag >> 2) + 1;
                        back = (int)BitConverter.ToUInt32(src, pos);
                        pos += 4;
                    }
                    if (back <= 0 || back > dp) throw new FormatException("snappy: bad back-reference");
                    if (dp + length > dst.Length) throw new FormatException("snappy: copy overflow");
                    for (int i = 0; i < length; i++)
                    {
                        dst[dp] = dst[dp - back];
                        dp++;
                    }
                }
            }
            if (dp != dst.Length) throw new FormatException("snappy: length mismatch");
            return dst;
        }
    }
}
