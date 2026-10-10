// Teams Message History - console flow (interactive or arguments).
//   TeamsMessageHistory.bat                       interactive
//   TeamsMessageHistory.bat -Link <url|id> [-Source <folder>]... [-Out <folder>] [-InPlace] [-Word <text>]...
// Interactive choices are made with the arrow keys (ConsoleMenu); links, words and folders are typed.
// Exit codes: 0 finished (see result), 2 bad input, 3 failure.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TeamsMessageHistory
{
    public static class Program
    {
        public const string Version = "0.1.7";

        public static int Run(string[] args)
        {
            try
            {
                return RunCore(args ?? new string[0]);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("エラー: " + ex.GetType().Name + ": " + ex.Message);
                return 3;
            }
        }

        private static int RunCore(string[] args)
        {
            string link = null;
            List<string> sources = new List<string>();
            string outRoot = null;
            bool inPlace = false;
            bool help = false;
            bool diag = false;
            bool review = false;
            string reviewFolder = null;
            bool traces = false;
            bool noTraces = false;
            List<string> traceSources = new List<string>();
            List<string> words = new List<string>();
            bool noThread = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string key = a.StartsWith("-", StringComparison.Ordinal) || a.StartsWith("/", StringComparison.Ordinal) ? a.Substring(1).ToLowerInvariant() : null;
                if (key == "link" || key == "url" || key == "id") { if (i + 1 < args.Length) link = args[++i]; }
                else if (key == "source" || key == "src") { if (i + 1 < args.Length) sources.Add(args[++i]); }
                else if (key == "out") { if (i + 1 < args.Length) outRoot = args[++i]; }
                else if (key == "inplace") inPlace = true;
                else if (key == "diag") diag = true;
                else if (key == "review" || key == "diag2")
                {
                    review = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal)) reviewFolder = args[++i];
                }
                else if (key == "traces") traces = true;
                else if (key == "notraces") noTraces = true;
                else if (key == "tracesource") { if (i + 1 < args.Length) traceSources.Add(args[++i]); }
                else if (key == "word" || key == "keyword" || key == "find") { if (i + 1 < args.Length) words.Add(args[++i]); }
                else if (key == "nothread") noThread = true;
                else if (key == "help" || key == "?" || key == "h") help = true;
                else if (link == null && !a.StartsWith("-", StringComparison.Ordinal)) link = a;
            }
            if (help)
            {
                PrintUsage();
                return 0;
            }
            if (review) return RunReview(reviewFolder);
            if (traces) return RunTraces(link, traceSources, outRoot);
            bool interactive = link == null;
            Banner();
            if (diag) Console.WriteLine("診断モード: 最後に本文・名前・ID・パスを含まない1画面の要約を表示し、diag.txt に保存します。");

            Timing.Reset();
            long tTotal = Timing.Start();
            RunContext ctx = new RunContext();
            ctx.ToolVersion = Version;
            ctx.Diagnostics = diag;
            ctx.StartedUtc = TimeText.NowIso();

            // 1. message link or id
            MessageReference reference = null;
            while (true)
            {
                string input = link;
                if (input == null)
                {
                    Console.WriteLine("Teams でメッセージの「…」→「リンクをコピー」で取ったリンク（またはメッセージIDの数字）を貼り付けて Enter:");
                    Console.Write("> ");
                    input = ConsoleMenu.ReadLine();
                    if (input == null) return 2;
                }
                reference = MessageReference.Parse(input);
                foreach (string n in reference.Notes) Console.WriteLine("  " + n);
                if (reference.IsValid) break;
                if (!interactive) return 2;
                link = null;
                Console.WriteLine();
            }
            ctx.Reference = reference;
            Console.WriteLine("  会話ID: " + (reference.ConversationId ?? "(なし)"));
            Console.WriteLine("  メッセージID: " + reference.MessageId);
            if (reference.ParentMessageId != null) Console.WriteLine("  parentMessageId: " + reference.ParentMessageId + " (親投稿。対象には使いません)");
            Console.WriteLine();

            // 1b. words the user remembers (optional): searched for in everything that is left, with or without an id nearby
            if (interactive && !noThread)
            {
                Console.WriteLine("探したい本文で覚えている言葉があれば入力して Enter（その言葉を含む文面を、残っているデータ全体から探します）。");
                Console.WriteLine("複数あるときは 1 つずつ入力します。無ければ、または入れ終えたら、何も入れずに Enter:");
                while (words.Count < ThreadSearch.MaxUserTerms)
                {
                    Console.Write("言葉 " + (words.Count + 1).ToString(CultureInfo.InvariantCulture) + "> ");
                    string word = ConsoleMenu.ReadLine();
                    if (word == null) return 2;
                    word = word.Trim();
                    if (word.Length == 0) break;
                    words.Add(word);
                }
                Console.WriteLine();
            }

            // 2. sources
            List<SourceCandidate> chosen = new List<SourceCandidate>();
            List<string> userPaths = new List<string>();   // folders the user specified (capture mode for the traces)
            bool copyNeeded = true;
            if (sources.Count > 0)
            {
                foreach (string s in sources)
                {
                    List<string> notes = new List<string>();
                    chosen.AddRange(SourceLocator.FromUserPath(s, notes));
                    userPaths.Add(s);
                    foreach (string n in notes) Console.WriteLine("  " + n);
                }
                copyNeeded = !inPlace;
            }
            else
            {
                if (!interactive)
                {
                    List<string> notes = new List<string>();
                    chosen = SourceLocator.FindStandardLocations(notes);
                    foreach (string n in notes) Console.WriteLine("  " + n);
                }
                else
                {
                    if (!ChooseSourcesInteractively(chosen, userPaths, out copyNeeded)) return 2;
                }
            }
            if (chosen.Count == 0)
            {
                Console.WriteLine("解析できる IndexedDB の保存データが見つかりませんでした。通知データベースとキャッシュの痕跡走査だけを行います。");
                ctx.Notes.Add("IndexedDB の解析元なし（痕跡走査のみ）");
            }
            ctx.Sources.AddRange(chosen);
            ctx.InPlace = !copyNeeded;

            // 3. output folder
            if (outRoot == null)
            {
                string defaultRoot = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TeamsMessageHistory"), TimeText.NowStamp());
                if (interactive)
                {
                    Console.WriteLine("結果の保存先フォルダー（Enter で既定: " + defaultRoot + "）:");
                    Console.Write("> ");
                    string typed = ConsoleMenu.ReadLine();
                    if (typed == null) return 2;
                    typed = typed.Trim().Trim('"');
                    outRoot = typed.Length == 0 ? defaultRoot : typed;
                }
                else outRoot = defaultRoot;
            }
            ctx.OutputFolder = Path.GetFullPath(outRoot);
            Directory.CreateDirectory(ctx.OutputFolder);
            Console.WriteLine("保存先: " + ctx.OutputFolder);
            Console.WriteLine();

            // 4. preservation copy
            List<KeyValuePair<SourceCandidate, string>> toAnalyze = new List<KeyValuePair<SourceCandidate, string>>();
            long tStage = Timing.Start();
            tTotal = tStage;   // the time spent waiting for typed input is not part of the run
            if (copyNeeded)
            {
                string preservedRoot = Path.Combine(ctx.OutputFolder, "preserved");
                Directory.CreateDirectory(preservedRoot);
                int index = 1;
                foreach (SourceCandidate s in chosen)
                {
                    Console.WriteLine("保全コピー " + index.ToString(CultureInfo.InvariantCulture) + "/" + chosen.Count.ToString(CultureInfo.InvariantCulture) + ": " + s.LevelDbPath);
                    PreservedCopy copy = Preservation.Copy(s, preservedRoot, index, delegate(string line) { Console.WriteLine(line); });
                    ctx.Copies.Add(copy);
                    if (!copy.Complete) Console.WriteLine("  一部のファイルを複製できませんでした（manifest 参照）。複製できた分だけ解析します。");
                    toAnalyze.Add(new KeyValuePair<SourceCandidate, string>(s, copy.DestinationLevelDb));
                    index++;
                }
                Console.WriteLine();
            }
            else
            {
                foreach (SourceCandidate s in chosen) toAnalyze.Add(new KeyValuePair<SourceCandidate, string>(s, s.LevelDbPath));
            }

            Timing.Stop("保全コピー（複製と SHA-256）", tStage);

            // 5. analysis
            tStage = Timing.Start();
            int analyzeIndex = 0;
            foreach (KeyValuePair<SourceCandidate, string> pair in toAnalyze)
            {
                AnalyzedSource a = new AnalyzedSource();
                a.Label = pair.Key.Label;
                a.LevelDbPath = pair.Value;
                a.Candidate = pair.Key;
                a.Copy = copyNeeded && analyzeIndex < ctx.Copies.Count ? ctx.Copies[analyzeIndex] : null;
                analyzeIndex++;
                ctx.Analyzed.Add(a);
                Console.WriteLine("解析: " + pair.Value);
                try
                {
                    a.Reader = new IndexedDbReader(pair.Value);
                    MessageHistoryFinder finder = new MessageHistoryFinder(a.Reader, reference, delegate(string line) { Console.WriteLine("  " + line); });
                    a.Result = finder.Search();
                }
                catch (Exception ex)
                {
                    a.Error = ex.GetType().Name + ": " + ex.Message;
                    Console.WriteLine("  読み取りエラー: " + a.Error);
                }
                Console.WriteLine();
            }
            Timing.Stop("IndexedDB の解析（本文候補の照合）", tStage);
            tStage = Timing.Start();
            foreach (PreservedCopy copy in ctx.Copies)
            {
                List<string> changed = new List<string>();
                Preservation.VerifyUnchanged(copy, changed);
            }
            Timing.Stop("保全コピーの再ハッシュ（解析後の不変確認）", tStage);
            tStage = Timing.Start();
            foreach (AnalyzedSource a in ctx.Analyzed)
            {
                if (a.Reader == null || a.Candidate == null) continue;
                try
                {
                    a.Diagnostics = new DiagnosticsCollector(ctx, a, a.Candidate, a.Copy).Build();
                }
                catch (Exception ex)
                {
                    a.Diagnostics = new DiagnosticReport();
                    a.Diagnostics.Lines.Add("診断を作れませんでした: " + ex.GetType().Name);
                }
            }

            Timing.Stop("診断の集計", tStage);

            // words for the thread-wide search: the user's, plus short runs cut from the body candidates just found
            List<string> termNotes = new List<string>();
            List<SearchTerm> terms = noThread ? new List<SearchTerm>() : ThreadSearch.BuildTerms(words, ctx, termNotes);

            // 5b. traces outside IndexedDB (notification database, caches of the same profiles): read-only, in place,
            // never counted as the message's history; the output goes into the same result folder
            Timing.Declare("痕跡走査（通知・キャッシュ）の全体");
            tStage = Timing.Start();
            ctx.Traces = RunTraceStage(ctx, reference, chosen, userPaths, traceSources, noTraces, terms);
            Timing.Stop("痕跡走査（通知・キャッシュ）の全体", tStage);

            // 5c. thread-wide search: records that carry the id in another field, the same thread or conversation,
            // and text containing the words. Unverified by nature; kept apart from the body candidates.
            if (noThread)
            {
                ctx.Thread = new ThreadSearchResult();
                ctx.Notes.Add("-NoThread が指定されたため、スレッド全体の探索は行っていない。");
            }
            else
            {
                Console.WriteLine("スレッド全体の探索: 対象の ID を別の項目に持つ記録、同じスレッド・会話の記録、言葉を含む文面を集めます（未確認の参考情報）。");
                Timing.Declare("スレッド全体の探索");
                tStage = Timing.Start();
                ctx.Thread = ThreadSearch.Run(ctx, terms, termNotes, delegate(string line) { Console.WriteLine("  " + line); });
                Timing.Stop("スレッド全体の探索", tStage);
                Console.WriteLine();
            }
            ctx.FinishedUtc = TimeText.NowIso();
            Timing.Stop("合計（入力待ちを除く。結果の書出しの前まで）", tTotal);

            // 6. report
            long tReport = Timing.Start();
            string textPath = ReportWriter.Write(ctx);
            double reportSeconds = (Timing.Start() - tReport) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (diag)
            {
                PrintDiagnostics(ctx);
                Console.WriteLine(interactive ? "diag.txt に保存しました。この画面を撮影できます。Enter で終了。" : "診断を diag.txt に保存しました。");
            }
            else
            {
                PrintSummary(ctx);
                Console.WriteLine("結果ファイル: " + textPath);
            }
            if (!diag) Console.WriteLine(ReportWriter.TimingLine() + " / 結果の書出し " + reportSeconds.ToString("0.0", CultureInfo.InvariantCulture));
            if (interactive)
            {
                if (!diag)
                {
                    Console.WriteLine();
                    Console.WriteLine("Enter で終了します。");
                }
                ConsoleMenu.ReadLine();
            }
            // 0 = a result was produced (IndexedDB analysed, or at least one trace source scanned); 2 = nothing to read at all
            return ctx.Analyzed.Count > 0 || (ctx.Traces != null && ctx.Traces.SourcesScanned > 0) ? 0 : 2;
        }

        /// <summary>The traces stage of a main run. Standard mode (this PC's Teams found automatically): the notification
        /// database plus the caches of exactly the profiles whose IndexedDB was analysed. Capture mode (a folder was
        /// specified): only caches under that folder's own profile and -TraceSource folders; running profiles of this PC
        /// are never mixed in. Files are read in place (read-only); nothing is copied.</summary>
        private static TraceStage RunTraceStage(RunContext ctx, MessageReference reference, List<SourceCandidate> chosen, List<string> userPaths, List<string> customFolders, bool skip, List<SearchTerm> terms)
        {
            TraceStage ts = new TraceStage();
            if (skip)
            {
                ts.Mode = "skipped";
                ts.Notes.Add("-NoTraces が指定されたため、IndexedDB 以外の痕跡（通知データベース・キャッシュ）は走査していない。");
                return ts;
            }
            bool capture = userPaths.Count > 0;
            Console.WriteLine("痕跡走査（IndexedDB 以外）: 読み取り専用、複製なし。対象メッセージの履歴には数えない参考情報です。");
            HashSet<string> seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (capture)
            {
                ts.Mode = "capture";
                ts.Notes.Add("採取済みフォルダーを指定した解析のため、この PC の稼働中プロファイルと通知データベースは走査していない。走査したのは指定フォルダーの範囲（同じプロファイル配下のキャッシュ、配下にあるキャッシュや通知データベースのコピー）と -TraceSource のフォルダーだけ。");
                foreach (string p in userPaths)
                    foreach (TraceSource s in TraceScanner.CaptureSources(p)) if (seenFolders.Add(s.Folder)) ts.Sources.Add(s);
            }
            else
            {
                ts.Mode = "standard";
                ts.Discovery = new TraceScanner.Discovery();
                if (chosen.Count > 0)
                {
                    TraceSource wpn = TraceScanner.FindNotificationSource(ts.Discovery);
                    if (wpn != null) ts.Sources.Add(wpn);
                    ts.Notes.Add("この PC の通知データベースと、解析した保存データと同じプロファイルのキャッシュ（HTTP / Service Worker）を走査した。他のプロファイルは走査していない。");
                }
                else
                {
                    // no IndexedDB on this PC: the standard Teams profiles are the only place left to look
                    foreach (TraceSource s in TraceScanner.FindStandardSources(ts.Discovery)) if (seenFolders.Add(s.Folder)) ts.Sources.Add(s);
                    ts.Notes.Add("IndexedDB の保存データが無いため、この PC の通知データベースと標準の場所にある Teams プロファイルのキャッシュ（HTTP / Service Worker）を走査した。");
                }
            }
            foreach (SourceCandidate c in chosen)
            {
                string profile;
                List<TraceSource> profileSources = TraceScanner.ProfileCacheSources(c.LevelDbPath, out profile);
                if (profile != null && seenProfiles.Add(profile) && ts.Discovery != null) ts.Discovery.TeamsProfiles++;
                foreach (TraceSource s in profileSources) if (seenFolders.Add(s.Folder)) ts.Sources.Add(s);
            }
            foreach (string f in customFolders)
            {
                string folder = f.Trim().Trim('"');
                if (!Directory.Exists(folder))
                {
                    TraceSource missing = new TraceSource();
                    missing.Kind = "custom";
                    missing.Label = "指定フォルダー";
                    missing.Folder = folder;
                    missing.EnumerationFailed = true;
                    missing.EnumerationErrorKind = "見つからない";
                    ts.Sources.Add(missing);
                    continue;
                }
                if (seenFolders.Add(folder)) ts.Sources.Add(TraceScanner.FromFolder(folder, "custom", "指定フォルダー"));
            }
            if (ts.Discovery != null) foreach (TraceSource s in ts.Sources) if (s.EnumerationFailed) ts.Discovery.EnumerationFailures++;
            if (ts.Sources.Count == 0) ts.Notes.Add("走査できる保存元が無かった（" + (capture ? "指定フォルダーのプロファイル配下にキャッシュが無く、-TraceSource も無い" : "標準の場所に通知データベースもキャッシュも見つからない") + "）。");
            foreach (TraceSource s in ts.Sources)
            {
                Console.WriteLine("  " + s.Label + (s.EnumerationFailed ? " (列挙失敗)" : " (" + s.Files.Count.ToString(CultureInfo.InvariantCulture) + " ファイル, " + (s.Bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB)"));
            }
            foreach (TraceSource s in ts.Sources)
            {
                Console.WriteLine("走査: " + s.Label);
                try { ts.Results.Add(TraceScanner.Scan(s, reference, delegate(string line) { Console.WriteLine(line); }, terms)); }
                catch (Exception ex)
                {
                    TraceSourceResult failed = new TraceSourceResult();
                    failed.Source = s;
                    failed.Error = "走査中断: " + ex.GetType().Name;
                    ts.Results.Add(failed);
                }
            }
            long tWrite = Timing.Start();
            try { ts.Output = TraceScanner.WriteDetails(ctx.OutputFolder, reference, ts.Results, ts.Discovery, ts.Mode, ts.Notes, Version); }
            catch (Exception ex) { ts.Error = ex.GetType().Name + ": " + ex.Message; }
            Timing.Stop("  出典ファイルの書出し（traces フォルダー）", tWrite);
            Console.WriteLine();
            return ts;
        }

        /// <summary>Traces outside IndexedDB: notification database and WebView2 caches, scanned read-only for the ids.
        /// Fragments go to the result folder only; the final screen shows counts, limits and unscanned files, and it is
        /// shown (with the console cleared) even when nothing could be scanned, so no input ever stays on screen.</summary>
        private static int RunTraces(string link, List<string> customFolders, string outRoot)
        {
            bool interactive = link == null;
            Console.WriteLine("IndexedDB 以外の痕跡走査 (TeamsMessageHistory " + Version + ")");
            Console.WriteLine("Windows の通知データベースと Teams の WebView2 プロファイルのキャッシュを読み取り専用で走査し、メッセージ ID・親投稿 ID・会話 ID を探します。");
            Console.WriteLine("キャッシュは項目ごとにキー・応答ヘッダー・本文を分け、本文は Content-Encoding に従って展開（gzip / deflate / br）してから照合します。");
            Console.WriteLine("通信はしません。原本は変更しません。抽出した断片は結果フォルダーにだけ保存します。");
            Console.WriteLine();
            MessageReference reference;
            while (true)
            {
                string input = link;
                if (input == null)
                {
                    Console.WriteLine("Teams でメッセージの「…」→「リンクをコピー」で取ったリンク（またはメッセージIDの数字）を貼り付けて Enter:");
                    Console.Write("> ");
                    input = ConsoleMenu.ReadLine();
                    if (input == null) return 2;
                }
                reference = MessageReference.Parse(input);
                foreach (string n in reference.Notes) Console.WriteLine("  " + n);
                if (reference.IsValid) break;
                if (!interactive) return 2;
                link = null;
            }
            List<TraceSource> sources = new List<TraceSource>();
            TraceScanner.Discovery discovery = null;
            if (customFolders.Count > 0)
            {
                foreach (string f in customFolders)
                {
                    string folder = f.Trim().Trim('"');
                    if (!Directory.Exists(folder))
                    {
                        TraceSource missing = new TraceSource();
                        missing.Kind = "custom";
                        missing.Label = "指定フォルダー";
                        missing.Folder = folder;
                        missing.EnumerationFailed = true;
                        missing.EnumerationErrorKind = "見つからない";
                        sources.Add(missing);
                        continue;
                    }
                    sources.Add(TraceScanner.FromFolder(folder, "custom", "指定フォルダー"));
                }
            }
            else
            {
                discovery = new TraceScanner.Discovery();
                sources = TraceScanner.FindStandardSources(discovery);
            }
            Console.WriteLine("走査する保存元: " + sources.Count.ToString(CultureInfo.InvariantCulture) + " 件");
            foreach (TraceSource s in sources)
                Console.WriteLine("  " + s.Label + (s.EnumerationFailed ? " (列挙失敗)" : " (" + s.Files.Count.ToString(CultureInfo.InvariantCulture) + " ファイル, " + (s.Bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB)"));
            if (outRoot == null)
            {
                outRoot = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TeamsMessageHistory"), TimeText.NowStamp() + "-traces");
            }
            string outFolder = Path.GetFullPath(outRoot);
            Directory.CreateDirectory(outFolder);
            List<TraceSourceResult> results = new List<TraceSourceResult>();
            foreach (TraceSource s in sources)
            {
                Console.WriteLine("走査: " + s.Label);
                results.Add(TraceScanner.Scan(s, reference, delegate(string line) { Console.WriteLine(line); }));
            }
            // private details (fragments may contain message text) and the counts-only screen
            TraceScanner.TraceOutput output = TraceScanner.WriteDetails(outFolder, reference, results, discovery, customFolders.Count > 0 ? "custom" : "standard", null, Version);
            try { Console.Title = "Teams Message History - Traces"; } catch (Exception) { }
            try { Console.Clear(); } catch (Exception) { }
            foreach (string line in output.Screen) Console.WriteLine(line);
            Console.WriteLine(interactive ? "traces.txt に保存しました（断片は traces フォルダー）。この画面を撮影できます。Enter で終了。" : "traces.txt に保存しました（断片は traces フォルダー）。");
            if (interactive) ConsoleMenu.ReadLine();
            return 0;
        }

        /// <summary>Review of an existing result folder: reads result.json only, writes review.txt/json into a new
        /// sibling folder, shows one screen of fixed labels and counts. Nothing in the result folder is modified.</summary>
        private static int RunReview(string folder)
        {
            bool interactive = folder == null;
            Console.WriteLine("既存の結果フォルダーの追加診断 (TeamsMessageHistory " + Version + ")");
            Console.WriteLine("result.json だけを読み、コピー失敗と読めなかった包まれた値の内訳を、本文・名前・ID・パスを含まない 1 画面で示します。");
            Console.WriteLine();
            if (folder == null)
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TeamsMessageHistory");
                List<string> candidates = new List<string>();
                if (Directory.Exists(root))
                {
                    string[] dirs = Directory.GetDirectories(root);
                    Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                    for (int i = dirs.Length - 1; i >= 0 && candidates.Count < 8; i--)
                    {
                        if (File.Exists(Path.Combine(dirs[i], "result.json"))) candidates.Add(dirs[i]);
                    }
                }
                int picked = candidates.Count;   // the last entry: type a path
                if (candidates.Count > 0)
                {
                    List<string> options = new List<string>();
                    foreach (string c in candidates) options.Add(Path.GetFileName(c));
                    options.Add("別の結果フォルダーのパスを入力する");
                    picked = ConsoleMenu.Choose("ドキュメント\\TeamsMessageHistory にある結果フォルダー（新しい順）から選んでください:", options, 0);
                    if (picked < 0) return 2;
                }
                if (picked < candidates.Count) folder = candidates[picked];
                else
                {
                    Console.WriteLine("結果フォルダーのパスを入力:");
                    Console.Write("> ");
                    string typed = ConsoleMenu.ReadLine();
                    if (typed == null) return 2;
                    folder = typed.Trim().Trim('"');
                }
            }
            if (string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder, "result.json")))
            {
                Console.WriteLine("result.json を含む結果フォルダーが見つかりません。");
                if (interactive) { Console.WriteLine("Enter で終了します。"); ConsoleMenu.ReadLine(); }
                return 2;
            }
            ResultReview result;
            try
            {
                result = ResultReview.Build(Path.GetFullPath(folder));
                ResultReview.Save(result, Path.GetFullPath(folder));
            }
            catch (Exception ex)
            {
                Console.WriteLine("追加診断を作れませんでした: " + ex.GetType().Name);
                if (interactive) { Console.WriteLine("Enter で終了します。"); ConsoleMenu.ReadLine(); }
                return 3;
            }
            try { Console.Title = "Teams Message History - Review"; } catch (Exception) { }
            try { Console.Clear(); } catch (Exception) { }
            foreach (string line in result.Lines) Console.WriteLine(line);
            Console.WriteLine(interactive ? "review.txt に保存しました（結果フォルダーの隣の *-review）。この画面を撮影できます。Enter で終了。" : "review.txt に保存しました（結果フォルダーの隣の *-review）。");
            if (interactive) ConsoleMenu.ReadLine();
            return 0;
        }

        /// <summary>One screen only: the analyzed source that holds the message (or the first one), plus a one-line
        /// tally of the other sources so they are not mistaken for covered or omitted. All screens are in diag.txt.</summary>
        private static void PrintDiagnostics(RunContext ctx)
        {
            try { Console.Title = "Teams Message History - Diagnostics"; } catch (Exception) { }
            try { Console.Clear(); } catch (Exception) { }
            AnalyzedSource primary = null;
            int found = 0, notFound = 0, unreadable = 0, errors = 0;
            foreach (AnalyzedSource a in ctx.Analyzed)
            {
                string status = a.Error != null ? "error" : (a.Result != null ? a.Result.Status : "not_found");
                if (status == "found") found++;
                else if (status == "unreadable") unreadable++;
                else if (status == "error") errors++;
                else notFound++;
                if (a.Diagnostics == null) continue;
                if (primary == null) primary = a;
                else if (status == "found" && (primary.Result == null || primary.Result.Status != "found")) primary = a;
                else if (status == "found" && primary.Result != null && primary.Result.Status == "found" && a.Result.Bodies.Count > primary.Result.Bodies.Count) primary = a;
            }
            if (primary == null)
            {
                Console.WriteLine("診断を作れる解析元がありませんでした。");
                foreach (string line in ThreadSearch.ScreenLines(ctx.Thread)) Console.WriteLine(line);
                return;
            }
            foreach (string line in primary.Diagnostics.Lines) Console.WriteLine(line);
            foreach (string line in ThreadSearch.ScreenLines(ctx.Thread)) Console.WriteLine(line);
            if (ctx.Analyzed.Count > 1)
            {
                Console.WriteLine("[他の保存元] 本文あり " + (found - (primary.Result != null && primary.Result.Status == "found" ? 1 : 0)).ToString(CultureInfo.InvariantCulture)
                    + " / 見つからず " + (notFound - (primary.Result != null && primary.Result.Status != "found" && primary.Result.Status != "unreadable" && primary.Error == null ? 1 : 0)).ToString(CultureInfo.InvariantCulture)
                    + " / 読めず " + (unreadable - (primary.Result != null && primary.Result.Status == "unreadable" ? 1 : 0)).ToString(CultureInfo.InvariantCulture)
                    + " / エラー " + (errors - (primary.Error != null ? 1 : 0)).ToString(CultureInfo.InvariantCulture) + "（各診断は diag.txt）");
            }
        }

        private static bool ChooseSourcesInteractively(List<SourceCandidate> chosen, List<string> userPaths, out bool copyNeeded)
        {
            copyNeeded = true;
            int source = ConsoleMenu.Choose("解析元を選んでください:", new string[]
            {
                "この PC の標準の Teams 保存データを探し、保全コピーを取って解析する（原本は読むだけ）",
                "採取済みのフォルダーを指定する"
            }, 0);
            if (source < 0) return false;
            if (source == 1)
            {
                Console.WriteLine("採取済みフォルダー（*.indexeddb.leveldb そのもの、またはそれを含むフォルダー）のパス:");
                Console.Write("> ");
                string path = ConsoleMenu.ReadLine();
                if (path == null) return false;
                List<string> notes = new List<string>();
                chosen.AddRange(SourceLocator.FromUserPath(path, notes));
                userPaths.Add(path);
                foreach (string n in notes) Console.WriteLine("  " + n);
                if (chosen.Count == 0) return true;
                int how = ConsoleMenu.Choose("採取済みフォルダーの読み方を選んでください:", new string[]
                {
                    "保全コピーを取ってから解析する",
                    "複製せずに、そのまま読み取り専用で解析する"
                }, 0);
                if (how < 0) return false;
                copyNeeded = how == 0;
                return true;
            }
            List<string> locatorNotes = new List<string>();
            List<SourceCandidate> found = SourceLocator.FindStandardLocations(locatorNotes);
            foreach (string n in locatorNotes) Console.WriteLine("  " + n);
            if (found.Count == 0)
            {
                Console.WriteLine("標準の保存先に Teams の IndexedDB が見つかりませんでした。");
                return true;
            }
            Console.WriteLine("見つかった保存データ:");
            List<string> labels = new List<string>();
            for (int i = 0; i < found.Count; i++)
            {
                SourceCandidate c = found[i];
                string label = c.Label + "  (" + (c.Bytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB, 最終更新 "
                    + (c.LastWriteUtc.HasValue ? c.LastWriteUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "?") + ")";
                labels.Add((i + 1).ToString(CultureInfo.InvariantCulture) + "  " + label);
                Console.WriteLine("  " + (i + 1).ToString(CultureInfo.InvariantCulture) + "  " + label);
                Console.WriteLine("     " + c.LevelDbPath);
            }
            if (found.Count == 1)
            {
                chosen.AddRange(found);
                return true;
            }
            bool[] marks = ConsoleMenu.ChooseMany("解析する保存データを選んでください:", labels, null);
            if (marks == null) return false;
            for (int i = 0; i < found.Count; i++) if (marks[i]) chosen.Add(found[i]);
            return true;
        }

        private static void PrintSummary(RunContext ctx)
        {
            Console.WriteLine("================================================================");
            Console.WriteLine("判定（IndexedDB の保存データ）: " + ReportWriter.StatusText(ReportWriter.OverallStatus(ctx)));
            foreach (ReportWriter.BodyRef b in ReportWriter.BodyRefs(ctx))
            {
                BodyGroup g = b.Group;
                Console.WriteLine("  " + (b.Number > 0 ? "本文候補 " + b.Number.ToString(CultureInfo.InvariantCulture) + " (" + b.TextFile + ")" : "本文の無い候補") + " 出現 " + g.Occurrences.Count.ToString(CultureInfo.InvariantCulture) + " 回, 保存順序 "
                    + ReportWriter.SequenceRange(g.Occurrences)
                    + (g.Variants.Count > 1 ? ", HTML 表現 " + g.Variants.Count.ToString(CultureInfo.InvariantCulture) + " 種" : "") + ": "
                    + (!g.HasContent ? "(本文なし)" : ReportWriter.OneLine(g.ContentText, 160)));
                string edit = ReportWriter.EditTimeOf(g);
                if (edit != null) Console.WriteLine("      レコード内の編集日時 properties.edittime: " + edit);
            }
            foreach (AnalyzedSource a in ctx.Analyzed)
            {
                if (a.Result == null) continue;
                if (a.Result.OtherConversation.Count > 0) Console.WriteLine("  別会話の同一ID候補: " + a.Result.OtherConversation.Count.ToString(CultureInfo.InvariantCulture) + " 件（除外）");
                if (a.Result.Unreadable.Count > 0) Console.WriteLine("  読めなかった候補: " + a.Result.Unreadable.Count.ToString(CultureInfo.InvariantCulture) + " 件");
            }
            Console.WriteLine("注: 保存順序は端末に書かれた順で、編集日時ではありません。");
            Console.WriteLine("痕跡（通知・キャッシュ）: " + ReportWriter.TraceSummary(ctx.Traces));
            List<ReportWriter.TraceVersion> versions = ReportWriter.TraceVersions(ctx);
            if (versions.Count > 0)
            {
                int newOnes = 0;
                foreach (ReportWriter.TraceVersion v in versions) if (v.SameAsBody == 0) newOnes++;
                Console.WriteLine("  痕跡の本文（未確認）: " + versions.Count.ToString(CultureInfo.InvariantCulture) + " 種（IndexedDB の本文候補に無いもの " + newOnes.ToString(CultureInfo.InvariantCulture) + " 種）。全文は result.txt の [痕跡の本文] にあります。");
            }
            Console.WriteLine("  一致ごとの出典は result.txt の後半と traces\\ にあります。本文候補とは別物です。");
            Console.WriteLine("スレッド全体の探索: " + ThreadSearch.Summary(ctx.Thread));
            if (ctx.Thread != null && ctx.Thread.Ran) Console.WriteLine("  文面は result.txt の [スレッド全体の探索] と thread\\texts.txt にあります。本文候補とは別物です。");
            Console.WriteLine("================================================================");
        }

        private static void Banner()
        {
            Console.WriteLine("Teams メッセージ本文履歴 (TeamsMessageHistory " + Version + ")");
            Console.WriteLine("端末に残る Teams の保存データ（IndexedDB/LevelDB）を読み取り専用で複製し、指定したメッセージの本文の残存履歴を出力します。");
            Console.WriteLine("通信はしません。Teams の原本は変更しません。");
            Console.WriteLine();
        }

        private static void PrintUsage()
        {
            Console.WriteLine("使い方:");
            Console.WriteLine("  TeamsMessageHistory.bat                      対話式（選択は矢印キーと Enter）");
            Console.WriteLine("  TeamsMessageHistory.bat -Link <リンクまたはID> [-Source <フォルダー>]... [-Out <保存先>] [-InPlace]");
            Console.WriteLine("    -Source   採取済みフォルダー（省略時はこの PC の標準の Teams 保存先を探す）");
            Console.WriteLine("    -InPlace  -Source のフォルダーを複製せずに読取専用で解析する");
            Console.WriteLine("    -Diag     最後に本文・名前・ID・パスを含まない 1 画面の診断を表示し diag.txt に保存する");
            Console.WriteLine("    -TraceSource <フォルダー>  痕跡走査に加える採取済みフォルダー（通知DB・キャッシュのコピー）");
            Console.WriteLine("    -NoTraces 解析の後の痕跡走査（通知データベース・キャッシュ）を行わない");
            Console.WriteLine("    -Word <言葉>  覚えている言葉。その言葉を含む文面を残っているデータ全体から探す（複数指定可）");
            Console.WriteLine("    -NoThread スレッド全体の探索（別の項目の ID、同じスレッド・会話、言葉）を行わない");
            Console.WriteLine("  TeamsMessageHistory.bat -Review [<結果フォルダー>]   既存の result.json から追加診断を 1 画面で表示（再取得なし）");
            Console.WriteLine("  TeamsMessageHistory.bat -Traces [-Link <リンク>] [-TraceSource <フォルダー>]... [-Out <保存先>]");
            Console.WriteLine("            通知データベースと WebView2 キャッシュを ID で走査（IndexedDB 以外の痕跡）");
        }
    }
}
