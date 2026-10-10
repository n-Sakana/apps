// Teams Message History - locate every stored body of one message inside a Teams IndexedDB copy.
// Sources searched: every version of every object-store record (live and overwritten), and the
// IndexedDB undo log (previous values written before an overwrite). Matching is structural:
// an object whose "id" (or messageMap key / clientMessageId) equals the requested message.
// Records that carry the id in another field, and text with no id nearby, are the thread-wide search's job
// (ThreadSearch.cs); they are never body candidates.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TeamsMessageHistory
{
    public sealed class Occurrence
    {
        public string SourceKind;      // "record" or "undo-log"
        public string MatchKind;       // "id", "messageMap-key" or "clientMessageId"
        public string Database;
        public string Store;
        public string KeyDisplay;
        public ulong Sequence;
        public string File;
        public long Offset;
        public bool Live;
        public bool FromCompressedBlock;
        public long UndoScope;
        public long UndoSequence;
        public string Path;
        public string ContentHtml;
        public bool HasContentField;
        public OrderedMap Fields = new OrderedMap();
        public OrderedMap RecordFields = new OrderedMap();
        public string ConversationIdInRecord;
        public string ConversationMatch; // "match", "mismatch", "unchecked", "unknown"
        public bool WasExternalBlob;
        public bool WasSnappy;
        public string BlobPath;
        public ulong BlobEntrySequence;
        public long BlobNumber;
        public bool BlobEntryFromUndoLog;
    }

    /// <summary>One exact HTML representation of a body text.</summary>
    public sealed class BodyVariant
    {
        public int Index;
        public string ContentHtml;
        public string Sha256;
        public ulong FirstSequence;
        public ulong LastSequence;
        public readonly List<Occurrence> Occurrences = new List<Occurrence>();
    }

    /// <summary>All occurrences whose plain text is the same body. Variants keep every distinct HTML form.</summary>
    public sealed class BodyGroup
    {
        public int Index;
        public bool HasContent;         // false when the matched objects had no readable content
        public string ContentText;      // plain text (tags stripped, entities decoded)
        public string TextSha256;
        public ulong FirstSequence;
        public ulong LastSequence;
        public readonly List<BodyVariant> Variants = new List<BodyVariant>();
        public readonly List<Occurrence> Occurrences = new List<Occurrence>();
    }

    public sealed class UnreadableItem
    {
        public string SourceKind;
        public string Database;
        public string Store;
        public string KeyDisplay;
        public ulong Sequence;
        public string File;
        public long Offset;
        public string Reason;
        public string Needle;
    }

    public sealed class SearchStats
    {
        public long ItemsScanned;
        public long DataVersions;
        public long UndoEntries;
        public long DeletionMarkers;
        public long ByteCandidates;
        public long Decoded;
        public long DecodeFailures;
        public long UnwrapFailures;
        public long WrappedUnreadable;   // values stored as blob reference / compressed whose payload could not be read at all
        public long OrphanBlobFiles;     // files in the blob folder no stored entry version refers to
        public long OrphanBlobUnreadable;
        public long OrphanBlobMatched;
        public readonly Dictionary<string, long> StoreVersions = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly Dictionary<string, long> StoreCandidates = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly Dictionary<string, StoreStats> Stores = new Dictionary<string, StoreStats>(StringComparer.Ordinal);
        public readonly TargetStats Target = new TargetStats();
        public SyncStateInfo SyncState;
    }

    public sealed class FinderResult
    {
        public string Status;           // "found", "found_in_other_conversation", "unreadable", "not_found"
        public readonly List<BodyGroup> Bodies = new List<BodyGroup>();
        public readonly List<Occurrence> OtherConversation = new List<Occurrence>();
        public readonly List<UnreadableItem> Unreadable = new List<UnreadableItem>();
        // Wrapped values (blob reference / compressed) that could not be read and whose key shows no relation to the
        // requested message or conversation. They might still hold anything; they are listed so nothing is silently dropped.
        public readonly List<UnreadableItem> UnreadableWrapped = new List<UnreadableItem>();
        public readonly List<UnreadableItem> UnmatchedRawHits = new List<UnreadableItem>();
        public readonly List<string> ClientMessageIds = new List<string>();
        public readonly List<string> Notes = new List<string>();
        public readonly SearchStats Stats = new SearchStats();
        // item|path of every object accepted as this message (body candidates and other-conversation matches):
        // the thread-wide search leaves these out so that nothing is reported twice
        public readonly HashSet<string> ConfirmedPaths = new HashSet<string>(StringComparer.Ordinal);
    }

    public sealed class MessageHistoryFinder
    {
        private sealed class Hit
        {
            public JsObject Object;
            public string Path;
            public string MatchKind;
        }

        private static readonly Regex Whitespace = new Regex(@"[\s ]+", RegexOptions.Compiled);

        private readonly IndexedDbReader _reader;
        private readonly MessageReference _reference;
        private readonly FinderResult _result = new FinderResult();
        private readonly List<Occurrence> _matched = new List<Occurrence>();
        private readonly HashSet<string> _emitted;      // item|path already reported (the result's ConfirmedPaths)
        private readonly HashSet<string> _unreadableKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _clientIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _targetCounted = new HashSet<string>(StringComparer.Ordinal);
        private ulong _syncStateSequence;
        private List<byte[]> _needles;
        private int _stage;
        private readonly Action<string> _progress;

        public MessageHistoryFinder(IndexedDbReader reader, MessageReference reference, Action<string> progress)
        {
            _reader = reader;
            _reference = reference;
            _emitted = _result.ConfirmedPaths;
            _progress = progress ?? delegate(string s) { };
        }

        public FinderResult Search()
        {
            _reader.LoadSchema();
            _progress("スキーマ読取: データベース " + _reader.Databases.Count.ToString(CultureInfo.InvariantCulture) + " 件、LevelDB レコード " + _reader.TotalRecords.ToString(CultureInfo.InvariantCulture) + " 件");

            _stage = 1;
            _needles = MakeNeedles(new string[] { _reference.MessageId });
            foreach (IdbDataItem item in _reader.ReadDataItems()) Process(item);
            _progress("第1段階（メッセージIDで照合）: 一致 " + _matched.Count.ToString(CultureInfo.InvariantCulture) + " 件");

            foreach (Occurrence o in _matched)
            {
                string cid = o.Fields.GetString("clientMessageId");
                if (!string.IsNullOrEmpty(cid) && cid != _reference.MessageId) _clientIds.Add(cid);
            }
            if (_clientIds.Count > 0)
            {
                _stage = 2;
                _result.ClientMessageIds.AddRange(_clientIds);
                List<byte[]> needles = MakeNeedles(_clientIds);
                needles.AddRange(_needles);
                _needles = needles;
                int before = _matched.Count;
                foreach (IdbDataItem item in _reader.ReadDataItems()) Process(item);
                _progress("第2段階（clientMessageId で照合）: 追加 " + (_matched.Count - before).ToString(CultureInfo.InvariantCulture) + " 件");
            }

            ScanOrphanBlobs();
            BuildGroups();
            return _result;
        }

        /// <summary>Blob files that no stored entry version refers to any more. Such a file is a complete wrapped record value
        /// written by Chromium, so it can be read on its own; only its storage order is unknown.</summary>
        private void ScanOrphanBlobs()
        {
            List<BlobFileEntry> files = _reader.ListBlobFiles();
            if (files.Count == 0) return;
            HashSet<string> referenced = _reader.ReferencedBlobs();
            SearchStats stats = _result.Stats;
            foreach (BlobFileEntry file in files)
            {
                // blob numbers are per database: only the pair (database id, blob number) identifies a reference
                if (file.DatabaseId >= 0 && file.BlobNumber >= 0 && referenced.Contains(IndexedDbReader.BlobRef(file.DatabaseId, file.BlobNumber))) continue;
                stats.OrphanBlobFiles++;
                string fullPath = file.FullPath;
                byte[] bytes;
                try
                {
                    FileInfo fi = new FileInfo(fullPath);
                    if (fi.Length > 64L * 1024 * 1024) { stats.OrphanBlobUnreadable++; continue; }
                    bytes = File.ReadAllBytes(fullPath);
                }
                catch (Exception)
                {
                    stats.OrphanBlobUnreadable++;
                    continue;
                }
                UnwrapResult unwrap = _reader.UnwrapStandalone(bytes);
                if (unwrap.Error != null)
                {
                    stats.OrphanBlobUnreadable++;
                    continue;
                }
                string which;
                if (!ContainsNeedle(unwrap.Ssv, unwrap.Ssv.Length, out which)) continue;
                object value;
                try
                {
                    ulong version;
                    value = V8Deserializer.Deserialize(unwrap.Ssv, unwrap.SsvOffset, out version);
                }
                catch (Exception)
                {
                    stats.OrphanBlobUnreadable++;
                    continue;
                }
                List<Hit> hits = new List<Hit>();
                List<object> seen = new List<object>();
                Walk(value, "$", hits, seen, 0);
                if (hits.Count == 0) continue;
                IdbDataItem item = new IdbDataItem();
                LevelDbRecord source = new LevelDbRecord();
                source.FileName = "blob/" + file.RelativePath;
                source.FileKind = "blob";
                source.Sequence = 0;
                source.Offset = 0;
                item.Source = source;
                if (file.DatabaseId >= 0) item.DatabaseId = file.DatabaseId;
                unwrap.WasExternalBlob = true;
                unwrap.BlobPath = fullPath;
                unwrap.BlobNumber = file.BlobNumber;
                JsObject root = value as JsObject;
                foreach (Hit hit in hits)
                {
                    string emitKey = "o|" + file.RelativePath + "|" + hit.Path;
                    if (_emitted.Contains(emitKey)) continue;
                    _emitted.Add(emitKey);
                    Occurrence o = MakeOccurrence(item, unwrap, hit, root, "(孤立 blob)");
                    o.SourceKind = "orphan-blob";
                    o.KeyDisplay = "(参照の無い blob ファイル)";
                    stats.OrphanBlobMatched++;
                    if (o.ConversationMatch == "mismatch") _result.OtherConversation.Add(o);
                    else _matched.Add(o);
                }
            }
        }

        private static List<byte[]> MakeNeedles(IEnumerable<string> values)
        {
            List<byte[]> needles = new List<byte[]>();
            foreach (string v in values)
            {
                if (string.IsNullOrEmpty(v)) continue;
                needles.Add(Encoding.UTF8.GetBytes(v));
                needles.Add(Encoding.Unicode.GetBytes(v));
            }
            return needles;
        }

        private bool ContainsNeedle(byte[] data, int count, out string which)
        {
            which = null;
            if (data == null) return false;
            for (int i = 0; i < _needles.Count; i++)
            {
                if (ByteSearch.Contains(data, count, _needles[i]))
                {
                    which = (i % 2 == 0) ? "utf-8" : "utf-16le";
                    return true;
                }
            }
            return false;
        }

        public static string ItemKey(IdbDataItem item)
        {
            return (item.IsUndoLog ? "u|" : "r|") + item.Source.FileName + "|" + item.Source.Sequence.ToString(CultureInfo.InvariantCulture) + "|" + item.UndoSequence.ToString(CultureInfo.InvariantCulture);
        }

        private void Process(IdbDataItem item)
        {
            SearchStats stats = _result.Stats;
            if (_stage == 1)
            {
                stats.ItemsScanned++;
                if (item.IsUndoLog) stats.UndoEntries++; else stats.DataVersions++;
                if (item.IsDeletion) stats.DeletionMarkers++;
                if (!item.IsUndoLog)
                {
                    Count(stats.StoreVersions, item);
                    CountStore(stats, item);
                }
                CaptureSyncState(item);
            }
            CountTarget(stats, item);
            if (item.IsDeletion || item.RawValue == null) return;
            string key = ItemKey(item);
            if (_unreadableKeys.Contains(key)) return;

            UnwrapResult unwrap = _reader.Unwrap(item);
            string which;
            if (unwrap.Error != null)
            {
                if (unwrap.IsWrapped)
                {
                    // The record only holds a reference (external blob) or a compressed payload, so the message id cannot be
                    // seen in it. Never drop such a version silently: report it, as a candidate when the key points at the
                    // requested message or conversation, otherwise in the separate list of unreadable wrapped values.
                    stats.UnwrapFailures++;
                    stats.WrappedUnreadable++;
                    bool related = KeyRelated(item);
                    AddUnreadable(item, "包まれた値（" + (unwrap.WasExternalBlob ? "外部 blob" : "圧縮") + "）が読めず、本文を確認できません: " + unwrap.Error,
                        "(payload not visible)", related ? _result.Unreadable : _result.UnreadableWrapped);
                    _unreadableKeys.Add(key);
                    return;
                }
                if (ContainsNeedle(item.RawValue, item.RawValue.Length, out which))
                {
                    stats.UnwrapFailures++;
                    AddUnreadable(item, unwrap.Error, which, _result.Unreadable);
                    _unreadableKeys.Add(key);
                }
                return;
            }
            if (!ContainsNeedle(unwrap.Ssv, unwrap.Ssv.Length, out which)) return;
            if (_stage == 1) { stats.ByteCandidates++; if (!item.IsUndoLog) Count(stats.StoreCandidates, item); }

            object value;
            try
            {
                ulong version;
                value = V8Deserializer.Deserialize(unwrap.Ssv, unwrap.SsvOffset, out version);
                if (_stage == 1) stats.Decoded++;
            }
            catch (Exception ex)
            {
                stats.DecodeFailures++;
                AddUnreadable(item, "復号できませんでした: " + ex.Message, which, _result.Unreadable);
                _unreadableKeys.Add(key);
                return;
            }

            List<Hit> hits = new List<Hit>();
            List<object> seen = new List<object>();
            Walk(value, "$", hits, seen, 0);
            if (hits.Count == 0)
            {
                if (_stage == 1) AddUnreadable(item, "IDの文字列を含みますが、メッセージの本文オブジェクトとしては一致しません（ID が別の項目に現れているだけの可能性）", which, _result.UnmatchedRawHits);
                return;
            }
            JsObject root = value as JsObject;
            string storeName = _reader.StoreName(item.DatabaseId, item.StoreId);
            foreach (Hit hit in hits)
            {
                string emitKey = key + "|" + hit.Path;
                if (_emitted.Contains(emitKey)) continue;
                _emitted.Add(emitKey);
                Occurrence o = MakeOccurrence(item, unwrap, hit, root, storeName);
                if (o.ConversationMatch == "mismatch") _result.OtherConversation.Add(o);
                else _matched.Add(o);
            }
        }

        private void CountStore(SearchStats stats, IdbDataItem item)
        {
            string name = _reader.DatabaseName(item.DatabaseId) + " / " + _reader.StoreName(item.DatabaseId, item.StoreId);
            StoreStats s;
            if (!stats.Stores.TryGetValue(name, out s))
            {
                s = new StoreStats();
                stats.Stores[name] = s;
            }
            s.Versions++;
            if (item.IsDeletion) s.Deletions++;
            if (item.Key != null && s.KeySet.Add(Hex.ToHex(item.Key.Raw))) s.Keys = s.KeySet.Count;
        }

        /// <summary>True when the record key names the requested message, its parent post or a known client id (not the conversation alone).</summary>
        private bool KeyNamesMessage(IdbDataItem item)
        {
            if (item.Key == null) return false;
            foreach (string part in item.Key.StringParts())
            {
                if (part == null) continue;
                if (IdMatches(part)) return true;
                if (!string.IsNullOrEmpty(_reference.ParentMessageId) && string.Equals(part, _reference.ParentMessageId, StringComparison.Ordinal)) return true;
                if (ClientIdMatches(part)) return true;
                foreach (string cid in _clientIds) if (part.EndsWith("_" + cid, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private void CountTarget(SearchStats stats, IdbDataItem item)
        {
            if (!KeyNamesMessage(item)) return;
            string key = ItemKey(item);
            if (!_targetCounted.Add(key)) return;
            TargetStats t = stats.Target;
            if (item.IsUndoLog)
            {
                t.UndoEntries++;
            }
            else
            {
                t.RecordVersions++;
                if (item.IsDeletion) t.Deletions++;
                if (item.Source.FileKind == "log") t.InLog++; else t.InLdb++;
            }
            if (item.Source.Sequence < t.MinSequence) t.MinSequence = item.Source.Sequence;
            if (item.Source.Sequence > t.MaxSequence) t.MaxSequence = item.Source.Sequence;
        }

        /// <summary>Keep the newest live sync-state record of the requested conversation (numbers and flags only).</summary>
        private void CaptureSyncState(IdbDataItem item)
        {
            if (item.IsUndoLog || item.IsDeletion || item.Key == null || item.RawValue == null) return;
            if (string.IsNullOrEmpty(_reference.ConversationId)) return;
            if (!string.Equals(_reader.StoreName(item.DatabaseId, item.StoreId), "syncstates", StringComparison.Ordinal)) return;
            if (_reader.DatabaseName(item.DatabaseId).IndexOf("syncstate-manager", StringComparison.Ordinal) < 0) return;
            if (!string.Equals(item.Key.Display(), _reference.ConversationId, StringComparison.OrdinalIgnoreCase)) return;
            if (_result.Stats.SyncState != null && item.Source.Sequence <= _syncStateSequence) return;
            UnwrapResult unwrap = _reader.Unwrap(item);
            if (unwrap.Error != null) return;
            object value;
            try
            {
                ulong version;
                value = V8Deserializer.Deserialize(unwrap.Ssv, unwrap.SsvOffset, out version);
            }
            catch (Exception)
            {
                return;
            }
            JsObject obj = value as JsObject;
            if (obj == null) return;
            SyncStateInfo info = new SyncStateInfo();
            info.Found = true;
            info.WindowStart = MillisField(obj, "chatServiceStartOfSyncWindow");
            info.WindowEnd = MillisField(obj, "chatServiceEndOfSyncWindow");
            info.LastSyncTime = MillisField(obj, "lastSyncTime");
            info.SyncedToStartOfTime = BoolField(obj, "_isSyncedToStartOfTime");
            info.GapDetected = BoolField(obj, "isGapDetected");
            info.MessageStale = BoolField(obj, "isMessageStale");
            info.IsChannel = BoolField(obj, "isChannel");
            info.HasErrorRecord = Prop(obj, "errorCode") != null;
            List<object> resets = Prop(obj, "syncStateResetHistory") as List<object>;
            if (resets != null) info.ResetCount = resets.Count;
            _result.Stats.SyncState = info;
            _syncStateSequence = item.Source.Sequence;
        }

        private static DateTime? MillisField(JsObject obj, string name)
        {
            string text = ValueText(Prop(obj, name));
            double millis;
            if (text == null || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out millis)) return null;
            if (millis < 946684800000.0 || millis > 4102444800000.0) return null; // sentinels (946684800, 8640000000000000) and nonsense
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(millis);
        }

        private static bool? BoolField(JsObject obj, string name)
        {
            object v = Prop(obj, name);
            if (v is bool) return (bool)v;
            return null;
        }

        /// <summary>True when the record key names the requested message, its parent post, a known client id, or the conversation.</summary>
        private bool KeyRelated(IdbDataItem item)
        {
            if (item.Key == null) return false;
            foreach (string part in item.Key.StringParts())
            {
                if (part == null) continue;
                if (IdMatches(part)) return true;
                if (!string.IsNullOrEmpty(_reference.ParentMessageId) && string.Equals(part, _reference.ParentMessageId, StringComparison.Ordinal)) return true;
                if (ClientIdMatches(part)) return true;
                foreach (string cid in _clientIds) if (part.EndsWith("_" + cid, StringComparison.Ordinal)) return true;
                if (!string.IsNullOrEmpty(_reference.ConversationId) && string.Equals(part, _reference.ConversationId, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private void Count(Dictionary<string, long> table, IdbDataItem item)
        {
            string name = _reader.DatabaseName(item.DatabaseId) + " / " + _reader.StoreName(item.DatabaseId, item.StoreId);
            long n;
            table.TryGetValue(name, out n);
            table[name] = n + 1;
        }

        private void AddUnreadable(IdbDataItem item, string reason, string needle, List<UnreadableItem> target)
        {
            UnreadableItem u = new UnreadableItem();
            u.SourceKind = item.IsUndoLog ? "undo-log" : "record";
            u.Database = _reader.DatabaseName(item.DatabaseId);
            u.Store = _reader.StoreName(item.DatabaseId, item.StoreId);
            u.KeyDisplay = item.Key != null ? item.Key.Display() : "(key unreadable)";
            u.Sequence = item.Source.Sequence;
            u.File = item.Source.FileName;
            u.Offset = item.Source.Offset;
            u.Reason = reason;
            u.Needle = needle;
            target.Add(u);
        }

        // ------------------------------------------------------------ structural matching

        private bool IdMatches(string candidate)
        {
            return candidate != null && string.Equals(candidate, _reference.MessageId, StringComparison.Ordinal);
        }

        private bool ClientIdMatches(string candidate)
        {
            return candidate != null && _clientIds.Contains(candidate);
        }

        private bool MapKeyMatches(string key)
        {
            if (IdMatches(key)) return true;
            foreach (string cid in _clientIds)
            {
                if (key.EndsWith("_" + cid, StringComparison.Ordinal) || key == cid) return true;
            }
            return false;
        }

        private static string ValueText(object value)
        {
            if (value == null || value is V8Undefined) return null;
            if (value is string) return (string)value;
            if (value is double)
            {
                double d = (double)value;
                if (d == Math.Floor(d) && Math.Abs(d) < 9007199254740992.0) return ((long)d).ToString(CultureInfo.InvariantCulture);
                return d.ToString("R", CultureInfo.InvariantCulture);
            }
            if (value is long || value is int || value is ulong) return Convert.ToString(value, CultureInfo.InvariantCulture);
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is V8Date) return value.ToString();
            return null;
        }

        private static object Prop(JsObject obj, params string[] names)
        {
            foreach (string name in names)
            {
                object v;
                if (obj.Properties.TryGet(name, out v) && !(v is V8Undefined)) return v;
            }
            return null;
        }

        private static bool LooksLikeMessage(JsObject obj)
        {
            return obj.Properties.ContainsKey("content") || obj.Properties.ContainsKey("clientMessageId") || obj.Properties.ContainsKey("clientmessageid")
                || obj.Properties.ContainsKey("messageType") || obj.Properties.ContainsKey("messagetype") || obj.Properties.ContainsKey("preview");
        }

        private void Walk(object node, string path, List<Hit> hits, List<object> seen, int depth)
        {
            if (depth > 64) return;
            JsObject obj = node as JsObject;
            if (obj != null)
            {
                object mapValue;
                if (obj.Properties.TryGet("messageMap", out mapValue) && mapValue is JsObject)
                {
                    foreach (KeyValuePair<string, object> pair in ((JsObject)mapValue).Properties)
                    {
                        JsObject msg = pair.Value as JsObject;
                        if (msg != null && MapKeyMatches(pair.Key) && !ContainsRef(seen, msg))
                        {
                            seen.Add(msg);
                            Hit h = new Hit();
                            h.Object = msg;
                            h.Path = path + ".messageMap." + pair.Key;
                            h.MatchKind = IdMatches(pair.Key) ? "messageMap-key" : "clientMessageId";
                            hits.Add(h);
                        }
                    }
                }
                if (LooksLikeMessage(obj) && !ContainsRef(seen, obj))
                {
                    string id = ValueText(Prop(obj, "id"));
                    string clientId = ValueText(Prop(obj, "clientMessageId", "clientmessageid"));
                    string kind = null;
                    if (IdMatches(id)) kind = "id";
                    else if (ClientIdMatches(clientId)) kind = "clientMessageId";
                    if (kind != null)
                    {
                        seen.Add(obj);
                        Hit h = new Hit();
                        h.Object = obj;
                        h.Path = path;
                        h.MatchKind = kind;
                        hits.Add(h);
                        AddNestedBodies(obj, path, hits, seen);
                    }
                }
                foreach (KeyValuePair<string, object> pair in obj.Properties)
                {
                    if (pair.Value is JsObject || pair.Value is List<object>) Walk(pair.Value, path + "." + pair.Key, hits, seen, depth + 1);
                }
                return;
            }
            List<object> list = node as List<object>;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is JsObject || list[i] is List<object>) Walk(list[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", hits, seen, depth + 1);
                }
            }
        }

        /// <summary>Objects nested directly inside a matched message that carry their own "content" but no id of their own
        /// (for example originalNonLieMessage, the pre-edit copy the client keeps while an edit is in flight).
        /// They belong to the same message, so their body must not be dropped.</summary>
        private static void AddNestedBodies(JsObject message, string path, List<Hit> hits, List<object> seen)
        {
            foreach (KeyValuePair<string, object> pair in message.Properties)
            {
                JsObject nested = pair.Value as JsObject;
                if (nested == null || ContainsRef(seen, nested)) continue;
                object content;
                if (!nested.Properties.TryGet("content", out content) || !(content is string)) continue;
                if (nested.Properties.ContainsKey("id") || nested.Properties.ContainsKey("clientMessageId") || nested.Properties.ContainsKey("clientmessageid")) continue;
                seen.Add(nested);
                Hit h = new Hit();
                h.Object = nested;
                h.Path = path + "." + pair.Key;
                h.MatchKind = "nested";
                hits.Add(h);
            }
        }

        private static bool ContainsRef(List<object> seen, object candidate)
        {
            foreach (object o in seen) if (ReferenceEquals(o, candidate)) return true;
            return false;
        }

        private static readonly string[] MessageFieldNames = new string[]
        {
            "id", "clientMessageId", "clientmessageid", "version", "type", "messageType", "messagetype", "contentType", "contenttype",
            "composetime", "originalArrivalTime", "originalarrivaltime", "clientArrivalTime", "imDisplayName", "imdisplayname",
            "creator", "from", "fromUserId", "isSentByCurrentUser", "state", "lieType", "parentMessageId", "conversationId", "conversationid",
            "conversationLink", "skypeeditedid", "contentHash", "deletionInfo", "sequenceId", "threadType", "threadtype", "preview", "dedupeKey"
        };

        private static readonly string[] PropertyFieldNames = new string[]
        {
            "edittime", "deletetime", "composetime", "subject", "title", "importance", "files", "formatVariant", "onbehalfof"
        };

        private Occurrence MakeOccurrence(IdbDataItem item, UnwrapResult unwrap, Hit hit, JsObject root, string storeName)
        {
            Occurrence o = new Occurrence();
            o.SourceKind = item.IsUndoLog ? "undo-log" : "record";
            o.MatchKind = hit.MatchKind;
            o.Database = _reader.DatabaseName(item.DatabaseId);
            o.Store = storeName;
            o.KeyDisplay = item.Key != null ? item.Key.Display() : "(key unreadable)";
            o.Sequence = item.Source.Sequence;
            o.File = item.Source.FileName;
            o.Offset = item.Source.Offset;
            o.Live = !item.Source.IsDeletion;
            o.FromCompressedBlock = item.Source.FromCompressedBlock;
            o.UndoScope = item.UndoScope;
            o.UndoSequence = item.UndoSequence;
            o.Path = hit.Path;
            o.WasExternalBlob = unwrap.WasExternalBlob;
            o.WasSnappy = unwrap.WasSnappy;
            o.BlobPath = unwrap.BlobPath;
            o.BlobEntrySequence = unwrap.BlobEntrySequence;
            o.BlobNumber = unwrap.BlobNumber;
            o.BlobEntryFromUndoLog = unwrap.BlobEntryFromUndoLog;

            JsObject msg = hit.Object;
            object content;
            if (msg.Properties.TryGet("content", out content))
            {
                o.HasContentField = true;
                o.ContentHtml = content as string;
                if (o.ContentHtml == null && content != null && !(content is V8Undefined)) o.ContentHtml = ValueText(content);
            }

            foreach (string name in MessageFieldNames)
            {
                object v;
                if (msg.Properties.TryGet(name, out v) && !(v is V8Undefined) && v != null)
                {
                    string text = ValueText(v);
                    if (text != null) SetField(o.Fields, name, text);
                    else if (v is JsObject) SetField(o.Fields, name, "(object)");
                    else if (v is List<object>) SetField(o.Fields, name, "(array)");
                }
            }
            object properties = Prop(msg, "properties");
            JsObject props = properties as JsObject;
            if (props != null)
            {
                foreach (string name in PropertyFieldNames)
                {
                    object v;
                    if (props.Properties.TryGet(name, out v) && !(v is V8Undefined) && v != null)
                    {
                        string text = ValueText(v);
                        if (text != null) SetField(o.Fields, "properties." + name, text);
                        else if (v is JsObject) SetField(o.Fields, "properties." + name, "(object)");
                        else if (v is List<object>) SetField(o.Fields, "properties." + name, "(array)");
                    }
                }
            }
            else if (properties is string)
            {
                SetField(o.Fields, "properties", (string)properties);
            }
            if (o.Fields.GetString("clientmessageid") != null && o.Fields.GetString("clientMessageId") == null) o.Fields.Set("clientMessageId", o.Fields.GetString("clientmessageid"));
            AddIso(o.Fields, "properties.edittime");
            AddIso(o.Fields, "properties.deletetime");
            AddIso(o.Fields, "originalArrivalTime");
            AddIso(o.Fields, "originalarrivaltime");
            AddIso(o.Fields, "clientArrivalTime");
            AddIso(o.Fields, "version");

            if (root != null)
            {
                foreach (string name in new string[] { "conversationId", "replyChainId", "parentMessageVersion", "latestDeliveryTime", "originalArrivalTime", "id", "lastMessageTimeUtc" })
                {
                    object v;
                    if (root.Properties.TryGet(name, out v) && !(v is V8Undefined) && v != null)
                    {
                        string text = ValueText(v);
                        if (text != null) o.RecordFields.Set(name, text);
                    }
                }
            }

            o.ConversationIdInRecord = FindConversationId(msg, root, storeName, item.Key);
            if (string.IsNullOrEmpty(_reference.ConversationId)) o.ConversationMatch = "unchecked";
            else if (o.ConversationIdInRecord == null) o.ConversationMatch = "unknown";
            else if (string.Equals(o.ConversationIdInRecord, _reference.ConversationId, StringComparison.OrdinalIgnoreCase)) o.ConversationMatch = "match";
            else o.ConversationMatch = "mismatch";
            return o;
        }

        private static void SetField(OrderedMap fields, string name, string value)
        {
            if (value.Length > 2000) value = value.Substring(0, 2000) + "...(" + value.Length.ToString(CultureInfo.InvariantCulture) + " chars)";
            fields.Set(name, value);
        }

        private static void AddIso(OrderedMap fields, string name)
        {
            string value = fields.GetString(name);
            if (value == null) return;
            string iso = TimeText.MillisToIso(value);
            if (iso != null) fields.Set(name + "Iso", iso);
        }

        private static string FindConversationId(JsObject msg, JsObject root, string storeName, IdbKey key)
        {
            string own = ValueText(Prop(msg, "conversationId", "conversationid"));
            if (!string.IsNullOrEmpty(own)) return own;
            string link = ValueText(Prop(msg, "conversationLink"));
            if (!string.IsNullOrEmpty(link))
            {
                int idx = link.IndexOf("/conversations/", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string rest = link.Substring(idx + "/conversations/".Length);
                    int end = rest.IndexOfAny(new char[] { '/', '?', ';' });
                    return Uri.UnescapeDataString(end >= 0 ? rest.Substring(0, end) : rest);
                }
            }
            if (root != null)
            {
                string rootConv = ValueText(Prop(root, "conversationId", "conversationid"));
                if (!string.IsNullOrEmpty(rootConv)) return rootConv;
                if (string.Equals(storeName, "conversations", StringComparison.OrdinalIgnoreCase))
                {
                    string rootId = ValueText(Prop(root, "id"));
                    if (!string.IsNullOrEmpty(rootId)) return rootId;
                }
            }
            if (key != null)
            {
                foreach (string part in key.StringParts())
                {
                    if (part.IndexOf(':') > 0 && (part.IndexOf('@') > 0 || part.StartsWith("48:", StringComparison.Ordinal))) return part;
                }
            }
            return null;
        }

        // ------------------------------------------------------------ grouping

        public static string NormalizeText(string text)
        {
            if (text == null) return "";
            return Whitespace.Replace(text, " ").Trim();
        }

        private void BuildGroups()
        {
            Dictionary<string, BodyGroup> groups = new Dictionary<string, BodyGroup>(StringComparer.Ordinal);
            List<BodyGroup> ordered = new List<BodyGroup>();
            foreach (Occurrence o in _matched)
            {
                string text = o.ContentHtml != null ? HtmlText.ToPlainText(o.ContentHtml) : null;
                string groupKey = o.ContentHtml != null ? "t|" + NormalizeText(text) : "\0(no content)";
                BodyGroup g;
                if (!groups.TryGetValue(groupKey, out g))
                {
                    g = new BodyGroup();
                    g.HasContent = o.ContentHtml != null;
                    g.ContentText = text;
                    g.TextSha256 = text != null ? Sha256Util.HexOfString(NormalizeText(text)) : null;
                    g.FirstSequence = o.Sequence;
                    g.LastSequence = o.Sequence;
                    groups[groupKey] = g;
                    ordered.Add(g);
                }
                g.Occurrences.Add(o);
                if (o.Sequence < g.FirstSequence) g.FirstSequence = o.Sequence;
                if (o.Sequence > g.LastSequence) g.LastSequence = o.Sequence;
                BodyVariant variant = null;
                foreach (BodyVariant v in g.Variants) if (string.Equals(v.ContentHtml, o.ContentHtml, StringComparison.Ordinal)) { variant = v; break; }
                if (variant == null)
                {
                    variant = new BodyVariant();
                    variant.ContentHtml = o.ContentHtml;
                    variant.Sha256 = o.ContentHtml != null ? Sha256Util.HexOfString(o.ContentHtml) : null;
                    variant.FirstSequence = o.Sequence;
                    variant.LastSequence = o.Sequence;
                    g.Variants.Add(variant);
                }
                variant.Occurrences.Add(o);
                if (o.Sequence < variant.FirstSequence) variant.FirstSequence = o.Sequence;
                if (o.Sequence > variant.LastSequence) variant.LastSequence = o.Sequence;
            }
            // Storage order: orphan blob files have no sequence, so a group that only has them sorts last.
            ordered.Sort(delegate(BodyGroup a, BodyGroup b) { return OrderKey(a).CompareTo(OrderKey(b)); });
            int index = 1;
            foreach (BodyGroup g in ordered)
            {
                g.Index = index++;
                g.Occurrences.Sort(CompareOccurrences);
                g.Variants.Sort(delegate(BodyVariant a, BodyVariant b) { return a.FirstSequence.CompareTo(b.FirstSequence); });
                int vi = 1;
                foreach (BodyVariant v in g.Variants)
                {
                    v.Index = vi++;
                    v.Occurrences.Sort(CompareOccurrences);
                }
                _result.Bodies.Add(g);
            }
            _result.OtherConversation.Sort(CompareOccurrences);
            _result.Unreadable.Sort(delegate(UnreadableItem a, UnreadableItem b) { return a.Sequence.CompareTo(b.Sequence); });
            _result.UnreadableWrapped.Sort(delegate(UnreadableItem a, UnreadableItem b) { return a.Sequence.CompareTo(b.Sequence); });

            if (_result.Bodies.Count > 0) _result.Status = "found";
            else if (_result.OtherConversation.Count > 0) _result.Status = "found_in_other_conversation";
            else if (_result.Unreadable.Count > 0) _result.Status = "unreadable";
            else _result.Status = "not_found";

            _result.Notes.Add("保存順序（LevelDB sequence）は端末へ書き込まれた順で、編集日時ではありません。編集日時はレコード内の properties.edittime がある場合だけ分かります。");
            if (_result.Bodies.Count > 1) _result.Notes.Add("本文候補が複数あります。同じメッセージIDの異なる本文が端末に残っていたことを示します。どれが最終版かは、最新の生存レコードと properties.edittime を見て判断してください。");
            bool conversationUnchecked = false;
            bool unknown = false;
            foreach (BodyGroup g in _result.Bodies) foreach (Occurrence o in g.Occurrences)
            {
                if (o.ConversationMatch == "unchecked") conversationUnchecked = true;
                if (o.ConversationMatch == "unknown") unknown = true;
            }
            if (conversationUnchecked) _result.Notes.Add("会話IDが入力に無かったため、会話IDの照合はしていません。");
            if (unknown) _result.Notes.Add("会話IDを確認できない候補があります（レコードに会話IDが無い）。");
            if (_result.OtherConversation.Count > 0) _result.Notes.Add("別の会話IDを持つ同じメッセージIDの候補 " + _result.OtherConversation.Count.ToString(CultureInfo.InvariantCulture) + " 件は除外して別枠に示します。");
            if (_result.Unreadable.Count > 0) _result.Notes.Add("読めなかった版が " + _result.Unreadable.Count.ToString(CultureInfo.InvariantCulture) + " 件あります。その版の本文は不明で、他の版の本文を当てはめてはいません。");
            if (_result.UnreadableWrapped.Count > 0) _result.Notes.Add("外部 blob／圧縮に包まれて読めなかった値が " + _result.UnreadableWrapped.Count.ToString(CultureInfo.InvariantCulture) + " 件あります（キーからは対象との関係を判断できないもの。result.json に列挙）。");
        }

        private static ulong OrderKey(BodyGroup g)
        {
            ulong key = ulong.MaxValue;
            foreach (Occurrence o in g.Occurrences) if (o.SourceKind != "orphan-blob" && o.Sequence < key) key = o.Sequence;
            return key;
        }

        private static int CompareOccurrences(Occurrence a, Occurrence b)
        {
            // orphan blob files have no storage order: they go after everything that has a sequence
            bool ao = a.SourceKind == "orphan-blob", bo = b.SourceKind == "orphan-blob";
            if (ao != bo) return ao ? 1 : -1;
            int c = a.Sequence.CompareTo(b.Sequence);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.File, b.File);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Path, b.Path);
        }
    }
}
