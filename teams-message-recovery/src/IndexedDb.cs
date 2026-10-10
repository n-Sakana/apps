// Teams Message History - Chromium IndexedDB layer on top of the LevelDB record stream.
// Key layout: https://chromium.googlesource.com/chromium/src/+/main/content/browser/indexed_db/docs/leveldb_coding_scheme.md
// Value wrapping (blob / snappy): third_party/blink/renderer/modules/indexeddb/idb_value_wrapping.cc
// Undo log (previous values): components/services/storage/indexed_db/scopes/README.md
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TeamsMessageHistory
{
    /// <summary>An IndexedDB key (IDBKey) decoded from its LevelDB encoding.</summary>
    public sealed class IdbKey
    {
        public const int TypeNull = 0, TypeString = 1, TypeDate = 2, TypeNumber = 3, TypeArray = 4, TypeMinKey = 5, TypeBinary = 6;

        public int Type;
        public object Value; // string, double, List<IdbKey>, byte[] or null
        public byte[] Raw;

        private static readonly Encoding Utf16Be = new UnicodeEncoding(true, false);

        public static IdbKey Parse(byte[] buffer, int offset, int end, out int consumed)
        {
            consumed = 0;
            if (offset >= end) throw new FormatException("IDB key truncated");
            IdbKey key = new IdbKey();
            int pos = offset;
            key.Type = buffer[pos++];
            switch (key.Type)
            {
                case TypeNull:
                case TypeMinKey:
                    key.Value = null;
                    break;
                case TypeString:
                    {
                        int units;
                        if (!Varint.TryReadInt(buffer, ref pos, end, out units) || pos + units * 2 > end) throw new FormatException("IDB string key truncated");
                        key.Value = Utf16Be.GetString(buffer, pos, units * 2);
                        pos += units * 2;
                        break;
                    }
                case TypeDate:
                case TypeNumber:
                    if (pos + 8 > end) throw new FormatException("IDB number key truncated");
                    key.Value = BitConverter.ToDouble(buffer, pos);
                    pos += 8;
                    break;
                case TypeArray:
                    {
                        int count;
                        if (!Varint.TryReadInt(buffer, ref pos, end, out count)) throw new FormatException("IDB array key truncated");
                        if (count > 100000) throw new FormatException("IDB array key implausibly long");
                        List<IdbKey> items = new List<IdbKey>(count);
                        for (int i = 0; i < count; i++)
                        {
                            int inner;
                            items.Add(Parse(buffer, pos, end, out inner));
                            pos += inner;
                        }
                        key.Value = items;
                        break;
                    }
                case TypeBinary:
                    {
                        int length;
                        if (!Varint.TryReadInt(buffer, ref pos, end, out length) || pos + length > end) throw new FormatException("IDB binary key truncated");
                        byte[] bytes = new byte[length];
                        Buffer.BlockCopy(buffer, pos, bytes, 0, length);
                        key.Value = bytes;
                        pos += length;
                        break;
                    }
                default:
                    throw new FormatException("IDB key type " + key.Type.ToString(CultureInfo.InvariantCulture) + " unknown");
            }
            consumed = pos - offset;
            key.Raw = new byte[consumed];
            Buffer.BlockCopy(buffer, offset, key.Raw, 0, consumed);
            return key;
        }

        public string Display()
        {
            switch (Type)
            {
                case TypeNull: return "null";
                case TypeMinKey: return "minkey";
                case TypeString: return (string)Value;
                case TypeDate: return "date(" + (TimeText.MillisToIso(Value) ?? Convert.ToString(Value, CultureInfo.InvariantCulture)) + ")";
                case TypeNumber:
                    {
                        double d = (double)Value;
                        if (Math.Abs(d) < 9007199254740992.0 && d == Math.Floor(d)) return ((long)d).ToString(CultureInfo.InvariantCulture);
                        return d.ToString("R", CultureInfo.InvariantCulture);
                    }
                case TypeArray:
                    {
                        List<IdbKey> items = (List<IdbKey>)Value;
                        List<string> parts = new List<string>();
                        foreach (IdbKey item in items) parts.Add(item.Display());
                        return "[" + string.Join(", ", parts.ToArray()) + "]";
                    }
                case TypeBinary: return "binary(" + Hex.ToHex((byte[])Value) + ")";
            }
            return "?";
        }

        /// <summary>All string components (array keys flattened), used for conversation-id matching.</summary>
        public List<string> StringParts()
        {
            List<string> parts = new List<string>();
            Collect(this, parts);
            return parts;
        }

        private static void Collect(IdbKey key, List<string> parts)
        {
            if (key.Type == TypeString) parts.Add((string)key.Value);
            else if (key.Type == TypeNumber || key.Type == TypeDate) parts.Add(key.Display());
            else if (key.Type == TypeArray) foreach (IdbKey item in (List<IdbKey>)key.Value) Collect(item, parts);
        }
    }

    public sealed class IdbDatabaseInfo
    {
        public long Id;
        public string Origin;
        public string Name;
        public readonly Dictionary<long, string> StoreNames = new Dictionary<long, string>();
    }

    public sealed class BlobFileEntry
    {
        public string FullPath;
        public string RelativePath; // "<db hex>/<xx>/<blob hex>"
        public long DatabaseId;     // -1 when the folder name is not hex
        public long BlobNumber;     // -1 when the file name is not hex
    }

    public sealed class IdbExternalObject
    {
        public int ObjectType; // 0 blob, 1 file, 2 file system access handle
        public long BlobNumber;
        public string MimeType;
        public long Size;
        public string FileName;
    }

    /// <summary>One stored version of the external-object (blob) entry of a record key (LevelDB index id 3).</summary>
    public sealed class ExternalEntryVersion
    {
        public ulong Sequence;        // LevelDB sequence of the entry record (or of the undo-log record that restored it)
        public bool IsDeletion;
        public bool FromUndoLog;      // restored "previous value" from the undo log
        public string FileName;
        public List<IdbExternalObject> Objects;
    }

    /// <summary>One IndexedDB object-store value as physically stored (one LevelDB record version, or one undo-log entry).</summary>
    public sealed class IdbDataItem
    {
        public bool IsUndoLog;
        public long DatabaseId;
        public long StoreId;
        public IdbKey Key;
        public byte[] RawValue; // IndexedDB value: varint version + wrapped serialized script value
        public LevelDbRecord Source;
        public long UndoScope;
        public long UndoSequence;
        public bool IsDeletion; // LevelDB deletion marker or undo "Delete(key)"
    }

    public sealed class UnwrapResult
    {
        public byte[] Ssv;      // serialized script value, starting with 0xFF <blink version>
        public int SsvOffset;   // offset in Ssv where the V8 payload (0xFF <v8 version> ...) starts
        public bool IsWrapped;  // the stored value is a wrapper (blob reference or compressed); the payload is not visible in the record itself
        public bool WasSnappy;
        public bool WasExternalBlob;
        public string BlobPath;
        public long BlobNumber;
        public ulong BlobEntrySequence;   // sequence of the external-object entry version that was used
        public bool BlobEntryFromUndoLog;
        public ulong BlinkVersion;
        public string Error;    // null when readable
    }

    /// <summary>Reads schema and data items from a LevelDB folder that holds a Chromium IndexedDB.</summary>
    public sealed class IndexedDbReader
    {
        private static readonly Encoding Utf16Be = new UnicodeEncoding(true, false);

        public readonly LevelDbFolder Folder;
        public readonly string BlobFolder; // may not exist
        public readonly Dictionary<long, IdbDatabaseInfo> Databases = new Dictionary<long, IdbDatabaseInfo>();
        public readonly List<string> SchemaNotes = new List<string>();
        public long TotalRecords;
        public long DataRecords;
        public long UndoRecords;
        public long ExternalEntryVersions;
        // Global metadata «0,0,0,5» / «0,0,0,6»: the next time Chromium allows its tombstone sweep / LevelDB compaction
        // (interpreted as microseconds since 1601-01-01 UTC, the base::Time Windows-epoch encoding).
        public DateTime? EarliestSweepTimeUtc;
        public DateTime? EarliestCompactionTimeUtc;
        // Every stored version of every external-object entry, by record key (db|store|keyhex), in sequence order.
        private readonly Dictionary<string, List<ExternalEntryVersion>> _externalVersions = new Dictionary<string, List<ExternalEntryVersion>>(StringComparer.Ordinal);
        // Every data record version (sequence) by record key, in sequence order: the version windows for blob-entry lookup.
        private readonly Dictionary<string, List<ulong>> _dataSequences = new Dictionary<string, List<ulong>>(StringComparer.Ordinal);
        private bool _schemaLoaded;

        public IndexedDbReader(string levelDbPath)
        {
            Folder = new LevelDbFolder(levelDbPath);
            string trimmed = levelDbPath.TrimEnd('\\', '/');
            string parent = Path.GetDirectoryName(trimmed);
            string name = Path.GetFileName(trimmed);
            if (name.EndsWith(".leveldb", StringComparison.OrdinalIgnoreCase) && parent != null)
            {
                BlobFolder = Path.Combine(parent, name.Substring(0, name.Length - ".leveldb".Length) + ".blob");
            }
        }

        public static bool TryParsePrefix(byte[] key, out long dbId, out long storeId, out long indexId, out int length)
        {
            dbId = storeId = indexId = 0;
            length = 0;
            if (key == null || key.Length < 1) return false;
            byte b = key[0];
            int dbLen = ((b >> 5) & 7) + 1;
            int storeLen = ((b >> 2) & 7) + 1;
            int indexLen = (b & 3) + 1;
            length = 1 + dbLen + storeLen + indexLen;
            if (key.Length < length) return false;
            int pos = 1;
            dbId = ReadLittleEndian(key, pos, dbLen); pos += dbLen;
            storeId = ReadLittleEndian(key, pos, storeLen); pos += storeLen;
            indexId = ReadLittleEndian(key, pos, indexLen);
            return true;
        }

        private static long ReadLittleEndian(byte[] data, int offset, int count)
        {
            long value = 0;
            for (int i = 0; i < count && i < 8; i++) value |= ((long)data[offset + i]) << (8 * i);
            return value;
        }

        private static bool TryReadStringWithLength(byte[] data, ref int pos, int end, out string text)
        {
            text = null;
            int units;
            if (!Varint.TryReadInt(data, ref pos, end, out units) || units < 0 || pos + units * 2 > end) return false;
            text = Utf16Be.GetString(data, pos, units * 2);
            pos += units * 2;
            return true;
        }

        private static string RecordKeyId(long db, long store, byte[] rawKey)
        {
            return db.ToString(CultureInfo.InvariantCulture) + "|" + store.ToString(CultureInfo.InvariantCulture) + "|" + Hex.ToHex(rawKey);
        }

        private static bool IsUndoLogKey(byte[] key, int prefixLength)
        {
            return key.Length > prefixLength + 1 && key[prefixLength] == 50 && key[prefixLength + 1] == 2;
        }

        /// <summary>Pass 1: database names and object-store names (latest live version), every version of every
        /// external-object entry (including deletions and undo-log restorations), and the sequence of every data record version.</summary>
        public void LoadSchema()
        {
            Dictionary<string, LevelDbRecord> latestMeta = new Dictionary<string, LevelDbRecord>(StringComparer.Ordinal);
            foreach (LevelDbRecord record in Folder.ReadRecords())
            {
                TotalRecords++;
                long db, store, index;
                int prefixLength;
                if (!TryParsePrefix(record.UserKey, out db, out store, out index, out prefixLength)) continue;
                if (db > 0 && store > 0 && index == 1)
                {
                    AddDataSequence(db, store, SliceKey(record.UserKey, prefixLength), record.Sequence);
                    continue;
                }
                if (db > 0 && store > 0 && index == 3)
                {
                    AddExternalVersion(db, store, SliceKey(record.UserKey, prefixLength), record.Sequence, record.IsDeletion, false, record.Value, record.FileName);
                    continue;
                }
                if (db == 0 && store == 0 && index == 0 && IsUndoLogKey(record.UserKey, prefixLength))
                {
                    if (record.IsDeletion || record.Value == null) continue;
                    byte[] restoredKey, restoredValue;
                    long scope, undoSequence;
                    bool isDelete;
                    if (!TryParseUndoTask(record, prefixLength + 2, out restoredKey, out restoredValue, out isDelete, out scope, out undoSequence)) continue;
                    long rdb, rstore, rindex;
                    int rprefix;
                    if (!TryParsePrefix(restoredKey, out rdb, out rstore, out rindex, out rprefix)) continue;
                    if (rdb > 0 && rstore > 0 && rindex == 3 && !isDelete && restoredValue != null)
                    {
                        AddExternalVersion(rdb, rstore, SliceKey(restoredKey, rprefix), record.Sequence, false, true, restoredValue, record.FileName);
                    }
                    continue;
                }
                bool meta = (db == 0 && store == 0 && index == 0) || (db > 0 && store == 0 && index == 0);
                if (!meta) continue;
                string keyHex = Hex.ToHex(record.UserKey);
                LevelDbRecord existing;
                if (!latestMeta.TryGetValue(keyHex, out existing) || existing.Sequence < record.Sequence) latestMeta[keyHex] = record;
            }
            foreach (List<ulong> seqs in _dataSequences.Values) seqs.Sort();
            foreach (List<ExternalEntryVersion> versions in _externalVersions.Values)
            {
                versions.Sort(delegate(ExternalEntryVersion a, ExternalEntryVersion b) { return a.Sequence.CompareTo(b.Sequence); });
            }
            foreach (LevelDbRecord record in latestMeta.Values)
            {
                if (record.IsDeletion) continue;
                long db, store, index;
                int prefixLength;
                TryParsePrefix(record.UserKey, out db, out store, out index, out prefixLength);
                byte[] key = record.UserKey;
                int pos = prefixLength;
                try
                {
                    if (db == 0 && store == 0 && index == 0)
                    {
                        if (pos >= key.Length) continue;
                        int type = key[pos++];
                        if ((type == 5 || type == 6) && record.Value != null && record.Value.Length > 0 && record.Value.Length <= 8)
                        {
                            long micros = ReadLittleEndian(record.Value, 0, record.Value.Length);
                            DateTime? when = WindowsEpochMicrosToUtc(micros);
                            if (type == 5) EarliestSweepTimeUtc = when; else EarliestCompactionTimeUtc = when;
                        }
                        if (type == 201)
                        {
                            string origin, name;
                            if (!TryReadStringWithLength(key, ref pos, key.Length, out origin) || !TryReadStringWithLength(key, ref pos, key.Length, out name)) continue;
                            long dbId = ReadLittleEndian(record.Value, 0, record.Value.Length);
                            IdbDatabaseInfo info;
                            if (!Databases.TryGetValue(dbId, out info))
                            {
                                info = new IdbDatabaseInfo();
                                info.Id = dbId;
                                Databases[dbId] = info;
                            }
                            info.Origin = origin;
                            info.Name = name;
                        }
                    }
                    else if (db > 0 && store == 0 && index == 0)
                    {
                        if (pos >= key.Length) continue;
                        int type = key[pos++];
                        if (type == 50)
                        {
                            ulong storeRaw;
                            if (!Varint.TryRead(key, ref pos, key.Length, out storeRaw)) continue;
                            long storeId = (long)storeRaw;
                            if (pos >= key.Length) continue;
                            int metaType = key[pos++];
                            if (metaType == 0)
                            {
                                IdbDatabaseInfo info;
                                if (!Databases.TryGetValue(db, out info))
                                {
                                    info = new IdbDatabaseInfo();
                                    info.Id = db;
                                    Databases[db] = info;
                                }
                                info.StoreNames[storeId] = Utf16Be.GetString(record.Value, 0, record.Value.Length - (record.Value.Length % 2));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    SchemaNotes.Add("metadata record " + record.FileName + " seq " + record.Sequence.ToString(CultureInfo.InvariantCulture) + " skipped: " + ex.Message);
                }
            }
            _schemaLoaded = true;
        }

        private static DateTime? WindowsEpochMicrosToUtc(long micros)
        {
            if (micros <= 0) return null;
            try
            {
                DateTime t = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(micros * 10);
                if (t.Year < 2000 || t.Year > 2100) return null;
                return t;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Blob numbers are allocated per database, so a reference is the pair (database id, blob number).</summary>
        public static string BlobRef(long databaseId, long blobNumber)
        {
            return databaseId.ToString(CultureInfo.InvariantCulture) + ":" + blobNumber.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>(database id, blob number) pairs referenced by any stored version of any external-object entry.</summary>
        public HashSet<string> ReferencedBlobs()
        {
            HashSet<string> refs = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<ExternalEntryVersion>> pair in _externalVersions)
            {
                int bar = pair.Key.IndexOf('|');
                long db;
                if (bar <= 0 || !long.TryParse(pair.Key.Substring(0, bar), NumberStyles.Integer, CultureInfo.InvariantCulture, out db)) continue;
                foreach (ExternalEntryVersion v in pair.Value)
                    foreach (IdbExternalObject o in v.Objects)
                        if (o.ObjectType == 0 || o.ObjectType == 1) refs.Add(BlobRef(db, o.BlobNumber));
            }
            return refs;
        }

        /// <summary>Files in the blob folder: relative path, database id (first folder, hex) and blob number (file name, hex); -1 when not parseable.</summary>
        public List<BlobFileEntry> ListBlobFiles()
        {
            List<BlobFileEntry> files = new List<BlobFileEntry>();
            if (BlobFolder == null || !Directory.Exists(BlobFolder)) return files;
            string[] all = Directory.GetFiles(BlobFolder, "*", SearchOption.AllDirectories);
            Array.Sort(all, StringComparer.OrdinalIgnoreCase);
            foreach (string path in all)
            {
                BlobFileEntry entry = new BlobFileEntry();
                entry.FullPath = path;
                entry.RelativePath = path.Substring(BlobFolder.Length).TrimStart('\\', '/').Replace('\\', '/');
                long number, db;
                entry.BlobNumber = long.TryParse(Path.GetFileName(path), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number) ? number : -1;
                int slash = entry.RelativePath.IndexOf('/');
                entry.DatabaseId = slash > 0 && long.TryParse(entry.RelativePath.Substring(0, slash), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out db) ? db : -1;
                files.Add(entry);
            }
            return files;
        }

        /// <summary>Unwrap a serialized script value that is stored on its own (a blob file holding a wrapped record value).</summary>
        public UnwrapResult UnwrapStandalone(byte[] bytes)
        {
            UnwrapResult result = new UnwrapResult();
            if (bytes == null || bytes.Length == 0)
            {
                result.Error = "empty file";
                return result;
            }
            UnwrapSsv(bytes, 0, null, result, 1);
            return result;
        }

        private static byte[] SliceKey(byte[] key, int prefixLength)
        {
            byte[] raw = new byte[key.Length - prefixLength];
            Buffer.BlockCopy(key, prefixLength, raw, 0, raw.Length);
            return raw;
        }

        private void AddDataSequence(long db, long store, byte[] rawKey, ulong sequence)
        {
            string id = RecordKeyId(db, store, rawKey);
            List<ulong> list;
            if (!_dataSequences.TryGetValue(id, out list))
            {
                list = new List<ulong>();
                _dataSequences[id] = list;
            }
            list.Add(sequence);
        }

        private void AddExternalVersion(long db, long store, byte[] rawKey, ulong sequence, bool isDeletion, bool fromUndo, byte[] value, string fileName)
        {
            ExternalEntryVersion v = new ExternalEntryVersion();
            v.Sequence = sequence;
            v.IsDeletion = isDeletion;
            v.FromUndoLog = fromUndo;
            v.FileName = fileName;
            v.Objects = isDeletion || value == null ? new List<IdbExternalObject>() : ParseExternalObjects(value);
            string id = RecordKeyId(db, store, rawKey);
            List<ExternalEntryVersion> list;
            if (!_externalVersions.TryGetValue(id, out list))
            {
                list = new List<ExternalEntryVersion>();
                _externalVersions[id] = list;
            }
            list.Add(v);
            ExternalEntryVersions++;
        }

        private static List<IdbExternalObject> ParseExternalObjects(byte[] value)
        {
            List<IdbExternalObject> result = new List<IdbExternalObject>();
            int pos = 0;
            int end = value.Length;
            while (pos < end)
            {
                ulong type;
                if (!Varint.TryRead(value, ref pos, end, out type)) break;
                IdbExternalObject obj = new IdbExternalObject();
                obj.ObjectType = (int)type;
                if (type == 0 || type == 1)
                {
                    ulong blobNumber, size;
                    string mime;
                    if (!Varint.TryRead(value, ref pos, end, out blobNumber)) break;
                    if (!TryReadStringWithLength(value, ref pos, end, out mime)) break;
                    if (!Varint.TryRead(value, ref pos, end, out size)) break;
                    obj.BlobNumber = (long)blobNumber;
                    obj.MimeType = mime;
                    obj.Size = (long)size;
                    if (type == 1)
                    {
                        string fileName;
                        ulong lastModified;
                        if (!TryReadStringWithLength(value, ref pos, end, out fileName)) break;
                        if (!Varint.TryRead(value, ref pos, end, out lastModified)) break;
                        obj.FileName = fileName;
                    }
                    result.Add(obj);
                }
                else if (type == 2)
                {
                    int length;
                    if (!Varint.TryReadInt(value, ref pos, end, out length) || pos + length > end) break;
                    pos += length;
                    result.Add(obj);
                }
                else break;
            }
            return result;
        }

        public string DatabaseName(long db)
        {
            IdbDatabaseInfo info;
            if (Databases.TryGetValue(db, out info) && info.Name != null) return info.Name;
            return "db#" + db.ToString(CultureInfo.InvariantCulture);
        }

        public string StoreName(long db, long store)
        {
            IdbDatabaseInfo info;
            string name;
            if (Databases.TryGetValue(db, out info) && info.StoreNames.TryGetValue(store, out name)) return name;
            return "store#" + store.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Pass 2: every object-store data record version and every undo-log entry that restores a data record, in file order.</summary>
        public IEnumerable<IdbDataItem> ReadDataItems()
        {
            if (!_schemaLoaded) LoadSchema();
            foreach (LevelDbRecord record in Folder.ReadRecords())
            {
                long db, store, index;
                int prefixLength;
                if (!TryParsePrefix(record.UserKey, out db, out store, out index, out prefixLength)) continue;
                if (db > 0 && store > 0 && index == 1)
                {
                    IdbDataItem item = new IdbDataItem();
                    item.DatabaseId = db;
                    item.StoreId = store;
                    item.Source = record;
                    item.IsDeletion = record.IsDeletion;
                    item.RawValue = record.Value;
                    try
                    {
                        int consumed;
                        item.Key = IdbKey.Parse(record.UserKey, prefixLength, record.UserKey.Length, out consumed);
                    }
                    catch (Exception)
                    {
                        item.Key = null;
                    }
                    DataRecords++;
                    yield return item;
                }
                else if (db == 0 && store == 0 && index == 0 && IsUndoLogKey(record.UserKey, prefixLength))
                {
                    if (record.IsDeletion || record.Value == null) continue;
                    byte[] restoredKey, restoredValue;
                    long scope, undoSequence;
                    bool isDelete;
                    if (!TryParseUndoTask(record, prefixLength + 2, out restoredKey, out restoredValue, out isDelete, out scope, out undoSequence)) continue;
                    long rdb, rstore, rindex;
                    int rprefix;
                    if (!TryParsePrefix(restoredKey, out rdb, out rstore, out rindex, out rprefix)) continue;
                    if (rdb < 1 || rstore < 1 || rindex != 1) continue;
                    IdbDataItem undo = new IdbDataItem();
                    undo.IsUndoLog = true;
                    undo.DatabaseId = rdb;
                    undo.StoreId = rstore;
                    undo.Source = record;
                    undo.UndoScope = scope;
                    undo.UndoSequence = undoSequence;
                    undo.IsDeletion = isDelete || restoredValue == null;
                    undo.RawValue = restoredValue;
                    try
                    {
                        int consumed;
                        undo.Key = IdbKey.Parse(restoredKey, rprefix, restoredKey.Length, out consumed);
                    }
                    catch (Exception)
                    {
                        undo.Key = null;
                    }
                    UndoRecords++;
                    yield return undo;
                }
            }
        }

        // Key: 00 00 00 00 32 02 <varint scope> 00 <int64 big-endian sequence>
        // Value: protobuf LevelDBScopesUndoTask { Put put = 1 {bytes key = 1; bytes value = 2;}; Delete delete = 2 {bytes key = 1;}; DeleteRange = 3 }
        private static bool TryParseUndoTask(LevelDbRecord record, int pos, out byte[] restoredKey, out byte[] restoredValue, out bool isDelete, out long scope, out long undoSequence)
        {
            restoredKey = null;
            restoredValue = null;
            isDelete = false;
            scope = 0;
            undoSequence = 0;
            byte[] key = record.UserKey;
            ulong scopeRaw;
            if (!Varint.TryRead(key, ref pos, key.Length, out scopeRaw)) return false;
            scope = (long)scopeRaw;
            if (pos >= key.Length || key[pos] != 0) return false;
            pos++;
            if (pos + 8 > key.Length) return false;
            for (int i = 0; i < 8; i++) undoSequence = (undoSequence << 8) | key[pos + i];

            byte[] value = record.Value;
            int vpos = 0;
            ulong tag;
            if (!Varint.TryRead(value, ref vpos, value.Length, out tag)) return false;
            int field = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            if (wire != 2 || (field != 1 && field != 2)) return false;
            int innerLength;
            if (!Varint.TryReadInt(value, ref vpos, value.Length, out innerLength) || vpos + innerLength > value.Length) return false;
            int innerEnd = vpos + innerLength;
            while (vpos < innerEnd)
            {
                ulong innerTag;
                if (!Varint.TryRead(value, ref vpos, innerEnd, out innerTag)) break;
                int innerField = (int)(innerTag >> 3);
                int innerWire = (int)(innerTag & 7);
                if (innerWire != 2) break;
                int length;
                if (!Varint.TryReadInt(value, ref vpos, innerEnd, out length) || vpos + length > innerEnd) break;
                byte[] data = new byte[length];
                Buffer.BlockCopy(value, vpos, data, 0, length);
                vpos += length;
                if (innerField == 1) restoredKey = data;
                else if (innerField == 2) restoredValue = data;
            }
            if (restoredKey == null) return false;
            isDelete = field == 2;
            return true;
        }

        /// <summary>Strip the IndexedDB value envelope and the Blink wrapper (blob reference or snappy) and locate the V8 payload.</summary>
        public UnwrapResult Unwrap(IdbDataItem item)
        {
            UnwrapResult result = new UnwrapResult();
            byte[] raw = item.RawValue;
            if (raw == null || raw.Length == 0)
            {
                result.Error = "empty value";
                return result;
            }
            int pos = 0;
            ulong idbVersion;
            if (!Varint.TryRead(raw, ref pos, raw.Length, out idbVersion))
            {
                result.Error = "value shorter than its version prefix";
                return result;
            }
            UnwrapSsv(raw, pos, item, result, 0);
            return result;
        }

        private void UnwrapSsv(byte[] buffer, int pos, IdbDataItem item, UnwrapResult result, int depth)
        {
            if (depth > 4)
            {
                result.Error = "wrapper nesting too deep";
                return;
            }
            if (pos >= buffer.Length || buffer[pos] != 0xFF)
            {
                result.Error = "no serialization version tag (0xFF) at offset " + pos.ToString(CultureInfo.InvariantCulture);
                return;
            }
            pos++;
            ulong blinkVersion;
            if (!Varint.TryRead(buffer, ref pos, buffer.Length, out blinkVersion))
            {
                result.Error = "serialization version truncated";
                return;
            }
            result.BlinkVersion = blinkVersion;
            if (blinkVersion == 17)
            {
                result.IsWrapped = true;
                if (pos >= buffer.Length)
                {
                    result.Error = "wrapper mode byte missing";
                    return;
                }
                int mode = buffer[pos++];
                if (mode == 1)
                {
                    ulong blobSize, blobIndex;
                    if (!Varint.TryRead(buffer, ref pos, buffer.Length, out blobSize) || !Varint.TryRead(buffer, ref pos, buffer.Length, out blobIndex))
                    {
                        result.Error = "external blob reference truncated";
                        return;
                    }
                    result.WasExternalBlob = true;
                    if (item == null)
                    {
                        result.Error = "external blob: a stand-alone value refers to yet another blob";
                        return;
                    }
                    byte[] blob = ReadExternalBlob(item, (int)blobIndex, result);
                    if (blob == null) return;
                    UnwrapSsv(blob, 0, item, result, depth + 1);
                    return;
                }
                if (mode == 2)
                {
                    byte[] decompressed;
                    try
                    {
                        decompressed = Snappy.Decompress(buffer, pos, buffer.Length - pos);
                    }
                    catch (Exception ex)
                    {
                        result.Error = "wrapped value could not be decompressed: " + ex.Message;
                        return;
                    }
                    result.WasSnappy = true;
                    UnwrapSsv(decompressed, 0, item, result, depth + 1);
                    return;
                }
                result.Error = "unknown wrapper mode " + mode.ToString(CultureInfo.InvariantCulture);
                return;
            }
            if (blinkVersion >= 21)
            {
                if (pos + 13 > buffer.Length || buffer[pos] != 0xFE)
                {
                    result.Error = "trailer offset tag missing for serialization version " + blinkVersion.ToString(CultureInfo.InvariantCulture);
                    return;
                }
                pos += 13;
            }
            result.Ssv = buffer;
            result.SsvOffset = pos;
            result.Error = null;
        }

        /// <summary>Pick the external-object entry version that belongs to this record version.
        /// Chromium writes the data record first and the blob entry (index id 3) later in the same transaction, and on an
        /// overwrite it deletes the old entry and writes a new one. So the entry of data version k is the first non-deleted
        /// entry at or after the data record's sequence and before the next data version. An undo-log item holds the value
        /// that was current before the transaction whose data put is nearest to it, so its entry is the last non-deleted
        /// entry before that data put. Nothing outside the version's window is ever used: that would attribute another
        /// version's body to this one.</summary>
        private ExternalEntryVersion SelectEntryVersion(IdbDataItem item, out string error)
        {
            error = null;
            string id = RecordKeyId(item.DatabaseId, item.StoreId, item.Key.Raw);
            List<ExternalEntryVersion> versions;
            if (!_externalVersions.TryGetValue(id, out versions) || versions.Count == 0)
            {
                error = "external blob: no blob entry stored for this key";
                return null;
            }
            List<ulong> dataSeqs;
            if (!_dataSequences.TryGetValue(id, out dataSeqs)) dataSeqs = new List<ulong>();
            ulong s = item.Source.Sequence;
            ulong lower, upper;
            bool preferEarliest;
            if (!item.IsUndoLog)
            {
                lower = s;
                upper = ulong.MaxValue;
                foreach (ulong d in dataSeqs) if (d > s) { upper = d; break; }
                preferEarliest = true;
            }
            else
            {
                // nearest data version to the undo record: the transaction that overwrote the restored value
                ulong nearest = 0;
                bool found = false;
                ulong bestDistance = ulong.MaxValue;
                foreach (ulong d in dataSeqs)
                {
                    ulong distance = d > s ? d - s : s - d;
                    if (distance < bestDistance) { bestDistance = distance; nearest = d; found = true; }
                }
                if (!found)
                {
                    lower = 0;
                    upper = s;
                }
                else
                {
                    upper = nearest;
                    lower = 0;
                    foreach (ulong d in dataSeqs) if (d < nearest) lower = d;
                }
                preferEarliest = false;
            }
            ExternalEntryVersion chosen = null;
            foreach (ExternalEntryVersion v in versions)
            {
                if (v.IsDeletion) continue;
                ulong eff = v.Sequence;
                if (v.FromUndoLog)
                {
                    // a restored entry belongs to the version window before the data put nearest to the undo record
                    ulong nearest = 0;
                    bool found = false;
                    ulong bestDistance = ulong.MaxValue;
                    foreach (ulong d in dataSeqs)
                    {
                        ulong distance = d > v.Sequence ? d - v.Sequence : v.Sequence - d;
                        if (distance < bestDistance) { bestDistance = distance; nearest = d; found = true; }
                    }
                    if (!found || nearest == 0) continue;
                    eff = nearest - 1;
                }
                if (eff < lower || eff >= upper) continue;
                if (chosen == null) { chosen = v; continue; }
                // A directly stored entry is better evidence than an undo-log restoration of the same version.
                if (chosen.FromUndoLog && !v.FromUndoLog) { chosen = v; continue; }
                if (!chosen.FromUndoLog && v.FromUndoLog) continue;
                ulong chosenEff = chosen.FromUndoLog ? EffectiveSequence(chosen, dataSeqs) : chosen.Sequence;
                if (preferEarliest ? eff < chosenEff : eff > chosenEff) chosen = v;
            }
            if (chosen == null)
            {
                error = "external blob: no blob entry belongs to this record version (window " + lower.ToString(CultureInfo.InvariantCulture) + " to " + (upper == ulong.MaxValue ? "end" : upper.ToString(CultureInfo.InvariantCulture)) + "); the entry may have been compacted away";
                return null;
            }
            return chosen;
        }

        private static ulong EffectiveSequence(ExternalEntryVersion v, List<ulong> dataSeqs)
        {
            if (!v.FromUndoLog) return v.Sequence;
            ulong nearest = 0;
            ulong bestDistance = ulong.MaxValue;
            foreach (ulong d in dataSeqs)
            {
                ulong distance = d > v.Sequence ? d - v.Sequence : v.Sequence - d;
                if (distance < bestDistance) { bestDistance = distance; nearest = d; }
            }
            return nearest == 0 ? 0 : nearest - 1;
        }

        private byte[] ReadExternalBlob(IdbDataItem item, int blobIndex, UnwrapResult result)
        {
            if (item.Key == null)
            {
                result.Error = "external blob: record key unreadable";
                return null;
            }
            string error;
            ExternalEntryVersion entry = SelectEntryVersion(item, out error);
            if (entry == null)
            {
                result.Error = error;
                return null;
            }
            result.BlobEntrySequence = entry.Sequence;
            result.BlobEntryFromUndoLog = entry.FromUndoLog;
            if (blobIndex < 0 || blobIndex >= entry.Objects.Count)
            {
                result.Error = "external blob: blob entry (seq " + entry.Sequence.ToString(CultureInfo.InvariantCulture) + ") has no object at index " + blobIndex.ToString(CultureInfo.InvariantCulture);
                return null;
            }
            IdbExternalObject obj = entry.Objects[blobIndex];
            result.BlobNumber = obj.BlobNumber;
            if (BlobFolder == null)
            {
                result.Error = "external blob: blob folder unknown";
                return null;
            }
            string path = Path.Combine(Path.Combine(Path.Combine(BlobFolder, item.DatabaseId.ToString("x", CultureInfo.InvariantCulture)),
                ((obj.BlobNumber >> 8) & 0xFF).ToString("x2", CultureInfo.InvariantCulture)),
                obj.BlobNumber.ToString("x", CultureInfo.InvariantCulture));
            result.BlobPath = path;
            if (!File.Exists(path))
            {
                result.Error = "external blob file missing (blob number " + obj.BlobNumber.ToString(CultureInfo.InvariantCulture) + ", entry seq " + entry.Sequence.ToString(CultureInfo.InvariantCulture) + "): " + path;
                return null;
            }
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                result.Error = "external blob file unreadable: " + ex.Message;
                return null;
            }
        }
    }
}
