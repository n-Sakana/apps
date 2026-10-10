// Teams Message History - review of an existing result folder (no re-acquisition, nothing modified).
// Reads result.json and breaks down, with fixed labels and counts only, the files that could not be
// copied and the wrapped values that could not be read. Every classified value maps onto one of the
// fixed labels below; anything unknown lands in the last label of its group so no count is hidden.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TeamsMessageHistory
{
    public sealed class ResultReview
    {
        private const int MaxColumns = 96;

        // Fixed label sets (the only strings that reach the screen besides numbers and dates)
        public static readonly string[] FileKindLabels = { "LOCK", "CURRENT", "MANIFEST", "LOG", "log", "ldb", "blob", "他" };
        public static readonly string[] CopyErrorLabels = { "共有違反", "アクセス拒否", "見つからない", "入出力", "他" };
        public static readonly string[] WrapperKindLabels = { "外部blob", "圧縮", "不明" };
        public static readonly string[] WrapperReasonLabels = { "本体見つからず", "版の参照特定不可", "キーの参照無し", "参照対象外", "展開失敗", "blobフォルダー無し", "多重参照", "キー不可", "ファイル不可", "他" };
        public static readonly string[] SourceKindLabels = { "レコード版", "undo", "不明" };
        public static readonly string[] StoreLabels = { "返信チェーン", "会話", "メッセージ系", "ファイル系", "アプリ系", "同期設定系", "Teams以外", "他" };
        public static readonly string[] RecordFileLabels = { "log", "ldb", "不明" };
        public static readonly string[] KnownStatuses = { "found", "found_in_other_conversation", "unreadable", "error", "not_found" };
        public static readonly string[] KnownInputKinds = { "link", "id", "conversation+id", "conversation-only", "invalid" };

        private readonly List<string> _lines = new List<string>();
        private readonly OrderedMap _data = new OrderedMap();

        public IList<string> Lines { get { return _lines; } }
        public OrderedMap Data { get { return _data; } }

        private static string N(long n)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }

        private void Add(string text)
        {
            if (DiagnosticsCollector.DisplayWidth(text) <= MaxColumns)
            {
                _lines.Add(text);
                return;
            }
            StringBuilder sb = new StringBuilder();
            foreach (char c in text)
            {
                if (DiagnosticsCollector.DisplayWidth(sb.ToString()) + (c > 0x2E7F ? 2 : 1) > MaxColumns - 1) break;
                sb.Append(c);
            }
            _lines.Add(sb.ToString() + "…");
        }

        /// <summary>Counts per fixed label; a value outside the label set is counted under the last label.</summary>
        public sealed class Counter
        {
            private readonly string[] _labels;
            private readonly long[] _counts;

            public Counter(string[] labels)
            {
                _labels = labels;
                _counts = new long[labels.Length];
            }

            public void Hit(string label)
            {
                int index = Array.IndexOf(_labels, label);
                if (index < 0) index = _labels.Length - 1;
                _counts[index]++;
            }

            public long Total
            {
                get { long t = 0; foreach (long c in _counts) t += c; return t; }
            }

            public OrderedMap Map
            {
                get
                {
                    OrderedMap m = new OrderedMap();
                    for (int i = 0; i < _labels.Length; i++) m.Set(_labels[i], _counts[i]);
                    return m;
                }
            }

            public string Text(int from, int to)
            {
                List<string> parts = new List<string>();
                for (int i = from; i < _labels.Length && i < to; i++) parts.Add(_labels[i] + " " + N(_counts[i]));
                return string.Join(" / ", parts.ToArray());
            }

            public string Text()
            {
                return Text(0, int.MaxValue);
            }
        }

        // ---------------------------------------------------------------- classification (labels only; raw strings never leave)

        public static string FileKind(string relativePath)
        {
            if (relativePath == null) return "他";
            string name = relativePath.Replace('\\', '/');
            if (name.StartsWith("blob/", StringComparison.OrdinalIgnoreCase)) return "blob";
            string file = name.Substring(name.LastIndexOf('/') + 1).ToUpperInvariant();
            if (file == "LOCK") return "LOCK";
            if (file == "CURRENT") return "CURRENT";
            if (file == "LOG" || file == "LOG.OLD") return "LOG";
            if (file.StartsWith("MANIFEST", StringComparison.Ordinal)) return "MANIFEST";
            if (file.EndsWith(".LOG", StringComparison.Ordinal)) return "log";
            if (file.EndsWith(".LDB", StringComparison.Ordinal) || file.EndsWith(".SST", StringComparison.Ordinal)) return "ldb";
            return "他";
        }

        public static string CopyErrorKind(string error)
        {
            if (string.IsNullOrEmpty(error)) return "他";
            string e = error.ToLowerInvariant();
            if (e.Contains("being used by another process") || e.Contains("sharing violation") || e.Contains("別のプロセスが使用中") || e.Contains("共有")) return "共有違反";
            if (e.StartsWith("unauthorizedaccessexception") || (e.Contains("access") && e.Contains("denied")) || e.Contains("アクセスが拒否")) return "アクセス拒否";
            if (e.StartsWith("filenotfoundexception") || e.StartsWith("directorynotfoundexception") || e.Contains("could not find") || e.Contains("見つかりません")) return "見つからない";
            if (e.StartsWith("ioexception")) return "入出力";
            return "他";
        }

        public static string WrapperKind(string reason)
        {
            if (reason == null) return "不明";
            if (reason.Contains("外部 blob") || reason.Contains("external blob")) return "外部blob";
            if (reason.Contains("圧縮") || reason.Contains("decompress")) return "圧縮";
            return "不明";
        }

        public static string WrapperReasonKind(string reason)
        {
            if (reason == null) return "他";
            if (reason.Contains("external blob file missing")) return "本体見つからず";
            if (reason.Contains("no blob entry belongs to this record version")) return "版の参照特定不可";
            if (reason.Contains("no blob entry stored for this key")) return "キーの参照無し";
            if (reason.Contains("has no object at index")) return "参照対象外";
            if (reason.Contains("could not be decompressed")) return "展開失敗";
            if (reason.Contains("blob folder unknown")) return "blobフォルダー無し";
            if (reason.Contains("record key unreadable")) return "キー不可";
            if (reason.Contains("refers to yet another blob")) return "多重参照";
            if (reason.Contains("file unreadable")) return "ファイル不可";
            return "他";
        }

        public static string SourceKindLabel(string sourceKind)
        {
            if (sourceKind == "undo-log") return "undo";
            if (sourceKind == "record") return "レコード版";
            return "不明";
        }

        public static string StoreCategory(string database, string store)
        {
            string manager = "";
            if (database != null && database.StartsWith("Teams:", StringComparison.Ordinal))
            {
                int end = database.IndexOf(':', 6);
                manager = (end > 6 ? database.Substring(6, end - 6) : database.Substring(6)).ToLowerInvariant();
            }
            string s = (store ?? "").ToLowerInvariant();
            if (manager.Contains("replychain") || s.StartsWith("replychains", StringComparison.Ordinal)) return "返信チェーン";
            if (manager == "conversation-manager" || s.StartsWith("conversations", StringComparison.Ordinal)) return "会話";
            if (manager.Contains("messag") || manager.Contains("mention") || manager.Contains("saved") || manager.Contains("draft") || manager.Contains("thread")
                || manager.Contains("chat") || manager.Contains("reply") || manager.Contains("activity") || manager.Contains("notification") || manager.Contains("search")) return "メッセージ系";
            if (manager.Contains("file") || manager.Contains("thumbnail") || manager.Contains("attach") || manager.Contains("p2p") || manager.Contains("artifact") || manager.Contains("ams")) return "ファイル系";
            if (manager.Contains("app") || manager.Contains("extension") || manager.Contains("copilot") || manager.Contains("tab") || manager.Contains("store") || manager.Contains("policy") || manager.Contains("emoji")) return "アプリ系";
            if (manager.Contains("sync") || manager.Contains("presence") || manager.Contains("profile") || manager.Contains("contact") || manager.Contains("settings") || manager.Contains("calendar") || manager.Contains("call")) return "同期設定系";
            if (database != null && !database.StartsWith("Teams:", StringComparison.Ordinal)) return "Teams以外";
            return "他";
        }

        public static string RecordFileKind(string file)
        {
            if (file == null) return "不明";
            string f = file.ToLowerInvariant();
            if (f.EndsWith(".log")) return "log";
            if (f.EndsWith(".ldb") || f.EndsWith(".sst")) return "ldb";
            return "不明";
        }

        private static readonly Regex VersionPattern = new Regex(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled);
        private static readonly Regex BlobNumberInReason = new Regex(@"blob number (\d+)", RegexOptions.Compiled);

        public static string KnownOr(string value, string[] known)
        {
            if (value == null) return "不明";
            foreach (string k in known) if (string.Equals(k, value, StringComparison.Ordinal)) return value;
            return "不明";
        }

        public static string SafeVersion(string value)
        {
            return value != null && VersionPattern.IsMatch(value) ? value : "不明";
        }

        // ---------------------------------------------------------------- build

        public static ResultReview Build(string resultFolder)
        {
            string jsonPath = Path.Combine(resultFolder, "result.json");
            if (!File.Exists(jsonPath)) throw new FileNotFoundException("result.json がありません");
            return BuildFromJson(File.ReadAllText(jsonPath, Encoding.UTF8));
        }

        public static ResultReview BuildFromJson(string json)
        {
            ResultReview r = new ResultReview();
            OrderedMap rootMap = JsonReader.AsMap(JsonReader.Parse(json));
            if (rootMap == null) throw new FormatException("result.json の形式が違います");

            string toolVersion = SafeVersion(JsonReader.AsString(JsonReader.Path(rootMap, "tool", "version")));
            string overall = KnownOr(JsonReader.AsString(rootMap.Get("overallStatus")), KnownStatuses);
            string inputKind = KnownOr(JsonReader.AsString(JsonReader.Path(rootMap, "query", "inputKind")), KnownInputKinds);
            List<object> analyzed = JsonReader.AsList(rootMap.Get("analyzed")) ?? new List<object>();
            List<object> copies = JsonReader.AsList(rootMap.Get("preservedCopies")) ?? new List<object>();
            bool hasWrappedList = false;

            r.Add("==== 追加診断（既存の結果から）v" + Program.Version + " | " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " ====");

            // --- result summary
            int found = 0, notFound = 0, unreadableSources = 0, errors = 0;
            long maxBodies = 0, wrappedTotal = 0, unreadableTotal = 0, orphanFiles = 0, orphanUnreadable = 0;
            foreach (object a in analyzed)
            {
                OrderedMap am = JsonReader.AsMap(a);
                if (am == null) continue;
                string status = am.Get("error") != null ? "error" : KnownOr(JsonReader.AsString(JsonReader.Path(am, "findings", "status")), KnownStatuses);
                if (status == "found") found++; else if (status == "unreadable") unreadableSources++; else if (status == "error") errors++; else notFound++;
                List<object> bodies = JsonReader.AsList(JsonReader.Path(am, "findings", "bodies"));
                if (bodies != null && bodies.Count > maxBodies) maxBodies = bodies.Count;
                List<object> wrapped = JsonReader.AsList(JsonReader.Path(am, "findings", "unreadableWrapped"));
                if (wrapped != null) { hasWrappedList = true; wrappedTotal += wrapped.Count; }
                List<object> unreadable = JsonReader.AsList(JsonReader.Path(am, "findings", "unreadable"));
                if (unreadable != null) unreadableTotal += unreadable.Count;
                orphanFiles += JsonReader.AsLong(JsonReader.Path(am, "findings", "stats", "orphanBlobFiles"));
                orphanUnreadable += JsonReader.AsLong(JsonReader.Path(am, "findings", "stats", "orphanBlobUnreadable"));
            }
            r._data.Set("toolVersion", toolVersion);
            r._data.Set("overallStatus", overall);
            r._data.Set("inputKind", inputKind);
            r._data.Set("sources", analyzed.Count);
            r._data.Set("sourceStatuses", MakeMap("found", found, "notFound", notFound, "unreadable", unreadableSources, "error", errors));
            r._data.Set("maxBodies", maxBodies);
            r._data.Set("unreadableTotal", unreadableTotal);
            r._data.Set("wrappedTotal", hasWrappedList ? (object)wrappedTotal : null);
            r._data.Set("orphanBlobFiles", orphanFiles);
            r._data.Set("orphanBlobUnreadable", orphanUnreadable);
            r.Add("[結果] 作成 v" + toolVersion + " | 入力 " + inputKind + " | 判定 " + overall + " | 解析元 " + N(analyzed.Count));
            r.Add("  本文あり " + N(found) + " / 見つからず " + N(notFound) + " / 読めず " + N(unreadableSources) + " / エラー " + N(errors) + " | 本文候補(最多) " + N(maxBodies));
            r.Add("  読めなかった(対象関連) " + N(unreadableTotal) + " | 包まれ不明 " + (hasWrappedList ? N(wrappedTotal) : "記録なし") + " | 孤立blob " + N(orphanFiles) + " (読取不可 " + N(orphanUnreadable) + ")");

            // --- copy failures, with the failed blob files remembered per copy (source index) and database id
            Counter failKind = new Counter(FileKindLabels);
            Counter failCause = new Counter(CopyErrorLabels);
            long totalFiles = 0, failedFiles = 0, failedBytes = 0, analysisRelevantFailures = 0, otherKindFailures = 0;
            List<HashSet<string>> failedBlobRefsPerCopy = new List<HashSet<string>>();
            foreach (object c in copies)
            {
                HashSet<string> refs = new HashSet<string>(StringComparer.Ordinal);
                failedBlobRefsPerCopy.Add(refs);
                OrderedMap cm = JsonReader.AsMap(c);
                if (cm == null) continue;
                List<object> files = JsonReader.AsList(cm.Get("files")) ?? new List<object>();
                foreach (object f in files)
                {
                    OrderedMap fm = JsonReader.AsMap(f);
                    if (fm == null) continue;
                    totalFiles++;
                    string error = JsonReader.AsString(fm.Get("error"));
                    if (error == null) continue;
                    failedFiles++;
                    failedBytes += JsonReader.AsLong(fm.Get("sourceBytes"));
                    string rel = JsonReader.AsString(fm.Get("relativePath"));
                    string kind = FileKind(rel);
                    failKind.Hit(kind);
                    failCause.Hit(CopyErrorKind(error));
                    if (kind == "log" || kind == "ldb" || kind == "blob") analysisRelevantFailures++;
                    if (kind == "他") otherKindFailures++;
                    if (kind == "blob" && rel != null)
                    {
                        string[] parts = rel.Replace('\\', '/').Split('/');
                        long db, number;
                        if (parts.Length >= 4 && long.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out db) && long.TryParse(parts[parts.Length - 1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number))
                            refs.Add(IndexedDbReader.BlobRef(db, number));
                    }
                }
            }
            r._data.Set("copyFiles", totalFiles);
            r._data.Set("copyFailed", failedFiles);
            r._data.Set("copyFailedBytes", failedBytes);
            r._data.Set("copyFailedKinds", failKind.Map);
            r._data.Set("copyFailedCauses", failCause.Map);
            r._data.Set("copyFailedAnalysisRelevant", analysisRelevantFailures);
            r.Add("[コピー失敗] " + N(failedFiles) + " 件 / " + N(totalFiles) + " ファイル | 失敗分 " + (failedBytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + "MB | 本文の読取に使う種別(log/ldb/blob)の失敗 " + N(analysisRelevantFailures));
            r.Add("  種別: " + failKind.Text());
            r.Add("  原因: " + failCause.Text());
            string impact;
            if (failedFiles == 0) impact = "失敗なし";
            else if (analysisRelevantFailures == 0 && otherKindFailures == 0) impact = "失敗は LOCK/CURRENT/MANIFEST/LOG だけ（本文の読取には使わない種別）";
            else if (analysisRelevantFailures == 0) impact = "本文の読取に使う種別の失敗は無いが、種別「他」があり影響は未確定";
            else impact = "本文の読取に使う種別の失敗あり（下の『包まれ不明』の『コピー失敗を参照』と照合）";
            r._data.Set("copyImpact", impact);
            r.Add("  → " + impact);

            // --- wrapped values that could not be read
            Counter wrapKind = new Counter(WrapperKindLabels);
            Counter wrapReason = new Counter(WrapperReasonLabels);
            Counter wrapSource = new Counter(SourceKindLabels);
            Counter wrapStore = new Counter(StoreLabels);
            Counter wrapFile = new Counter(RecordFileLabels);
            long wrapRefFailedCopy = 0, wrapRefUnresolvable = 0;
            ulong wrapMinSeq = ulong.MaxValue, wrapMaxSeq = 0;
            HashSet<string> wrapDbs = new HashSet<string>(StringComparer.Ordinal);
            for (int sourceIndex = 0; sourceIndex < analyzed.Count; sourceIndex++)
            {
                OrderedMap am = JsonReader.AsMap(analyzed[sourceIndex]);
                if (am == null) continue;
                List<object> wrapped = JsonReader.AsList(JsonReader.Path(am, "findings", "unreadableWrapped"));
                if (wrapped == null) continue;
                Dictionary<string, long> dbIds = DatabaseIds(am);
                HashSet<string> failedRefs = sourceIndex < failedBlobRefsPerCopy.Count ? failedBlobRefsPerCopy[sourceIndex] : new HashSet<string>(StringComparer.Ordinal);
                foreach (object w in wrapped)
                {
                    OrderedMap wm = JsonReader.AsMap(w);
                    if (wm == null) continue;
                    string reason = JsonReader.AsString(wm.Get("reason"));
                    wrapKind.Hit(WrapperKind(reason));
                    wrapReason.Hit(WrapperReasonKind(reason));
                    wrapSource.Hit(SourceKindLabel(JsonReader.AsString(wm.Get("sourceKind"))));
                    string database = JsonReader.AsString(wm.Get("database"));
                    wrapStore.Hit(StoreCategory(database, JsonReader.AsString(wm.Get("store"))));
                    wrapFile.Hit(RecordFileKind(JsonReader.AsString(wm.Get("file"))));
                    if (database != null) wrapDbs.Add(N(sourceIndex) + "|" + database);
                    long seq = JsonReader.AsLong(wm.Get("sequence"));
                    if (seq >= 0)
                    {
                        if ((ulong)seq < wrapMinSeq) wrapMinSeq = (ulong)seq;
                        if ((ulong)seq > wrapMaxSeq) wrapMaxSeq = (ulong)seq;
                    }
                    // Did this value refer to a blob file that could not be copied? Same source, same database id, same blob number.
                    if (failedRefs.Count > 0 && reason != null)
                    {
                        Match m = BlobNumberInReason.Match(reason);
                        if (m.Success)
                        {
                            long dbId;
                            if (database != null && dbIds.TryGetValue(database, out dbId))
                            {
                                if (failedRefs.Contains(IndexedDbReader.BlobRef(dbId, long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)))) wrapRefFailedCopy++;
                            }
                            else wrapRefUnresolvable++;
                        }
                    }
                }
            }
            r._data.Set("wrappedKinds", wrapKind.Map);
            r._data.Set("wrappedReasons", wrapReason.Map);
            r._data.Set("wrappedSources", wrapSource.Map);
            r._data.Set("wrappedStores", wrapStore.Map);
            r._data.Set("wrappedFiles", wrapFile.Map);
            r._data.Set("wrappedDistinctDatabases", wrapDbs.Count);
            r._data.Set("wrappedReferencingFailedCopy", wrapRefFailedCopy);
            r._data.Set("wrappedReferenceUnresolvable", wrapRefUnresolvable);
            r.Add("[包まれ不明 " + N(wrappedTotal) + " 件] 形式: " + wrapKind.Text() + " | 出典: " + wrapSource.Text());
            r.Add("  ファイル: " + wrapFile.Text() + " | DB数 " + N(wrapDbs.Count) + " | seq 範囲 " + (wrapMinSeq == ulong.MaxValue ? "-" : N((long)wrapMinSeq) + "-" + N((long)wrapMaxSeq)));
            r.Add("  コピー失敗ファイルを参照 " + N(wrapRefFailedCopy) + "（照合不能 " + N(wrapRefUnresolvable) + "）| 対象のキー/会話を指すもの 0（定義上）");
            r.Add("  原因: " + wrapReason.Text(0, 4));
            r.Add("        " + wrapReason.Text(4, int.MaxValue));
            r.Add("  ストア: " + wrapStore.Text(0, 4));
            r.Add("          " + wrapStore.Text(4, int.MaxValue));

            // --- unreadable (target-related)
            Counter unKind = new Counter(WrapperKindLabels);
            Counter unReason = new Counter(WrapperReasonLabels);
            long unRaw = 0;
            foreach (object a in analyzed)
            {
                OrderedMap am = JsonReader.AsMap(a);
                if (am == null) continue;
                List<object> unreadable = JsonReader.AsList(JsonReader.Path(am, "findings", "unreadable"));
                if (unreadable == null) continue;
                foreach (object u in unreadable)
                {
                    OrderedMap um = JsonReader.AsMap(u);
                    if (um == null) continue;
                    string reason = JsonReader.AsString(um.Get("reason"));
                    if (reason != null && reason.Contains("包まれた値"))
                    {
                        unKind.Hit(WrapperKind(reason));
                        unReason.Hit(WrapperReasonKind(reason));
                    }
                    else unRaw++;
                }
            }
            r._data.Set("unreadableKinds", unKind.Map);
            r._data.Set("unreadableReasons", unReason.Map);
            r._data.Set("unreadableRawDecode", unRaw);
            r.Add("[読めなかった(対象関連) " + N(unreadableTotal) + " 件] 包まれた値: " + unKind.Text() + " | 復号失敗など " + N(unRaw));
            if (unreadableTotal > 0)
            {
                r.Add("  原因: " + unReason.Text(0, 4));
                r.Add("        " + unReason.Text(4, int.MaxValue));
            }

            // --- reading guide (observations only, no cause asserted)
            r.Add("[読み方] 本体見つからず = 解析先に本体が無い / 版の参照特定不可 = 対応する参照を特定できず");
            r.Add("  読めなかった値の中身は分からない（ストア名から本文の有無は判定しない）");
            r.Add("  上の件数は今回の解析で記録された範囲。記録に残らない欠落は本ツールでは検出しない");
            return r;
        }

        private static Dictionary<string, long> DatabaseIds(OrderedMap analyzedSource)
        {
            Dictionary<string, long> ids = new Dictionary<string, long>(StringComparer.Ordinal);
            List<object> dbs = JsonReader.AsList(JsonReader.Path(analyzedSource, "parse", "databases"));
            if (dbs == null) return ids;
            foreach (object d in dbs)
            {
                OrderedMap dm = JsonReader.AsMap(d);
                if (dm == null) continue;
                string name = JsonReader.AsString(dm.Get("name"));
                if (name != null && !ids.ContainsKey(name)) ids[name] = JsonReader.AsLong(dm.Get("id"));
            }
            return ids;
        }

        private static OrderedMap MakeMap(params object[] pairs)
        {
            OrderedMap m = new OrderedMap();
            for (int i = 0; i + 1 < pairs.Length; i += 2) m.Set((string)pairs[i], pairs[i + 1]);
            return m;
        }

        public static string Save(ResultReview review, string resultFolder)
        {
            string trimmed = resultFolder.TrimEnd('\\', '/');
            string outDir = trimmed + "-review";
            Directory.CreateDirectory(outDir);
            StringBuilder sb = new StringBuilder();
            foreach (string line in review.Lines) sb.AppendLine(line);
            File.WriteAllText(Path.Combine(outDir, "review.txt"), sb.ToString(), new UTF8Encoding(false));
            OrderedMap json = new OrderedMap();
            json.Set("tool", Program.Version);
            json.Set("createdUtc", TimeText.NowIso());
            json.Set("data", review.Data);
            json.Set("lines", new List<object>(new List<string>(review.Lines).ToArray()));
            File.WriteAllText(Path.Combine(outDir, "review.json"), JsonWriter.Serialize(json), new UTF8Encoding(false));
            return outDir;
        }
    }
}
