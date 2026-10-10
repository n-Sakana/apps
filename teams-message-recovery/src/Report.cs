// Teams Message History - result files: result.json (machine readable), result.txt (human readable),
// bodies/body-N.html and body-N.txt (one file per distinct body).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TeamsMessageHistory
{
    public sealed class RunContext
    {
        public string ToolVersion;
        public string StartedUtc;
        public string FinishedUtc;
        public MessageReference Reference;
        public string OutputFolder;
        public readonly List<SourceCandidate> Sources = new List<SourceCandidate>();
        public readonly List<PreservedCopy> Copies = new List<PreservedCopy>();
        public readonly List<string> Notes = new List<string>();
        public readonly List<AnalyzedSource> Analyzed = new List<AnalyzedSource>();
        public bool InPlace;
        public bool Diagnostics;
        public TraceStage Traces;   // traces outside IndexedDB, run after the analysis (null when the run ended early)
        public ThreadSearchResult Thread;   // thread-wide search (other fields, same thread, words), run after the traces
    }

    /// <summary>The traces stage of a main run: which sources were scanned (and why), the results, and the files written.</summary>
    public sealed class TraceStage
    {
        public string Mode;   // standard (this PC: notification db + caches of the analysed profiles) / capture (specified copy only) / skipped
        public readonly List<string> Notes = new List<string>();
        public readonly List<TraceSource> Sources = new List<TraceSource>();
        public readonly List<TraceSourceResult> Results = new List<TraceSourceResult>();
        public TraceScanner.Discovery Discovery;
        public TraceScanner.TraceOutput Output;
        public string Error;

        public List<KeyValuePair<TraceHit, string>> HitsOf(string classification)
        {
            List<KeyValuePair<TraceHit, string>> list = new List<KeyValuePair<TraceHit, string>>();
            if (Output == null) return list;
            foreach (KeyValuePair<TraceHit, string> p in Output.HitFiles) if (p.Key.Classification == classification) list.Add(p);
            return list;
        }

        public int CountOf(string classification)
        {
            int n = 0;
            foreach (TraceSourceResult r in Results) foreach (TraceHit h in r.Hits) if (h.Classification == classification) n++;
            return n;
        }

        public int NearbyOnly(string needleKind)
        {
            int n = 0;
            foreach (TraceSourceResult r in Results) foreach (TraceHit h in r.Hits) if (h.Classification == "nearby-only" && h.NeedleKind == needleKind) n++;
            return n;
        }

        public int SourcesScanned
        {
            get { int n = 0; foreach (TraceSourceResult r in Results) if (!r.Source.EnumerationFailed && r.Error == null) n++; return n; }
        }

        public bool Ran { get { return Mode != null && Mode != "skipped"; } }
    }

    public sealed class AnalyzedSource
    {
        public string LevelDbPath;
        public string Label;
        public SourceCandidate Candidate;
        public PreservedCopy Copy;
        public IndexedDbReader Reader;
        public FinderResult Result;
        public DiagnosticReport Diagnostics;
        public string Error;
    }

    public static class ReportWriter
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static string Write(RunContext ctx)
        {
            Directory.CreateDirectory(ctx.OutputFolder);
            string jsonPath = Path.Combine(ctx.OutputFolder, "result.json");
            string textPath = Path.Combine(ctx.OutputFolder, "result.txt");
            File.WriteAllText(jsonPath, JsonWriter.Serialize(BuildJson(ctx)), Utf8NoBom);
            File.WriteAllText(textPath, BuildText(ctx), Utf8NoBom);
            WriteBodies(ctx);
            StringBuilder diag = new StringBuilder();
            foreach (AnalyzedSource a in ctx.Analyzed)
            {
                if (a.Diagnostics == null) continue;
                foreach (string line in a.Diagnostics.Lines) diag.AppendLine(line);
                diag.AppendLine();
            }
            if (diag.Length > 0)
            {
                foreach (string line in ThreadSearch.ScreenLines(ctx.Thread)) diag.AppendLine(line);
                File.WriteAllText(Path.Combine(ctx.OutputFolder, "diag.txt"), diag.ToString(), Utf8NoBom);
            }
            return textPath;
        }

        private static void WriteBodies(RunContext ctx)
        {
            string bodiesDir = Path.Combine(ctx.OutputFolder, "bodies");
            int n = 0;
            foreach (AnalyzedSource a in ctx.Analyzed)
            {
                if (a.Result == null) continue;
                foreach (BodyGroup g in a.Result.Bodies)
                {
                    if (!g.HasContent) continue;
                    n++;
                    Directory.CreateDirectory(bodiesDir);
                    string stem = "body-" + n.ToString("00", CultureInfo.InvariantCulture);
                    File.WriteAllText(Path.Combine(bodiesDir, stem + ".txt"), g.ContentText ?? "", Utf8NoBom);
                    foreach (BodyVariant v in g.Variants)
                    {
                        if (v.ContentHtml == null) continue;
                        string suffix = g.Variants.Count > 1 ? "-v" + v.Index.ToString(CultureInfo.InvariantCulture) : "";
                        // The raw HTML is evidence, so it is kept verbatim, but under a .txt extension: opening it must
                        // not render it (no external image requests, no script execution).
                        File.WriteAllText(Path.Combine(bodiesDir, stem + suffix + ".html.txt"), v.ContentHtml, Utf8NoBom);
                    }
                }
            }
        }

        public static OrderedMap BuildJson(RunContext ctx)
        {
            OrderedMap root = new OrderedMap();
            OrderedMap tool = new OrderedMap();
            tool.Set("name", "TeamsMessageHistory");
            tool.Set("version", ctx.ToolVersion);
            root.Set("tool", tool);
            root.Set("startedUtc", ctx.StartedUtc);
            root.Set("finishedUtc", ctx.FinishedUtc);
            root.Set("query", ctx.Reference != null ? ctx.Reference.ToMap() : null);
            root.Set("outputFolder", ctx.OutputFolder);
            root.Set("inPlace", ctx.InPlace);
            List<object> sources = new List<object>();
            foreach (SourceCandidate s in ctx.Sources) sources.Add(s.ToMap());
            root.Set("sources", sources);
            List<object> copies = new List<object>();
            foreach (PreservedCopy c in ctx.Copies) copies.Add(c.ToMap());
            root.Set("preservedCopies", copies);
            List<object> analyzed = new List<object>();
            foreach (AnalyzedSource a in ctx.Analyzed) analyzed.Add(AnalyzedToMap(a));
            root.Set("analyzed", analyzed);
            root.Set("overallStatus", OverallStatus(ctx));
            root.Set("overallStatusNote", "IndexedDB の保存データだけの判定。通知・キャッシュ側は traces を参照。");
            OrderedMap traces = TracesToMap(ctx.Traces);
            traces.Set("versions", VersionsToList(ctx));
            root.Set("traces", traces);
            // counts only: the texts themselves are in thread/thread.json and thread/texts.txt
            root.Set("thread", ThreadSearch.ToMap(ctx.Thread, false));
            root.Set("notes", new List<object>(ctx.Notes.ToArray()));
            OrderedMap timings = new OrderedMap();
            foreach (KeyValuePair<string, double> t in Timing.Seconds()) timings.Set(t.Key, Math.Round(t.Value, 2));
            string compile = Environment.GetEnvironmentVariable("TMH_COMPILE_SECONDS");
            if (!string.IsNullOrEmpty(compile)) timings.Set("起動時のコンパイル（BAT から起動した場合）", compile);
            root.Set("timingsSeconds", timings);
            return root;
        }

        private static OrderedMap TracesToMap(TraceStage t)
        {
            OrderedMap m = new OrderedMap();
            if (t == null)
            {
                m.Set("mode", "not_run");
                return m;
            }
            m.Set("mode", t.Mode);
            m.Set("notes", new List<object>(t.Notes.ToArray()));
            m.Set("error", t.Error);
            m.Set("note", "痕跡は IndexedDB 以外（通知データベース・キャッシュ）の一致で、対象メッセージの本文とは証明されていない。candidates だけが対象 ID の一致。otherConversation は別会話の同じ ID、idElsewhere は ID が別レコードのフィールド内、nearbyOnly は会話 ID・親 ID だけの一致で、いずれも対象の履歴には数えない。キャッシュの応答日時は編集日時ではない。");
            List<object> sources = new List<object>();
            foreach (TraceSourceResult r in t.Results) sources.Add(TraceScanner.ToMap(r, false));
            m.Set("sources", sources);
            m.Set("sourcesScanned", t.SourcesScanned);
            m.Set("candidateCount", t.CountOf("candidate"));
            m.Set("otherConversationCount", t.CountOf("other-conversation"));
            m.Set("idElsewhereCount", t.CountOf("id-elsewhere"));
            OrderedMap nearby = new OrderedMap();
            nearby.Set("conversationId", t.NearbyOnly("conversationId"));
            nearby.Set("parentMessageId", t.NearbyOnly("parentMessageId"));
            m.Set("nearbyOnlyCount", nearby);
            List<object> candidates = new List<object>();
            foreach (string cls in new string[] { "candidate", "other-conversation", "id-elsewhere" })
            {
                foreach (KeyValuePair<TraceHit, string> p in t.HitsOf(cls))
                {
                    TraceHit h = p.Key;
                    OrderedMap c = new OrderedMap();
                    c.Set("classification", h.Classification);
                    c.Set("sourceKind", h.SourceKind);
                    c.Set("fileKind", h.FileKind);
                    c.Set("relativePath", h.RelativePath);
                    c.Set("offset", h.Offset);
                    c.Set("fromGzip", h.FromGzip);
                    c.Set("inDecodedCacheBody", h.Entry != null);
                    c.Set("conversationMatch", h.ConversationMatch);
                    c.Set("recordFound", h.ObjectFound);
                    c.Set("recordParsed", h.ObjectParsed);
                    c.Set("recordComplete", h.ObjectFound && !h.ObjectTruncated);
                    c.Set("recordLength", h.ObjectLength);
                    c.Set("recordTextCut", h.ObjectTextCut);
                    c.Set("recordId", h.ObjectId);
                    c.Set("recordIdIsTarget", h.ObjectIdIsTarget);
                    c.Set("recordConversation", h.ObjectConversation);
                    c.Set("recordEditTime", h.ObjectEditTime);
                    c.Set("recordClientMessageId", h.ObjectClientMessageId);
                    c.Set("contentText", h.ObjectContentText);
                    c.Set("content", h.ObjectContent);
                    c.Set("nearestElement", h.Fragment);
                    c.Set("nearestElementKind", h.ContextKind);
                    if (h.Entry != null)
                    {
                        OrderedMap e = new OrderedMap();
                        e.Set("format", h.Entry.Format);
                        e.Set("location", h.Entry.Location);
                        e.Set("fromIndex", h.Entry.FromIndex);
                        e.Set("key", h.Entry.Key);
                        e.Set("contentEncoding", h.Entry.ContentEncoding);
                        e.Set("decodeStatus", h.Entry.DecodeStatus);
                        e.Set("responseTime", h.Entry.ResponseTime);
                        e.Set("responseTimeNote", "キャッシュに記録された応答日時。編集日時ではない。");
                        c.Set("cacheEntry", e);
                    }
                    c.Set("hitFile", p.Value);
                    candidates.Add(c);
                }
            }
            m.Set("candidates", candidates);
            m.Set("detailFile", t.Output != null ? "traces/traces.json" : null);
            m.Set("screenFile", t.Output != null ? "traces.txt" : null);
            return m;
        }

        private static List<object> VersionsToList(RunContext ctx)
        {
            List<object> list = new List<object>();
            foreach (TraceVersion v in TraceVersions(ctx))
            {
                OrderedMap m = new OrderedMap();
                m.Set("number", v.Number);
                m.Set("text", v.Text);
                m.Set("textSha256", Sha256Util.HexOfString(v.Text ?? ""));
                m.Set("sameAsBody", v.SameAsBody > 0 ? (object)v.SameAsBody : null);
                m.Set("occurrences", v.Occurrences.Count);
                m.Set("cacheOccurrences", v.CacheCount);
                m.Set("notificationOccurrences", v.NotificationCount);
                m.Set("conversationMatch", v.MatchCount);
                m.Set("conversationUnknown", v.UnknownCount);
                m.Set("conversationUnchecked", v.UncheckedCount);
                m.Set("recordComplete", v.CompleteCount);
                m.Set("recordIncomplete", v.IncompleteCount);
                m.Set("recordTextCut", v.TextCutCount);
                m.Set("editTimes", new List<object>(v.EditTimes.ToArray()));
                m.Set("htmlForms", new List<object>(v.HtmlForms.ToArray()));
                List<object> files = new List<object>();
                foreach (KeyValuePair<TraceHit, string> p in v.Occurrences) files.Add(p.Value);
                m.Set("hitFiles", files);
                m.Set("note", "痕跡の記録から読めた本文。対象メッセージの版だと証明されたものではない。");
                list.Add(m);
            }
            return list;
        }

        public static string OverallStatus(RunContext ctx)
        {
            bool found = false, other = false, unreadable = false, error = false;
            foreach (AnalyzedSource a in ctx.Analyzed)
            {
                if (a.Error != null) { error = true; continue; }
                if (a.Result == null) continue;
                if (a.Result.Status == "found") found = true;
                else if (a.Result.Status == "found_in_other_conversation") other = true;
                else if (a.Result.Status == "unreadable") unreadable = true;
            }
            if (found) return "found";
            if (other) return "found_in_other_conversation";
            if (unreadable) return "unreadable";
            if (error && ctx.Analyzed.Count > 0) return "error";
            if (ctx.Analyzed.Count == 0) return "no_sources";
            return "not_found";
        }

        /// <summary>A body group with its global number: the number of its files (bodies\body-NN) across all analysed
        /// sources, which is what the report and the console show. Groups without content have no number and no file.</summary>
        public sealed class BodyRef
        {
            public int Number;            // 0 when the group has no content (no file)
            public int SourceIndex;       // 1-based index of the analysed source
            public AnalyzedSource Source;
            public BodyGroup Group;
            public string TextFile;       // bodies\body-NN.txt or null
            public string HtmlFile;       // bodies\body-NN.html.txt / -vN form or null
        }

        public static List<BodyRef> BodyRefs(RunContext ctx)
        {
            List<BodyRef> refs = new List<BodyRef>();
            int n = 0, sourceIndex = 0;
            foreach (AnalyzedSource a in ctx.Analyzed)
            {
                sourceIndex++;
                if (a.Result == null) continue;
                foreach (BodyGroup g in a.Result.Bodies)
                {
                    BodyRef r = new BodyRef();
                    r.SourceIndex = sourceIndex;
                    r.Source = a;
                    r.Group = g;
                    if (g.HasContent)
                    {
                        n++;
                        r.Number = n;
                        string stem = "body-" + n.ToString("00", CultureInfo.InvariantCulture);
                        r.TextFile = "bodies\\" + stem + ".txt";
                        bool html = false;
                        foreach (BodyVariant v in g.Variants) if (v.ContentHtml != null) html = true;
                        if (html) r.HtmlFile = g.Variants.Count > 1 ? "bodies\\" + stem + "-vN.html.txt" : "bodies\\" + stem + ".html.txt";
                    }
                    refs.Add(r);
                }
            }
            return refs;
        }

        public static string EditTimeOf(BodyGroup g)
        {
            foreach (Occurrence o in g.Occurrences)
            {
                string e = o.Fields.GetString("properties.edittimeIso") ?? o.Fields.GetString("properties.edittime");
                if (e != null) return e;
            }
            return null;
        }

        /// <summary>Candidates from the traces whose record carried both the target id and a content field, grouped by
        /// identical plain text: one entry per distinct version, with every occurrence as provenance.</summary>
        public sealed class TraceVersion
        {
            public int Number;
            public string Text;
            public readonly List<KeyValuePair<TraceHit, string>> Occurrences = new List<KeyValuePair<TraceHit, string>>();
            public readonly List<string> HtmlForms = new List<string>();
            public int SameAsBody;        // global body number with the same text, or 0
            public int MatchCount, UnknownCount, UncheckedCount, CompleteCount, IncompleteCount, TextCutCount, CacheCount, NotificationCount;
            public readonly List<string> EditTimes = new List<string>();
        }

        public static List<TraceVersion> TraceVersions(RunContext ctx)
        {
            List<TraceVersion> versions = new List<TraceVersion>();
            if (ctx.Traces == null || ctx.Traces.Output == null) return versions;
            Dictionary<string, TraceVersion> byText = new Dictionary<string, TraceVersion>(StringComparer.Ordinal);
            List<BodyRef> bodies = BodyRefs(ctx);
            foreach (KeyValuePair<TraceHit, string> p in ctx.Traces.Output.HitFiles)
            {
                TraceHit h = p.Key;
                if (h.Classification != "candidate" || !h.ObjectParsed || !h.ObjectIdIsTarget || h.ObjectContentText == null) continue;
                string key = h.ObjectContentText.Trim();
                TraceVersion v;
                if (!byText.TryGetValue(key, out v))
                {
                    v = new TraceVersion();
                    v.Number = versions.Count + 1;
                    v.Text = h.ObjectContentText;
                    foreach (BodyRef b in bodies)
                        if (b.Number > 0 && b.Group.ContentText != null && b.Group.ContentText.Trim() == key) { v.SameAsBody = b.Number; break; }
                    byText[key] = v;
                    versions.Add(v);
                }
                v.Occurrences.Add(p);
                if (h.ObjectContent != null && !v.HtmlForms.Contains(h.ObjectContent)) v.HtmlForms.Add(h.ObjectContent);
                if (h.ConversationMatch == "match") v.MatchCount++; else if (h.ConversationMatch == "unchecked") v.UncheckedCount++; else v.UnknownCount++;
                if (h.ObjectTruncated) v.IncompleteCount++; else v.CompleteCount++;
                if (h.ObjectTextCut) v.TextCutCount++;
                if (h.SourceKind == "wpn") v.NotificationCount++; else v.CacheCount++;
                if (h.ObjectEditTime != null && !v.EditTimes.Contains(h.ObjectEditTime)) v.EditTimes.Add(h.ObjectEditTime);
            }
            return versions;
        }

        private static OrderedMap AnalyzedToMap(AnalyzedSource a)
        {
            OrderedMap m = new OrderedMap();
            m.Set("label", a.Label);
            m.Set("levelDbPath", a.LevelDbPath);
            m.Set("error", a.Error);
            if (a.Reader != null)
            {
                OrderedMap parse = new OrderedMap();
                List<object> files = new List<object>();
                List<string> names = new List<string>(a.Reader.Folder.Files.Keys);
                names.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string name in names)
                {
                    LevelDbFileInfo fi = a.Reader.Folder.Files[name];
                    OrderedMap f = new OrderedMap();
                    f.Set("file", fi.FileName);
                    f.Set("kind", fi.Kind);
                    f.Set("bytes", fi.Bytes);
                    f.Set("records", fi.Records);
                    f.Set("deletionMarkers", fi.DeletionMarkers);
                    f.Set("unsupportedBlocks", fi.UnsupportedBlocks);
                    files.Add(f);
                }
                parse.Set("files", files);
                List<object> warnings = new List<object>();
                foreach (LevelDbWarning w in a.Reader.Folder.Warnings) warnings.Add(w.ToString());
                parse.Set("levelDbWarnings", warnings);
                parse.Set("schemaNotes", new List<object>(a.Reader.SchemaNotes.ToArray()));
                List<object> dbs = new List<object>();
                List<long> ids = new List<long>(a.Reader.Databases.Keys);
                ids.Sort();
                foreach (long id in ids)
                {
                    IdbDatabaseInfo info = a.Reader.Databases[id];
                    OrderedMap d = new OrderedMap();
                    d.Set("id", info.Id);
                    d.Set("origin", info.Origin);
                    d.Set("name", info.Name);
                    List<object> stores = new List<object>();
                    List<long> sids = new List<long>(info.StoreNames.Keys);
                    sids.Sort();
                    foreach (long sid in sids) stores.Add(info.StoreNames[sid]);
                    d.Set("stores", stores);
                    dbs.Add(d);
                }
                parse.Set("databases", dbs);
                m.Set("parse", parse);
            }
            if (a.Result != null) m.Set("findings", ResultToMap(a.Result));
            if (a.Diagnostics != null)
            {
                OrderedMap dm = new OrderedMap();
                dm.Set("data", a.Diagnostics.Data);
                dm.Set("lines", new List<object>(a.Diagnostics.Lines.ToArray()));
                m.Set("diagnostics", dm);
            }
            return m;
        }

        private static OrderedMap ResultToMap(FinderResult r)
        {
            OrderedMap m = new OrderedMap();
            m.Set("status", r.Status);
            OrderedMap stats = new OrderedMap();
            stats.Set("itemsScanned", r.Stats.ItemsScanned);
            stats.Set("dataVersions", r.Stats.DataVersions);
            stats.Set("undoEntries", r.Stats.UndoEntries);
            stats.Set("deletionMarkers", r.Stats.DeletionMarkers);
            stats.Set("byteCandidates", r.Stats.ByteCandidates);
            stats.Set("decoded", r.Stats.Decoded);
            stats.Set("decodeFailures", r.Stats.DecodeFailures);
            stats.Set("unwrapFailures", r.Stats.UnwrapFailures);
            stats.Set("wrappedUnreadable", r.Stats.WrappedUnreadable);
            stats.Set("orphanBlobFiles", r.Stats.OrphanBlobFiles);
            stats.Set("orphanBlobUnreadable", r.Stats.OrphanBlobUnreadable);
            stats.Set("orphanBlobMatched", r.Stats.OrphanBlobMatched);
            stats.Set("target", r.Stats.Target.ToMap());
            stats.Set("syncState", r.Stats.SyncState != null && r.Stats.SyncState.Found ? r.Stats.SyncState.ToMap() : null);
            stats.Set("storeVersions", DictToMap(r.Stats.StoreVersions));
            stats.Set("storeCandidates", DictToMap(r.Stats.StoreCandidates));
            m.Set("stats", stats);
            m.Set("clientMessageIds", new List<object>(r.ClientMessageIds.ToArray()));
            List<object> bodies = new List<object>();
            foreach (BodyGroup g in r.Bodies)
            {
                OrderedMap b = new OrderedMap();
                b.Set("index", g.Index);
                b.Set("hasContent", g.HasContent);
                b.Set("contentText", g.ContentText);
                b.Set("textSha256", g.TextSha256);
                b.Set("firstSequence", SequenceBoundary(g.Occurrences, true));
                b.Set("lastSequence", SequenceBoundary(g.Occurrences, false));
                b.Set("occurrenceCount", g.Occurrences.Count);
                List<object> variants = new List<object>();
                foreach (BodyVariant v in g.Variants)
                {
                    OrderedMap vm = new OrderedMap();
                    vm.Set("index", v.Index);
                    vm.Set("contentHtml", v.ContentHtml);
                    vm.Set("sha256", v.Sha256);
                    vm.Set("firstSequence", SequenceBoundary(v.Occurrences, true));
                    vm.Set("lastSequence", SequenceBoundary(v.Occurrences, false));
                    List<object> occ = new List<object>();
                    foreach (Occurrence o in v.Occurrences) occ.Add(OccurrenceToMap(o));
                    vm.Set("occurrences", occ);
                    variants.Add(vm);
                }
                b.Set("variants", variants);
                bodies.Add(b);
            }
            m.Set("bodies", bodies);
            List<object> others = new List<object>();
            foreach (Occurrence o in r.OtherConversation) others.Add(OccurrenceToMap(o));
            m.Set("otherConversation", others);
            m.Set("unreadable", UnreadableList(r.Unreadable));
            m.Set("unreadableWrapped", UnreadableList(r.UnreadableWrapped));
            m.Set("unmatchedRawHits", UnreadableList(r.UnmatchedRawHits));
            m.Set("notes", new List<object>(r.Notes.ToArray()));
            return m;
        }

        private static List<object> UnreadableList(List<UnreadableItem> items)
        {
            List<object> list = new List<object>();
            foreach (UnreadableItem u in items)
            {
                OrderedMap m = new OrderedMap();
                m.Set("sourceKind", u.SourceKind);
                m.Set("database", u.Database);
                m.Set("store", u.Store);
                m.Set("key", u.KeyDisplay);
                m.Set("sequence", u.Sequence);
                m.Set("file", u.File);
                m.Set("offset", u.Offset);
                m.Set("needleEncoding", u.Needle);
                m.Set("reason", u.Reason);
                list.Add(m);
            }
            return list;
        }

        // Standalone blob files carry no LevelDB sequence. Never present their sentinel zero
        // as an observed storage order, including when they join a group with known records.
        private static object SequenceBoundary(IEnumerable<Occurrence> occurrences, bool first)
        {
            ulong? value = null;
            foreach (Occurrence o in occurrences)
            {
                if (o.SourceKind == "orphan-blob") continue;
                if (!value.HasValue || (first ? o.Sequence < value.Value : o.Sequence > value.Value)) value = o.Sequence;
            }
            return value.HasValue ? (object)value.Value : null;
        }

        public static string SequenceRange(IEnumerable<Occurrence> occurrences)
        {
            object first = SequenceBoundary(occurrences, true);
            if (first == null) return "不明";
            return Convert.ToString(first, CultureInfo.InvariantCulture) + "-" + Convert.ToString(SequenceBoundary(occurrences, false), CultureInfo.InvariantCulture);
        }

        private static OrderedMap DictToMap(Dictionary<string, long> dict)
        {
            OrderedMap m = new OrderedMap();
            List<string> keys = new List<string>(dict.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (string k in keys) m.Set(k, dict[k]);
            return m;
        }

        public static OrderedMap OccurrenceToMap(Occurrence o)
        {
            OrderedMap m = new OrderedMap();
            m.Set("sourceKind", o.SourceKind);
            m.Set("matchKind", o.MatchKind);
            m.Set("database", o.Database);
            m.Set("store", o.Store);
            m.Set("key", o.KeyDisplay);
            m.Set("file", o.File);
            m.Set("sequence", o.SourceKind == "orphan-blob" ? null : (object)o.Sequence);
            m.Set("offset", o.Offset);
            m.Set("live", o.SourceKind == "orphan-blob" ? null : (object)o.Live);
            m.Set("fromCompressedBlock", o.FromCompressedBlock);
            if (o.SourceKind == "undo-log")
            {
                m.Set("undoScope", o.UndoScope);
                m.Set("undoSequence", o.UndoSequence);
            }
            m.Set("path", o.Path);
            m.Set("conversationIdInRecord", o.ConversationIdInRecord);
            m.Set("conversationMatch", o.ConversationMatch);
            m.Set("hasContentField", o.HasContentField);
            m.Set("contentSha256", o.ContentHtml != null ? Sha256Util.HexOfString(o.ContentHtml) : null);
            m.Set("wasExternalBlob", o.WasExternalBlob);
            m.Set("wasSnappy", o.WasSnappy);
            m.Set("blobPath", o.BlobPath);
            if (o.WasExternalBlob)
            {
                m.Set("blobEntrySequence", o.SourceKind == "orphan-blob" ? null : (object)o.BlobEntrySequence);
                m.Set("blobNumber", o.BlobNumber);
                m.Set("blobEntryFromUndoLog", o.BlobEntryFromUndoLog);
            }
            m.Set("fields", o.Fields);
            m.Set("recordFields", o.RecordFields);
            return m;
        }

        // ------------------------------------------------------------------ text report

        public static string BuildText(RunContext ctx)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Teams メッセージ本文履歴 - 結果");
            sb.AppendLine("ツール: TeamsMessageHistory " + ctx.ToolVersion);
            sb.AppendLine("開始(UTC): " + ctx.StartedUtc + "  終了(UTC): " + ctx.FinishedUtc);
            sb.AppendLine();
            if (ctx.Reference != null)
            {
                sb.AppendLine("[入力]");
                sb.AppendLine("  入力の種類: " + ctx.Reference.InputKind);
                sb.AppendLine("  会話ID: " + (ctx.Reference.ConversationId ?? "(なし)"));
                sb.AppendLine("  メッセージID: " + (ctx.Reference.MessageId ?? "(なし)"));
                if (ctx.Reference.ParentMessageId != null) sb.AppendLine("  parentMessageId（親投稿、対象ではない）: " + ctx.Reference.ParentMessageId);
                foreach (string n in ctx.Reference.Notes) sb.AppendLine("  注: " + n);
                sb.AppendLine();
            }
            string overall = OverallStatus(ctx);
            sb.AppendLine("[判定（IndexedDB の保存データ）] " + StatusText(overall));
            sb.AppendLine("[痕跡（通知・キャッシュ）] " + TraceSummary(ctx.Traces));
            sb.AppendLine("[スレッド全体の探索] " + ThreadSearch.Summary(ctx.Thread));
            sb.AppendLine();

            // 1. the bodies themselves, in full, with the files they are saved in (the details follow further down)
            sb.AppendLine("[本文候補]  IndexedDB の保存データに残っていた対象メッセージの本文。番号は bodies\\body-NN.txt / .html.txt に対応。保存順序は端末に書かれた順で、編集日時ではない。");
            List<BodyRef> bodyRefs = BodyRefs(ctx);
            foreach (BodyRef b in bodyRefs)
            {
                BodyGroup g = b.Group;
                string edit = EditTimeOf(g);
                sb.AppendLine("  --- " + (b.Number > 0 ? "本文候補 " + b.Number.ToString(CultureInfo.InvariantCulture) + "  " + b.TextFile + (b.HtmlFile != null ? " / " + b.HtmlFile : "") : "本文の無い候補（ファイルなし）"));
                sb.AppendLine("      出現 " + g.Occurrences.Count.ToString(CultureInfo.InvariantCulture) + " 回 / 保存順序 " + SequenceRange(g.Occurrences)
                    + " / 解析元 " + b.SourceIndex.ToString(CultureInfo.InvariantCulture) + ": " + b.Source.Label + " の候補 " + g.Index.ToString(CultureInfo.InvariantCulture)
                    + (edit != null ? " / レコード内の properties.edittime: " + edit : " / レコード内の properties.edittime: なし")
                    + (g.Variants.Count > 1 ? " / HTML 表現 " + g.Variants.Count.ToString(CultureInfo.InvariantCulture) + " 種" : ""));
                if (!g.HasContent) sb.AppendLine("      本文: (content フィールドが空、または読めない)");
                else
                {
                    sb.AppendLine("      本文（全文）:");
                    foreach (string line in (g.ContentText ?? "").Replace("\r\n", "\n").Split('\n')) sb.AppendLine("        " + line);
                }
            }
            if (bodyRefs.Count == 0) sb.AppendLine("  (残存する本文候補なし。" + (ctx.Analyzed.Count == 0 ? "IndexedDB の保存データが無く、解析していない" : "IndexedDB の保存データに該当する本文が無い") + ")");
            sb.AppendLine();

            // 2. versions found in the traces whose record carried both the id and a content field, one entry per distinct text
            AppendTraceVersions(sb, ctx, bodyRefs);

            // 3. everything else related to the message, its thread or the words given (never a body candidate)
            ThreadSearch.AppendReport(sb, ctx.Thread);

            sb.AppendLine("[解析元]");
            if (ctx.Sources.Count == 0) sb.AppendLine("  (なし)");
            for (int i = 0; i < ctx.Sources.Count; i++)
            {
                SourceCandidate s = ctx.Sources[i];
                sb.AppendLine("  " + (i + 1).ToString(CultureInfo.InvariantCulture) + ". " + s.Label);
                sb.AppendLine("     " + s.LevelDbPath);
                if (s.BlobPath != null) sb.AppendLine("     blob: " + s.BlobPath);
            }
            foreach (PreservedCopy c in ctx.Copies)
            {
                sb.AppendLine("  保全コピー: " + c.DestinationLevelDb + (c.Complete ? " (全ファイル複製)" : " (一部のファイルを複製できず)"));
                foreach (PreservedFile f in c.Files)
                {
                    if (f.Error != null) sb.AppendLine("     複製失敗: " + f.RelativePath + " - " + f.Error);
                }
                if (c.UnchangedAfterAnalysis.HasValue) sb.AppendLine("  解析後の再ハッシュ: " + (c.UnchangedAfterAnalysis.Value ? "保全コピーは変化なし" : "保全コピーに変化あり（要確認）"));
            }
            if (ctx.InPlace) sb.AppendLine("  指定フォルダーをそのまま読取専用で解析しました（複製なし）。");
            sb.AppendLine();
            sb.AppendLine("[詳細な根拠]  解析元ごとのファイル統計、本文候補の出現ごとの出典（ファイル・保存順序・ストア・キー・位置）、読めなかった候補、痕跡の一致ごとの出典");
            sb.AppendLine();

            foreach (AnalyzedSource a in ctx.Analyzed)
            {
                sb.AppendLine("==== " + a.Label);
                sb.AppendLine("  " + a.LevelDbPath);
                if (a.Error != null)
                {
                    sb.AppendLine("  読み取りエラー: " + a.Error);
                    sb.AppendLine();
                    continue;
                }
                if (a.Reader != null)
                {
                    List<string> names = new List<string>(a.Reader.Folder.Files.Keys);
                    names.Sort(StringComparer.OrdinalIgnoreCase);
                    foreach (string name in names)
                    {
                        LevelDbFileInfo fi = a.Reader.Folder.Files[name];
                        sb.AppendLine("  ファイル " + fi.FileName + ": " + fi.Bytes.ToString(CultureInfo.InvariantCulture) + " bytes, レコード " + fi.Records.ToString(CultureInfo.InvariantCulture)
                            + " 件 (削除マーカー " + fi.DeletionMarkers.ToString(CultureInfo.InvariantCulture) + ")" + (fi.UnsupportedBlocks > 0 ? ", 読めないブロック " + fi.UnsupportedBlocks.ToString(CultureInfo.InvariantCulture) : ""));
                    }
                    if (a.Reader.Folder.Warnings.Count > 0) sb.AppendLine("  LevelDB の警告: " + a.Reader.Folder.Warnings.Count.ToString(CultureInfo.InvariantCulture) + " 件（result.json 参照）");
                }
                FinderResult r = a.Result;
                if (r == null) { sb.AppendLine(); continue; }
                sb.AppendLine("  判定: " + StatusText(r.Status));
                sb.AppendLine("  走査: レコード版 " + r.Stats.DataVersions.ToString(CultureInfo.InvariantCulture) + " 件, undo ログ " + r.Stats.UndoEntries.ToString(CultureInfo.InvariantCulture)
                    + " 件, ID を含む候補 " + r.Stats.ByteCandidates.ToString(CultureInfo.InvariantCulture) + " 件, 復号 " + r.Stats.Decoded.ToString(CultureInfo.InvariantCulture)
                    + " 件, 復号失敗 " + r.Stats.DecodeFailures.ToString(CultureInfo.InvariantCulture) + " 件");
                List<string> storeNames = new List<string>(r.Stats.StoreVersions.Keys);
                storeNames.Sort(StringComparer.Ordinal);
                foreach (string sn in storeNames)
                {
                    if (sn.IndexOf("replychain", StringComparison.OrdinalIgnoreCase) < 0 && sn.IndexOf("conversation-manager", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    long cand;
                    r.Stats.StoreCandidates.TryGetValue(sn, out cand);
                    sb.AppendLine("    " + sn + ": レコード版 " + r.Stats.StoreVersions[sn].ToString(CultureInfo.InvariantCulture) + " 件, ID 含有 " + cand.ToString(CultureInfo.InvariantCulture) + " 件");
                }
                if (r.ClientMessageIds.Count > 0) sb.AppendLine("  clientMessageId で追加照合: " + string.Join(", ", r.ClientMessageIds.ToArray()));
                sb.AppendLine();
                foreach (BodyGroup g in r.Bodies)
                {
                    int globalNumber = 0;
                    foreach (BodyRef b in bodyRefs) if (b.Source == a && b.Group == g) { globalNumber = b.Number; break; }
                    sb.AppendLine("  --- この解析元の候補 " + g.Index.ToString(CultureInfo.InvariantCulture) + (globalNumber > 0 ? " = 本文候補 " + globalNumber.ToString(CultureInfo.InvariantCulture) + " (bodies\\body-" + globalNumber.ToString("00", CultureInfo.InvariantCulture) + ".txt)" : " (本文なし)")
                        + " (出現 " + g.Occurrences.Count.ToString(CultureInfo.InvariantCulture) + " 回, 保存順序 " + SequenceRange(g.Occurrences) + ")");
                    if (!g.HasContent) sb.AppendLine("    本文: (content フィールドが空、または読めない)");
                    else
                    {
                        sb.AppendLine("    本文(テキスト): " + OneLine(g.ContentText, 400));
                        sb.AppendLine("    テキストの SHA-256: " + g.TextSha256);
                    }
                    foreach (BodyVariant v in g.Variants)
                    {
                        if (g.Variants.Count > 1 || v.ContentHtml != null)
                        {
                            sb.AppendLine("    [HTML 表現 " + v.Index.ToString(CultureInfo.InvariantCulture) + "] (出現 " + v.Occurrences.Count.ToString(CultureInfo.InvariantCulture) + " 回) " + (v.ContentHtml == null ? "(なし)" : OneLine(v.ContentHtml, 400)));
                            if (v.Sha256 != null) sb.AppendLine("      HTML の SHA-256: " + v.Sha256);
                        }
                        foreach (Occurrence o in v.Occurrences)
                        {
                            sb.AppendLine("    * " + SourceText(o) + (o.SourceKind == "orphan-blob" ? " 保存順序不明 " : " 保存順序 seq=" + o.Sequence.ToString(CultureInfo.InvariantCulture) + " ") + o.File + " offset=" + o.Offset.ToString(CultureInfo.InvariantCulture)
                                + (o.Live || o.SourceKind == "orphan-blob" ? "" : " (削除マーカー)") + (o.SourceKind == "undo-log" ? " undo scope=" + o.UndoScope.ToString(CultureInfo.InvariantCulture) + " #" + o.UndoSequence.ToString(CultureInfo.InvariantCulture) : ""));
                            sb.AppendLine("      DB: " + o.Database + " / ストア: " + o.Store + " / キー: " + o.KeyDisplay);
                            sb.AppendLine("      位置: " + o.Path + " / 照合: " + MatchText(o.MatchKind) + " / 会話ID: " + (o.ConversationIdInRecord ?? "(不明)") + " (" + ConversationText(o.ConversationMatch) + ")");
                            if (o.WasExternalBlob) sb.AppendLine("      値は外部 blob ファイル: " + o.BlobPath + (o.SourceKind == "orphan-blob" ? " (参照の記録なし" : " (blob 参照 seq=" + o.BlobEntrySequence.ToString(CultureInfo.InvariantCulture))
                                + (o.BlobEntryFromUndoLog ? " undoログ由来" : "") + ", blob 番号 " + o.BlobNumber.ToString(CultureInfo.InvariantCulture) + ")");
                            List<string> fieldParts = new List<string>();
                            foreach (KeyValuePair<string, object> pair in o.Fields)
                            {
                                if (pair.Key == "preview") continue;
                                fieldParts.Add(pair.Key + "=" + OneLine(Convert.ToString(pair.Value, CultureInfo.InvariantCulture), 120));
                            }
                            if (fieldParts.Count > 0) sb.AppendLine("      フィールド: " + string.Join(", ", fieldParts.ToArray()));
                            List<string> recParts = new List<string>();
                            foreach (KeyValuePair<string, object> pair in o.RecordFields) recParts.Add(pair.Key + "=" + OneLine(Convert.ToString(pair.Value, CultureInfo.InvariantCulture), 80));
                            if (recParts.Count > 0) sb.AppendLine("      レコード: " + string.Join(", ", recParts.ToArray()));
                        }
                    }
                    sb.AppendLine();
                }
                if (r.OtherConversation.Count > 0)
                {
                    sb.AppendLine("  --- 別の会話IDで同じメッセージIDを持つ候補（対象外として除外）: " + r.OtherConversation.Count.ToString(CultureInfo.InvariantCulture) + " 件");
                    foreach (Occurrence o in r.OtherConversation)
                        sb.AppendLine("    * " + SourceText(o) + (o.SourceKind == "orphan-blob" ? " 保存順序不明 " : " seq=" + o.Sequence.ToString(CultureInfo.InvariantCulture) + " ") + o.File + " 会話ID=" + (o.ConversationIdInRecord ?? "(不明)") + " / " + o.Database + " / " + o.Store);
                    sb.AppendLine();
                }
                if (r.Unreadable.Count > 0)
                {
                    sb.AppendLine("  --- 読めなかった候補（ID の文字列を含むが復号できない）: " + r.Unreadable.Count.ToString(CultureInfo.InvariantCulture) + " 件");
                    foreach (UnreadableItem u in r.Unreadable)
                        sb.AppendLine("    * " + u.SourceKind + " seq=" + u.Sequence.ToString(CultureInfo.InvariantCulture) + " " + u.File + " offset=" + u.Offset.ToString(CultureInfo.InvariantCulture) + " / " + u.Database + " / " + u.Store + " / キー: " + u.KeyDisplay + " / 理由: " + u.Reason);
                    sb.AppendLine();
                }
                if (r.UnreadableWrapped.Count > 0)
                {
                    sb.AppendLine("  --- 外部 blob／圧縮に包まれて読めなかった値（キーから対象との関係を判断できないもの）: " + r.UnreadableWrapped.Count.ToString(CultureInfo.InvariantCulture) + " 件（result.json 参照）");
                    int shown = 0;
                    foreach (UnreadableItem u in r.UnreadableWrapped)
                    {
                        if (shown++ >= 10) { sb.AppendLine("    ..."); break; }
                        sb.AppendLine("    * " + u.SourceKind + " seq=" + u.Sequence.ToString(CultureInfo.InvariantCulture) + " " + u.File + " / " + u.Store + " / キー: " + OneLine(u.KeyDisplay, 80));
                    }
                    sb.AppendLine();
                }
                if (r.UnmatchedRawHits.Count > 0)
                {
                    sb.AppendLine("  --- ID の文字列は含むが本文オブジェクトとして一致しないレコード: " + r.UnmatchedRawHits.Count.ToString(CultureInfo.InvariantCulture) + " 件（参考。result.json 参照）");
                    sb.AppendLine();
                }
                foreach (string n in r.Notes) sb.AppendLine("  注: " + n);
                sb.AppendLine();
                if (a.Diagnostics != null)
                {
                    sb.AppendLine("  --- 診断（本文・名前・ID・パスを含まない要約。diag.txt にも保存）");
                    foreach (string line in a.Diagnostics.Lines) sb.AppendLine("  " + line);
                    sb.AppendLine();
                }
            }
            // traces: every match with its provenance (the grouped versions are at the top)
            AppendTraces(sb, ctx.Traces);

            foreach (string n in ctx.Notes) sb.AppendLine("注: " + n);
            sb.AppendLine();
            AppendTimings(sb);
            sb.AppendLine("保存先: " + ctx.OutputFolder);
            sb.AppendLine("  result.json  詳細（JSON）");
            sb.AppendLine("  result.txt   この要約");
            sb.AppendLine("  bodies\\      本文候補ごとのテキスト (.txt) と生の HTML (.html.txt: 表示されない拡張子で保存)");
            sb.AppendLine("  preserved\\   保全コピーと manifest（複製した場合）");
            if (ctx.Traces != null && ctx.Traces.Output != null)
            {
                sb.AppendLine("  traces.txt   痕跡走査の件数だけの画面");
                sb.AppendLine("  traces\\      痕跡走査の出典（traces.json、一致ごとの hit-NNN-*.txt。本文の断片を含むため共有しない）");
            }
            if (ctx.Thread != null && ctx.Thread.TextsFile != null)
                sb.AppendLine("  thread\\      スレッド全体の探索で集めた文面（texts.txt は全件、thread.json は機械可読。本文を含むため共有しない）");
            return sb.ToString();
        }

        /// <summary>One console line: the stages that took the time (seconds). The full table is in result.txt / result.json.</summary>
        public static string TimingLine()
        {
            StringBuilder sb = new StringBuilder("所要時間（秒）:");
            string compile = Environment.GetEnvironmentVariable("TMH_COMPILE_SECONDS");
            if (!string.IsNullOrEmpty(compile)) sb.Append(" 起動時のコンパイル " + compile + " /");
            foreach (KeyValuePair<string, double> t in Timing.Seconds())
            {
                if (t.Key.StartsWith("  ", StringComparison.Ordinal)) continue;   // sub-stages: result.txt only
                string name = t.Key;
                int paren = name.IndexOf('（');
                if (paren > 0) name = name.Substring(0, paren);
                sb.Append(" " + name + " " + t.Value.ToString("0.0", CultureInfo.InvariantCulture) + " /");
            }
            return sb.ToString().TrimEnd('/', ' ');
        }

        private static void AppendTimings(StringBuilder sb)
        {
            sb.AppendLine("[所要時間]  段階ごとの秒数。字下げした行は、すぐ上の段階の内訳（その段階の時間に含まれる）。入力待ちと、この結果ファイルの書出しは含まない。");
            string compile = Environment.GetEnvironmentVariable("TMH_COMPILE_SECONDS");
            if (!string.IsNullOrEmpty(compile)) sb.AppendLine("  起動時のコンパイル（BAT から起動した場合）: " + compile);
            foreach (KeyValuePair<string, double> t in Timing.Seconds()) sb.AppendLine("  " + t.Key + ": " + t.Value.ToString("0.00", CultureInfo.InvariantCulture));
            sb.AppendLine();
        }

        private static string KindLabel(string kind)
        {
            switch (kind)
            {
                case "wpn": return "通知DB";
                case "httpcache": return "HTTPキャッシュ";
                case "swcache": return "SWキャッシュ";
                default: return "指定フォルダー";
            }
        }

        /// <summary>One line for the top of the report and the console.</summary>
        public static string TraceSummary(TraceStage t)
        {
            if (t == null) return "痕跡走査は行っていない（解析が途中で終了）";
            if (!t.Ran) return "痕跡走査は行っていない（-NoTraces）";
            if (t.Error != null) return "痕跡走査の出力に失敗（" + t.Error + "）";
            string scope = t.Mode == "capture" ? "指定フォルダーの配下のみ" : "この PC の通知DBと解析したプロファイルのキャッシュ";
            if (t.SourcesScanned == 0) return "走査できる保存元なし（" + scope + "。稼働中の他のプロファイルは走査していない）";
            return "対象 ID の一致 " + t.CountOf("candidate").ToString(CultureInfo.InvariantCulture) + " 件（未確認断片。本文候補ではない）"
                + " / 別会話の同じ ID " + t.CountOf("other-conversation").ToString(CultureInfo.InvariantCulture)
                + " / 会話 ID・親 ID だけ " + (t.NearbyOnly("conversationId") + t.NearbyOnly("parentMessageId")).ToString(CultureInfo.InvariantCulture)
                + " / 保存元 " + t.SourcesScanned.ToString(CultureInfo.InvariantCulture) + " 件（" + scope + "）";
        }

        private static void AppendTraces(StringBuilder sb, TraceStage t)
        {
            sb.AppendLine("[痕跡（IndexedDB 以外）]  通知データベースとキャッシュに残る、対象 ID と同じ文字列の周辺。対象メッセージの本文とは証明されておらず、本文候補には数えない。");
            if (t == null) { sb.AppendLine("  (行っていない)"); sb.AppendLine(); return; }
            foreach (string n in t.Notes) sb.AppendLine("  範囲: " + n);
            if (t.Error != null) sb.AppendLine("  出力エラー: " + t.Error);
            if (!t.Ran) { sb.AppendLine(); return; }
            for (int i = 0; i < t.Results.Count; i++)
            {
                TraceSourceResult r = t.Results[i];
                sb.AppendLine("  保存元 " + (i + 1).ToString(CultureInfo.InvariantCulture) + ": " + r.Source.Label + " [" + KindLabel(r.Source.Kind) + "] " + r.Source.Folder
                    + (r.Source.EnumerationFailed ? " (列挙失敗: " + r.Source.EnumerationErrorKind + ")" : " (ファイル " + r.Source.Files.Count.ToString(CultureInfo.InvariantCulture) + ", 走査 " + r.FilesScanned.ToString(CultureInfo.InvariantCulture) + ", 未走査 " + r.FilesUnscanned.ToString(CultureInfo.InvariantCulture) + ")"));
                if (!r.Source.EnumerationFailed && r.Error == null && r.Cache.Directories > 0)
                    sb.AppendLine("     キャッシュ項目 " + r.Cache.FormatLabel + " " + r.Cache.Entries.ToString(CultureInfo.InvariantCulture) + " (索引外 " + r.Cache.Orphan.ToString(CultureInfo.InvariantCulture) + ", 読めず " + r.Cache.Unreadable.ToString(CultureInfo.InvariantCulture)
                        + ") / 展開 成功 " + r.Cache.DecodeOk.ToString(CultureInfo.InvariantCulture) + ", 失敗 " + r.Cache.DecodeFailed.ToString(CultureInfo.InvariantCulture) + ", 未対応 " + r.Cache.DecodeUnsupported.ToString(CultureInfo.InvariantCulture));
            }
            List<KeyValuePair<TraceHit, string>> candidates = t.HitsOf("candidate");
            sb.AppendLine("  --- 対象 ID の一致: " + candidates.Count.ToString(CultureInfo.InvariantCulture) + " 件（一致したレコードの content を示す。記録の保存状態と出典は各 hit ファイル）");
            int k = 0;
            foreach (KeyValuePair<TraceHit, string> p in candidates)
            {
                k++;
                TraceHit h = p.Key;
                sb.AppendLine("    * 一致 " + k.ToString(CultureInfo.InvariantCulture) + ": [" + KindLabel(h.SourceKind) + "] " + h.RelativePath + " 位置 " + h.Offset.ToString(CultureInfo.InvariantCulture)
                    + (h.FromGzip ? " (gzip 展開後)" : "") + (h.Entry != null ? " (キャッシュ項目の展開後の本文)" : "") + " / 会話照合: " + TraceScanner.ConversationMatchText(h.ConversationMatch));
                if (h.ObjectFound && h.ObjectParsed)
                {
                    sb.AppendLine("      レコード id: " + (h.ObjectId ?? "(なし)") + (h.ObjectIdIsTarget ? " (= 対象 ID)" : "") + " / 会話: " + (h.ObjectConversation ?? "(記録なし)")
                        + " / properties.edittime（レコードの値）: " + (h.ObjectEditTime ?? "なし") + " / JSON の読取: " + (h.ObjectTruncated ? "不完全（窓の中で閉じない）" : "完全（レコードは閉じている）")
                        + " / 記録の長さ " + h.ObjectLength.ToString(CultureInfo.InvariantCulture) + " 文字" + (h.ObjectTextCut ? " / 出典ファイルの写しは先頭 " + TraceScanner.ObjectTextLimit.ToString(CultureInfo.InvariantCulture) + " 文字で切り詰め（content は切り詰め前の記録から取得）" : ""));
                    if (h.ObjectContentText != null)
                    {
                        sb.AppendLine("      content（全文）:");
                        foreach (string line in h.ObjectContentText.Replace("\r\n", "\n").Split('\n')) sb.AppendLine("        " + line);
                    }
                    else sb.AppendLine("      content: なし（このレコードに content フィールドが無い）");
                }
                else
                {
                    sb.AppendLine("      レコード: " + (h.ObjectFound ? "JSON として読めない" : "JSON オブジェクトの中ではない") + " / 最寄りの要素（" + h.ContextKind + "。対象との対応は未確認）: " + OneLine(h.Fragment ?? "(なし)", 400));
                }
                if (h.Entry != null) sb.AppendLine("      キャッシュ項目: " + h.Entry.Format + " " + h.Entry.Location + (h.Entry.FromIndex ? "" : " 索引外（削除・退避済みの残り）") + " / 応答日時（キャッシュの記録。編集日時ではない）: " + (h.Entry.ResponseTime ?? "不明") + " / 符号化 " + (h.Entry.ContentEncoding ?? "なし"));
                sb.AppendLine("      出典ファイル: " + p.Value);
            }
            List<KeyValuePair<TraceHit, string>> other = t.HitsOf("other-conversation");
            sb.AppendLine("  --- 別の会話の同じ ID: " + other.Count.ToString(CultureInfo.InvariantCulture) + " 件（対象外。会話 ID が入力と異なる）");
            foreach (KeyValuePair<TraceHit, string> p in other)
                sb.AppendLine("    * [" + KindLabel(p.Key.SourceKind) + "] " + p.Key.RelativePath + " 位置 " + p.Key.Offset.ToString(CultureInfo.InvariantCulture) + " / 会話: " + (p.Key.ObjectConversation ?? "?") + " / " + p.Value);
            List<KeyValuePair<TraceHit, string>> elsewhere = t.HitsOf("id-elsewhere");
            sb.AppendLine("  --- ID が別レコードのフィールド内: " + elsewhere.Count.ToString(CultureInfo.InvariantCulture) + " 件（参考。そのレコードの本文は対象のものではない可能性）");
            foreach (KeyValuePair<TraceHit, string> p in elsewhere)
                sb.AppendLine("    * [" + KindLabel(p.Key.SourceKind) + "] " + p.Key.RelativePath + " 位置 " + p.Key.Offset.ToString(CultureInfo.InvariantCulture) + " / レコード id: " + (p.Key.ObjectId ?? "?") + " / " + p.Value);
            sb.AppendLine("  --- 会話 ID だけの一致 " + t.NearbyOnly("conversationId").ToString(CultureInfo.InvariantCulture) + " 件、親投稿 ID だけの一致 " + t.NearbyOnly("parentMessageId").ToString(CultureInfo.InvariantCulture)
                + " 件（対象メッセージの履歴としては扱わない。出典は traces\\traces.json）");
            if (t.Output != null)
            {
                sb.AppendLine("  --- 件数の画面（traces.txt と同じ）");
                foreach (string line in t.Output.Screen) sb.AppendLine("    " + line);
            }
            sb.AppendLine();
        }

        public static string StatusText(string status)
        {
            switch (status)
            {
                case "found": return "この本文が見つかった（IndexedDB の保存データに残存する本文候補あり）";
                case "found_in_other_conversation": return "同じメッセージIDは別の会話にだけあった（対象の会話では IndexedDB に見つからない）";
                case "unreadable": return "ID を含むデータは IndexedDB にあるが読めなかった（復号失敗・未対応形式）";
                case "error": return "IndexedDB の解析元を読み取れなかった";
                case "no_sources": return "解析元なし（IndexedDB の保存データが見つからない。通知・キャッシュ側の走査結果は [痕跡] を参照）";
                default: return "見つからない（IndexedDB の保存データに該当する本文が無い。通知・キャッシュ側の一致は [痕跡] を参照）";
            }
        }

        private static void AppendTraceVersions(StringBuilder sb, RunContext ctx, List<BodyRef> bodyRefs)
        {
            sb.AppendLine("[痕跡の本文（未確認）]  通知データベース・キャッシュの記録のうち、対象のメッセージ ID と content を同じレコードから読めたもの。本文が同じものを 1 つに束ねた。IndexedDB の本文候補ではなく、対象メッセージの版だと証明されたものでもない。");
            sb.AppendLine("  出所の件数は一致の数で、同じ記録を生バイト走査と項目の展開の両方で見つけた場合は 2 件になる。");
            TraceStage t = ctx.Traces;
            if (t == null || !t.Ran)
            {
                sb.AppendLine("  (痕跡走査なし)");
                sb.AppendLine();
                return;
            }
            List<TraceVersion> versions = TraceVersions(ctx);
            if (versions.Count == 0) sb.AppendLine("  (該当なし。対象 ID と content を同じレコードから読めた一致は無かった)");
            foreach (TraceVersion v in versions)
            {
                sb.AppendLine("  --- 痕跡本文 " + v.Number.ToString(CultureInfo.InvariantCulture)
                    + (v.SameAsBody > 0 ? "  = IndexedDB の本文候補 " + v.SameAsBody.ToString(CultureInfo.InvariantCulture) + " と同じ本文" : "  IndexedDB の本文候補には無い本文"));
                sb.AppendLine("      出所: " + (v.CacheCount > 0 ? "キャッシュ " + v.CacheCount.ToString(CultureInfo.InvariantCulture) + " 件" : "") + (v.CacheCount > 0 && v.NotificationCount > 0 ? " / " : "") + (v.NotificationCount > 0 ? "通知DB " + v.NotificationCount.ToString(CultureInfo.InvariantCulture) + " 件" : "")
                    + " / 会話照合: 一致 " + v.MatchCount.ToString(CultureInfo.InvariantCulture) + "・不明 " + v.UnknownCount.ToString(CultureInfo.InvariantCulture) + (v.UncheckedCount > 0 ? "・未照合 " + v.UncheckedCount.ToString(CultureInfo.InvariantCulture) : "")
                    + " / JSON の読取: 完全 " + v.CompleteCount.ToString(CultureInfo.InvariantCulture) + "・不完全 " + v.IncompleteCount.ToString(CultureInfo.InvariantCulture)
                    + " / 出典ファイルの写しの切り詰め " + v.TextCutCount.ToString(CultureInfo.InvariantCulture) + " 件（content は切り詰め前の記録から取得）"
                    + " / レコードの properties.edittime: " + (v.EditTimes.Count > 0 ? string.Join(", ", v.EditTimes.ToArray()) : "なし")
                    + (v.HtmlForms.Count > 1 ? " / HTML 表現 " + v.HtmlForms.Count.ToString(CultureInfo.InvariantCulture) + " 種" : ""));
                if (v.UnknownCount > 0 && v.MatchCount == 0) sb.AppendLine("      注意: 会話 ID をレコードから確かめられていない（同じ ID の別会話の記録である可能性が残る）");
                sb.AppendLine("      本文（全文）:");
                foreach (string line in (v.Text ?? "").Replace("\r\n", "\n").Split('\n')) sb.AppendLine("        " + line);
                List<string> files = new List<string>();
                foreach (KeyValuePair<TraceHit, string> p in v.Occurrences) files.Add(p.Value);
                sb.AppendLine("      出典: " + string.Join(", ", files.ToArray()));
            }
            int contentless = 0, elsewhere = t.CountOf("id-elsewhere"), other = t.CountOf("other-conversation");
            foreach (TraceSourceResult r in t.Results) foreach (TraceHit h in r.Hits) if (h.Classification == "candidate" && !(h.ObjectParsed && h.ObjectIdIsTarget && h.ObjectContentText != null)) contentless++;
            sb.AppendLine("  そのほかの一致: 対象 ID の近傍だが content を同じレコードから読めないもの " + contentless.ToString(CultureInfo.InvariantCulture) + " 件、別の会話の同じ ID " + other.ToString(CultureInfo.InvariantCulture)
                + " 件、ID が別レコードのフィールド内 " + elsewhere.ToString(CultureInfo.InvariantCulture) + " 件（いずれも本文としては扱わない。後半の [痕跡（IndexedDB 以外）] に出典）");
            sb.AppendLine();
        }

        private static string SourceText(Occurrence o)
        {
            if (o.SourceKind == "undo-log") return "[undoログ: 上書き前の旧値]";
            if (o.SourceKind == "orphan-blob") return "[孤立blob: 参照の無い blob ファイル、保存順序不明]";
            return o.Live ? "[レコード版]" : "[レコード版/削除マーカー]";
        }

        private static string MatchText(string kind)
        {
            switch (kind)
            {
                case "id": return "id フィールド一致";
                case "messageMap-key": return "messageMap のキー一致";
                case "clientMessageId": return "clientMessageId 一致（送信前の版など）";
                case "nested": return "一致したメッセージの中の入れ子（編集前の控えなど。独自の id は持たない）";
                default: return kind;
            }
        }

        private static string ConversationText(string match)
        {
            switch (match)
            {
                case "match": return "入力の会話IDと一致";
                case "unchecked": return "未照合";
                case "unknown": return "レコードから判別できず";
                case "mismatch": return "不一致";
                default: return match ?? "";
            }
        }

        public static string OneLine(string text, int max)
        {
            if (text == null) return "";
            string s = text.Replace("\r", " ").Replace("\n", " ");
            if (s.Length > max) s = s.Substring(0, max) + "...(" + text.Length.ToString(CultureInfo.InvariantCulture) + " 文字)";
            return s;
        }
    }
}
