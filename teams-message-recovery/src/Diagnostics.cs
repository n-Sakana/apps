// Teams Message History - one-screen diagnostics (why only one body may remain).
// Everything here is derived from the preserved copy and the analysis already done. The screen shows
// counts, dates and fixed labels only: no message text, no names, no ids, no folder names, no paths,
// no URLs, no free-form strings from the data.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TeamsMessageHistory
{
    /// <summary>Current version state recorded in the newest MANIFEST file (which files belong to which level).</summary>
    public sealed class ManifestSummary
    {
        public long LastSequence;
        public long LogNumber;
        public int Edits;
        public readonly Dictionary<int, int> FilesPerLevel = new Dictionary<int, int>();
        public readonly HashSet<long> CurrentFiles = new HashSet<long>();
        public bool Readable;
    }

    public static class ManifestReader
    {
        public static ManifestSummary Read(string levelDbFolder)
        {
            ManifestSummary summary = new ManifestSummary();
            try
            {
                string[] manifests = Directory.GetFiles(levelDbFolder, "MANIFEST-*");
                if (manifests.Length == 0) return summary;
                // CURRENT names the manifest in use; otherwise take the highest file number (numeric, any digit count)
                string chosen = null;
                string currentPath = Path.Combine(levelDbFolder, "CURRENT");
                if (File.Exists(currentPath))
                {
                    string named = File.ReadAllText(currentPath, Encoding.ASCII).Trim();
                    string candidate = Path.Combine(levelDbFolder, named);
                    if (named.StartsWith("MANIFEST-", StringComparison.Ordinal) && File.Exists(candidate)) chosen = candidate;
                }
                if (chosen == null)
                {
                    long best = -1;
                    foreach (string m in manifests)
                    {
                        long n;
                        string digits = Path.GetFileName(m).Substring("MANIFEST-".Length);
                        if (long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > best) { best = n; chosen = m; }
                    }
                    if (chosen == null) chosen = manifests[manifests.Length - 1];
                }
                byte[] data = File.ReadAllBytes(chosen);
                Dictionary<long, int> fileLevel = new Dictionary<long, int>();
                foreach (byte[] payload in ReadLogPayloads(data))
                {
                    summary.Edits++;
                    ApplyVersionEdit(payload, summary, fileLevel);
                }
                foreach (KeyValuePair<long, int> pair in fileLevel)
                {
                    summary.CurrentFiles.Add(pair.Key);
                    int n;
                    summary.FilesPerLevel.TryGetValue(pair.Value, out n);
                    summary.FilesPerLevel[pair.Value] = n + 1;
                }
                summary.Readable = summary.Edits > 0;
            }
            catch (Exception)
            {
                summary.Readable = false;
            }
            return summary;
        }

        private static List<byte[]> ReadLogPayloads(byte[] data)
        {
            List<byte[]> payloads = new List<byte[]>();
            const int BlockSize = 32768;
            List<byte> pending = null;
            for (int blockStart = 0; blockStart < data.Length; blockStart += BlockSize)
            {
                int blockEnd = Math.Min(blockStart + BlockSize, data.Length);
                int pos = blockStart;
                while (blockEnd - pos >= 7)
                {
                    int length = data[pos + 4] | (data[pos + 5] << 8);
                    int type = data[pos + 6];
                    int payloadStart = pos + 7;
                    if (type == 0 && length == 0) break;
                    if (payloadStart + length > blockEnd) break;
                    if (type == 1)
                    {
                        byte[] full = new byte[length];
                        Buffer.BlockCopy(data, payloadStart, full, 0, length);
                        payloads.Add(full);
                        pending = null;
                    }
                    else if (type == 2)
                    {
                        pending = new List<byte>(length);
                        for (int i = 0; i < length; i++) pending.Add(data[payloadStart + i]);
                    }
                    else if (type == 3 && pending != null)
                    {
                        for (int i = 0; i < length; i++) pending.Add(data[payloadStart + i]);
                    }
                    else if (type == 4 && pending != null)
                    {
                        for (int i = 0; i < length; i++) pending.Add(data[payloadStart + i]);
                        payloads.Add(pending.ToArray());
                        pending = null;
                    }
                    else break;
                    pos = payloadStart + length;
                }
            }
            return payloads;
        }

        // See leveldb/db/version_edit.cc: tags 1 comparator, 2 log number, 3 next file number, 4 last sequence,
        // 5 compact pointer, 6 deleted file, 7 new file, 9 previous log number.
        private static void ApplyVersionEdit(byte[] buf, ManifestSummary summary, Dictionary<long, int> fileLevel)
        {
            int pos = 0;
            int end = buf.Length;
            while (pos < end)
            {
                ulong tag;
                if (!Varint.TryRead(buf, ref pos, end, out tag)) return;
                ulong v1, v2, v3;
                int len;
                switch (tag)
                {
                    case 1:
                        if (!Varint.TryReadInt(buf, ref pos, end, out len) || pos + len > end) return;
                        pos += len;
                        break;
                    case 2:
                        if (!Varint.TryRead(buf, ref pos, end, out v1)) return;
                        summary.LogNumber = (long)v1;
                        break;
                    case 9:
                    case 3:
                        if (!Varint.TryRead(buf, ref pos, end, out v1)) return;
                        break;
                    case 4:
                        if (!Varint.TryRead(buf, ref pos, end, out v1)) return;
                        summary.LastSequence = (long)v1;
                        break;
                    case 5:
                        if (!Varint.TryRead(buf, ref pos, end, out v1)) return;
                        if (!Varint.TryReadInt(buf, ref pos, end, out len) || pos + len > end) return;
                        pos += len;
                        break;
                    case 6:
                        if (!Varint.TryRead(buf, ref pos, end, out v1) || !Varint.TryRead(buf, ref pos, end, out v2)) return;
                        fileLevel.Remove((long)v2);
                        break;
                    case 7:
                        if (!Varint.TryRead(buf, ref pos, end, out v1) || !Varint.TryRead(buf, ref pos, end, out v2) || !Varint.TryRead(buf, ref pos, end, out v3)) return;
                        if (!Varint.TryReadInt(buf, ref pos, end, out len) || pos + len > end) return;
                        pos += len;
                        if (!Varint.TryReadInt(buf, ref pos, end, out len) || pos + len > end) return;
                        pos += len;
                        fileLevel[(long)v2] = (int)v1;
                        break;
                    default:
                        return;
                }
            }
        }
    }

    /// <summary>Events found in the LevelDB text log (LOG, LOG.old): counts and times only.</summary>
    public sealed class LevelDbLogSummary
    {
        public int Lines;
        public int Compactions;          // "Compacting a@x + b@y files"
        public DateTime? LastCompaction;
        public int ManualCompactions;    // "Manual compaction" (whole-range compaction requested by the application)
        public DateTime? LastManualCompaction;
        public int Level0Flushes;        // "Level-0 table #n: started"
        public DateTime? LastLevel0Flush;
        public int LogReuses;            // "Reusing old log"
        public int Recoveries;           // "Recovering log"
        public DateTime? First;
        public DateTime? Last;
        public bool Readable;
    }

    public static class LevelDbLogText
    {
        private static readonly Regex Stamp = new Regex(@"^(\d{4})/(\d\d)/(\d\d)-(\d\d):(\d\d):(\d\d)", RegexOptions.Compiled);

        public static LevelDbLogSummary Read(string levelDbFolder)
        {
            LevelDbLogSummary s = new LevelDbLogSummary();
            try
            {
                foreach (string name in new string[] { "LOG.old", "LOG" })
                {
                    string path = Path.Combine(levelDbFolder, name);
                    if (!File.Exists(path)) continue;
                    s.Readable = true;
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        s.Lines++;
                        DateTime? t = ParseStamp(line);
                        if (t.HasValue)
                        {
                            if (!s.First.HasValue || t.Value < s.First.Value) s.First = t;
                            if (!s.Last.HasValue || t.Value > s.Last.Value) s.Last = t;
                        }
                        if (line.IndexOf("Compacting ", StringComparison.Ordinal) >= 0 && line.IndexOf(" files", StringComparison.Ordinal) >= 0)
                        {
                            s.Compactions++;
                            if (t.HasValue) s.LastCompaction = t;
                        }
                        if (line.IndexOf("Manual compaction", StringComparison.Ordinal) >= 0)
                        {
                            s.ManualCompactions++;
                            if (t.HasValue) s.LastManualCompaction = t;
                        }
                        if (line.IndexOf("Level-0 table #", StringComparison.Ordinal) >= 0 && line.IndexOf("started", StringComparison.Ordinal) >= 0)
                        {
                            s.Level0Flushes++;
                            if (t.HasValue) s.LastLevel0Flush = t;
                        }
                        if (line.IndexOf("Reusing old log", StringComparison.Ordinal) >= 0) s.LogReuses++;
                        if (line.IndexOf("Recovering log", StringComparison.Ordinal) >= 0) s.Recoveries++;
                    }
                }
            }
            catch (Exception)
            {
                s.Readable = false;
            }
            return s;
        }

        private static DateTime? ParseStamp(string line)
        {
            Match m = Stamp.Match(line);
            if (!m.Success) return null;
            try
            {
                return new DateTime(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture), DateTimeKind.Local);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public sealed class DiagnosticReport
    {
        public readonly OrderedMap Data = new OrderedMap();
        public readonly List<string> Lines = new List<string>();
    }

    /// <summary>Builds the diagnostic screen for one analyzed source.</summary>
    public sealed class DiagnosticsCollector
    {
        private const string ConsumerTenant = "9188040d-6c67-4c5b-b112-36a304b66dad"; // Microsoft account (consumer) tenant id; compared, never printed
        private const int MaxColumns = 96;

        private readonly RunContext _ctx;
        private readonly AnalyzedSource _source;
        private readonly SourceCandidate _candidate;
        private readonly PreservedCopy _copy;
        private readonly MessageReference _reference;

        public DiagnosticsCollector(RunContext ctx, AnalyzedSource source, SourceCandidate candidate, PreservedCopy copy)
        {
            _ctx = ctx;
            _source = source;
            _candidate = candidate;
            _copy = copy;
            _reference = ctx.Reference;
        }

        // ---------------------------------------------------------------- formatting helpers

        private static string Local(DateTime? t)
        {
            if (!t.HasValue) return "不明";
            DateTime v = t.Value.Kind == DateTimeKind.Local ? t.Value : t.Value.ToLocalTime();
            return v.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        private static string LocalSeconds(DateTime? t)
        {
            if (!t.HasValue) return "不明";
            DateTime v = t.Value.Kind == DateTimeKind.Local ? t.Value : t.Value.ToLocalTime();
            return v.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static object Json(DateTime? t)
        {
            if (!t.HasValue) return null;
            return t.Value.Kind == DateTimeKind.Local ? t.Value.ToUniversalTime() : t.Value;
        }

        private static DateTime? FromIsoOrMillis(string value)
        {
            if (value == null) return null;
            DateTime parsed;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed)) return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            double millis;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out millis)) return null;
            if (millis < 946684800000.0 || millis > 4102444800000.0) return null;
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(millis);
        }

        private static string Mb(long bytes)
        {
            return (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + "MB";
        }

        private static string YesNo(bool? v)
        {
            if (!v.HasValue) return "不明";
            return v.Value ? "はい" : "いいえ";
        }

        private static string N(long n)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Display width with East Asian characters counted as two columns.</summary>
        public static int DisplayWidth(string s)
        {
            int w = 0;
            foreach (char c in s)
            {
                if (c >= 0x1100 && (c <= 0x115F || c == 0x2329 || c == 0x232A || (c >= 0x2E80 && c <= 0xA4CF) || (c >= 0xAC00 && c <= 0xD7A3)
                    || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE30 && c <= 0xFE4F) || (c >= 0xFF00 && c <= 0xFF60) || (c >= 0xFFE0 && c <= 0xFFE6))) w += 2;
                else w += 1;
            }
            return w;
        }

        private static void Add(List<string> lines, string text)
        {
            if (DisplayWidth(text) <= MaxColumns)
            {
                lines.Add(text);
                return;
            }
            StringBuilder sb = new StringBuilder();
            foreach (char c in text)
            {
                if (DisplayWidth(sb.ToString()) + (c > 0x2E7F ? 2 : 1) > MaxColumns - 1) break;
                sb.Append(c);
            }
            lines.Add(sb.ToString() + "…");
        }

        // ---------------------------------------------------------------- classification (fixed labels only)

        private string ConversationKind()
        {
            if (_reference.ConversationId == null) return "不明";
            string c = _reference.ConversationId.ToLowerInvariant();
            if (c == "48:notes") return "自分宛てチャット";
            if (c.EndsWith("@thread.tacv2") || c.EndsWith("@thread.skype")) return "チャネル";
            if (c.EndsWith("@unq.gbl.spaces")) return "1対1チャット";
            if (c.EndsWith("@thread.v2")) return "チャット";
            return "その他";
        }

        private string ProfileKind()
        {
            foreach (string part in _candidate.LevelDbPath.Split('\\', '/'))
            {
                string p = part.ToLowerInvariant();
                if (p == "wv2profile_tfw") return "職場/学校プロファイル";
                if (p == "wv2profile_tfl") return "個人プロファイル";
                if (p.StartsWith("wv2profile", StringComparison.Ordinal)) return "その他のプロファイル";
            }
            return _candidate.Kind == "custom" ? "採取済みコピー" : "プロファイル名なし";
        }

        private string OriginKind()
        {
            string name = Path.GetFileName(_candidate.LevelDbPath.TrimEnd('\\', '/')).ToLowerInvariant();
            if (name.StartsWith("https_teams.microsoft.com_", StringComparison.Ordinal)) return "teams.microsoft.com";
            if (name.StartsWith("https_teams.live.com_", StringComparison.Ordinal)) return "teams.live.com";
            if (name.StartsWith("https_teams.cloud.microsoft_", StringComparison.Ordinal)) return "teams.cloud.microsoft";
            if (name.StartsWith("https_teams.", StringComparison.Ordinal)) return "teams.* (その他)";
            return "その他のオリジン";
        }

        private string AccountKind(IndexedDbReader reader)
        {
            // Database names carry tenant and user ids in either order; a database belongs to a personal account when
            // the consumer tenant id appears among its id segments, otherwise to an organization when a GUID appears.
            int consumer = 0, org = 0;
            foreach (IdbDatabaseInfo info in reader.Databases.Values)
            {
                if (info.Name == null) continue;
                int idx = info.Name.IndexOf(":react-web-client:", StringComparison.Ordinal);
                if (idx < 0) continue;
                string[] segments = info.Name.Substring(idx + ":react-web-client:".Length).Split(':');
                bool isConsumer = false, hasGuid = false;
                foreach (string seg in segments)
                {
                    if (string.Equals(seg, ConsumerTenant, StringComparison.OrdinalIgnoreCase)) isConsumer = true;
                    else if (seg.Length == 36 && seg.Split('-').Length == 5 && !seg.StartsWith("00000000-0000-0000-", StringComparison.Ordinal)) hasGuid = true;
                }
                if (isConsumer) consumer++;
                else if (hasGuid) org++;
            }
            if (consumer > 0 && org == 0) return "個人";
            if (org > 0 && consumer == 0) return "組織";
            if (org > 0 && consumer > 0) return "個人+組織";
            return "不明";
        }

        private static string SourceKindText(string kind)
        {
            switch (kind)
            {
                case "teams-new": return "新しいTeams";
                case "teams-classic": return "従来のTeams";
                case "edge": return "Edge";
                case "chrome": return "Chrome";
                default: return "指定フォルダー";
            }
        }

        private static string InputKindText(string kind)
        {
            switch (kind)
            {
                case "link": return "リンク";
                case "id": return "IDのみ";
                case "conversation+id": return "会話ID+ID";
                default: return "その他";
            }
        }

        // ---------------------------------------------------------------- build

        public DiagnosticReport Build()
        {
            DiagnosticReport r = new DiagnosticReport();
            OrderedMap d = r.Data;
            List<string> L = r.Lines;
            IndexedDbReader reader = _source.Reader;
            FinderResult result = _source.Result;
            SearchStats st = result != null ? result.Stats : new SearchStats();

            string osVersion;
            try { osVersion = Environment.OSVersion.Version.ToString(); } catch (Exception) { osVersion = "?"; }
            Add(L, "==== Teams メッセージ履歴 診断 v" + Program.Version + " | " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " | OS " + osVersion + " ====");

            // input
            string conversationKind = ConversationKind();
            d.Set("inputKind", _reference.InputKind);
            d.Set("conversationKind", conversationKind);
            d.Set("isReplyLink", _reference.IsReplyLink);
            d.Set("conversationChecked", _reference.ConversationId != null);
            Add(L, "[入力] " + InputKindText(_reference.InputKind) + " | 会話: " + conversationKind + " | 返信リンク: " + (_reference.IsReplyLink ? "はい" : "いいえ")
                + " | 会話ID照合: " + (_reference.ConversationId != null ? "あり" : "なし"));

            // source
            string profileKind = ProfileKind();
            string originKind = OriginKind();
            string accountKind = AccountKind(reader);
            int blobFiles = 0;
            bool hasBlobFolder = reader.BlobFolder != null && Directory.Exists(reader.BlobFolder);
            if (hasBlobFolder) blobFiles = Directory.GetFiles(reader.BlobFolder, "*", SearchOption.AllDirectories).Length;
            int teamsNew = 0, teamsClassic = 0, edge = 0, chrome = 0, custom = 0;
            foreach (SourceCandidate c in _ctx.Sources)
            {
                if (c.Kind == "teams-new") teamsNew++; else if (c.Kind == "teams-classic") teamsClassic++; else if (c.Kind == "edge") edge++; else if (c.Kind == "chrome") chrome++; else custom++;
            }
            int copyFiles = 0, copyFailed = 0;
            long copyBytes = 0;
            if (_copy != null) foreach (PreservedFile f in _copy.Files) { copyFiles++; if (f.Error != null) copyFailed++; copyBytes += f.CopiedBytes; }
            d.Set("sourceKind", _candidate.Kind);
            d.Set("profileKind", profileKind);
            d.Set("originKind", originKind);
            d.Set("accountKind", accountKind);
            d.Set("candidates", MakeMap("teamsNew", teamsNew, "teamsClassic", teamsClassic, "edge", edge, "chrome", chrome, "custom", custom));
            d.Set("sourceLastWriteUtc", Json(_candidate.LastWriteUtc));
            d.Set("copy", _copy == null ? null : MakeMap("files", copyFiles, "failed", copyFailed, "bytes", copyBytes));
            d.Set("blobFolder", hasBlobFolder);
            d.Set("blobFiles", blobFiles);
            d.Set("orphanBlobFiles", st.OrphanBlobFiles);
            Add(L, "[保存元] " + SourceKindText(_candidate.Kind) + " | " + profileKind + " | " + originKind + " | アカウント: " + accountKind);
            Add(L, "  検出: 新Teams " + N(teamsNew) + " / 従来 " + N(teamsClassic) + " / Edge " + N(edge) + " / Chrome " + N(chrome) + (custom > 0 ? " / 指定 " + N(custom) : "")
                + " | 原本の最終更新 " + Local(_candidate.LastWriteUtc));
            Add(L, "  複製: " + (_copy == null ? "なし(その場で読取)" : N(copyFiles) + " ファイル " + Mb(copyBytes) + " (失敗 " + N(copyFailed) + ")")
                + " | blob: " + (hasBlobFolder ? N(blobFiles) + " ファイル (参照なし " + N(st.OrphanBlobFiles) + ")" : "なし"));

            // LevelDB
            long versions = 0, deletions = 0, unsupported = 0, logFiles = 0, ldbFiles = 0, logBytes = 0, ldbBytes = 0;
            foreach (LevelDbFileInfo fi in reader.Folder.Files.Values)
            {
                versions += fi.Records;
                deletions += fi.DeletionMarkers;
                unsupported += fi.UnsupportedBlocks;
                if (fi.Kind == "log") { logFiles++; logBytes += fi.Bytes; } else { ldbFiles++; ldbBytes += fi.Bytes; }
            }
            ManifestSummary manifest = ManifestReader.Read(_source.LevelDbPath);
            int outsideManifest = 0;
            foreach (string file in reader.Folder.DataFiles)
            {
                string name = Path.GetFileName(file);
                long number = LevelDbFolder.FileNumber(name);
                if ((name.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".sst", StringComparison.OrdinalIgnoreCase))
                    && manifest.Readable && number >= 0 && !manifest.CurrentFiles.Contains(number)) outsideManifest++;
            }
            LevelDbLogSummary log = LevelDbLogText.Read(_source.LevelDbPath);
            StringBuilder levelText = new StringBuilder();
            OrderedMap levels = new OrderedMap();
            List<int> levelKeys = new List<int>(manifest.FilesPerLevel.Keys);
            levelKeys.Sort();
            foreach (int lv in levelKeys)
            {
                levels.Set("L" + N(lv), manifest.FilesPerLevel[lv]);
                if (levelText.Length > 0) levelText.Append(' ');
                levelText.Append("L" + N(lv) + ":" + N(manifest.FilesPerLevel[lv]));
            }
            d.Set("recordVersions", versions);
            d.Set("deletionMarkers", deletions);
            d.Set("undoEntries", st.UndoEntries);
            d.Set("unsupportedBlocks", unsupported);
            d.Set("levelDbWarnings", reader.Folder.Warnings.Count);
            d.Set("files", MakeMap("log", logFiles, "logBytes", logBytes, "ldb", ldbFiles, "ldbBytes", ldbBytes, "outsideManifest", outsideManifest));
            d.Set("manifestReadable", manifest.Readable);
            d.Set("manifestLevels", levels);
            d.Set("manifestLastSequence", manifest.LastSequence);
            Add(L, "[LevelDB] 版 " + N(versions) + " (削除 " + N(deletions) + ") | undo " + N(st.UndoEntries) + " | 読めないブロック " + N(unsupported) + " | 警告 " + N(reader.Folder.Warnings.Count));
            d.Set("unrecognizedFiles", reader.Folder.UnrecognizedFiles.Count);
            Add(L, "  log " + N(logFiles) + " (" + Mb(logBytes) + ") / ldb " + N(ldbFiles) + " (" + Mb(ldbBytes) + ") | レベル " + (levelText.Length > 0 ? levelText.ToString() : "不明") + " | MANIFEST外 " + N(outsideManifest) + " | 規則外 " + N(reader.Folder.UnrecognizedFiles.Count));
            Add(L, "  最終seq " + N(manifest.LastSequence) + " | MANIFEST " + (manifest.Readable ? "読取OK" : "読取不可") + " | LOG " + (log.Readable ? "読取OK" : "なし"));
            OrderedMap logMap = new OrderedMap();
            logMap.Set("compactions", log.Compactions);
            logMap.Set("lastCompaction", Json(log.LastCompaction));
            logMap.Set("manualCompactions", log.ManualCompactions);
            logMap.Set("lastManualCompaction", Json(log.LastManualCompaction));
            logMap.Set("level0Flushes", log.Level0Flushes);
            logMap.Set("lastLevel0Flush", Json(log.LastLevel0Flush));
            logMap.Set("logReuses", log.LogReuses);
            logMap.Set("recoveries", log.Recoveries);
            logMap.Set("first", Json(log.First));
            logMap.Set("last", Json(log.Last));
            d.Set("levelDbLog", logMap);
            Add(L, "  LOG: compaction " + N(log.Compactions) + " (最後 " + Local(log.LastCompaction) + ") | 全体 " + N(log.ManualCompactions) + " | L0書出 " + N(log.Level0Flushes) + " (最後 " + Local(log.LastLevel0Flush) + ")");
            int logPeriodLine = L.Count;   // the compaction-after-edit flag is appended once the edit time is known
            Add(L, "  LOG 記録期間 " + Local(log.First) + " ～ " + Local(log.Last) + " | log再利用 " + N(log.LogReuses) + " | 復旧 " + N(log.Recoveries));
            d.Set("chromiumEarliestSweepUtc", Json(reader.EarliestSweepTimeUtc));
            d.Set("chromiumEarliestCompactionUtc", Json(reader.EarliestCompactionTimeUtc));
            Add(L, "  整理予定(記録値): sweep " + Local(reader.EarliestSweepTimeUtc) + " | compaction " + Local(reader.EarliestCompactionTimeUtc));

            // IndexedDB stores
            StoreStats replychains = StatsFor(st, "replychain-manager", "replychains");
            StoreStats replychains2 = StatsFor(st, "replychain-manager", "replychains-2");
            StoreStats conversations = StatsFor(st, "conversation-manager", "conversations");
            StoreStats syncstates = StatsFor(st, "syncstate-manager", "syncstates");
            d.Set("databases", reader.Databases.Count);
            d.Set("replychains", replychains.ToMap());
            d.Set("replychains2", replychains2.ToMap());
            d.Set("conversations", conversations.ToMap());
            d.Set("syncstates", syncstates.ToMap());
            Add(L, "[IndexedDB] DB " + N(reader.Databases.Count) + " | replychains 版 " + N(replychains.Versions) + " / キー " + N(replychains.Keys) + " / 削除 " + N(replychains.Deletions) + " | replychains-2 版 " + N(replychains2.Versions));
            Add(L, "  conversations 版 " + N(conversations.Versions) + " / キー " + N(conversations.Keys) + " | syncstates 版 " + N(syncstates.Versions));

            // target
            TargetStats target = st.Target;
            int bodies = result != null ? result.Bodies.Count : 0;
            int htmlForms = 0;
            if (result != null) foreach (BodyGroup g in result.Bodies) htmlForms += g.Variants.Count;
            d.Set("target", target.ToMap());
            d.Set("bodies", bodies);
            d.Set("htmlForms", htmlForms);
            d.Set("occurrences", MakeMap("record", CountSource(result, "record"), "undo", CountSource(result, "undo-log"), "nested", CountMatch(result, "nested"), "orphanBlob", CountSource(result, "orphan-blob"), "lastMessage", CountStore(result, "conversations")));
            d.Set("unreadable", result != null ? result.Unreadable.Count : 0);
            d.Set("unreadableWrapped", result != null ? result.UnreadableWrapped.Count : 0);
            d.Set("otherConversation", result != null ? result.OtherConversation.Count : 0);
            d.Set("decodeFailures", st.DecodeFailures);
            d.Set("orphanBlobUnreadable", st.OrphanBlobUnreadable);
            Add(L, "[対象] チェーンのキー: 版 " + N(target.RecordVersions) + " (log " + N(target.InLog) + " / ldb " + N(target.InLdb) + " / 削除 " + N(target.Deletions) + ") | undo " + N(target.UndoEntries)
                + " | lastMessage 該当 " + N(CountStore(result, "conversations")));
            Add(L, "  本文候補 " + N(bodies) + " (HTML " + N(htmlForms) + ") | record " + N(CountSource(result, "record")) + " / undo " + N(CountSource(result, "undo-log")) + " / 入れ子 " + N(CountMatch(result, "nested"))
                + " / 孤立blob " + N(CountSource(result, "orphan-blob")));
            Add(L, "  読めなかった " + N(result != null ? result.Unreadable.Count : 0) + " | 包まれ不明 " + N(result != null ? result.UnreadableWrapped.Count : 0) + " | 別会話 " + N(result != null ? result.OtherConversation.Count : 0)
                + " | 復号失敗 " + N(st.DecodeFailures) + " | 孤立blob 読取不可 " + N(st.OrphanBlobUnreadable));

            DateTime? created = null, edited = null, earliestArrival = null;
            ulong minSeq = ulong.MaxValue, maxSeq = 0;
            if (result != null)
            {
                foreach (BodyGroup g in result.Bodies) foreach (Occurrence o in g.Occurrences)
                {
                    DateTime? c = FromIsoOrMillis(o.Fields.GetString("originalArrivalTime") ?? o.Fields.GetString("originalarrivaltime") ?? o.Fields.GetString("composetime") ?? o.Fields.GetString("properties.composetime"));
                    if (c.HasValue && (!created.HasValue || c.Value < created.Value)) created = c;
                    DateTime? e = FromIsoOrMillis(o.Fields.GetString("properties.edittime"));
                    if (e.HasValue && (!edited.HasValue || e.Value > edited.Value)) edited = e;
                    DateTime? a = FromIsoOrMillis(o.Fields.GetString("clientArrivalTime"));
                    if (a.HasValue && (!earliestArrival.HasValue || a.Value < earliestArrival.Value)) earliestArrival = a;
                    if (o.SourceKind != "orphan-blob")
                    {
                        if (o.Sequence < minSeq) minSeq = o.Sequence;
                        if (o.Sequence > maxSeq) maxSeq = o.Sequence;
                    }
                }
            }
            d.Set("messageCreatedUtc", Json(created));
            d.Set("messageEditedUtc", Json(edited));
            d.Set("earliestClientArrivalUtc", Json(earliestArrival));
            d.Set("sequenceRange", minSeq == ulong.MaxValue ? null : (object)MakeMap("min", (long)minSeq, "max", (long)maxSeq));
            Add(L, "  作成 " + LocalSeconds(created) + " | 編集 " + (edited.HasValue ? LocalSeconds(edited) : "記録なし") + " | seq " + (minSeq == ulong.MaxValue ? "-" : N((long)minSeq) + "-" + N((long)maxSeq)));
            string arrivalText = "不明";
            if (earliestArrival.HasValue && edited.HasValue)
            {
                double diff = (earliestArrival.Value - edited.Value).TotalSeconds;
                arrivalText = LocalSeconds(earliestArrival) + " (編集の " + (diff >= 0 ? "+" : "-") + Math.Abs(diff).ToString("0", CultureInfo.InvariantCulture) + " 秒)";
            }
            else if (earliestArrival.HasValue) arrivalText = LocalSeconds(earliestArrival);
            Add(L, "  残存する最古の版の受信 " + arrivalText);

            // sync state
            SyncStateInfo sync = st.SyncState;
            if (sync != null && sync.Found)
            {
                d.Set("syncState", sync.ToMap());
                Add(L, "[同期] 窓 " + Local(sync.WindowStart) + " ～ " + Local(sync.WindowEnd) + " | 先頭まで " + YesNo(sync.SyncedToStartOfTime) + " | ギャップ " + YesNo(sync.GapDetected) + " | 古い " + YesNo(sync.MessageStale));
                Add(L, "  最終同期 " + Local(sync.LastSyncTime) + " | リセット " + N(sync.ResetCount) + " 回 | エラー記録 " + (sync.HasErrorRecord ? "あり" : "なし") + " | チャネル " + YesNo(sync.IsChannel));
            }
            else
            {
                d.Set("syncState", null);
                Add(L, "[同期] 会話の同期記録: " + (_reference.ConversationId == null ? "会話ID未指定のため未照合" : "見つからず"));
            }

            // reading guide (never an assertion about what was displayed on this PC)
            bool compactionAfterEdit = edited.HasValue && log.LastCompaction.HasValue && log.LastCompaction.Value.ToUniversalTime() > edited.Value;
            string guide;
            if (bodies == 0) guide = "今回の解析で本文を確認できず。";
            else if (bodies == 1) guide = "今回の解析で旧本文を確認できず（本文1件）。";
            else guide = "異なる本文が " + N(bodies) + " 件残っている。";
            d.Set("guide", guide);
            d.Set("compactionAfterEdit", edited.HasValue ? (object)compactionAfterEdit : null);
            Add(L, "[目安] " + guide + " 受信時刻だけでは当時の表示を確定不可。");
            string afterEdit = " | 編集後のcompaction " + (edited.HasValue ? (compactionAfterEdit ? "あり" : "無し") : "-");
            if (DisplayWidth(L[logPeriodLine] + afterEdit) <= MaxColumns) L[logPeriodLine] = L[logPeriodLine] + afterEdit;
            else Add(L, " " + afterEdit);
            return r;
        }

        private static OrderedMap MakeMap(params object[] pairs)
        {
            OrderedMap m = new OrderedMap();
            for (int i = 0; i + 1 < pairs.Length; i += 2) m.Set((string)pairs[i], pairs[i + 1]);
            return m;
        }

        private static StoreStats StatsFor(SearchStats st, string manager, string store)
        {
            StoreStats s = new StoreStats();
            foreach (KeyValuePair<string, StoreStats> pair in st.Stores)
            {
                if (pair.Key.IndexOf("Teams:" + manager + ":", StringComparison.Ordinal) >= 0 && pair.Key.EndsWith(" / " + store, StringComparison.Ordinal))
                {
                    s.Versions += pair.Value.Versions;
                    s.Keys += pair.Value.Keys;
                    s.Deletions += pair.Value.Deletions;
                }
            }
            return s;
        }

        private static int CountSource(FinderResult result, string sourceKind)
        {
            if (result == null) return 0;
            int n = 0;
            foreach (BodyGroup g in result.Bodies) foreach (Occurrence o in g.Occurrences) if (o.SourceKind == sourceKind) n++;
            return n;
        }

        private static int CountMatch(FinderResult result, string matchKind)
        {
            if (result == null) return 0;
            int n = 0;
            foreach (BodyGroup g in result.Bodies) foreach (Occurrence o in g.Occurrences) if (o.MatchKind == matchKind) n++;
            return n;
        }

        private static int CountStore(FinderResult result, string store)
        {
            if (result == null) return 0;
            int n = 0;
            foreach (BodyGroup g in result.Bodies) foreach (Occurrence o in g.Occurrences) if (string.Equals(o.Store, store, StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }
    }

    /// <summary>Per-store counts gathered during the search (versions, distinct keys, deletion markers).</summary>
    public sealed class StoreStats
    {
        public long Versions;
        public long Keys;
        public long Deletions;
        public readonly HashSet<string> KeySet = new HashSet<string>(StringComparer.Ordinal);

        public OrderedMap ToMap()
        {
            OrderedMap m = new OrderedMap();
            m.Set("versions", Versions);
            m.Set("keys", Keys);
            m.Set("deletions", Deletions);
            return m;
        }
    }

    /// <summary>Counts about the record key(s) that name the requested message or its chain.</summary>
    public sealed class TargetStats
    {
        public long RecordVersions;
        public long InLog;
        public long InLdb;
        public long Deletions;
        public long UndoEntries;
        public ulong MinSequence = ulong.MaxValue;
        public ulong MaxSequence;

        public OrderedMap ToMap()
        {
            OrderedMap m = new OrderedMap();
            m.Set("recordVersions", RecordVersions);
            m.Set("inLog", InLog);
            m.Set("inLdb", InLdb);
            m.Set("deletions", Deletions);
            m.Set("undoEntries", UndoEntries);
            m.Set("minSequence", MinSequence == ulong.MaxValue ? (object)null : MinSequence);
            m.Set("maxSequence", MaxSequence);
            return m;
        }
    }

    /// <summary>Numbers and flags from the client's own sync-state record of the conversation (no ids, no tokens, no free text).</summary>
    public sealed class SyncStateInfo
    {
        public bool Found;
        public DateTime? WindowStart;
        public DateTime? WindowEnd;
        public DateTime? LastSyncTime;
        public bool? SyncedToStartOfTime;
        public bool? GapDetected;
        public bool? MessageStale;
        public bool? IsChannel;
        public bool HasErrorRecord;
        public int ResetCount;

        public OrderedMap ToMap()
        {
            OrderedMap m = new OrderedMap();
            m.Set("windowStartUtc", WindowStart.HasValue ? (object)WindowStart.Value : null);
            m.Set("windowEndUtc", WindowEnd.HasValue ? (object)WindowEnd.Value : null);
            m.Set("lastSyncTimeUtc", LastSyncTime.HasValue ? (object)LastSyncTime.Value : null);
            m.Set("syncedToStartOfTime", SyncedToStartOfTime.HasValue ? (object)SyncedToStartOfTime.Value : null);
            m.Set("gapDetected", GapDetected.HasValue ? (object)GapDetected.Value : null);
            m.Set("messageStale", MessageStale.HasValue ? (object)MessageStale.Value : null);
            m.Set("isChannel", IsChannel.HasValue ? (object)IsChannel.Value : null);
            m.Set("hasErrorRecord", HasErrorRecord);
            m.Set("resetCount", ResetCount);
            return m;
        }
    }
}
