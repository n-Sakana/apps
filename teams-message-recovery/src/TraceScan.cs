// Teams Message History - traces outside IndexedDB (user-level, read-only):
//   * Windows notification database (wpndatabase.db with its -wal/-shm): toast payloads of shown notifications
//   * the Teams WebView2 profile's HTTP cache (Cache\Cache_Data) and Service Worker CacheStorage
// The files are scanned as raw bytes for the message id, the parent post id and the conversation id
// (UTF-8 and UTF-16LE, plus URL-encoded conversation ids), including inside gzip members found in the
// files. Around each hit a bounded text window is decoded and the nearest displayable element (toast
// <text>, JSON "content", HTML <p>) is kept as an UNVERIFIED FRAGMENT in the private result folder:
// it is text found near the id, not proven to be this message's body. The screen shows counts only,
// including every limit that was hit and every file that was not scanned.
// Cache folders are additionally read by structure (CacheFormat.cs): every entry's key, response headers
// and body are separated, the body is decoded per Content-Encoding (gzip / deflate / br) and the decoded
// bytes are searched for the same ids. Entries no longer reachable from a blockfile index are read too and
// always marked "索引外". Unreadable entries, unsupported encodings and failed decodes stay as counts.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace TeamsMessageHistory
{
    public sealed class TraceSource
    {
        public string Kind;   // "wpn", "httpcache", "swcache", "custom"
        public string Label;
        public string Folder;
        public readonly List<string> Files = new List<string>();
        public long Bytes;
        public bool EnumerationFailed;
        public string EnumerationErrorKind;   // fixed label: アクセス拒否 / 見つからない / その他
        public string EnumerationErrorDetail; // private
    }

    public sealed class TraceHit
    {
        public string SourceKind;
        public string FileKind;      // db, wal, shm, entry, index, block, other, cachebody (decoded cache body)
        public string RelativePath;  // private detail
        public CacheEntry Entry;     // private: set when the hit is inside a decoded cache body (key, headers, location)
        public long Offset;          // offset of the hit (raw) or of the gzip member (gzip)
        public long OffsetInMember;  // offset inside the decompressed member (gzip only)
        public string NeedleKind;    // messageId, parentMessageId, conversationId
        public string Encoding;      // utf-8, utf-16le
        public bool FromGzip;
        public string ContextKind;   // toast, json, html, text
        public string Fragment;      // private: the element containing (or nearest to) the hit
        public int FragmentStartRelative;  // characters from the hit to the element start (negative = before)
        public int FragmentEndRelative;
        public int OtherElementsNearby;    // other elements of the same kind in the window (listed, never merged)
        public bool WindowTruncated;       // the decoded window hit the fixed radius
        public bool FragmentTruncated;     // the element text was cut to the length limit
        public string Window;              // private: the whole decoded window used for matching and selection
        public int WindowHitIndex;         // character index of the hit inside Window
        public readonly List<TraceElement> Elements = new List<TraceElement>(); // private: every element of the chosen kind in the window, in order
        // The JSON object that contains the hit (API responses, cached records): the record the id belongs to.
        // Adjacent records in the same response are never merged into it.
        public bool ObjectFound;
        public bool ObjectTruncated;       // the object did not close inside the object radius (record incomplete)
        public bool ObjectParsed;          // the object text parsed as JSON
        public string ObjectText;          // private: the record as stored; cut at ObjectTextLimit when ObjectTextCut is set
        public bool ObjectTextCut;         // ObjectText was cut at the limit (the fields above were taken from the whole record)
        public int ObjectLength;           // length of the whole record text as found
        public string ObjectId;            // its "id" field
        public bool ObjectIdIsTarget;      // ObjectId equals the message id searched
        public string ObjectContent;       // its "content" field as stored (usually HTML)
        public string ObjectContentText;   // plain text of it
        public string ObjectConversation;  // conversation id found in the object (conversationId / conversationid / conversationLink / threadId)
        public string ObjectEditTime;      // properties.edittime as stored (a field of the record, not a cache time)
        public string ObjectClientMessageId;
        public List<KeyValuePair<string, string>> ObjectTexts;   // private: every display text of the record (or of the toast) the hit is in: field, plain text
        public string ConversationMatch;   // match / mismatch / unknown / unchecked (against the conversation id of the input)
        public string Classification;      // candidate / other-conversation / id-elsewhere / nearby-only

        public bool IsTargetIdHit { get { return NeedleKind == "messageId"; } }
    }

    /// <summary>A word of the thread-wide search found in the bytes: the readable text around it (private).</summary>
    public sealed class TraceWordHit
    {
        public SearchTerm Term;
        public string SourceKind;
        public string FileKind;
        public string RelativePath;
        public CacheEntry Entry;
        public long Offset;
        public bool FromGzip;
        public string Encoding;      // utf-8, utf-16le, json-escape
        public bool JsonString;      // the text is the JSON string value the word sits in
        public string Text;
        public int MatchIndex;
    }

    public sealed class TraceElement
    {
        public string Text;
        public int StartRelative;
        public int EndRelative;
        public bool ContainsHit;
        public bool Truncated;
    }

    public sealed class TraceSourceResult
    {
        public TraceSource Source;
        public long FilesScanned;
        public long FilesTooLarge;
        public long FilesReadFailed;
        public long BytesScanned;
        public long GzipMembers;
        public long GzipFailures;
        public long GzipMemberCapReached;   // files where more gzip members may exist than were tried
        public long GzipOutputTruncated;    // members whose decompressed output was cut at the limit
        public long HitCapReached;          // buffers where more hits of a needle may exist than were kept
        public readonly Dictionary<string, long> FilesByKind = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly Dictionary<string, long> HitsByNeedle = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly Dictionary<string, long> FragmentsByKind = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly List<TraceHit> Hits = new List<TraceHit>();
        public readonly List<TraceWordHit> WordHits = new List<TraceWordHit>();   // thread-wide search: words, never ids
        public long WordHitCapReached;      // buffers where more occurrences of a word may exist than were kept
        public readonly List<KeyValuePair<string, string>> SkippedFiles = new List<KeyValuePair<string, string>>(); // private: relative path, reason
        public readonly CacheStats Cache = new CacheStats();   // structural pass over cache folders (keys / headers / decoded bodies)
        public long DistinctFragments;
        public string Error;

        public long FilesUnscanned { get { return FilesTooLarge + FilesReadFailed; } }
        public bool AnyLimitHit { get { return GzipMemberCapReached > 0 || GzipOutputTruncated > 0 || HitCapReached > 0 || Cache.DecodeTruncated > 0 || Cache.DecodeSkippedCap > 0; } }
    }

    /// <summary>Reads a list of files ahead of their consumer on a few threads and hands them over in list order.
    /// Only the reading is concurrent: what is read, the size limit and the error reporting are the same as a
    /// plain loop. Memory is bounded (the file being waited for is always allowed through).
    /// TMH_READ_THREADS=1 reads one file at a time on the calling thread, as versions up to 0.1.6 did.</summary>
    public sealed class FileReadAhead : IDisposable
    {
        public struct Result
        {
            public byte[] Data;
            public bool TooLarge;
            public long Length;
            public string ErrorType;   // exception type name when the file could not be read
        }

        private const long BufferBudget = 256L * 1024 * 1024;   // bytes read but not yet taken

        private readonly List<string> _paths;
        private readonly long _maxBytes;
        private readonly object _gate = new object();
        private readonly Result[] _results;
        private readonly bool[] _ready;
        private readonly int _threads;
        private int _next;        // next index a worker will read
        private int _waitingFor;  // index the consumer is at
        private long _buffered;
        private bool _stop;

        public static int ThreadCount()
        {
            int n;
            string setting = Environment.GetEnvironmentVariable("TMH_READ_THREADS");
            if (!string.IsNullOrEmpty(setting) && int.TryParse(setting, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 1) return Math.Min(n, 32);
            return Math.Max(2, Math.Min(8, Environment.ProcessorCount));
        }

        public static string WorkerStageName()
        {
            return "    先読みスレッドが読取に使った時間の合計（最大 " + ThreadCount().ToString(CultureInfo.InvariantCulture) + " スレッド。上の行は走査側が待った時間）";
        }

        public FileReadAhead(List<string> paths, long maxBytes)
        {
            _paths = paths;
            _maxBytes = maxBytes;
            _results = new Result[paths.Count];
            _ready = new bool[paths.Count];
            _threads = Math.Min(ThreadCount(), Math.Max(1, paths.Count));
            if (_threads <= 1) return;
            for (int i = 0; i < _threads; i++)
            {
                System.Threading.Thread t = new System.Threading.Thread(Work);
                t.IsBackground = true;
                t.Name = "TMH read-ahead";
                t.Start();
            }
        }

        /// <summary>The same steps as before: size check, shared read-only open, read to the end.</summary>
        public static Result ReadOne(string path, long maxBytes)
        {
            Result result = new Result();
            try
            {
                FileInfo fi = new FileInfo(path);
                if (fi.Length > maxBytes)
                {
                    result.TooLarge = true;
                    result.Length = fi.Length;
                    return result;
                }
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
                {
                    byte[] data = new byte[fs.Length];
                    int read = 0;
                    while (read < data.Length)
                    {
                        int n = fs.Read(data, read, data.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    result.Data = data;
                    result.Length = data.Length;
                }
            }
            catch (Exception ex)
            {
                result.Data = null;
                result.ErrorType = ex.GetType().Name;
            }
            return result;
        }

        private void Work()
        {
            while (true)
            {
                int index;
                lock (_gate)
                {
                    // hold back while enough is buffered, unless the consumer is waiting for the very next file
                    while (!_stop && _next < _paths.Count && _buffered > BufferBudget && _next != _waitingFor) System.Threading.Monitor.Wait(_gate);
                    if (_stop || _next >= _paths.Count) return;
                    index = _next++;
                }
                long started = Timing.Start();
                Result result = ReadOne(_paths[index], _maxBytes);
                Timing.Stop(WorkerStageName(), started);
                lock (_gate)
                {
                    _results[index] = result;
                    _ready[index] = true;
                    if (result.Data != null) _buffered += result.Data.Length;
                    System.Threading.Monitor.PulseAll(_gate);
                }
            }
        }

        /// <summary>The file at this index; indexes must be taken in increasing order.</summary>
        public Result Take(int index)
        {
            if (_threads <= 1) return ReadOne(_paths[index], _maxBytes);
            lock (_gate)
            {
                _waitingFor = index;
                System.Threading.Monitor.PulseAll(_gate);
                while (!_ready[index]) System.Threading.Monitor.Wait(_gate);
                Result result = _results[index];
                _results[index] = new Result();
                if (result.Data != null) _buffered -= result.Data.Length;
                _waitingFor = index + 1;
                System.Threading.Monitor.PulseAll(_gate);
                return result;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _stop = true;
                System.Threading.Monitor.PulseAll(_gate);
            }
        }
    }

    public static class TraceScanner
    {
        public const long MaxFileBytes = 256L * 1024 * 1024;
        public const int MaxHitsPerNeedlePerBuffer = 50;
        public const int MaxGzipMembersPerFile = 500;
        public const int MaxGzipOutput = 32 * 1024 * 1024;
        public const int ContextRadius = 2048;
        public const int ObjectRadius = 65536;     // bytes around a hit searched for the enclosing JSON object
        public const int ObjectTextLimit = 20000;  // characters of the object text kept in the private record
        public const int FragmentLimit = 2000;
        public const long MaxTotalDecodedBytes = 2L * 1024 * 1024 * 1024;   // decoded cache bodies per source
        private const int MaxColumns = 96;

        private sealed class Needle
        {
            public string Kind;
            public string Encoding;
            public byte[] Bytes;
        }

        // ---------------------------------------------------------------- sources

        public sealed class Discovery
        {
            public bool LocalAppDataMissing;
            public bool NotificationFolderMissing;
            public int TeamsProfiles;
            public int EnumerationFailures;
        }

        public static List<TraceSource> FindStandardSources(Discovery discovery)
        {
            List<TraceSource> sources = new List<TraceSource>();
            string localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrEmpty(localAppData))
            {
                discovery.LocalAppDataMissing = true;
                return sources;
            }
            TraceSource wpnSource = FindNotificationSource(discovery);
            if (wpnSource != null) sources.Add(wpnSource);
            string packages = Path.Combine(localAppData, "Packages");
            if (Directory.Exists(packages))
            {
                string[] pkgs;
                try { pkgs = Directory.GetDirectories(packages, "MSTeams_*"); } catch (Exception) { pkgs = new string[0]; discovery.EnumerationFailures++; }
                Array.Sort(pkgs, StringComparer.OrdinalIgnoreCase);
                foreach (string pkg in pkgs)
                {
                    string ebWebView = Path.Combine(Path.Combine(Path.Combine(Path.Combine(pkg, "LocalCache"), "Microsoft"), "MSTeams"), "EBWebView");
                    if (!Directory.Exists(ebWebView)) continue;
                    string[] profiles;
                    try { profiles = Directory.GetDirectories(ebWebView, "WV2Profile_*"); } catch (Exception) { profiles = new string[0]; discovery.EnumerationFailures++; }
                    Array.Sort(profiles, StringComparer.OrdinalIgnoreCase);
                    foreach (string profile in profiles)
                    {
                        discovery.TeamsProfiles++;
                        string cache = Path.Combine(Path.Combine(profile, "Cache"), "Cache_Data");
                        if (Directory.Exists(cache)) sources.Add(FromFolder(cache, "httpcache", "HTTP キャッシュ"));
                        string sw = Path.Combine(Path.Combine(profile, "Service Worker"), "CacheStorage");
                        if (Directory.Exists(sw)) sources.Add(FromFolder(sw, "swcache", "Service Worker キャッシュ"));
                    }
                }
            }
            foreach (TraceSource s in sources) if (s.EnumerationFailed) discovery.EnumerationFailures++;
            return sources;
        }

        /// <summary>The Windows notification database of this user (standard location only). Null when absent.</summary>
        public static TraceSource FindNotificationSource(Discovery discovery)
        {
            string localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrEmpty(localAppData)) { discovery.LocalAppDataMissing = true; return null; }
            string wpn = Path.Combine(Path.Combine(Path.Combine(localAppData, "Microsoft"), "Windows"), "Notifications");
            if (!Directory.Exists(wpn)) { discovery.NotificationFolderMissing = true; return null; }
            TraceSource s = new TraceSource();
            s.Kind = "wpn";
            s.Label = "Windows 通知データベース";
            s.Folder = wpn;
            foreach (string name in new string[] { "wpndatabase.db", "wpndatabase.db-wal", "wpndatabase.db-shm" })
            {
                string p = Path.Combine(wpn, name);
                if (File.Exists(p)) AddFile(s, p);
            }
            if (s.Files.Count > 0) return s;
            discovery.NotificationFolderMissing = true;
            return null;
        }

        /// <summary>The cache folders that belong to the same browser profile as an IndexedDB LevelDB folder
        /// (profile\IndexedDB\&lt;origin&gt;.indexeddb.leveldb → profile\Cache\Cache_Data and profile\Service Worker\CacheStorage).
        /// Only that profile is looked at: analysing a captured copy never pulls in a running profile.</summary>
        public static List<TraceSource> ProfileCacheSources(string levelDbPath, out string profileFolder)
        {
            List<TraceSource> sources = new List<TraceSource>();
            profileFolder = null;
            if (string.IsNullOrEmpty(levelDbPath)) return sources;
            string trimmed = levelDbPath.TrimEnd('\\', '/');
            string indexedDb = Path.GetDirectoryName(trimmed);
            if (indexedDb == null || !string.Equals(Path.GetFileName(indexedDb), "IndexedDB", StringComparison.OrdinalIgnoreCase)) return sources;
            string profile = Path.GetDirectoryName(indexedDb);
            if (profile == null) return sources;
            profileFolder = profile;
            string cache = Path.Combine(Path.Combine(profile, "Cache"), "Cache_Data");
            if (Directory.Exists(cache)) sources.Add(FromFolder(cache, "httpcache", "HTTP キャッシュ"));
            string sw = Path.Combine(Path.Combine(profile, "Service Worker"), "CacheStorage");
            if (Directory.Exists(sw)) sources.Add(FromFolder(sw, "swcache", "Service Worker キャッシュ"));
            return sources;
        }

        /// <summary>Trace sources inside a folder the user specified (a captured copy): the caches of the profile that
        /// contains it, cache folders and notification-database copies found underneath it (bounded depth). Nothing
        /// outside that folder is looked at.</summary>
        public static List<TraceSource> CaptureSources(string userPath)
        {
            List<TraceSource> sources = new List<TraceSource>();
            if (string.IsNullOrEmpty(userPath)) return sources;
            string path = userPath.Trim().Trim('"').TrimEnd('\\', '/');
            if (!Directory.Exists(path)) return sources;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string profile;
            foreach (TraceSource s in ProfileCacheSources(path, out profile)) if (seen.Add(s.Folder)) sources.Add(s);
            string name = Path.GetFileName(path);
            if (string.Equals(name, "IndexedDB", StringComparison.OrdinalIgnoreCase))
            {
                string parent = Path.GetDirectoryName(path);
                if (parent != null) foreach (TraceSource s in CacheFoldersUnder(parent, 0, 1)) if (seen.Add(s.Folder)) sources.Add(s);
            }
            foreach (TraceSource s in CacheFoldersUnder(path, 0, 5)) if (seen.Add(s.Folder)) sources.Add(s);
            return sources;
        }

        private static List<TraceSource> CacheFoldersUnder(string folder, int depth, int maxDepth)
        {
            List<TraceSource> sources = new List<TraceSource>();
            string name = Path.GetFileName(folder.TrimEnd('\\', '/'));
            if (string.Equals(name, "Cache_Data", StringComparison.OrdinalIgnoreCase)) { sources.Add(FromFolder(folder, "httpcache", "HTTP キャッシュ")); return sources; }
            if (string.Equals(name, "CacheStorage", StringComparison.OrdinalIgnoreCase)) { sources.Add(FromFolder(folder, "swcache", "Service Worker キャッシュ")); return sources; }
            if (depth == 0 && (BlockfileReader.HasIndex(folder) || HasSimpleEntries(folder))) { sources.Add(FromFolder(folder, "custom", "指定フォルダー")); return sources; }
            string wpnDb = Path.Combine(folder, "wpndatabase.db");
            if (File.Exists(wpnDb))
            {
                TraceSource s = new TraceSource();
                s.Kind = "wpn";
                s.Label = "通知データベース（コピー）";
                s.Folder = folder;
                foreach (string n in new string[] { "wpndatabase.db", "wpndatabase.db-wal", "wpndatabase.db-shm" })
                {
                    string p = Path.Combine(folder, n);
                    if (File.Exists(p)) AddFile(s, p);
                }
                sources.Add(s);
            }
            if (depth >= maxDepth) return sources;
            string[] children;
            try { children = Directory.GetDirectories(folder); } catch (Exception) { return sources; }
            Array.Sort(children, StringComparer.OrdinalIgnoreCase);
            foreach (string child in children)
            {
                string childName = Path.GetFileName(child);
                if (string.Equals(childName, "IndexedDB", StringComparison.OrdinalIgnoreCase) || childName.EndsWith(".leveldb", StringComparison.OrdinalIgnoreCase) || childName.EndsWith(".blob", StringComparison.OrdinalIgnoreCase)) continue;
                sources.AddRange(CacheFoldersUnder(child, depth + 1, maxDepth));
            }
            return sources;
        }

        private static bool HasSimpleEntries(string folder)
        {
            try
            {
                foreach (string f in Directory.GetFiles(folder)) if (SimpleCacheFormat.EntryFileName.IsMatch(Path.GetFileName(f))) return true;
            }
            catch (Exception) { }
            return false;
        }

        public static TraceSource FromFolder(string folder, string kind, string label)
        {
            TraceSource s = new TraceSource();
            s.Kind = kind;
            s.Label = label;
            s.Folder = folder;
            try
            {
                string[] files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string f in files) AddFile(s, f);
            }
            catch (UnauthorizedAccessException ex)
            {
                s.EnumerationFailed = true; s.EnumerationErrorKind = "アクセス拒否"; s.EnumerationErrorDetail = ex.Message;
            }
            catch (DirectoryNotFoundException ex)
            {
                s.EnumerationFailed = true; s.EnumerationErrorKind = "見つからない"; s.EnumerationErrorDetail = ex.Message;
            }
            catch (Exception ex)
            {
                s.EnumerationFailed = true; s.EnumerationErrorKind = "その他"; s.EnumerationErrorDetail = ex.GetType().Name + ": " + ex.Message;
            }
            return s;
        }

        private static void AddFile(TraceSource s, string path)
        {
            s.Files.Add(path);
            try { s.Bytes += new FileInfo(path).Length; } catch (Exception) { }
        }

        public static string FileKind(string path)
        {
            string name = Path.GetFileName(path).ToLowerInvariant();
            if (name.EndsWith(".db")) return "db";
            if (name.EndsWith(".db-wal")) return "wal";
            if (name.EndsWith(".db-shm")) return "shm";
            if (name == "index" || name.EndsWith("the-real-index")) return "index";
            if (name.EndsWith("_0") || name.EndsWith("_1") || name.EndsWith("_s") || name.StartsWith("f_")) return "entry";
            if (name.StartsWith("data_")) return "block";
            return "other";
        }

        // ---------------------------------------------------------------- needles

        private static List<Needle> MakeNeedles(MessageReference reference)
        {
            List<Needle> needles = new List<Needle>();
            AddNeedle(needles, "messageId", reference.MessageId);
            if (!string.IsNullOrEmpty(reference.ParentMessageId) && reference.ParentMessageId != reference.MessageId) AddNeedle(needles, "parentMessageId", reference.ParentMessageId);
            if (!string.IsNullOrEmpty(reference.ConversationId))
            {
                AddNeedle(needles, "conversationId", reference.ConversationId);
                string encoded = reference.ConversationId.Replace(":", "%3A").Replace("@", "%40");
                if (encoded != reference.ConversationId) AddNeedle(needles, "conversationId", encoded);
                string encodedLower = reference.ConversationId.Replace(":", "%3a").Replace("@", "%40");
                if (encodedLower != encoded) AddNeedle(needles, "conversationId", encodedLower);
            }
            return needles;
        }

        private static void AddNeedle(List<Needle> needles, string kind, string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 4) return;
            Needle a = new Needle(); a.Kind = kind; a.Encoding = "utf-8"; a.Bytes = Encoding.UTF8.GetBytes(text); needles.Add(a);
            Needle b = new Needle(); b.Kind = kind; b.Encoding = "utf-16le"; b.Bytes = Encoding.Unicode.GetBytes(text); needles.Add(b);
        }

        // ---------------------------------------------------------------- scan

        public static TraceSourceResult Scan(TraceSource source, MessageReference reference, Action<string> progress)
        {
            return Scan(source, reference, progress, null);
        }

        /// <summary>With terms: the same bytes (raw files, gzip members, decoded cache bodies) are also searched for the
        /// words of the thread-wide search. Word hits are kept apart from the id hits and never classified as candidates.</summary>
        public static TraceSourceResult Scan(TraceSource source, MessageReference reference, Action<string> progress, List<SearchTerm> terms)
        {
            TraceSourceResult r = new TraceSourceResult();
            r.Source = source;
            if (source.EnumerationFailed)
            {
                r.Error = "列挙失敗";
                return r;
            }
            List<Needle> needles = MakeNeedles(reference);
            if (needles.Count == 0)
            {
                r.Error = "照合する ID がありません";
                return r;
            }
            HashSet<string> seenHits = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> seenFragments = new HashSet<string>(StringComparer.Ordinal);
            _reference = reference;
            SetWords(terms);
            Timing.Declare("  ファイルの読取");
            if (FileReadAhead.ThreadCount() > 1) Timing.Declare(FileReadAhead.WorkerStageName());
            foreach (string stage in new string[] { "  生バイトの照合", "  ファイル内の gzip の探索・展開・照合", "  キャッシュ項目の構造読取（下の展開・照合を含む）" }) Timing.Declare(stage);
            int fileIndex = 0;
            // the files are read ahead on several threads (the first read of a file is the slow part); the scan
            // itself stays on this thread, in the same order, so hits, counts and their numbering do not change
            using (FileReadAhead reader = new FileReadAhead(source.Files, MaxFileBytes))
            foreach (string path in source.Files)
            {
                fileIndex++;
                string kind = FileKind(path);
                Count(r.FilesByKind, kind);
                string relative = path.Length > source.Folder.Length ? path.Substring(source.Folder.Length).TrimStart('\\', '/') : Path.GetFileName(path);
                byte[] data;
                long tRead = Timing.Start();
                FileReadAhead.Result read = reader.Take(fileIndex - 1);
                Timing.Stop("  ファイルの読取", tRead);
                if (read.TooLarge)
                {
                    r.FilesTooLarge++;
                    r.SkippedFiles.Add(new KeyValuePair<string, string>(relative, "上限超過 (" + read.Length.ToString(CultureInfo.InvariantCulture) + " bytes)"));
                    continue;
                }
                if (read.ErrorType != null)
                {
                    r.FilesReadFailed++;
                    r.SkippedFiles.Add(new KeyValuePair<string, string>(relative, "読取失敗: " + read.ErrorType));
                    continue;
                }
                data = read.Data;
                r.FilesScanned++;
                r.BytesScanned += data.Length;
                long tRaw = Timing.Start();
                ScanBuffer(r, data, data.Length, needles, source.Kind, kind, relative, false, 0, seenHits, seenFragments, null);
                Timing.Stop("  生バイトの照合", tRaw);
                long tGzip = Timing.Start();
                ScanGzipMembers(r, data, needles, source.Kind, kind, relative, seenHits, seenFragments);
                Timing.Stop("  ファイル内の gzip の探索・展開・照合", tGzip);
                if (progress != null && (fileIndex % 200 == 0 || fileIndex == source.Files.Count)) progress("  " + source.Label + ": " + fileIndex.ToString(CultureInfo.InvariantCulture) + "/" + source.Files.Count.ToString(CultureInfo.InvariantCulture) + " ファイル");
            }
            long tCache = Timing.Start();
            ScanCacheStructures(r, source, needles, seenHits, seenFragments, progress);
            Timing.Stop("  キャッシュ項目の構造読取（下の展開・照合を含む）", tCache);
            r.DistinctFragments = seenFragments.Count;
            return r;
        }

        // ---------------------------------------------------------------- cache structures (key / headers / decoded body)

        private static void ScanCacheStructures(TraceSourceResult r, TraceSource source, List<Needle> needles, HashSet<string> seenHits, HashSet<string> seenFragments, Action<string> progress)
        {
            CacheStats stats = r.Cache;
            Dictionary<string, List<string>> byDir = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in source.Files)
            {
                string dir = Path.GetDirectoryName(path);
                if (dir == null) dir = source.Folder;
                List<string> list;
                if (!byDir.TryGetValue(dir, out list)) { list = new List<string>(); byDir[dir] = list; }
                list.Add(path);
            }
            List<string> dirs = new List<string>(byDir.Keys);
            dirs.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in dirs)
            {
                List<string> files = byDir[dir];
                string relativeDir = dir.Length > source.Folder.Length ? dir.Substring(source.Folder.Length).TrimStart('\\', '/') : "";
                bool simple = false;
                foreach (string f in files)
                {
                    string name = Path.GetFileName(f);
                    if (SimpleCacheFormat.EntryFileName.IsMatch(name)) simple = true;
                    else if (SimpleCacheFormat.OtherFileName.IsMatch(name)) stats.OtherFiles++;
                }
                bool blockfile = BlockfileReader.HasIndex(dir);
                if (!simple && !blockfile) continue;
                stats.Directories++;
                try
                {
                    if (blockfile) { stats.BlockfileDirs++; ScanBlockfileDir(r, dir, relativeDir, needles, seenHits, seenFragments); }
                    if (simple) { stats.SimpleDirs++; ScanSimpleDir(r, relativeDir, files, needles, seenHits, seenFragments); }
                }
                catch (Exception ex)
                {
                    stats.Unreadable++;
                    if (stats.Warnings.Count < 200) stats.Warnings.Add(relativeDir + ": 構造解析の中断: " + ex.GetType().Name);
                }
                if (progress != null) progress("  " + source.Label + ": キャッシュ項目 " + stats.Entries.ToString(CultureInfo.InvariantCulture) + " 件を解析（索引外 " + stats.Orphan.ToString(CultureInfo.InvariantCulture) + "）");
            }
        }

        private static string BlockId(int file, int block)
        {
            return file.ToString(CultureInfo.InvariantCulture) + ":" + block.ToString(CultureInfo.InvariantCulture);
        }

        private static void ScanBlockfileDir(TraceSourceResult r, string dir, string relativeDir, List<Needle> needles, HashSet<string> seenHits, HashSet<string> seenFragments)
        {
            CacheStats stats = r.Cache;
            using (BlockfileReader reader = new BlockfileReader(dir))
            {
                uint[] table;
                try { table = reader.ReadIndexTable(); }
                catch (Exception ex)
                {
                    stats.Unreadable++;
                    stats.Warnings.Add(relativeDir + ": index 読取不可: " + ex.GetType().Name + ": " + ex.Message);
                    return;
                }
                HashSet<string> reached = new HashSet<string>(StringComparer.Ordinal);
                foreach (uint slot in table)
                {
                    uint addr = slot;
                    int hops = 0;
                    while (BlockfileReader.Initialized(addr) && hops < 100000)
                    {
                        hops++;
                        if (BlockfileReader.FileType(addr) != 2) { stats.Unreadable++; break; }
                        int file = BlockfileReader.FileSelector(addr);
                        int block = BlockfileReader.StartBlock(addr);
                        if (!reached.Add(BlockId(file, block))) break;   // already read (shared tail or cycle)
                        for (int k = 1; k < BlockfileReader.ContiguousBlocks(addr); k++) reached.Add(BlockId(file, block + k));
                        byte[] raw;
                        string location, error;
                        if (!reader.ReadAddress(addr, MaxFileBytes, out raw, out location, out error))
                        {
                            stats.Unreadable++;
                            if (stats.Warnings.Count < 200) stats.Warnings.Add(relativeDir + ": 項目 " + location + " 読取不可: " + error);
                            break;
                        }
                        EntryStore es = EntryStore.Parse(raw);
                        if (es == null) { stats.Unreadable++; break; }
                        CacheEntry e = MakeBlockfileEntry(reader, es, raw, location, relativeDir, true);
                        e.Allocated = reader.IsAllocated(file, block);
                        ProcessEntry(r, reader, e, needles, seenHits, seenFragments);
                        addr = es.Next;
                    }
                }
                // records that no index slot leads to any more (evicted / deleted entries whose blocks were not reused yet):
                // accepted only when the stored key hash matches the key that is still in the block
                foreach (int number in reader.DataFileNumbers())
                {
                    if (reader.EntrySizeOf(number) != EntryStore.Size) continue;
                    long blocks = reader.BlockCountOf(number);
                    for (int b = 0; b < blocks; b++)
                    {
                        if (reached.Contains(BlockId(number, b))) continue;
                        byte[] raw;
                        string error;
                        if (!reader.ReadBlocksOf(number, b, 1, out raw, out error)) break;
                        if (IsZero(raw)) continue;
                        EntryStore es = EntryStore.Parse(raw);
                        if (es == null || es.KeyLength <= 0 || es.KeyLength > 65536 || es.State < 0 || es.State > 2) continue;
                        int need = 1;
                        if (!BlockfileReader.Initialized(es.LongKey))
                        {
                            need = (EntryStore.KeyOffset + es.KeyLength + EntryStore.Size - 1) / EntryStore.Size;
                            if (need > BlockfileReader.MaxContiguousBlocks || b + need > blocks) continue;
                            if (need > 1)
                            {
                                if (!reader.ReadBlocksOf(number, b, need, out raw, out error)) continue;
                                es = EntryStore.Parse(raw);
                            }
                        }
                        string location = "data_" + number.ToString(CultureInfo.InvariantCulture) + " ブロック " + b.ToString(CultureInfo.InvariantCulture) + " (" + need.ToString(CultureInfo.InvariantCulture) + " ブロック)";
                        CacheEntry e = MakeBlockfileEntry(reader, es, raw, location, relativeDir, false);
                        if (e.Key == null || !e.KeyHashMatches || !PlausibleKey(e.Key)) continue;
                        for (int k = 0; k < need; k++) reached.Add(BlockId(number, b + k));
                        e.Allocated = reader.IsAllocated(number, b);
                        ProcessEntry(r, reader, e, needles, seenHits, seenFragments);
                    }
                }
                foreach (string w in reader.Warnings) if (stats.Warnings.Count < 200) stats.Warnings.Add(relativeDir + ": " + w);
            }
        }

        private static bool IsZero(byte[] b)
        {
            for (int i = 0; i < b.Length; i++) if (b[i] != 0) return false;
            return true;
        }

        private static bool PlausibleKey(string key)
        {
            if (key.Length < 8) return false;
            foreach (char c in key) if (c < 0x20 || c > 0x7E) return false;
            return true;
        }

        private static CacheEntry MakeBlockfileEntry(BlockfileReader reader, EntryStore es, byte[] raw, string location, string relativeDir, bool fromIndex)
        {
            CacheEntry e = new CacheEntry();
            e.Format = "blockfile";
            e.Directory = relativeDir;
            e.Location = location;
            e.FromIndex = fromIndex;
            e.State = es.State;
            e.Child = (es.Flags & 2) != 0;
            e.Record = es;
            byte[] keyBytes = null;
            if (BlockfileReader.Initialized(es.LongKey))
            {
                byte[] kb = null;
                string loc = null, err = "キー長 0";
                if (es.KeyLength > 0 && reader.ReadAddress(es.LongKey, MaxFileBytes, out kb, out loc, out err) && kb.Length >= es.KeyLength)
                {
                    keyBytes = new byte[es.KeyLength];
                    Array.Copy(kb, 0, keyBytes, 0, es.KeyLength);
                }
                else e.Error = "長いキー読取不可: " + err;
            }
            else if (es.KeyLength > 0 && EntryStore.KeyOffset + es.KeyLength <= raw.Length)
            {
                keyBytes = new byte[es.KeyLength];
                Array.Copy(raw, EntryStore.KeyOffset, keyBytes, 0, es.KeyLength);
            }
            else e.Error = "キーが記録の範囲外";
            if (keyBytes != null)
            {
                e.Key = Encoding.UTF8.GetString(keyBytes);
                e.KeyHashChecked = true;
                e.KeyHashMatches = SuperFastHash.Compute(keyBytes) == es.Hash;
            }
            return e;
        }

        private static void ScanSimpleDir(TraceSourceResult r, string relativeDir, List<string> files, List<Needle> needles, HashSet<string> seenHits, HashSet<string> seenFragments)
        {
            CacheStats stats = r.Cache;
            foreach (string path in files)
            {
                string name = Path.GetFileName(path);
                if (!SimpleCacheFormat.EntryFileName.IsMatch(name)) continue;
                byte[] data;
                try
                {
                    FileInfo fi = new FileInfo(path);
                    if (fi.Length > MaxFileBytes) { stats.Unreadable++; continue; }
                    data = File.ReadAllBytes(path);
                }
                catch (Exception) { stats.Unreadable++; continue; }
                string error;
                CacheEntry e = SimpleCacheFormat.Parse(data, name, out error);
                if (e == null)
                {
                    stats.Unreadable++;
                    if (stats.Warnings.Count < 200) stats.Warnings.Add((relativeDir.Length > 0 ? relativeDir + "/" : "") + name + ": " + error);
                    continue;
                }
                e.Directory = relativeDir;
                ProcessEntry(r, null, e, needles, seenHits, seenFragments);
            }
        }

        private static byte[] Trim(byte[] b, int n)
        {
            if (b.Length == n) return b;
            byte[] t = new byte[n];
            Array.Copy(b, 0, t, 0, n);
            return t;
        }

        private static void ParseHeaders(CacheEntry e)
        {
            e.HeaderKind = "none";
            e.EncodingClass = "unknown";
            if (e.Stream0 == null || e.Stream0.Length == 0) return;
            ResponseHeaders h = HttpResponseInfoPickle.Parse(e.Stream0);
            if (h != null)
            {
                e.HeaderKind = "http";
                e.StatusLine = h.StatusLine;
                e.ContentType = h.Get("content-type");
                e.ContentEncoding = h.Get("content-encoding");
                e.ResponseTime = h.ResponseTime ?? h.RequestTime;
                e.EncodingClass = BodyDecoder.Classify(e.ContentEncoding ?? "");
                return;
            }
            string ce = HttpResponseInfoPickle.FindProtobufHeader(e.Stream0, "content-encoding");
            string ct = HttpResponseInfoPickle.FindProtobufHeader(e.Stream0, "content-type");
            if (ce != null || ct != null)
            {
                e.HeaderKind = "cachestorage";
                e.ContentType = ct;
                e.ContentEncoding = ce;
                e.EncodingClass = BodyDecoder.Classify(ce ?? "");
                return;
            }
            e.HeaderKind = "unknown";
        }

        /// <summary>Key match (count only: keys are plain text and the raw scan already keeps those hits), headers,
        /// body read, decode per Content-Encoding and the id search over the decoded bytes.</summary>
        private static void ProcessEntry(TraceSourceResult r, BlockfileReader reader, CacheEntry e, List<Needle> needles, HashSet<string> seenHits, HashSet<string> seenFragments)
        {
            CacheStats stats = r.Cache;
            stats.Entries++;
            if (e.FromIndex) stats.FromIndex++; else stats.Orphan++;
            if (e.KeyHashChecked && !e.KeyHashMatches) stats.KeyHashMismatch++;
            if (e.Key != null)
            {
                byte[] keyBytes = Encoding.UTF8.GetBytes(e.Key);
                bool any = false;
                foreach (Needle n in needles)
                {
                    if (n.Encoding != "utf-8") continue;
                    if (IndexOf(keyBytes, keyBytes.Length, n.Bytes, 0) >= 0) { Count(stats.KeyHitsByNeedle, n.Kind); Count(e.KeyHits, n.Kind); any = true; }
                }
                if (any) stats.EntriesWithKeyHit++;
            }
            EntryStore es = e.Record as EntryStore;
            if (reader != null && es != null)
            {
                if (BlockfileReader.Initialized(es.DataAddr[0]) && es.DataSize[0] > 0)
                {
                    byte[] s0;
                    string loc, err;
                    if (reader.ReadAddress(es.DataAddr[0], MaxFileBytes, out s0, out loc, out err) && s0.Length >= es.DataSize[0]) e.Stream0 = Trim(s0, es.DataSize[0]);
                }
            }
            ParseHeaders(e);
            Count(stats.HeaderKinds, e.HeaderKind);
            Count(stats.Encodings, e.EncodingClass);
            if (reader != null && es != null)
            {
                if (!BlockfileReader.Initialized(es.DataAddr[1]) || es.DataSize[1] <= 0) e.BodyStatus = "empty";
                else
                {
                    e.BodyLocation = BlockfileReader.Describe(es.DataAddr[1]);
                    e.BodyBytes = es.DataSize[1];
                    if (es.DataSize[1] > MaxFileBytes) e.BodyStatus = "toolarge";
                    else if (CacheBytes.IsMedia(e.ContentType)) e.BodyStatus = "media";
                    else
                    {
                        byte[] body;
                        string loc, err;
                        if (reader.ReadAddress(es.DataAddr[1], MaxFileBytes, out body, out loc, out err) && body.Length >= es.DataSize[1]) { e.Body = Trim(body, es.DataSize[1]); e.BodyStatus = "ok"; }
                        else { e.BodyStatus = "unreadable"; e.Error = err; }
                    }
                }
            }
            else
            {
                if (e.Body == null) e.BodyStatus = "unreadable";
                else if (e.Body.Length == 0) e.BodyStatus = "empty";
                else if (CacheBytes.IsMedia(e.ContentType)) e.BodyStatus = "media";
                else e.BodyStatus = "ok";
            }
            switch (e.BodyStatus)
            {
                case "ok": stats.BodiesRead++; break;
                case "empty": stats.BodiesEmpty++; break;
                case "media": stats.BodiesMedia++; break;
                case "toolarge": stats.BodiesTooLarge++; break;
                default: stats.BodiesUnreadable++; break;
            }
            e.DecodeStatus = "none";
            if (e.BodyStatus == "ok")
            {
                if (stats.DecodedBytes > MaxTotalDecodedBytes)
                {
                    e.DecodeStatus = "skipped";
                    stats.DecodeSkippedCap++;
                }
                else
                {
                    string status;
                    long tDecode = Timing.Start();
                    byte[] decoded = BodyDecoder.Decode(e.Body, e.EncodingClass, out status);
                    Timing.Stop("    本文の展開 " + e.EncodingClass, tDecode);
                    if (decoded == null && status == "failed" && e.HeaderKind == "cachestorage")
                    {
                        // CacheStorage keeps response bodies as the page saw them (already decoded) while the
                        // headers still carry the original Content-Encoding: fall back to the bytes as they are.
                        decoded = e.Body;
                        status = "mismatch";
                    }
                    e.DecodeStatus = status;
                    switch (status)
                    {
                        case "ok": stats.DecodeOk++; break;
                        case "truncated": stats.DecodeOk++; stats.DecodeTruncated++; break;
                        case "failed": stats.DecodeFailed++; break;
                        case "unsupported": stats.DecodeUnsupported++; break;
                        default: stats.DecodeAsIs++; break;
                    }
                    if (decoded != null)
                    {
                        e.DecodedBytes = decoded.Length;
                        if (status == "ok" || status == "truncated")
                        {
                            stats.DecodedBytes += decoded.Length;
                            long tScan = Timing.Start();
                            ScanBuffer(r, decoded, decoded.Length, needles, r.Source.Kind, "cachebody", e.Location, false, 0, seenHits, seenFragments, e);
                            Timing.Stop("    展開後の照合", tScan);
                        }
                        else
                        {
                            // not compressed: the raw scan already covers these bytes; only attribute the ids to the entry
                            foreach (Needle n in needles)
                                if (IndexOf(decoded, decoded.Length, n.Bytes, 0) >= 0) Count(e.BodyHits, n.Kind);
                        }
                    }
                }
            }
            e.Body = null;
            e.Stream0 = null;
            e.Record = null;
            stats.EntryList.Add(e);
        }

        private static void Count(Dictionary<string, long> table, string key)
        {
            long n;
            table.TryGetValue(key, out n);
            table[key] = n + 1;
        }

        [ThreadStatic] private static MessageReference _reference;   // the input of the current Scan (conversation match)

        // ---------------------------------------------------------------- words (thread-wide search)

        [ThreadStatic] private static MultiNeedle _wordNeedles;
        [ThreadStatic] private static List<SearchTerm> _wordTerms;       // term of each needle
        [ThreadStatic] private static List<string> _wordEncodings;      // encoding of each needle
        private const int WordRadius = 1200;   // bytes decoded on each side of a word
        private const int WordTextLimit = 1500;

        private static void SetWords(List<SearchTerm> terms)
        {
            _wordNeedles = null;
            _wordTerms = new List<SearchTerm>();
            _wordEncodings = new List<string>();
            if (terms == null || terms.Count == 0) return;
            MultiNeedle needles = new MultiNeedle();
            foreach (SearchTerm t in terms)
            {
                // too few bytes would match by chance all over binary data
                byte[] utf8 = Encoding.UTF8.GetBytes(t.Text);
                if (utf8.Length >= 4) { needles.Add(utf8); _wordTerms.Add(t); _wordEncodings.Add("utf-8"); }
                byte[] utf16 = Encoding.Unicode.GetBytes(t.Text);
                if (utf16.Length >= 4) { needles.Add(utf16); _wordTerms.Add(t); _wordEncodings.Add("utf-16le"); }
                // JSON written with \uXXXX escapes for non-ASCII characters
                bool wide = false;
                StringBuilder lower = new StringBuilder(), upper = new StringBuilder();
                foreach (char c in t.Text)
                {
                    if (c > 0x7E) { wide = true; lower.Append("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture)); upper.Append("\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture)); }
                    else { lower.Append(c); upper.Append(c); }
                }
                if (wide)
                {
                    needles.Add(Encoding.ASCII.GetBytes(lower.ToString())); _wordTerms.Add(t); _wordEncodings.Add("json-escape");
                    if (upper.ToString() != lower.ToString()) { needles.Add(Encoding.ASCII.GetBytes(upper.ToString())); _wordTerms.Add(t); _wordEncodings.Add("json-escape"); }
                }
            }
            if (needles.Count > 0) _wordNeedles = needles;
        }

        private static void ScanWords(TraceSourceResult r, byte[] data, int count, string sourceKind, string fileKind, string relative, bool fromGzip, long baseOffset, CacheEntry entry)
        {
            MultiNeedle needles = _wordNeedles;
            if (needles == null) return;
            List<SearchTerm> terms = _wordTerms;
            List<string> encodings = _wordEncodings;
            Dictionary<SearchTerm, int> perTerm = new Dictionary<SearchTerm, int>();
            bool capped = false;
            needles.FindAll(data, count, delegate(int needleIndex, int offset)
            {
                SearchTerm term = terms[needleIndex];
                int n;
                perTerm.TryGetValue(term, out n);
                if (n >= MaxHitsPerNeedlePerBuffer) { capped = true; return true; }
                perTerm[term] = n + 1;
                string encoding = encodings[needleIndex];
                int hitChar;
                bool truncated;
                string window = DecodeWindow(data, count, offset, encoding == "utf-16le" ? "utf-16le" : "utf-8", WordRadius, out hitChar, out truncated);
                if (string.IsNullOrEmpty(window) || hitChar >= window.Length) return true;
                TraceWordHit hit = new TraceWordHit();
                hit.Term = term;
                hit.SourceKind = sourceKind;
                hit.FileKind = fileKind;
                hit.RelativePath = relative;
                hit.Entry = entry;
                hit.Offset = fromGzip ? baseOffset : offset;
                hit.FromGzip = fromGzip;
                hit.Encoding = encoding;
                ReadableAround(window, hitChar, hit);
                if (hit.Text != null && hit.Text.Length > 0) r.WordHits.Add(hit);
                return true;
            });
            if (capped) r.WordHitCapReached++;
        }

        /// <summary>The readable run of characters around the word: it ends at the first control character or
        /// undecodable byte on each side. When the word sits inside a JSON string value, that value alone is taken.</summary>
        private static void ReadableAround(string window, int hitChar, TraceWordHit hit)
        {
            int start = hitChar, end = hitChar;
            while (start > 0 && Readable(window[start - 1])) start--;
            while (end < window.Length && Readable(window[end])) end++;
            string run = window.Substring(start, end - start);
            int index = hitChar - start;
            // a JSON string value: from the quote before the word to the quote after it (escaped quotes skipped)
            int q1 = -1, q2 = -1;
            for (int i = index - 1; i >= 0; i--) if (run[i] == '"' && !Escaped(run, i)) { q1 = i; break; }
            for (int i = index; i < run.Length; i++) if (run[i] == '"' && !Escaped(run, i)) { q2 = i; break; }
            if (q1 >= 0 && q2 > q1)
            {
                int before = q1 - 1;
                while (before >= 0 && (run[before] == ' ' || run[before] == '\n' || run[before] == '\r' || run[before] == '\t')) before--;
                if (before >= 0 && (run[before] == ':' || run[before] == ',' || run[before] == '['))
                {
                    string prefix = UnescapeJson(run.Substring(q1 + 1, index - q1 - 1));
                    run = prefix + UnescapeJson(run.Substring(index, q2 - index));
                    index = prefix.Length;
                    hit.JsonString = true;
                }
            }
            if (!hit.JsonString && hit.Encoding == "json-escape") { string prefix = UnescapeJson(run.Substring(0, index)); run = prefix + UnescapeJson(run.Substring(index)); index = prefix.Length; }
            if (run.IndexOf('<') >= 0 && run.IndexOf('>') >= 0)
            {
                // HTML: the plain text, with the position of the word found again in it
                string plain = HtmlText.ToPlainText(run);
                int again = ThreadTextRules.IndexOfTerm(plain, hit.Term.Text);
                run = plain;
                index = again >= 0 ? again : -1;
            }
            if (run.Length > WordTextLimit)
            {
                int from = index > WordTextLimit / 2 ? Math.Min(index - WordTextLimit / 2, run.Length - WordTextLimit) : 0;
                run = run.Substring(from, WordTextLimit);
                index = index >= 0 ? index - from : -1;
            }
            hit.Text = run.Trim();
            hit.MatchIndex = index >= 0 ? Math.Max(0, index - (run.Length - run.TrimStart().Length)) : -1;
        }

        private static bool Readable(char c)
        {
            if (c == '\n' || c == '\r' || c == '\t') return true;
            return !char.IsControl(c) && c != '\uFFFD' && c != '\uFFFE' && c != '\uFFFF';
        }

        private static bool Escaped(string s, int quote)
        {
            int slashes = 0;
            for (int i = quote - 1; i >= 0 && s[i] == '\\'; i--) slashes++;
            return (slashes & 1) == 1;
        }

        private static void ScanBuffer(TraceSourceResult r, byte[] data, int count, List<Needle> needles, string sourceKind, string fileKind, string relative, bool fromGzip, long baseOffset,
            HashSet<string> seenHits, HashSet<string> seenFragments, CacheEntry entry)
        {
            ScanWords(r, data, count, sourceKind, fileKind, relative, fromGzip, baseOffset, entry);
            foreach (Needle needle in needles)
            {
                int from = 0;
                int found = 0;
                while (from <= count - needle.Bytes.Length)
                {
                    int idx = IndexOf(data, count, needle.Bytes, from);
                    if (idx < 0) break;
                    if (found >= MaxHitsPerNeedlePerBuffer)
                    {
                        r.HitCapReached++;
                        break;
                    }
                    found++;
                    from = idx + needle.Bytes.Length;
                    string hitKey = relative + "|" + (entry != null ? "d" : (fromGzip ? "g" : "r")) + "|" + baseOffset.ToString(CultureInfo.InvariantCulture) + "|" + idx.ToString(CultureInfo.InvariantCulture) + "|" + needle.Kind;
                    if (!seenHits.Add(hitKey)) continue;
                    if (entry != null) { Count(r.Cache.BodyHitsByNeedle, needle.Kind); Count(entry.BodyHits, needle.Kind); }
                    else Count(r.HitsByNeedle, needle.Kind);
                    TraceHit hit = new TraceHit();
                    hit.SourceKind = sourceKind;
                    hit.FileKind = fileKind;
                    hit.RelativePath = relative;
                    hit.Entry = entry;
                    hit.Offset = fromGzip ? baseOffset : idx;
                    hit.OffsetInMember = fromGzip ? idx : 0;
                    hit.NeedleKind = needle.Kind;
                    hit.Encoding = needle.Encoding;
                    hit.FromGzip = fromGzip;
                    int hitCharIndex;
                    string window = DecodeWindow(data, count, idx, needle.Encoding, ContextRadius, out hitCharIndex, out hit.WindowTruncated);
                    ExtractFragment(window, hitCharIndex, hit);
                    ExtractObjectContext(data, count, idx, needle.Encoding, hit, _reference);
                    Count(r.FragmentsByKind, hit.ContextKind);
                    foreach (TraceElement e in hit.Elements) seenFragments.Add(hit.ContextKind + "|" + e.Text);
                    if (hit.Elements.Count == 0 && hit.Fragment != null) seenFragments.Add(hit.ContextKind + "|" + hit.Fragment);
                    r.Hits.Add(hit);
                }
            }
        }

        private static int IndexOf(byte[] data, int count, byte[] needle, int from)
        {
            if (needle.Length == 0) return -1;
            byte first = needle[0];
            int last = count - needle.Length;
            for (int i = from; i <= last; i++)
            {
                if (data[i] != first) continue;
                int j = 1;
                while (j < needle.Length && data[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        private static void ScanGzipMembers(TraceSourceResult r, byte[] data, List<Needle> needles, string sourceKind, string fileKind, string relative, HashSet<string> seenHits, HashSet<string> seenFragments)
        {
            int members = 0;
            for (int i = 0; i + 3 < data.Length; i++)
            {
                if (data[i] != 0x1F || data[i + 1] != 0x8B || data[i + 2] != 0x08) continue;
                if (members >= MaxGzipMembersPerFile)
                {
                    r.GzipMemberCapReached++;
                    break;
                }
                members++;
                r.GzipMembers++;
                byte[] output;
                bool truncated = false;
                try
                {
                    using (MemoryStream input = new MemoryStream(data, i, data.Length - i, false))
                    using (GZipStream gz = new GZipStream(input, CompressionMode.Decompress))
                    using (MemoryStream outStream = new MemoryStream())
                    {
                        byte[] buffer = new byte[65536];
                        int n;
                        while ((n = gz.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            outStream.Write(buffer, 0, n);
                            if (outStream.Length > MaxGzipOutput) { truncated = true; break; }
                        }
                        output = outStream.ToArray();
                    }
                }
                catch (Exception)
                {
                    r.GzipFailures++;
                    continue;
                }
                if (truncated) r.GzipOutputTruncated++;
                if (output.Length == 0) { r.GzipFailures++; continue; }
                ScanBuffer(r, output, output.Length, needles, sourceKind, fileKind, relative, true, i, seenHits, seenFragments, null);
            }
        }

        private static string DecodeWindow(byte[] data, int count, int hitOffset, string encoding, int radius, out int hitCharIndex, out bool truncated)
        {
            int start = Math.Max(0, hitOffset - radius);
            int end = Math.Min(count, hitOffset + radius);
            truncated = start > 0 || end < count;
            hitCharIndex = 0;
            if (encoding == "utf-16le")
            {
                if (((hitOffset - start) & 1) == 1) start++;
                int len = end - start;
                if ((len & 1) == 1) len--;
                try
                {
                    string s = Encoding.Unicode.GetString(data, start, len);
                    hitCharIndex = (hitOffset - start) / 2;
                    return s;
                }
                catch (Exception) { return ""; }
            }
            try
            {
                string before = Encoding.UTF8.GetString(data, start, hitOffset - start);
                string after = Encoding.UTF8.GetString(data, hitOffset, end - hitOffset);
                hitCharIndex = before.Length;
                return before + after;
            }
            catch (Exception) { return ""; }
        }

        private static readonly Regex ToastText = new Regex(@"<text[^>]*>(.*?)</text>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex JsonContent = new Regex("\"content\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex HtmlParagraph = new Regex(@"<p[^>]*>(.*?)</p>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Printable = new Regex(@"[^\p{C}]{8,}", RegexOptions.Compiled);

        /// <summary>The fragment is ONE element (the one containing the hit, else the nearest). Every element of that kind
        /// in the window is listed with its position, never merged, and the whole window is kept for the private record.</summary>
        private static void ExtractFragment(string window, int hitCharIndex, TraceHit hit)
        {
            hit.Window = window;
            hit.WindowHitIndex = hitCharIndex;
            if (string.IsNullOrEmpty(window)) { hit.ContextKind = "text"; return; }
            if (TryPick(ToastText.Matches(window), hitCharIndex, hit, "toast", true)) return;
            if (TryPick(JsonContent.Matches(window), hitCharIndex, hit, "json", false)) return;
            if (TryPick(HtmlParagraph.Matches(window), hitCharIndex, hit, "html", true)) return;
            hit.ContextKind = "text";
            List<TraceElement> runs = new List<TraceElement>();
            foreach (Match m in Printable.Matches(window)) runs.Add(MakeElement(m.Value, m.Index, m.Index + m.Length, hitCharIndex));
            Choose(runs, hit);
        }

        private static bool TryPick(MatchCollection matches, int hitCharIndex, TraceHit hit, string kind, bool html)
        {
            if (matches.Count == 0) return false;
            hit.ContextKind = kind;
            List<TraceElement> elements = new List<TraceElement>();
            foreach (Match m in matches)
            {
                string text = m.Groups[1].Value;
                text = kind == "json" ? UnescapeJson(text) : (html ? HtmlText.ToPlainText(text) : text);
                elements.Add(MakeElement(text, m.Index, m.Index + m.Length, hitCharIndex));
            }
            Choose(elements, hit);
            return true;
        }

        private static TraceElement MakeElement(string text, int start, int end, int hitCharIndex)
        {
            TraceElement e = new TraceElement();
            e.StartRelative = start - hitCharIndex;
            e.EndRelative = end - hitCharIndex;
            e.ContainsHit = hitCharIndex >= start && hitCharIndex <= end;
            if (text.Length > FragmentLimit) { text = text.Substring(0, FragmentLimit); e.Truncated = true; }
            e.Text = text;
            return e;
        }

        private static void Choose(List<TraceElement> elements, TraceHit hit)
        {
            hit.Elements.AddRange(elements);
            TraceElement best = null;
            int bestDistance = int.MaxValue;
            foreach (TraceElement e in elements)
            {
                int distance = e.ContainsHit ? 0 : (e.StartRelative > 0 ? e.StartRelative : -e.EndRelative);
                if (distance < bestDistance) { bestDistance = distance; best = e; }
            }
            if (best == null) return;
            hit.Fragment = best.Text;
            hit.FragmentStartRelative = best.StartRelative;
            hit.FragmentEndRelative = best.EndRelative;
            hit.FragmentTruncated = best.Truncated;
            hit.OtherElementsNearby = Math.Max(0, elements.Count - 1);
        }

        // ---------------------------------------------------------------- the JSON object that contains the hit

        /// <summary>Finds the innermost JSON object enclosing the hit (within the object radius), parses it and keeps
        /// its own id / content / conversation / properties.edittime. The record is the only thing taken; neighbouring
        /// records in the same response stay out. Then the hit is classified against the input.</summary>
        private static void ExtractObjectContext(byte[] data, int count, int hitOffset, string encoding, TraceHit hit, MessageReference reference)
        {
            hit.ConversationMatch = reference == null || string.IsNullOrEmpty(reference.ConversationId) ? "unchecked" : "unknown";
            int hitChar;
            bool truncatedWindow;
            string text = DecodeWindow(data, count, hitOffset, encoding, ObjectRadius, out hitChar, out truncatedWindow);
            if (!string.IsNullOrEmpty(text))
            {
                List<int> starts;
                int end;
                bool truncated;
                if (FindJsonObject(text, hitChar, out starts, out end, out truncated))
                {
                    // innermost first; step outwards (at most two levels) while the object has neither "id" nor "content",
                    // e.g. when the hit sits inside a "properties" object of the record
                    string chosenText = null;
                    OrderedMap chosenObj = null;
                    bool chosenTruncated = false;
                    int chosenLevel = starts.Count - 1;
                    for (int level = starts.Count - 1; level >= Math.Max(0, starts.Count - 3); level--)
                    {
                        int start = starts[level];
                        int objEnd = level == starts.Count - 1 ? end : FindMatchingBrace(text, start);
                        bool objTruncated = objEnd < 0;
                        string objText = text.Substring(start, (objTruncated ? text.Length : objEnd + 1) - start);
                        OrderedMap obj = null;
                        if (!objTruncated)
                        {
                            try { obj = JsonReader.AsMap(JsonReader.Parse(objText)); } catch (Exception) { obj = null; }
                        }
                        if (chosenText == null) { chosenText = objText; chosenObj = obj; chosenTruncated = objTruncated; chosenLevel = level; }
                        if (obj != null && (obj.ContainsKey("id") || obj.ContainsKey("content"))) { chosenText = objText; chosenObj = obj; chosenTruncated = objTruncated; chosenLevel = level; break; }
                    }
                    hit.ObjectFound = true;
                    hit.ObjectTruncated = chosenTruncated;
                    hit.ObjectParsed = chosenObj != null;
                    hit.ObjectLength = chosenText.Length;
                    hit.ObjectTextCut = chosenText.Length > ObjectTextLimit;
                    hit.ObjectText = hit.ObjectTextCut ? chosenText.Substring(0, ObjectTextLimit) : chosenText;
                    if (chosenObj != null) FillObjectFields(chosenObj, hit, reference);
                    // No conversation field in the record itself: only a structural parent object (the response that
                    // encloses this record) may supply one. Text that merely occurs nearby never counts: it could
                    // belong to the neighbouring record.
                    if (hit.ConversationMatch == "unknown" && reference != null)
                    {
                        for (int level = chosenLevel - 1; level >= 0; level--)
                        {
                            int parentEnd = FindMatchingBrace(text, starts[level]);
                            if (parentEnd < 0) break;
                            OrderedMap parent = null;
                            try { parent = JsonReader.AsMap(JsonReader.Parse(text.Substring(starts[level], parentEnd - starts[level] + 1))); } catch (Exception) { parent = null; }
                            if (parent == null) break;
                            string parentConv = ConversationOf(parent);
                            if (string.IsNullOrEmpty(parentConv)) continue;
                            hit.ObjectConversation = parentConv + " (親オブジェクトの値)";
                            hit.ConversationMatch = SameConversation(parentConv, reference.ConversationId) ? "match" : "mismatch";
                            break;
                        }
                    }
                }
                else if (hit.ContextKind == "toast" && hit.ConversationMatch == "unknown" && reference != null && hit.Window != null)
                {
                    // the toast element that contains the hit is the structural unit; neighbouring toasts in the same page are not
                    foreach (Match m in ToastElement.Matches(hit.Window))
                    {
                        if (hit.WindowHitIndex < m.Index || hit.WindowHitIndex > m.Index + m.Length) continue;
                        if (ContainsConversationId(m.Value, reference.ConversationId)) hit.ConversationMatch = "match";
                        break;
                    }
                }
            }
            if (hit.ObjectTexts == null && hit.ContextKind == "toast" && hit.Window != null)
            {
                // every <text> of the toast that contains the hit (title, body lines); neighbouring toasts stay out
                foreach (Match m in ToastElement.Matches(hit.Window))
                {
                    if (hit.WindowHitIndex < m.Index || hit.WindowHitIndex > m.Index + m.Length) continue;
                    hit.ObjectTexts = new List<KeyValuePair<string, string>>();
                    int n = 0;
                    foreach (Match t in ToastText.Matches(m.Value))
                    {
                        n++;
                        string toastText = HtmlText.ToPlainText(t.Groups[1].Value);
                        if (toastText.Length > 0) hit.ObjectTexts.Add(new KeyValuePair<string, string>("通知の text " + n.ToString(CultureInfo.InvariantCulture), toastText));
                    }
                    break;
                }
            }
            if (!hit.IsTargetIdHit) hit.Classification = "nearby-only";
            else if (hit.ObjectFound && hit.ObjectParsed && hit.ObjectId != null && !hit.ObjectIdIsTarget) hit.Classification = "id-elsewhere";
            else if (hit.ConversationMatch == "mismatch") hit.Classification = "other-conversation";
            else hit.Classification = "candidate";
        }

        private static void FillObjectFields(OrderedMap obj, TraceHit hit, MessageReference reference)
        {
            string id = JsonReader.AsString(obj.Get("id"));
            if (id == null && obj.Get("id") != null) id = Convert.ToString(obj.Get("id"), CultureInfo.InvariantCulture);
            hit.ObjectId = id;
            hit.ObjectIdIsTarget = id != null && reference != null && id == reference.MessageId;
            string content = JsonReader.AsString(obj.Get("content"));
            hit.ObjectContent = content;
            hit.ObjectContentText = content == null ? null : HtmlText.ToPlainText(content).Trim();
            hit.ObjectClientMessageId = JsonReader.AsString(obj.Get("clientmessageid")) ?? JsonReader.AsString(obj.Get("clientMessageId"));
            hit.ObjectTexts = ThreadTextRules.Collect(obj);
            object edit = JsonReader.Path(obj, "properties", "edittime");
            if (edit != null) hit.ObjectEditTime = Convert.ToString(edit, CultureInfo.InvariantCulture);
            string conv = ConversationOf(obj);
            if (!string.IsNullOrEmpty(conv))
            {
                hit.ObjectConversation = conv;
                if (reference != null && !string.IsNullOrEmpty(reference.ConversationId))
                    hit.ConversationMatch = SameConversation(conv, reference.ConversationId) ? "match" : "mismatch";
            }
        }

        /// <summary>The conversation id a record states about itself (direct fields only: conversationId / conversationid /
        /// threadId / conversationLink). Nothing is searched deeper or in neighbouring records.</summary>
        private static string ConversationOf(OrderedMap obj)
        {
            string conv = JsonReader.AsString(obj.Get("conversationId")) ?? JsonReader.AsString(obj.Get("conversationid")) ?? JsonReader.AsString(obj.Get("threadId"));
            if (!string.IsNullOrEmpty(conv)) return conv;
            string link = JsonReader.AsString(obj.Get("conversationLink"));
            if (string.IsNullOrEmpty(link)) return null;
            int idx = link.IndexOf("/conversations/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            string rest = link.Substring(idx + "/conversations/".Length);
            int stop = rest.IndexOfAny(new char[] { '/', '?', ';' });
            return Uri.UnescapeDataString(stop >= 0 ? rest.Substring(0, stop) : rest);
        }

        private static bool SameConversation(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            string ua, ub;
            try { ua = Uri.UnescapeDataString(a); } catch (Exception) { ua = a; }
            try { ub = Uri.UnescapeDataString(b); } catch (Exception) { ub = b; }
            return string.Equals(ua, ub, StringComparison.OrdinalIgnoreCase);
        }

        private static readonly Regex ToastElement = new Regex(@"<toast\b.*?</toast>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static bool ContainsConversationId(string text, string conversationId)
        {
            if (string.IsNullOrEmpty(conversationId)) return false;
            if (text.IndexOf(conversationId, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            string encoded = conversationId.Replace(":", "%3A").Replace("@", "%40");
            return text.IndexOf(encoded, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Forward scan with string tracking. Returns the stack of '{' positions open at the hit (outermost first)
        /// and the end of the innermost one (-1 when it does not close inside the text). Two start assumptions are tried
        /// because the text may begin inside a string; the one whose innermost object parses wins.</summary>
        private static bool FindJsonObject(string text, int hitChar, out List<int> starts, out int end, out bool truncated)
        {
            starts = null;
            end = -1;
            truncated = false;
            List<int> fallbackStarts = null;
            int fallbackEnd = -1;
            foreach (bool startInString in new bool[] { false, true })
            {
                List<int> stack = new List<int>();
                bool inString = startInString;
                bool escape = false;
                List<int> atHit = null;
                for (int i = 0; i < text.Length; i++)
                {
                    if (i == hitChar) atHit = new List<int>(stack);
                    char c = text[i];
                    if (inString)
                    {
                        if (escape) escape = false;
                        else if (c == '\\') escape = true;
                        else if (c == '"') inString = false;
                        continue;
                    }
                    if (c == '"') inString = true;
                    else if (c == '{') stack.Add(i);
                    else if (c == '}')
                    {
                        if (stack.Count > 0)
                        {
                            int open = stack[stack.Count - 1];
                            stack.RemoveAt(stack.Count - 1);
                            if (atHit != null && atHit.Count > 0 && open == atHit[atHit.Count - 1])
                            {
                                string candidate = text.Substring(open, i - open + 1);
                                bool parses = false;
                                try { parses = JsonReader.AsMap(JsonReader.Parse(candidate)) != null; } catch (Exception) { parses = false; }
                                if (parses) { starts = atHit; end = i; return true; }
                                if (fallbackStarts == null) { fallbackStarts = atHit; fallbackEnd = i; }
                                atHit = null;
                                break;
                            }
                        }
                    }
                }
                if (atHit != null && atHit.Count > 0 && fallbackStarts == null) { fallbackStarts = atHit; fallbackEnd = -1; }
            }
            if (fallbackStarts != null)
            {
                starts = fallbackStarts;
                end = fallbackEnd;
                truncated = fallbackEnd < 0;
                return true;
            }
            return false;
        }

        private static int FindMatchingBrace(string text, int start)
        {
            int depth = 0;
            bool inString = false, escape = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        public static string ClassificationText(string classification)
        {
            switch (classification)
            {
                case "candidate": return "対象 ID の一致（未確認断片）";
                case "other-conversation": return "別の会話の同じ ID（対象外）";
                case "id-elsewhere": return "ID は別レコードのフィールド内（本文はこのメッセージのものではない可能性）";
                case "nearby-only": return "会話 ID・親 ID だけの一致（対象の履歴としては扱わない）";
                default: return classification ?? "";
            }
        }

        public static string ConversationMatchText(string match)
        {
            switch (match)
            {
                case "match": return "入力の会話 ID と一致";
                case "mismatch": return "入力の会話 ID と不一致";
                case "unchecked": return "未照合（入力に会話 ID なし）";
                default: return "会話 ID を判別できず";
            }
        }

        private static string UnescapeJson(string s)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char e = s[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 < s.Length)
                        {
                            int code;
                            if (int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) { sb.Append((char)code); i += 4; }
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- output (private details)

        /// <summary>The text written into one hit file: labelled as an unverified fragment with its provenance.</summary>
        public static string HitFileText(TraceHit h)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("【一致周辺の未確認断片】ID の文字列の近くにあった表示文です。同じメッセージの本文だとは証明されていません。");
            sb.AppendLine("出典: " + h.SourceKind + " / ファイル種別 " + h.FileKind + " / " + h.RelativePath + " / 位置 " + h.Offset.ToString(CultureInfo.InvariantCulture)
                + (h.FromGzip ? " (gzip 展開後 " + h.OffsetInMember.ToString(CultureInfo.InvariantCulture) + ")" : "") + (h.Entry != null ? " (展開後の本文内)" : "") + " / 照合 " + h.NeedleKind + " / " + h.Encoding);
            if (h.Entry != null)
            {
                CacheEntry e = h.Entry;
                sb.AppendLine("キャッシュ項目: 形式 " + e.Format + " / " + (e.FromIndex ? "索引あり" : "索引外（削除・退避済みの残り。他の内容で上書きされている可能性がある）")
                    + " / 項目位置 " + e.Location + " / 状態 " + e.State.ToString(CultureInfo.InvariantCulture) + (e.Child ? " / 部分(sparse)" : "") + " / ヘッダー " + e.HeaderKind
                    + " / 応答日時 " + (e.ResponseTime ?? "不明") + " / 符号化 " + (e.ContentEncoding ?? "(なし)") + " → 展開 " + e.DecodeStatus
                    + " / 本文 " + (e.BodyLocation ?? "?") + " " + e.BodyBytes.ToString(CultureInfo.InvariantCulture) + " バイト → 展開後 " + e.DecodedBytes.ToString(CultureInfo.InvariantCulture) + " バイト");
                sb.AppendLine("キー: " + (e.Key ?? "(読めず)"));
                if (e.StatusLine != null) sb.AppendLine("応答: " + e.StatusLine + (e.ContentType != null ? " / " + e.ContentType : ""));
            }
            sb.AppendLine("断片の種別: " + h.ContextKind + " / 一致位置からの文字数: 開始 " + h.FragmentStartRelative.ToString(CultureInfo.InvariantCulture) + ", 終了 " + h.FragmentEndRelative.ToString(CultureInfo.InvariantCulture)
                + " (0 = 一致位置、負 = 前) / 近くの同種の要素 " + h.OtherElementsNearby.ToString(CultureInfo.InvariantCulture) + " 件（結合していない）");
            sb.AppendLine("切り詰め: 窓 " + (h.WindowTruncated ? "あり(±" + ContextRadius.ToString(CultureInfo.InvariantCulture) + " バイト)" : "なし") + " / 断片 " + (h.FragmentTruncated ? "あり(" + FragmentLimit.ToString(CultureInfo.InvariantCulture) + " 文字)" : "なし"));
            sb.AppendLine("--- 一致を含む要素（無ければ最寄りの要素） ---");
            sb.AppendLine(h.Fragment ?? "(なし)");
            sb.AppendLine("--- 窓の中の同種の要素（位置順。結合していない。開始/終了は一致位置からの文字数） ---");
            if (h.Elements.Count == 0) sb.AppendLine("(なし)");
            int i = 0;
            foreach (TraceElement e in h.Elements)
            {
                i++;
                sb.AppendLine("#" + i.ToString(CultureInfo.InvariantCulture) + " [開始 " + e.StartRelative.ToString(CultureInfo.InvariantCulture) + ", 終了 " + e.EndRelative.ToString(CultureInfo.InvariantCulture) + (e.ContainsHit ? ", 一致を含む" : "") + (e.Truncated ? ", 切り詰め" : "") + "] " + e.Text);
            }
            sb.AppendLine("--- 一致を含む JSON オブジェクト（隣のレコードは含めない） ---");
            if (!h.ObjectFound) sb.AppendLine("(JSON オブジェクトの中ではない)");
            else
            {
                sb.AppendLine("判定: " + ClassificationText(h.Classification) + " / 会話照合: " + ConversationMatchText(h.ConversationMatch)
                    + " / 解析: " + (h.ObjectParsed ? "可" : (h.ObjectTruncated ? "不可（窓の中で閉じない）" : "不可（JSON として読めない）")));
                sb.AppendLine("id: " + (h.ObjectId ?? "(なし)") + (h.ObjectIdIsTarget ? " = 対象のメッセージ ID" : "") + " / clientmessageid: " + (h.ObjectClientMessageId ?? "(なし)")
                    + " / 会話: " + (h.ObjectConversation ?? "(なし)") + " / properties.edittime: " + (h.ObjectEditTime ?? "(なし)") + "（レコードの値。キャッシュの日時ではない）");
                sb.AppendLine("content（テキスト。記録全体から取得）: " + (h.ObjectContentText ?? "(なし)"));
                sb.AppendLine("content（保存形）: " + (h.ObjectContent ?? "(なし)"));
                sb.AppendLine("記録の保存: 長さ " + h.ObjectLength.ToString(CultureInfo.InvariantCulture) + " 文字 / "
                    + (h.ObjectTruncated ? "記録は窓の中で閉じておらず不完全" : "記録は完全に読めた")
                    + (h.ObjectTextCut ? " / 以下の写しは先頭 " + ObjectTextLimit.ToString(CultureInfo.InvariantCulture) + " 文字で切り詰め（上の content は切り詰め前の記録から取得）" : " / 以下の写しは全体"));
                sb.AppendLine("オブジェクト全体:");
                sb.AppendLine(Readable(h.ObjectText));
            }
            sb.AppendLine("--- 照合と選択に使った窓全体（一致位置は文字 " + h.WindowHitIndex.ToString(CultureInfo.InvariantCulture) + "。制御文字は · に置換） ---");
            sb.AppendLine(Readable(h.Window));
            return sb.ToString();
        }

        public sealed class TraceOutput
        {
            public string TracesDir;
            public string ScreenFile;
            public readonly List<string> Screen = new List<string>();
            public readonly List<KeyValuePair<TraceHit, string>> HitFiles = new List<KeyValuePair<TraceHit, string>>();   // hit → path relative to the output folder
        }

        /// <summary>Writes the private details (traces\traces.json, one file per hit) and the counts-only screen (traces.txt)
        /// into an output folder. Shared by the stand-alone -Traces run and the main flow.</summary>
        public static TraceOutput WriteDetails(string outFolder, MessageReference reference, List<TraceSourceResult> results, Discovery discovery, string mode, List<string> notes, string toolVersion)
        {
            TraceOutput o = new TraceOutput();
            o.TracesDir = Path.Combine(outFolder, "traces");
            Directory.CreateDirectory(o.TracesDir);
            OrderedMap full = new OrderedMap();
            full.Set("tool", toolVersion);
            full.Set("createdUtc", TimeText.NowIso());
            full.Set("mode", mode);
            full.Set("notes", new List<object>((notes ?? new List<string>()).ToArray()));
            full.Set("query", reference.ToMap());
            full.Set("note", "hits は一致周辺の未確認断片。同じメッセージの本文とは証明されていない。classification が candidate のものだけが対象 ID の一致で、other-conversation は別会話、nearby-only は会話 ID・親 ID だけの一致。");
            List<object> list = new List<object>();
            foreach (TraceSourceResult r in results) list.Add(ToMap(r, true));
            full.Set("sources", list);
            File.WriteAllText(Path.Combine(o.TracesDir, "traces.json"), JsonWriter.Serialize(full), new UTF8Encoding(false));
            int hitNumber = 0;
            foreach (TraceSourceResult r in results)
            {
                foreach (TraceHit h in r.Hits)
                {
                    hitNumber++;
                    string name = "hit-" + hitNumber.ToString("000", CultureInfo.InvariantCulture) + "-" + (h.Entry != null ? "decoded-" : "") + h.ContextKind + ".txt";
                    File.WriteAllText(Path.Combine(o.TracesDir, name), HitFileText(h), new UTF8Encoding(false));
                    o.HitFiles.Add(new KeyValuePair<TraceHit, string>(h, Path.Combine("traces", name)));
                }
            }
            o.Screen.AddRange(ScreenLines(results, reference, discovery));
            StringBuilder sb = new StringBuilder();
            foreach (string line in o.Screen) sb.AppendLine(line);
            o.ScreenFile = Path.Combine(outFolder, "traces.txt");
            File.WriteAllText(o.ScreenFile, sb.ToString(), new UTF8Encoding(false));
            return o;
        }

        private static string Readable(string s)
        {
            if (s == null) return "(なし)";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '\n' || c == '\r' || c == '\t') sb.Append(c);
                else if (char.IsControl(c) || c == '�') sb.Append('·');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static OrderedMap ToMap(TraceSourceResult r, bool includeDetails)
        {
            OrderedMap m = new OrderedMap();
            m.Set("kind", r.Source.Kind);
            m.Set("label", r.Source.Label);
            m.Set("folder", includeDetails ? r.Source.Folder : null);
            m.Set("enumerationFailed", r.Source.EnumerationFailed);
            m.Set("enumerationErrorKind", r.Source.EnumerationErrorKind);
            m.Set("enumerationErrorDetail", includeDetails ? r.Source.EnumerationErrorDetail : null);
            m.Set("files", r.Source.Files.Count);
            m.Set("bytes", r.Source.Bytes);
            m.Set("filesScanned", r.FilesScanned);
            m.Set("filesTooLarge", r.FilesTooLarge);
            m.Set("filesReadFailed", r.FilesReadFailed);
            m.Set("bytesScanned", r.BytesScanned);
            m.Set("gzipMembers", r.GzipMembers);
            m.Set("gzipFailures", r.GzipFailures);
            m.Set("gzipMemberCapReached", r.GzipMemberCapReached);
            m.Set("gzipOutputTruncated", r.GzipOutputTruncated);
            m.Set("hitCapReached", r.HitCapReached);
            OrderedMap limits = new OrderedMap();
            limits.Set("maxFileBytes", MaxFileBytes);
            limits.Set("maxHitsPerNeedlePerBuffer", MaxHitsPerNeedlePerBuffer);
            limits.Set("maxGzipMembersPerFile", MaxGzipMembersPerFile);
            limits.Set("maxGzipOutputBytes", MaxGzipOutput);
            limits.Set("contextRadiusBytes", ContextRadius);
            limits.Set("fragmentLimitChars", FragmentLimit);
            m.Set("limits", limits);
            m.Set("filesByKind", Dict(r.FilesByKind));
            m.Set("hitsByNeedle", Dict(r.HitsByNeedle));
            m.Set("fragmentsByKind", Dict(r.FragmentsByKind));
            m.Set("distinctFragments", r.DistinctFragments);
            m.Set("cache", CacheMap(r.Cache, includeDetails));
            m.Set("error", r.Error);
            if (includeDetails)
            {
                List<object> skipped = new List<object>();
                foreach (KeyValuePair<string, string> s in r.SkippedFiles)
                {
                    OrderedMap sm = new OrderedMap();
                    sm.Set("relativePath", s.Key);
                    sm.Set("reason", s.Value);
                    skipped.Add(sm);
                }
                m.Set("skippedFiles", skipped);
                List<object> hits = new List<object>();
                foreach (TraceHit h in r.Hits)
                {
                    OrderedMap hm = new OrderedMap();
                    hm.Set("fileKind", h.FileKind);
                    hm.Set("relativePath", h.RelativePath);
                    hm.Set("offset", h.Offset);
                    hm.Set("offsetInMember", h.OffsetInMember);
                    hm.Set("needle", h.NeedleKind);
                    hm.Set("encoding", h.Encoding);
                    hm.Set("fromGzip", h.FromGzip);
                    hm.Set("fragmentKind", h.ContextKind);
                    hm.Set("fragment", h.Fragment);
                    hm.Set("fragmentStartRelative", h.FragmentStartRelative);
                    hm.Set("fragmentEndRelative", h.FragmentEndRelative);
                    hm.Set("otherElementsNearby", h.OtherElementsNearby);
                    hm.Set("windowTruncated", h.WindowTruncated);
                    hm.Set("fragmentTruncated", h.FragmentTruncated);
                    List<object> elements = new List<object>();
                    foreach (TraceElement e in h.Elements)
                    {
                        OrderedMap em = new OrderedMap();
                        em.Set("startRelative", e.StartRelative);
                        em.Set("endRelative", e.EndRelative);
                        em.Set("containsHit", e.ContainsHit);
                        em.Set("truncated", e.Truncated);
                        em.Set("text", e.Text);
                        elements.Add(em);
                    }
                    hm.Set("elements", elements);
                    hm.Set("windowHitIndex", h.WindowHitIndex);
                    hm.Set("window", h.Window);
                    hm.Set("classification", h.Classification);
                    hm.Set("conversationMatch", h.ConversationMatch);
                    OrderedMap om = new OrderedMap();
                    om.Set("found", h.ObjectFound);
                    om.Set("parsed", h.ObjectParsed);
                    om.Set("truncated", h.ObjectTruncated);
                    om.Set("length", h.ObjectLength);
                    om.Set("textCut", h.ObjectTextCut);
                    om.Set("id", h.ObjectId);
                    om.Set("idIsTarget", h.ObjectIdIsTarget);
                    om.Set("clientMessageId", h.ObjectClientMessageId);
                    om.Set("conversation", h.ObjectConversation);
                    om.Set("editTime", h.ObjectEditTime);
                    om.Set("contentText", h.ObjectContentText);
                    om.Set("content", h.ObjectContent);
                    om.Set("text", h.ObjectText);
                    hm.Set("object", om);
                    if (h.Entry != null)
                    {
                        OrderedMap ce = new OrderedMap();
                        ce.Set("format", h.Entry.Format);
                        ce.Set("location", h.Entry.Location);
                        ce.Set("fromIndex", h.Entry.FromIndex);
                        ce.Set("key", h.Entry.Key);
                        ce.Set("headerKind", h.Entry.HeaderKind);
                        ce.Set("contentEncoding", h.Entry.ContentEncoding);
                        ce.Set("decodeStatus", h.Entry.DecodeStatus);
                        ce.Set("responseTime", h.Entry.ResponseTime);
                        hm.Set("cacheEntry", ce);
                    }
                    hm.Set("note", "一致周辺の未確認断片。同じメッセージの本文とは証明されていない。elements は窓内の同種要素を位置順に列挙（結合なし）。");
                    hits.Add(hm);
                }
                m.Set("hits", hits);
            }
            return m;
        }

        private static OrderedMap CacheMap(CacheStats c, bool includeDetails)
        {
            OrderedMap m = new OrderedMap();
            m.Set("format", c.FormatLabel);
            m.Set("directories", c.Directories);
            m.Set("blockfileDirectories", c.BlockfileDirs);
            m.Set("simpleDirectories", c.SimpleDirs);
            m.Set("entries", c.Entries);
            m.Set("fromIndex", c.FromIndex);
            m.Set("orphan", c.Orphan);
            m.Set("unreadable", c.Unreadable);
            m.Set("keyHashMismatch", c.KeyHashMismatch);
            m.Set("otherFiles", c.OtherFiles);
            m.Set("bodiesRead", c.BodiesRead);
            m.Set("bodiesUnreadable", c.BodiesUnreadable);
            m.Set("bodiesEmpty", c.BodiesEmpty);
            m.Set("bodiesMedia", c.BodiesMedia);
            m.Set("bodiesTooLarge", c.BodiesTooLarge);
            m.Set("encodings", Dict(c.Encodings));
            m.Set("headerKinds", Dict(c.HeaderKinds));
            m.Set("decodeOk", c.DecodeOk);
            m.Set("decodeFailed", c.DecodeFailed);
            m.Set("decodeUnsupported", c.DecodeUnsupported);
            m.Set("decodeTruncated", c.DecodeTruncated);
            m.Set("decodeAsIs", c.DecodeAsIs);
            m.Set("decodeSkippedByTotalCap", c.DecodeSkippedCap);
            m.Set("decodedBytes", c.DecodedBytes);
            m.Set("keyHitsByNeedle", Dict(c.KeyHitsByNeedle));
            m.Set("entriesWithKeyHit", c.EntriesWithKeyHit);
            m.Set("bodyHitsByNeedle", Dict(c.BodyHitsByNeedle));
            OrderedMap limits = new OrderedMap();
            limits.Set("maxDecodedBytesPerBody", BodyDecoder.MaxDecodedBytes);
            limits.Set("maxTotalDecodedBytes", MaxTotalDecodedBytes);
            m.Set("limits", limits);
            m.Set("note", "entries は項目ごとの出典（キー・位置・ヘッダー・符号化・展開結果）。索引外は削除・退避済みの残りで、上書きされている可能性がある。");
            if (includeDetails)
            {
                m.Set("warnings", new List<object>(c.Warnings.ToArray()));
                List<object> entries = new List<object>();
                foreach (CacheEntry e in c.EntryList)
                {
                    OrderedMap em = new OrderedMap();
                    em.Set("format", e.Format);
                    em.Set("directory", e.Directory);
                    em.Set("location", e.Location);
                    em.Set("fromIndex", e.FromIndex);
                    em.Set("allocated", e.Allocated);
                    em.Set("state", e.State);
                    em.Set("child", e.Child);
                    em.Set("keyHashMatches", e.KeyHashChecked ? (object)e.KeyHashMatches : null);
                    em.Set("key", e.Key);
                    em.Set("headerKind", e.HeaderKind);
                    em.Set("statusLine", e.StatusLine);
                    em.Set("contentType", e.ContentType);
                    em.Set("contentEncoding", e.ContentEncoding);
                    em.Set("encodingClass", e.EncodingClass);
                    em.Set("responseTime", e.ResponseTime);
                    em.Set("bodyLocation", e.BodyLocation);
                    em.Set("bodyBytes", e.BodyBytes);
                    em.Set("bodyStatus", e.BodyStatus);
                    em.Set("decodeStatus", e.DecodeStatus);
                    em.Set("decodedBytes", e.DecodedBytes);
                    em.Set("keyHits", Dict(e.KeyHits));
                    em.Set("bodyHits", Dict(e.BodyHits));
                    em.Set("error", e.Error);
                    entries.Add(em);
                }
                m.Set("entries", entries);
            }
            return m;
        }

        private static OrderedMap Dict(Dictionary<string, long> d)
        {
            OrderedMap m = new OrderedMap();
            List<string> keys = new List<string>(d.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (string k in keys) m.Set(k, d[k]);
            return m;
        }

        // ---------------------------------------------------------------- screen (counts only, aggregated per fixed kind)

        private sealed class KindTotals
        {
            public int Sources;
            public int EnumerationFailed;
            public long Files, Scanned, TooLarge, ReadFailed, Bytes, Gzip, GzipFailures, GzipCap, GzipOut, HitCap;
            public long Db, Wal, Shm, Entry, Index, Block, Other;
            public long IdHits, ParentHits, ConvHits;
            public long Toast, Json, Html, Text, Distinct;
            // structural cache pass
            public long CacheDirs, CacheBlockfile, CacheSimple, Entries, FromIndex, Orphan, Unreadable, HashMismatch;
            public long BodiesRead, BodiesUnreadable, BodiesEmpty, BodiesMedia, BodiesTooLarge;
            public long EncIdentity, EncGzip, EncBr, EncDeflate, EncZstd, EncOther, EncUnknown;
            public long DecodeOk, DecodeFailed, DecodeUnsupported, DecodeAsIs, DecodeTruncated, DecodeSkipped;
            public long BodyIdHits, BodyParentHits, BodyConvHits, EntriesWithKeyHit;
        }

        private static long Get(Dictionary<string, long> d, string key)
        {
            long n;
            return d.TryGetValue(key, out n) ? n : 0;
        }

        private static string N(long n)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }

        private static string Mb(long bytes)
        {
            return (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + "MB";
        }

        /// <summary>Never cuts: a line wider than the screen is wrapped at its " | " separators onto continuation lines.</summary>
        private static void Add(List<string> lines, string text)
        {
            if (DiagnosticsCollector.DisplayWidth(text) <= MaxColumns) { lines.Add(text); return; }
            string[] parts = text.Split(new string[] { " | " }, StringSplitOptions.None);
            string indent = "    ";
            StringBuilder current = new StringBuilder();
            foreach (string part in parts)
            {
                string candidate = current.Length == 0 ? part : current + " | " + part;
                if (current.Length > 0 && DiagnosticsCollector.DisplayWidth(candidate) > MaxColumns)
                {
                    lines.Add(current.ToString());
                    current.Length = 0;
                    current.Append(indent + part);
                }
                else
                {
                    current.Length = 0;
                    current.Append(candidate);
                }
            }
            if (current.Length > 0) lines.Add(current.ToString());
        }

        /// <summary>At most 25 lines, 96 columns: one block per fixed source kind (profiles merged), limits and
        /// unscanned files visible, no ids, no names, no paths, no fragments.</summary>
        public static List<string> ScreenLines(List<TraceSourceResult> results, MessageReference reference, Discovery discovery)
        {
            List<string> lines = new List<string>();
            Add(lines, "==== 痕跡走査（IndexedDB 以外）v" + Program.Version + " | " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " ====");
            Add(lines, "[照合] ID あり | 親 " + (!string.IsNullOrEmpty(reference.ParentMessageId) && reference.ParentMessageId != reference.MessageId ? "あり" : "なし")
                + " | 会話 " + (!string.IsNullOrEmpty(reference.ConversationId) ? "あり(URL符号化も)" : "なし") + " | 展開 gzip/br/deflate | 未対応 zstd");
            Dictionary<string, KindTotals> totals = new Dictionary<string, KindTotals>(StringComparer.Ordinal);
            foreach (string k in new string[] { "wpn", "httpcache", "swcache", "custom" }) totals[k] = new KindTotals();
            foreach (TraceSourceResult r in results)
            {
                KindTotals t;
                if (!totals.TryGetValue(r.Source.Kind, out t)) { t = new KindTotals(); totals[r.Source.Kind] = t; }
                t.Sources++;
                if (r.Source.EnumerationFailed || r.Error != null) { t.EnumerationFailed++; continue; }
                t.Files += r.Source.Files.Count; t.Scanned += r.FilesScanned; t.TooLarge += r.FilesTooLarge; t.ReadFailed += r.FilesReadFailed; t.Bytes += r.BytesScanned;
                t.Gzip += r.GzipMembers; t.GzipFailures += r.GzipFailures; t.GzipCap += r.GzipMemberCapReached; t.GzipOut += r.GzipOutputTruncated; t.HitCap += r.HitCapReached;
                t.Db += Get(r.FilesByKind, "db"); t.Wal += Get(r.FilesByKind, "wal"); t.Shm += Get(r.FilesByKind, "shm");
                t.Entry += Get(r.FilesByKind, "entry"); t.Index += Get(r.FilesByKind, "index"); t.Block += Get(r.FilesByKind, "block"); t.Other += Get(r.FilesByKind, "other");
                t.IdHits += Get(r.HitsByNeedle, "messageId"); t.ParentHits += Get(r.HitsByNeedle, "parentMessageId"); t.ConvHits += Get(r.HitsByNeedle, "conversationId");
                t.Toast += Get(r.FragmentsByKind, "toast"); t.Json += Get(r.FragmentsByKind, "json"); t.Html += Get(r.FragmentsByKind, "html"); t.Text += Get(r.FragmentsByKind, "text");
                t.Distinct += r.DistinctFragments;
                CacheStats c = r.Cache;
                t.CacheDirs += c.Directories; t.CacheBlockfile += c.BlockfileDirs; t.CacheSimple += c.SimpleDirs;
                t.Entries += c.Entries; t.FromIndex += c.FromIndex; t.Orphan += c.Orphan; t.Unreadable += c.Unreadable; t.HashMismatch += c.KeyHashMismatch;
                t.BodiesRead += c.BodiesRead; t.BodiesUnreadable += c.BodiesUnreadable; t.BodiesEmpty += c.BodiesEmpty; t.BodiesMedia += c.BodiesMedia; t.BodiesTooLarge += c.BodiesTooLarge;
                t.EncIdentity += Get(c.Encodings, "identity"); t.EncGzip += Get(c.Encodings, "gzip"); t.EncBr += Get(c.Encodings, "br"); t.EncDeflate += Get(c.Encodings, "deflate");
                t.EncZstd += Get(c.Encodings, "zstd"); t.EncOther += Get(c.Encodings, "other"); t.EncUnknown += Get(c.Encodings, "unknown");
                t.DecodeOk += c.DecodeOk; t.DecodeFailed += c.DecodeFailed; t.DecodeUnsupported += c.DecodeUnsupported; t.DecodeAsIs += c.DecodeAsIs; t.DecodeTruncated += c.DecodeTruncated; t.DecodeSkipped += c.DecodeSkippedCap;
                t.BodyIdHits += Get(c.BodyHitsByNeedle, "messageId"); t.BodyParentHits += Get(c.BodyHitsByNeedle, "parentMessageId"); t.BodyConvHits += Get(c.BodyHitsByNeedle, "conversationId");
                t.EntriesWithKeyHit += c.EntriesWithKeyHit;
            }
            if (discovery != null)
            {
                Add(lines, "[列挙] " + (discovery.LocalAppDataMissing ? "LOCALAPPDATA なし" : "通知DB " + (discovery.NotificationFolderMissing ? "見つからず" : "あり") + " | Teams プロファイル " + N(discovery.TeamsProfiles))
                    + " | 列挙失敗 " + N(discovery.EnumerationFailures) + " | 保存元 " + N(results.Count));
            }
            else Add(lines, "[列挙] 指定フォルダー " + N(results.Count) + " 件");
            bool any = false;
            foreach (string kind in new string[] { "wpn", "httpcache", "swcache", "custom" })
            {
                KindTotals t = totals[kind];
                if (t.Sources == 0) continue;
                any = true;
                string label = kind == "wpn" ? "通知DB" : kind == "httpcache" ? "HTTPキャッシュ" : kind == "swcache" ? "SWキャッシュ" : "指定フォルダー";
                string fileKinds = kind == "wpn" ? "db " + N(t.Db) + " / wal " + N(t.Wal) + " / shm " + N(t.Shm) : "entry " + N(t.Entry) + " / index " + N(t.Index) + " / block " + N(t.Block) + " / 他 " + N(t.Other);
                Add(lines, "[" + label + "] 保存元 " + N(t.Sources) + " (列挙失敗 " + N(t.EnumerationFailed) + ") | ファイル " + N(t.Files) + " | 走査 " + Mb(t.Bytes));
                Add(lines, "  種別 " + fileKinds + " | gzip " + N(t.Gzip) + " (失敗 " + N(t.GzipFailures) + ") | 未走査 " + N(t.TooLarge + t.ReadFailed) + " (超過 " + N(t.TooLarge) + " / 読取失敗 " + N(t.ReadFailed) + ")");
                Add(lines, "  一致 ID " + N(t.IdHits) + " / 親 " + N(t.ParentHits) + " / 会話 " + N(t.ConvHits) + " | 断片 toast " + N(t.Toast) + " / json " + N(t.Json) + " / html " + N(t.Html) + " / text " + N(t.Text) + " | 異なる " + N(t.Distinct));
                if (kind != "wpn" || t.CacheDirs > 0)
                {
                    string format = t.CacheBlockfile > 0 && t.CacheSimple > 0 ? "混在" : t.CacheBlockfile > 0 ? "blockfile" : t.CacheSimple > 0 ? "simple" : "なし";
                    Add(lines, "  項目 " + format + " " + N(t.Entries) + " | 索引 " + N(t.FromIndex) + " / 索引外 " + N(t.Orphan) + " / 読めず " + N(t.Unreadable) + " / hash不一致 " + N(t.HashMismatch));
                    Add(lines, "  本文 読取 " + N(t.BodiesRead) + " / 不可 " + N(t.BodiesUnreadable) + " / 空 " + N(t.BodiesEmpty) + " / 媒体 " + N(t.BodiesMedia) + " / 超過 " + N(t.BodiesTooLarge));
                    Add(lines, "  符号化 無し " + N(t.EncIdentity) + " / gzip " + N(t.EncGzip) + " / br " + N(t.EncBr) + " / deflate " + N(t.EncDeflate) + " / zstd " + N(t.EncZstd) + " / 他 " + N(t.EncOther) + " / 不明 " + N(t.EncUnknown));
                    Add(lines, "  展開 成功 " + N(t.DecodeOk) + " / 失敗 " + N(t.DecodeFailed) + " / 未対応 " + N(t.DecodeUnsupported) + " / そのまま " + N(t.DecodeAsIs)
                        + " | 展開後 ID " + N(t.BodyIdHits) + " / 親 " + N(t.BodyParentHits) + " / 会話 " + N(t.BodyConvHits) + " | キー " + N(t.EntriesWithKeyHit));
                }
                if (t.HitCap > 0 || t.GzipCap > 0 || t.GzipOut > 0 || t.DecodeTruncated > 0 || t.DecodeSkipped > 0)
                    Add(lines, "  打切り 一致上限 " + N(t.HitCap) + " / gzip数上限 " + N(t.GzipCap) + " / gzip展開上限 " + N(t.GzipOut) + " / 本文展開上限 " + N(t.DecodeTruncated) + " / 展開合計上限 " + N(t.DecodeSkipped) + " | その分は調べていない");
            }
            if (!any) Add(lines, "[痕跡] 走査した保存元はありません（この走査では何も調べていない）");
            Add(lines, "[注] 一致は ID の位置数。断片は未確認で本文の証明ではない。0 は「この走査では見つからず」。");
            Add(lines, "  未走査・打切り・列挙失敗は未調査。断片・窓・キーは traces フォルダーのみ（共有しない）。");
            return lines;
        }
    }
}
