// Teams Message History - thread-wide search (everything related to the message and its thread, whatever the field).
// The history search (MessageHistoryFinder) only accepts objects whose id / messageMap key / clientMessageId is the
// requested message. This pass looks at what that leaves out, and never promotes any of it to a body candidate:
//   * records that carry the message id in some OTHER field (activity items, quotes, links, map keys)   -> "id-field"
//   * records of the same thread (parent post id) or conversation that do not carry the message id       -> "parent" / "conversation"
//   * any text that contains a word the user remembers, wherever it is (no id needed)                    -> "word"
//   * any text that contains a run of characters cut from a body candidate (looked for automatically)    -> "word" with an auto term
// IndexedDB is read record by record (every stored version, the undo log, unreferenced blob files); the traces
// (notification database, caches) contribute the hits the trace scan already made plus its word hits.
// Everything found here is UNVERIFIED: text found near an id or containing a word, not proven to be a version of the message.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace TeamsMessageHistory
{
    public sealed class SearchTerm
    {
        public int Number;       // 1-based within its kind
        public string Text;
        public bool Auto;        // cut from a body candidate by the tool, not typed by the user
        public int BodyNumber;   // auto: the body candidate it was cut from
    }

    public sealed class ThreadOccurrence
    {
        public string Category;  // id-field / parent / conversation / word
        public string Origin;    // indexeddb / wpn / httpcache / swcache / custom
        public string Where;     // private: where the record is
        public string Field;     // private: the field (or element) the text was read from
        public string IdField;   // private: the field that carried the id
        public SearchTerm Term;
    }

    /// <summary>One distinct text (whitespace-normalised) with everything that led to it.</summary>
    public sealed class ThreadText
    {
        public int Number;
        public string Text;
        public bool Cut;              // the stored text was cut at the limit
        public int SameAsBody;        // body candidate with the same text, or 0
        public int PartOfBody;        // body candidate that contains this text (an excerpt of it), or 0
        public int SameAsTraceBody;   // trace body (痕跡本文) with the same text, or 0
        public int IdField, Parent, Conversation, Word;   // occurrences per category
        public int FromIndexedDb, FromTraces;
        public int OccurrenceCount;
        public int MatchIndex = -1;   // character index of the first word match, when known
        public readonly List<SearchTerm> Terms = new List<SearchTerm>();   // terms this text contains
        public readonly List<ThreadOccurrence> Occurrences = new List<ThreadOccurrence>();   // the first few

        public bool NotInBodies { get { return SameAsBody == 0 && PartOfBody == 0; } }

        public bool HasUserTerm { get { foreach (SearchTerm t in Terms) if (!t.Auto) return true; return false; } }

        public bool HasAutoTerm { get { foreach (SearchTerm t in Terms) if (t.Auto) return true; return false; } }

        /// <summary>The section it is listed under: 0 user word, 1 id in another field, 2 auto, 3 thread, 4 conversation,
        /// 5 a body candidate itself (or an excerpt of one) that only the automatic runs matched.</summary>
        public int Section
        {
            get
            {
                if (HasUserTerm) return 0;
                if (IdField > 0) return 1;
                if (HasAutoTerm && NotInBodies) return 2;
                if (Parent > 0) return 3;
                if (Conversation > 0) return 4;
                return 5;
            }
        }
    }

    public sealed class ThreadSearchResult
    {
        public bool Ran;
        public string Error;
        public readonly List<string> Notes = new List<string>();
        public readonly List<SearchTerm> Terms = new List<SearchTerm>();
        public long ItemsScanned, Decoded, DecodeFailures, Unreadable, OrphanBlobs;
        public long IdFieldRecords, ParentRecords, ConversationRecords;   // IndexedDB objects, one per stored version
        public long IdFieldTraces, ParentTraces, ConversationTraces;      // trace hits
        public long WordHitsIndexedDb, WordHitsTraces, AutoHitsIndexedDb, AutoHitsTraces;
        public long TextsDropped, WordHitCap;
        public long TimeFieldMatches;   // a time / version field holding the same number as the id: not taken as a reference to the message
        public bool TracesIncluded;
        // private: which store / field carried the id, per category (records counted, with or without display text)
        public readonly Dictionary<string, long> FieldTally = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly List<ThreadText> Texts = new List<ThreadText>();
        public string DetailFile, TextsFile;

        public int UserTerms { get { int n = 0; foreach (SearchTerm t in Terms) if (!t.Auto) n++; return n; } }

        public int AutoTerms { get { int n = 0; foreach (SearchTerm t in Terms) if (t.Auto) n++; return n; } }

        public int CountTexts(int section, bool onlyNotInBodies)
        {
            int n = 0;
            foreach (ThreadText t in Texts)
            {
                bool inSection = section == 0 ? t.HasUserTerm : section == 1 ? t.IdField > 0 : section == 2 ? (t.HasAutoTerm && t.NotInBodies) : section == 3 ? t.Parent > 0 : t.Conversation > 0;
                if (inSection && (!onlyNotInBodies || t.NotInBodies)) n++;
            }
            return n;
        }
    }

    /// <summary>What counts as display text, and how ids are recognised inside a value. Shared with the trace scan.</summary>
    public static class ThreadTextRules
    {
        public const int MaxTextChars = 20000;
        private const int MaxTextsPerObject = 60;

        private static readonly string[] TextyNames = new string[]
        {
            "content", "preview", "text", "body", "subject", "title", "summary", "snippet", "description", "message", "topic", "caption", "quote"
        };

        public static OrderedMap AsMap(object node)
        {
            JsObject js = node as JsObject;
            if (js != null) return js.Properties;
            return node as OrderedMap;
        }

        /// <summary>Strings and numbers as text (ids are stored either way); everything else is null.</summary>
        public static string Scalar(object value)
        {
            string s = value as string;
            if (s != null) return s;
            if (value is double)
            {
                double d = (double)value;
                if (d == Math.Floor(d) && Math.Abs(d) < 9007199254740992.0) return ((long)d).ToString(CultureInfo.InvariantCulture);
                return null;
            }
            if (value is long || value is int || value is ulong) return Convert.ToString(value, CultureInfo.InvariantCulture);
            return null;
        }

        /// <summary>The id occurs as a whole token: not as part of a longer number or word.</summary>
        public static bool ContainsToken(string text, string id)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(id) || text.Length < id.Length) return false;
            int from = 0;
            while (true)
            {
                int idx = text.IndexOf(id, from, StringComparison.Ordinal);
                if (idx < 0) return false;
                bool left = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
                int end = idx + id.Length;
                bool right = end >= text.Length || !char.IsLetterOrDigit(text[end]);
                if (left && right) return true;
                from = idx + 1;
            }
        }

        public static bool ContainsConversation(string text, string conversationId)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(conversationId) || text.Length < conversationId.Length) return false;
            if (text.IndexOf(conversationId, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf('%') < 0) return false;
            string encoded = conversationId.Replace(":", "%3A").Replace("@", "%40");
            return encoded != conversationId && text.IndexOf(encoded, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool LooksJson(string s)
        {
            if (s == null || s.Length < 2 || s.Length > 2000000) return false;
            char a = s[0], b = s[s.Length - 1];
            return (a == '{' && b == '}') || (a == '[' && b == ']');
        }

        /// <summary>A value that reads as display text. Person names, ids, urls, timestamps and tokens are left out by
        /// their shape (one ASCII run without a space) or, for names, by the field name.</summary>
        public static bool IsTextLike(string name, string value)
        {
            if (value == null) return false;
            string v = value.Trim();
            if (v.Length < 2) return false;
            string lname = (name ?? "").ToLowerInvariant();
            if (lname.IndexOf("name", StringComparison.Ordinal) >= 0) return false;
            if (LooksJson(v)) return false;
            bool hasSpace = false, hasWide = false;
            foreach (char c in v)
            {
                if (c > 0x7E) hasWide = true;
                else if (c == ' ' || c == '\n' || c == '\t' || c == '\r') hasSpace = true;
            }
            if (!hasSpace && (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || v.StartsWith("msteams:", StringComparison.OrdinalIgnoreCase))) return false;
            if (hasWide || hasSpace) return true;
            // one ASCII run: text only when the field is named like one and the value is not shaped like an id
            if (v.IndexOf("://", StringComparison.Ordinal) >= 0 || v.IndexOf('@') >= 0 || v.Length > 40) return false;
            int digits = 0;
            foreach (char c in v) if (c >= '0' && c <= '9') digits++;
            if (digits * 2 > v.Length) return false;
            if (v.IndexOf('/') >= 0) return false;
            foreach (string t in TextyNames) if (lname.EndsWith(t, StringComparison.Ordinal)) return true;
            return false;
        }

        public static string Plain(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.IndexOf('<') >= 0 && value.IndexOf('>') >= 0) return HtmlText.ToPlainText(value);
            if (value.IndexOf('&') >= 0) return HtmlText.DecodeEntities(value).Trim();
            return value.Trim();
        }

        public static string StripWhiteSpace(string text)
        {
            bool any = false;
            foreach (char c in text) if (char.IsWhiteSpace(c)) { any = true; break; }
            if (!any) return text;
            StringBuilder sb = new StringBuilder(text.Length);
            foreach (char c in text) if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>Index of the term in the text (ignoring case); -2 when it only matches with the white space taken
        /// out (a word broken across lines); -1 when it is not there.</summary>
        public static int IndexOfTerm(string plain, string term)
        {
            if (plain == null || term == null || plain.Length < term.Length) return -1;
            int idx = plain.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return idx;
            bool space = false;
            foreach (char c in plain) if (char.IsWhiteSpace(c)) { space = true; break; }
            if (!space) return -1;
            StringBuilder sb = new StringBuilder(plain.Length);
            foreach (char c in plain) if (!char.IsWhiteSpace(c)) sb.Append(c);
            StringBuilder tb = new StringBuilder(term.Length);
            foreach (char c in term) if (!char.IsWhiteSpace(c)) tb.Append(c);
            if (tb.Length == 0) return -1;
            return sb.ToString().IndexOf(tb.ToString(), StringComparison.OrdinalIgnoreCase) >= 0 ? -2 : -1;
        }

        /// <summary>Every display text inside a parsed JSON record (field path, plain text), bounded in depth and number.</summary>
        public static List<KeyValuePair<string, string>> Collect(OrderedMap map)
        {
            List<KeyValuePair<string, string>> texts = new List<KeyValuePair<string, string>>();
            Collect(map, "", texts, 0);
            return texts;
        }

        private static void Collect(OrderedMap map, string path, List<KeyValuePair<string, string>> texts, int depth)
        {
            if (map == null || depth > 5) return;
            foreach (KeyValuePair<string, object> p in map)
            {
                if (texts.Count >= MaxTextsPerObject) return;
                CollectValue(p.Key, p.Value, path + (path.Length > 0 ? "." : "") + p.Key, texts, depth);
            }
        }

        private static void CollectValue(string name, object value, string path, List<KeyValuePair<string, string>> texts, int depth)
        {
            string s = value as string;
            if (s != null)
            {
                if (LooksJson(s.Trim()))
                {
                    object parsed = null;
                    try { parsed = JsonReader.Parse(s); } catch (Exception) { parsed = null; }
                    if (parsed != null) { CollectValue(name, parsed, path, texts, depth + 1); return; }
                }
                if (!IsTextLike(name, s)) return;
                string plain = Plain(s);
                if (plain.Length == 0) return;
                texts.Add(new KeyValuePair<string, string>(path, plain.Length > MaxTextChars ? plain.Substring(0, MaxTextChars) : plain));
                return;
            }
            OrderedMap child = AsMap(value);
            if (child != null) { Collect(child, path, texts, depth + 1); return; }
            List<object> list = value as List<object>;
            if (list == null || depth > 5) return;
            for (int i = 0; i < list.Count && texts.Count < MaxTextsPerObject; i++)
                CollectValue(name, list[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", texts, depth + 1);
        }
    }

    public sealed class ThreadSearch
    {
        public const int MaxUserTerms = 8;
        public const int MinTermChars = 2;
        public const int AutoTermChars = 8;
        public const int MaxAutoTerms = 6;
        private const int MaxOccurrencesKept = 8;
        private const int MaxTextsShown = 30;      // per section in result.txt (the rest is in thread\texts.txt)
        private const int MaxCharsShown = 3000;

        private sealed class RefComparer : IEqualityComparer<object>
        {
            public static readonly RefComparer Instance = new RefComparer();
            bool IEqualityComparer<object>.Equals(object a, object b) { return ReferenceEquals(a, b); }
            int IEqualityComparer<object>.GetHashCode(object o) { return RuntimeHelpers.GetHashCode(o); }
        }

        private sealed class Node
        {
            public OrderedMap Map;
            public string Path;
            public bool Confirmed;   // a body candidate of the history search (or inside one): never reported here
            public bool Id, Parent, Conv, Listed;
            public string IdField, ParentField, ConvField;
            public bool Flagged { get { return Id || Parent || Conv; } }
        }

        private readonly RunContext _ctx;
        private readonly MessageReference _reference;
        private readonly ThreadSearchResult _r = new ThreadSearchResult();
        private readonly Dictionary<string, ThreadText> _groups = new Dictionary<string, ThreadText>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _perCategory = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> _bodyKeys = new List<string>();   // index = body number - 1
        private readonly List<string> _traceBodyKeys = new List<string>();   // index = trace body number - 1
        private List<string> _clientIds = new List<string>();

        // state of the record being walked
        private HashSet<string> _confirmed;
        private string _itemKey;
        private string _where;
        private string _store;
        private HashSet<object> _visited;
        private Dictionary<object, Node> _nodes;
        private List<Node> _flagged;
        private Dictionary<string, object> _json;

        private ThreadSearch(RunContext ctx, List<SearchTerm> terms)
        {
            _ctx = ctx;
            _reference = ctx.Reference;
            if (terms != null) _r.Terms.AddRange(terms);
            foreach (ReportWriter.BodyRef b in ReportWriter.BodyRefs(ctx))
            {
                if (b.Number <= 0) continue;
                while (_bodyKeys.Count < b.Number) _bodyKeys.Add(null);
                _bodyKeys[b.Number - 1] = MessageHistoryFinder.NormalizeText(b.Group.ContentText);
            }
        }

        // ---------------------------------------------------------------- terms

        /// <summary>The words the user typed (trimmed, de-duplicated) followed by short runs cut from each body candidate.
        /// The automatic runs let texts that share wording with a known body surface without any id.</summary>
        public static List<SearchTerm> BuildTerms(List<string> words, RunContext ctx, List<string> notes)
        {
            List<SearchTerm> terms = new List<SearchTerm>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (words != null)
            {
                foreach (string w in words)
                {
                    string t = (w ?? "").Trim().Trim('"').Trim();
                    if (t.Length == 0) continue;
                    if (t.Length < MinTermChars) { notes.Add("1 文字の言葉は一致が多すぎるため探していない（" + MinTermChars.ToString(CultureInfo.InvariantCulture) + " 文字以上で指定）。"); continue; }
                    if (!seen.Add(t)) continue;
                    if (terms.Count >= MaxUserTerms) { notes.Add("言葉は " + MaxUserTerms.ToString(CultureInfo.InvariantCulture) + " 個までで、それ以降は探していない。"); break; }
                    SearchTerm term = new SearchTerm();
                    term.Number = terms.Count + 1;
                    term.Text = t;
                    terms.Add(term);
                }
            }
            int auto = 0;
            foreach (ReportWriter.BodyRef b in ReportWriter.BodyRefs(ctx))
            {
                if (b.Number <= 0 || auto >= MaxAutoTerms) continue;
                string text = MessageHistoryFinder.NormalizeText(b.Group.ContentText);
                if (text.Length < 4) continue;
                List<int> starts = new List<int>();
                if (text.Length <= AutoTermChars) starts.Add(0);
                else
                {
                    int last = text.Length - AutoTermChars;
                    starts.Add(0);
                    if (last / 2 > 0 && last / 2 < last) starts.Add(last / 2);
                    starts.Add(last);
                }
                foreach (int start in starts)
                {
                    if (auto >= MaxAutoTerms) break;
                    string piece = text.Substring(start, Math.Min(AutoTermChars, text.Length - start)).Trim();
                    if (piece.Length < 4 || !seen.Add(piece)) continue;
                    SearchTerm term = new SearchTerm();
                    term.Auto = true;
                    term.Number = ++auto;
                    term.BodyNumber = b.Number;
                    term.Text = piece;
                    terms.Add(term);
                }
            }
            return terms;
        }

        // ---------------------------------------------------------------- run

        public static ThreadSearchResult Run(RunContext ctx, List<SearchTerm> terms, List<string> termNotes, Action<string> progress)
        {
            ThreadSearch search = new ThreadSearch(ctx, terms);
            ThreadSearchResult r = search._r;
            r.Ran = true;
            if (termNotes != null) r.Notes.AddRange(termNotes);
            try
            {
                int index = 0;
                foreach (AnalyzedSource a in ctx.Analyzed)
                {
                    index++;
                    if (a.Reader == null || a.Error != null) continue;
                    if (progress != null) progress("IndexedDB の保存データ " + index.ToString(CultureInfo.InvariantCulture) + " を記録ごとに読み直しています");
                    long tIdb = Timing.Start();
                    try { search.ScanIndexedDb(a, index); }
                    catch (Exception ex) { r.Notes.Add("解析元 " + index.ToString(CultureInfo.InvariantCulture) + " の探索を途中で止めた（" + ex.GetType().Name + "）。そこまでの結果だけを含む。"); }
                    Timing.Stop("  IndexedDB の読み直し（全ての版の復号と照合）", tIdb);
                }
                long tRest = Timing.Start();
                search.AddTraces(ctx.Traces);
                search.Finish();
                search.WriteDetails();
                Timing.Stop("  痕跡の一致の取込みと thread フォルダーの書出し", tRest);
            }
            catch (Exception ex)
            {
                r.Error = ex.GetType().Name + ": " + ex.Message;
            }
            return r;
        }

        // ---------------------------------------------------------------- IndexedDB

        private MultiNeedle BuildPrefilter()
        {
            MultiNeedle needles = new MultiNeedle();
            List<string> ids = new List<string>();
            ids.Add(_reference.MessageId);
            if (!string.IsNullOrEmpty(_reference.ParentMessageId)) ids.Add(_reference.ParentMessageId);
            ids.AddRange(_clientIds);
            foreach (string id in ids)
            {
                if (string.IsNullOrEmpty(id)) continue;
                needles.Add(Encoding.UTF8.GetBytes(id));
                needles.Add(Encoding.Unicode.GetBytes(id));
                // an id kept as a JavaScript number is serialized as an IEEE double
                long n;
                if (long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > int.MaxValue && n < 9007199254740992L) needles.Add(BitConverter.GetBytes((double)n));
            }
            if (!string.IsNullOrEmpty(_reference.ConversationId))
            {
                string conv = _reference.ConversationId;
                string encoded = conv.Replace(":", "%3A").Replace("@", "%40");
                foreach (string c in new string[] { conv, encoded, encoded.Replace("%3A", "%3a") })
                {
                    needles.Add(Encoding.UTF8.GetBytes(c));
                    needles.Add(Encoding.Unicode.GetBytes(c));
                }
            }
            foreach (SearchTerm t in _r.Terms)
            {
                needles.Add(Encoding.Unicode.GetBytes(t.Text));
                bool latin = true;
                foreach (char c in t.Text) if (c > 0xFF) { latin = false; break; }
                if (latin) needles.Add(Encoding.GetEncoding(28591).GetBytes(t.Text));   // one-byte strings
                else needles.Add(Encoding.UTF8.GetBytes(t.Text));
            }
            return needles;
        }

        private bool KeyRelated(IdbKey key)
        {
            if (key == null) return false;
            foreach (string part in key.StringParts())
            {
                if (part == null) continue;
                if (ThreadTextRules.ContainsToken(part, _reference.MessageId)) return true;
                if (!string.IsNullOrEmpty(_reference.ParentMessageId) && ThreadTextRules.ContainsToken(part, _reference.ParentMessageId)) return true;
                if (ThreadTextRules.ContainsConversation(part, _reference.ConversationId)) return true;
            }
            return false;
        }

        private void ScanIndexedDb(AnalyzedSource a, int sourceIndex)
        {
            IndexedDbReader reader = a.Reader;
            _confirmed = a.Result != null ? a.Result.ConfirmedPaths : new HashSet<string>(StringComparer.Ordinal);
            _clientIds = a.Result != null ? a.Result.ClientMessageIds : new List<string>();
            MultiNeedle prefilter = BuildPrefilter();
            string sourceLabel = "解析元 " + sourceIndex.ToString(CultureInfo.InvariantCulture);
            foreach (IdbDataItem item in reader.ReadDataItems())
            {
                _r.ItemsScanned++;
                if (item.IsDeletion || item.RawValue == null) continue;
                UnwrapResult unwrap = reader.Unwrap(item);
                if (unwrap.Error != null) { _r.Unreadable++; continue; }
                if (!KeyRelated(item.Key) && !prefilter.Any(unwrap.Ssv, unwrap.Ssv.Length)) continue;
                object value;
                try
                {
                    ulong version;
                    value = V8Deserializer.Deserialize(unwrap.Ssv, unwrap.SsvOffset, out version);
                }
                catch (Exception) { _r.DecodeFailures++; continue; }
                _r.Decoded++;
                _itemKey = MessageHistoryFinder.ItemKey(item);
                _where = sourceLabel + " " + (item.IsUndoLog ? "[undoログ]" : "[レコード版]") + " seq=" + item.Source.Sequence.ToString(CultureInfo.InvariantCulture) + " " + item.Source.FileName
                    + " / " + reader.DatabaseName(item.DatabaseId) + " / " + reader.StoreName(item.DatabaseId, item.StoreId) + " / キー: " + (item.Key != null ? item.Key.Display() : "(読めず)");
                _store = reader.StoreName(item.DatabaseId, item.StoreId);
                ProcessRecord(value, item.Key);
            }
            // blob files that no stored version refers to any more
            List<BlobFileEntry> files = reader.ListBlobFiles();
            if (files.Count == 0) return;
            HashSet<string> referenced = reader.ReferencedBlobs();
            foreach (BlobFileEntry file in files)
            {
                if (file.DatabaseId >= 0 && file.BlobNumber >= 0 && referenced.Contains(IndexedDbReader.BlobRef(file.DatabaseId, file.BlobNumber))) continue;
                _r.OrphanBlobs++;
                byte[] bytes;
                try
                {
                    if (new FileInfo(file.FullPath).Length > 64L * 1024 * 1024) { _r.Unreadable++; continue; }
                    bytes = File.ReadAllBytes(file.FullPath);
                }
                catch (Exception) { _r.Unreadable++; continue; }
                UnwrapResult unwrap = reader.UnwrapStandalone(bytes);
                if (unwrap.Error != null) { _r.Unreadable++; continue; }
                if (!prefilter.Any(unwrap.Ssv, unwrap.Ssv.Length)) continue;
                object value;
                try
                {
                    ulong version;
                    value = V8Deserializer.Deserialize(unwrap.Ssv, unwrap.SsvOffset, out version);
                }
                catch (Exception) { _r.DecodeFailures++; continue; }
                _r.Decoded++;
                _itemKey = "o|" + file.RelativePath;
                _where = sourceLabel + " [孤立blob] blob/" + file.RelativePath + "（保存順序不明）";
                _store = "(孤立 blob)";
                ProcessRecord(value, null);
            }
        }

        private void ProcessRecord(object value, IdbKey key)
        {
            _visited = new HashSet<object>(RefComparer.Instance);
            _nodes = new Dictionary<object, Node>(RefComparer.Instance);
            _flagged = new List<Node>();
            _json = new Dictionary<string, object>(StringComparer.Ordinal);
            Node root = Visit(value, "$", false, null, null, 0);
            string rootText = value as string;
            if (rootText != null) CheckWords(rootText, "$");
            if (root != null && !root.Confirmed && key != null)
            {
                foreach (string part in key.StringParts()) if (part != null) CheckIds(root, "(レコードのキー)", part);
                if (root.Flagged && !root.Listed) { root.Listed = true; _flagged.Add(root); }
            }
            foreach (Node n in _flagged) Emit(n);
        }

        private Node Visit(object node, string path, bool inConfirmed, string keyName, Node owner, int depth)
        {
            if (node == null || depth > 64) return null;
            OrderedMap map = ThreadTextRules.AsMap(node);
            if (map != null)
            {
                if (!_visited.Add(map)) return null;
                Node n = new Node();
                n.Map = map;
                n.Path = path;
                n.Confirmed = inConfirmed || _confirmed.Contains(_itemKey + "|" + path);
                _nodes[map] = n;
                if (!n.Confirmed && keyName != null) CheckIds(n, "(項目名 " + keyName + ")", keyName);
                foreach (KeyValuePair<string, object> p in map)
                {
                    VisitValue(p.Key, p.Value, path + "." + p.Key, n, n.Confirmed, depth);
                }
                if (n.Flagged && !n.Listed) { n.Listed = true; _flagged.Add(n); }
                return n;
            }
            List<object> list = node as List<object>;
            if (list != null)
            {
                if (!_visited.Add(list)) return null;
                for (int i = 0; i < list.Count; i++)
                    VisitValue(keyName != null ? keyName + "[" + i.ToString(CultureInfo.InvariantCulture) + "]" : null, list[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", owner, inConfirmed, depth);
            }
            return null;
        }

        /// <summary>One value of an object (or of a list that belongs to it): ids in scalars flag the owner, strings are
        /// checked for the words, JSON kept inside a string is walked like a nested value.</summary>
        private void VisitValue(string name, object value, string path, Node owner, bool inConfirmed, int depth)
        {
            string s = ThreadTextRules.Scalar(value);
            if (s != null)
            {
                if (owner != null && !owner.Confirmed && name != null) CheckIds(owner, name, s);
                if (!(value is string)) return;
                if (ThreadTextRules.LooksJson(s))
                {
                    object parsed;
                    if (!_json.TryGetValue(s, out parsed))
                    {
                        try { parsed = JsonReader.Parse(s); } catch (Exception) { parsed = null; }
                        _json[s] = parsed;
                    }
                    if (parsed != null && (parsed is OrderedMap || parsed is List<object>))
                    {
                        // the key name is not handed on: the JSON's own fields are what can carry an id
                        if (parsed is OrderedMap) Visit(parsed, path + "#json", inConfirmed, null, owner, depth + 1);
                        else VisitJsonList((List<object>)parsed, name, path + "#json", owner, inConfirmed, depth + 1);
                        return;
                    }
                }
                CheckWords(s, path);
                return;
            }
            if (value is JsObject || value is OrderedMap) Visit(value, path, inConfirmed, name, owner, depth + 1);
            else if (value is List<object>)
            {
                if (!_visited.Add(value)) return;
                List<object> list = (List<object>)value;
                for (int i = 0; i < list.Count; i++)
                    VisitValue(name, list[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", owner, inConfirmed, depth + 1);
            }
        }

        private void VisitJsonList(List<object> list, string name, string path, Node owner, bool inConfirmed, int depth)
        {
            if (depth > 64) return;
            for (int i = 0; i < list.Count; i++)
            {
                object v = list[i];
                string childPath = path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                if (v is OrderedMap) Visit(v, childPath, inConfirmed, null, owner, depth + 1);
                else VisitValue(name, v, childPath, owner, inConfirmed, depth + 1);
            }
        }

        /// <summary>Message ids are millisecond timestamps, so a time or version field can hold the very same number
        /// (a conversation's last-message time, a message's version) without referring to the message.</summary>
        private static bool IsTimeField(string field)
        {
            string f = field.ToLowerInvariant();
            return f.IndexOf("time", StringComparison.Ordinal) >= 0 || f.IndexOf("version", StringComparison.Ordinal) >= 0 || f.EndsWith("utc", StringComparison.Ordinal) || f.IndexOf("timestamp", StringComparison.Ordinal) >= 0;
        }

        private void CheckIds(Node n, string field, string value)
        {
            if (value == _reference.MessageId && IsTimeField(field))
            {
                _r.TimeFieldMatches++;
                return;
            }
            if (!n.Id)
            {
                if (ThreadTextRules.ContainsToken(value, _reference.MessageId)) { n.Id = true; n.IdField = field; }
                else foreach (string cid in _clientIds) if (ThreadTextRules.ContainsToken(value, cid)) { n.Id = true; n.IdField = field + "（clientMessageId の値）"; break; }
            }
            if (!n.Parent && !string.IsNullOrEmpty(_reference.ParentMessageId) && _reference.ParentMessageId != _reference.MessageId
                && ThreadTextRules.ContainsToken(value, _reference.ParentMessageId)) { n.Parent = true; n.ParentField = field; }
            if (!n.Conv && ThreadTextRules.ContainsConversation(value, _reference.ConversationId)) { n.Conv = true; n.ConvField = field; }
        }

        private void CheckWords(string raw, string path)
        {
            if (_r.Terms.Count == 0 || raw.Length < MinTermChars) return;
            string plain = ThreadTextRules.Plain(raw);
            if (plain.Length < MinTermChars) return;
            // one occurrence per text, under the user's word when there is one (the group is tagged with every term it contains)
            SearchTerm first = null;
            int firstIndex = -1;
            string stripped = null;
            foreach (SearchTerm t in _r.Terms)
            {
                int idx = plain.IndexOf(t.Text, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    // a word broken across lines: compare with the white space taken out (built once per text)
                    if (stripped == null) stripped = ThreadTextRules.StripWhiteSpace(plain);
                    if (stripped.Length == plain.Length || stripped.IndexOf(ThreadTextRules.StripWhiteSpace(t.Text), StringComparison.OrdinalIgnoreCase) < 0) continue;
                    idx = -2;
                }
                if (t.Auto) _r.AutoHitsIndexedDb++; else _r.WordHitsIndexedDb++;
                if (first == null || (first.Auto && !t.Auto)) { first = t; firstIndex = idx; }
            }
            if (first != null) AddOccurrence("word", first, plain, "indexeddb", _where, path, null, firstIndex);
        }

        private void Tally(string key)
        {
            long n;
            _r.FieldTally.TryGetValue(key, out n);
            _r.FieldTally[key] = n + 1;
        }

        private void Emit(Node n)
        {
            string category = n.Id ? "id-field" : (n.Parent ? "parent" : "conversation");
            string idField = n.Id ? n.IdField : (n.Parent ? n.ParentField : n.ConvField);
            if (n.Id) _r.IdFieldRecords++; else if (n.Parent) _r.ParentRecords++; else _r.ConversationRecords++;
            Tally((n.Id ? "対象 ID" : (n.Parent ? "親投稿 ID" : "会話 ID")) + " | " + _store + " | " + idField);
            List<KeyValuePair<string, string>> texts = new List<KeyValuePair<string, string>>();
            CollectTexts(n.Map, n.Path, texts, 0);
            foreach (KeyValuePair<string, string> t in texts) AddOccurrence(category, null, t.Value, "indexeddb", _where, t.Key, idField, -1);
        }

        /// <summary>The display texts of one flagged object. Sub-objects that are reported on their own (flagged) or
        /// that are body candidates are left out, so no text is attributed twice or lifted from a confirmed body.</summary>
        private void CollectTexts(OrderedMap map, string path, List<KeyValuePair<string, string>> texts, int depth)
        {
            if (depth > 6) return;
            foreach (KeyValuePair<string, object> p in map)
            {
                if (texts.Count >= 60) return;
                CollectTextValue(p.Key, p.Value, path + "." + p.Key, texts, depth);
            }
        }

        private void CollectTextValue(string name, object value, string path, List<KeyValuePair<string, string>> texts, int depth)
        {
            string s = value as string;
            if (s != null)
            {
                object parsed;
                if (_json.TryGetValue(s, out parsed) && parsed != null && (parsed is OrderedMap || parsed is List<object>)) { CollectTextValue(name, parsed, path + "#json", texts, depth + 1); return; }
                if (!ThreadTextRules.IsTextLike(name, s)) return;
                string plain = ThreadTextRules.Plain(s);
                if (plain.Length > 0) texts.Add(new KeyValuePair<string, string>(path, plain));
                return;
            }
            OrderedMap child = ThreadTextRules.AsMap(value);
            if (child != null)
            {
                Node c;
                if (_nodes.TryGetValue(child, out c) && (c.Confirmed || c.Flagged)) return;
                CollectTexts(child, path, texts, depth + 1);
                return;
            }
            List<object> list = value as List<object>;
            if (list == null || depth > 6) return;
            for (int i = 0; i < list.Count && texts.Count < 60; i++)
                CollectTextValue(name, list[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", texts, depth + 1);
        }

        // ---------------------------------------------------------------- traces (hits the trace scan already made)

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

        /// <summary>A raw run of bytes that is a cache key or a url list (ASCII only, with a scheme): not display text.</summary>
        private static bool LooksLikeAddress(string text)
        {
            if (text.IndexOf("://", StringComparison.Ordinal) < 0) return false;
            foreach (char c in text) if (c > 0x7E) return false;
            return true;
        }

        private void AddTraces(TraceStage ts)
        {
            if (ts == null || !ts.Ran) return;
            _r.TracesIncluded = true;
            foreach (ReportWriter.TraceVersion v in ReportWriter.TraceVersions(_ctx)) _traceBodyKeys.Add(MessageHistoryFinder.NormalizeText(v.Text));
            Dictionary<TraceHit, string> files = new Dictionary<TraceHit, string>();
            if (ts.Output != null) foreach (KeyValuePair<TraceHit, string> p in ts.Output.HitFiles) files[p.Key] = p.Value;
            foreach (TraceSourceResult r in ts.Results)
            {
                foreach (TraceHit h in r.Hits)
                {
                    if (h.Classification == "other-conversation") continue;   // another conversation's record: not this thread
                    // the message's own record with its content is already listed as a trace body, whichever id led to it
                    if (h.ObjectParsed && h.ObjectIdIsTarget && h.ObjectContentText != null) continue;
                    string category;
                    if (h.NeedleKind == "messageId")
                    {
                        category = "id-field";
                        _r.IdFieldTraces++;
                    }
                    else if (h.NeedleKind == "parentMessageId") { category = "parent"; _r.ParentTraces++; }
                    else { category = "conversation"; _r.ConversationTraces++; }
                    string file;
                    files.TryGetValue(h, out file);
                    string where = "[" + KindLabel(h.SourceKind) + "] " + h.RelativePath + " 位置 " + h.Offset.ToString(CultureInfo.InvariantCulture)
                        + (h.FromGzip ? " (gzip 展開後)" : "") + (h.Entry != null ? " (キャッシュ項目の展開後の本文)" : "") + (file != null ? " → " + file : "");
                    string idField = h.ObjectFound && h.ObjectParsed ? (h.ObjectIdIsTarget ? "id が対象の記録（content の項目なし）" : "id " + (h.ObjectId ?? "なし") + " の記録の中") : "一致の周辺（" + h.ContextKind + "）";
                    if (h.ObjectTexts != null && h.ObjectTexts.Count > 0)
                    {
                        foreach (KeyValuePair<string, string> t in h.ObjectTexts) AddOccurrence(category, null, t.Value, h.SourceKind, where, t.Key, idField, -1);
                    }
                    else if (h.Fragment != null && ThreadTextRules.IsTextLike("", h.Fragment) && !(h.ContextKind == "text" && LooksLikeAddress(h.Fragment)))
                    {
                        AddOccurrence(category, null, h.Fragment, h.SourceKind, where, "最寄りの要素（" + h.ContextKind + "）", idField, -1);
                    }
                }
                foreach (TraceWordHit w in r.WordHits)
                {
                    if (w.Term.Auto) _r.AutoHitsTraces++; else _r.WordHitsTraces++;
                    string where = "[" + KindLabel(w.SourceKind) + "] " + w.RelativePath + " 位置 " + w.Offset.ToString(CultureInfo.InvariantCulture)
                        + (w.FromGzip ? " (gzip 展開後)" : "") + (w.Entry != null ? " (キャッシュ項目の展開後の本文。キー: " + (w.Entry.Key ?? "読めず") + ")" : "");
                    AddOccurrence("word", w.Term, w.Text, w.SourceKind, where, "生バイトの前後（" + w.Encoding + (w.JsonString ? "、JSON の文字列値" : "") + "）", null, w.MatchIndex);
                }
                _r.WordHitCap += r.WordHitCapReached;
            }
        }

        // ---------------------------------------------------------------- grouping

        private static int Cap(string category)
        {
            switch (category)
            {
                case "word": return 1500;
                case "id-field": return 1500;
                case "auto": return 500;
                default: return 2000;   // parent, conversation
            }
        }

        private static string TrimEllipsis(string s)
        {
            return s.TrimEnd('…', '.', ' ', '・');
        }

        private void AddOccurrence(string category, SearchTerm term, string text, string origin, string where, string field, string idField, int matchIndex)
        {
            if (string.IsNullOrEmpty(text)) return;
            bool cut = text.Length > ThreadTextRules.MaxTextChars;
            if (cut) text = text.Substring(0, ThreadTextRules.MaxTextChars);
            string key = MessageHistoryFinder.NormalizeText(text);
            if (key.Length == 0) return;
            ThreadText g;
            if (!_groups.TryGetValue(key, out g))
            {
                string capKey = term != null ? (term.Auto ? "auto" : "word") : category;
                int n;
                _perCategory.TryGetValue(capKey, out n);
                if (n >= Cap(capKey)) { _r.TextsDropped++; return; }
                _perCategory[capKey] = n + 1;
                g = new ThreadText();
                g.Text = text;
                g.Cut = cut;
                g.MatchIndex = matchIndex;
                for (int i = 0; i < _bodyKeys.Count; i++)
                {
                    string body = _bodyKeys[i];
                    if (string.IsNullOrEmpty(body)) continue;
                    if (g.SameAsBody == 0 && body == key) g.SameAsBody = i + 1;
                    else if (g.PartOfBody == 0)
                    {
                        string excerpt = TrimEllipsis(key);
                        if (excerpt.Length >= 4 && body.IndexOf(excerpt, StringComparison.Ordinal) >= 0) g.PartOfBody = i + 1;
                    }
                }
                if (g.SameAsBody > 0) g.PartOfBody = 0;
                for (int i = 0; i < _traceBodyKeys.Count; i++) if (_traceBodyKeys[i] == key) { g.SameAsTraceBody = i + 1; break; }
                foreach (SearchTerm t in _r.Terms) if (ThreadTextRules.IndexOfTerm(text, t.Text) != -1) g.Terms.Add(t);
                _groups[key] = g;
                _r.Texts.Add(g);
            }
            g.OccurrenceCount++;
            if (category == "id-field") g.IdField++; else if (category == "parent") g.Parent++; else if (category == "conversation") g.Conversation++; else g.Word++;
            if (origin == "indexeddb") g.FromIndexedDb++; else g.FromTraces++;
            if (term != null && !g.Terms.Contains(term)) g.Terms.Add(term);
            if (g.MatchIndex < 0 && matchIndex >= 0) g.MatchIndex = matchIndex;
            if (g.Occurrences.Count >= MaxOccurrencesKept) return;
            ThreadOccurrence o = new ThreadOccurrence();
            o.Category = category;
            o.Origin = origin;
            o.Where = where;
            o.Field = field;
            o.IdField = idField;
            o.Term = term;
            g.Occurrences.Add(o);
        }

        private void Finish()
        {
            // stable order: by section, then in the order found
            List<ThreadText> ordered = new List<ThreadText>();
            for (int section = 0; section <= 5; section++) foreach (ThreadText t in _r.Texts) if (t.Section == section) ordered.Add(t);
            _r.Texts.Clear();
            _r.Texts.AddRange(ordered);
            int number = 1;
            foreach (ThreadText t in _r.Texts) t.Number = number++;
            if (_r.TextsDropped > 0) _r.Notes.Add("文面の種類が上限に達し、" + _r.TextsDropped.ToString(CultureInfo.InvariantCulture) + " 件の出現は文面として残していない（件数には数えている）。");
            if (_r.WordHitCap > 0) _r.Notes.Add("言葉の一致が 1 つの領域あたりの上限（" + TraceScanner.MaxHitsPerNeedlePerBuffer.ToString(CultureInfo.InvariantCulture) + " 件）に達した領域が " + _r.WordHitCap.ToString(CultureInfo.InvariantCulture) + " あり、その先は調べていない。");
        }

        // ---------------------------------------------------------------- output

        public static string SectionTitle(int section)
        {
            switch (section)
            {
                case 0: return "入力した言葉を含む文面";
                case 1: return "対象のメッセージ ID を別の項目に持つ記録の文面";
                case 2: return "本文候補の一部と同じ並びを含む文面（自動。本文候補と同じもの・その抜粋は除く）";
                case 3: return "同じスレッド（親投稿 ID）の記録の文面";
                case 5: return "本文候補そのもの・その抜粋（自動の並びが一致しただけのもの）";
                default: return "同じ会話（会話 ID）だけが一致する記録の文面";
            }
        }

        public static string BodyRelation(ThreadText t)
        {
            string trace = t.SameAsTraceBody > 0 ? "（痕跡本文 " + t.SameAsTraceBody.ToString(CultureInfo.InvariantCulture) + " と同じ）" : "";
            if (t.SameAsBody > 0) return "= 本文候補 " + t.SameAsBody.ToString(CultureInfo.InvariantCulture) + " と同じ本文" + trace;
            if (t.PartOfBody > 0) return "本文候補 " + t.PartOfBody.ToString(CultureInfo.InvariantCulture) + " の一部と同じ（抜粋）" + trace;
            return "本文候補には無い文面" + trace;
        }

        private static string TermList(ThreadText t, bool auto)
        {
            List<string> parts = new List<string>();
            foreach (SearchTerm term in t.Terms) if (term.Auto == auto) parts.Add(auto ? "本文候補 " + term.BodyNumber.ToString(CultureInfo.InvariantCulture) + " の「" + term.Text + "」" : "「" + term.Text + "」");
            return string.Join("、", parts.ToArray());
        }

        public static void AppendText(StringBuilder sb, ThreadText t, int maxChars)
        {
            sb.AppendLine("  --- 文面 " + t.Number.ToString(CultureInfo.InvariantCulture) + "  " + BodyRelation(t));
            List<string> why = new List<string>();
            if (t.IdField > 0) why.Add("別項目に対象 ID " + t.IdField.ToString(CultureInfo.InvariantCulture));
            if (t.Parent > 0) why.Add("親投稿 ID " + t.Parent.ToString(CultureInfo.InvariantCulture));
            if (t.Conversation > 0) why.Add("会話 ID だけ " + t.Conversation.ToString(CultureInfo.InvariantCulture));
            if (t.Word > 0) why.Add("言葉の一致 " + t.Word.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("      見つけ方（出現数）: " + string.Join(" / ", why.ToArray()) + " | 出所: IndexedDB " + t.FromIndexedDb.ToString(CultureInfo.InvariantCulture) + " / 通知・キャッシュ " + t.FromTraces.ToString(CultureInfo.InvariantCulture));
            string user = TermList(t, false), auto = TermList(t, true);
            if (user.Length > 0) sb.AppendLine("      含まれる言葉: " + user + (t.MatchIndex >= 0 ? "（最初の一致は " + (t.MatchIndex + 1).ToString(CultureInfo.InvariantCulture) + " 文字目）" : ""));
            if (auto.Length > 0) sb.AppendLine("      本文候補と同じ並び: " + auto);
            string text = t.Text ?? "";
            bool shortened = maxChars > 0 && text.Length > maxChars;
            if (shortened)
            {
                // keep the surroundings of the first word match in view
                int start = t.MatchIndex > maxChars / 2 ? Math.Min(t.MatchIndex - maxChars / 2, text.Length - maxChars) : 0;
                text = (start > 0 ? "…" : "") + text.Substring(start, maxChars) + "…";
            }
            sb.AppendLine("      文面" + (shortened ? "（" + maxChars.ToString(CultureInfo.InvariantCulture) + " 文字に切り詰め。全体は thread\\texts.txt）" : "") + (t.Cut ? "（保存時に " + ThreadTextRules.MaxTextChars.ToString(CultureInfo.InvariantCulture) + " 文字で切り詰め）" : "") + ":");
            foreach (string line in text.Replace("\r\n", "\n").Split('\n')) sb.AppendLine("        " + line);
            int shown = 0;
            foreach (ThreadOccurrence o in t.Occurrences)
            {
                if (shown >= 3 && maxChars > 0) break;
                shown++;
                sb.AppendLine("      出典: " + o.Where + " / 項目: " + (o.Field ?? "?") + (o.IdField != null ? " / ID のあった項目: " + o.IdField : ""));
            }
            if (t.OccurrenceCount > shown) sb.AppendLine("      （出現は全部で " + t.OccurrenceCount.ToString(CultureInfo.InvariantCulture) + " 件。記録の版ごとに 1 件と数える）");
        }

        /// <summary>The section of result.txt: every category with its counts, the texts up to a stated number each.</summary>
        public static void AppendReport(StringBuilder sb, ThreadSearchResult r)
        {
            sb.AppendLine("[スレッド全体の探索（未確認）]  対象の ID を別の項目に持つ記録、同じスレッド・会話の記録、入力した言葉を含む文面を、項目名に関係なく集めたもの。"
                + "どれも対象メッセージの版だと証明されたものではなく、本文候補には数えない。");
            if (r == null || !r.Ran)
            {
                sb.AppendLine("  (行っていない" + (r == null ? "" : "。-NoThread") + ")");
                sb.AppendLine();
                return;
            }
            if (r.Error != null) sb.AppendLine("  出力エラー: " + r.Error);
            sb.AppendLine("  範囲: IndexedDB はレコードの全ての版・undo ログ・参照の無い blob を読み直した（走査 " + r.ItemsScanned.ToString(CultureInfo.InvariantCulture) + " 件、ID か言葉を含み復号したもの " + r.Decoded.ToString(CultureInfo.InvariantCulture)
                + " 件、復号失敗 " + r.DecodeFailures.ToString(CultureInfo.InvariantCulture) + " 件、包みを解けず読めないもの " + r.Unreadable.ToString(CultureInfo.InvariantCulture) + " 件）。"
                + (r.TracesIncluded ? "通知データベースとキャッシュは、痕跡走査の一致と言葉の一致を使った。" : "通知データベースとキャッシュは含まない（痕跡走査なし）。"));
            List<string> userTerms = new List<string>();
            foreach (SearchTerm t in r.Terms) if (!t.Auto) userTerms.Add("「" + t.Text + "」");
            sb.AppendLine("  言葉: " + (userTerms.Count > 0 ? string.Join("、", userTerms.ToArray()) : "(指定なし)") + " / 本文候補から自動で取った並び " + r.AutoTerms.ToString(CultureInfo.InvariantCulture) + " 個"
                + "（各 " + AutoTermChars.ToString(CultureInfo.InvariantCulture) + " 文字。本文候補と言い回しが重なる文面を ID なしで探すため）");
            sb.AppendLine("  件数: 別項目に対象 ID を持つ記録 " + (r.IdFieldRecords + r.IdFieldTraces).ToString(CultureInfo.InvariantCulture) + "（IndexedDB " + r.IdFieldRecords.ToString(CultureInfo.InvariantCulture) + " / 通知・キャッシュ " + r.IdFieldTraces.ToString(CultureInfo.InvariantCulture) + "）"
                + "、親投稿 ID の記録 " + (r.ParentRecords + r.ParentTraces).ToString(CultureInfo.InvariantCulture) + "、会話 ID だけの記録 " + (r.ConversationRecords + r.ConversationTraces).ToString(CultureInfo.InvariantCulture)
                + "、言葉の一致 " + (r.WordHitsIndexedDb + r.WordHitsTraces).ToString(CultureInfo.InvariantCulture) + "（IndexedDB " + r.WordHitsIndexedDb.ToString(CultureInfo.InvariantCulture) + " / 通知・キャッシュ " + r.WordHitsTraces.ToString(CultureInfo.InvariantCulture) + "）"
                + "、自動の並びの一致 " + (r.AutoHitsIndexedDb + r.AutoHitsTraces).ToString(CultureInfo.InvariantCulture) + "。IndexedDB の記録は保存された版ごとに数える。");
            if (r.FieldTally.Count > 0)
            {
                sb.AppendLine("  IndexedDB で ID のあった場所（種類 | ストア | 項目: 記録数。文面の無い記録も数える）:");
                List<string> keys = new List<string>(r.FieldTally.Keys);
                keys.Sort(StringComparer.Ordinal);
                int shown = 0;
                foreach (string k in keys)
                {
                    if (shown++ >= 40) { sb.AppendLine("    … 残りは thread\\thread.json"); break; }
                    sb.AppendLine("    " + k + ": " + r.FieldTally[k].ToString(CultureInfo.InvariantCulture));
                }
            }
            if (r.TimeFieldMatches > 0) sb.AppendLine("  注: 時刻・版の項目（項目名に time / version / utc を含むもの）が対象 ID と同じ数値だった箇所 " + r.TimeFieldMatches.ToString(CultureInfo.InvariantCulture)
                + " 件は、メッセージへの参照ではなく時刻の一致とみなし、「別項目に対象 ID を持つ記録」には数えていない（メッセージ ID は送信時刻の数値のため）。");
            foreach (string n in r.Notes) sb.AppendLine("  注: " + n);
            for (int section = 0; section <= 4; section++)
            {
                List<ThreadText> texts = new List<ThreadText>();
                foreach (ThreadText t in r.Texts) if (t.Section == section) texts.Add(t);
                int notIn = 0;
                foreach (ThreadText t in texts) if (t.NotInBodies) notIn++;
                sb.AppendLine("  ==== " + SectionTitle(section) + ": " + texts.Count.ToString(CultureInfo.InvariantCulture) + " 種（本文候補に無いもの " + notIn.ToString(CultureInfo.InvariantCulture) + " 種）");
                if (section == 0 && r.UserTerms == 0) { sb.AppendLine("    (言葉の指定なし。-Word <言葉> か、対話式の入力で指定できる)"); continue; }
                if (texts.Count == 0) { sb.AppendLine("    (該当なし)"); continue; }
                // texts that are not a known body come first: they are the ones worth reading
                List<ThreadText> ordered = new List<ThreadText>();
                foreach (ThreadText t in texts) if (t.NotInBodies) ordered.Add(t);
                foreach (ThreadText t in texts) if (!t.NotInBodies) ordered.Add(t);
                int shown = 0;
                foreach (ThreadText t in ordered)
                {
                    if (shown++ >= MaxTextsShown)
                    {
                        sb.AppendLine("    … 残り " + (ordered.Count - MaxTextsShown).ToString(CultureInfo.InvariantCulture) + " 種は thread\\texts.txt にある。");
                        break;
                    }
                    AppendText(sb, t, MaxCharsShown);
                }
            }
            sb.AppendLine();
        }

        private void WriteDetails()
        {
            string dir = Path.Combine(_ctx.OutputFolder, "thread");
            Directory.CreateDirectory(dir);
            UTF8Encoding utf8 = new UTF8Encoding(false);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("スレッド全体の探索で集めた文面（全件）。どれも未確認で、対象メッセージの版だと証明されたものではありません。");
            sb.AppendLine("本文・ID・パスを含むため共有しないでください。");
            sb.AppendLine();
            for (int section = 0; section <= 5; section++)
            {
                sb.AppendLine("==== " + SectionTitle(section));
                foreach (ThreadText t in _r.Texts) if (t.Section == section) AppendText(sb, t, 0);
                sb.AppendLine();
            }
            _r.TextsFile = Path.Combine("thread", "texts.txt");
            File.WriteAllText(Path.Combine(dir, "texts.txt"), sb.ToString(), utf8);
            OrderedMap full = ToMap(_r, true);
            full.Set("tool", _ctx.ToolVersion);
            full.Set("query", _reference.ToMap());
            _r.DetailFile = Path.Combine("thread", "thread.json");
            File.WriteAllText(Path.Combine(dir, "thread.json"), JsonWriter.Serialize(full), utf8);
        }

        public static OrderedMap ToMap(ThreadSearchResult r, bool includeTexts)
        {
            OrderedMap m = new OrderedMap();
            if (r == null) { m.Set("mode", "not_run"); return m; }
            m.Set("mode", r.Ran ? "ran" : "skipped");
            m.Set("note", "すべて未確認。対象の ID を別の項目に持つ記録、同じスレッド・会話の記録、言葉を含む文面で、対象メッセージの版だと証明されたものではなく、本文候補には数えない。");
            m.Set("error", r.Error);
            m.Set("notes", new List<object>(r.Notes.ToArray()));
            List<object> terms = new List<object>();
            foreach (SearchTerm t in r.Terms)
            {
                OrderedMap tm = new OrderedMap();
                tm.Set("kind", t.Auto ? "auto" : "user");
                tm.Set("number", t.Number);
                tm.Set("text", t.Text);
                tm.Set("fromBody", t.Auto ? (object)t.BodyNumber : null);
                terms.Add(tm);
            }
            m.Set("terms", terms);
            m.Set("tracesIncluded", r.TracesIncluded);
            OrderedMap idb = new OrderedMap();
            idb.Set("itemsScanned", r.ItemsScanned);
            idb.Set("decoded", r.Decoded);
            idb.Set("decodeFailures", r.DecodeFailures);
            idb.Set("unreadable", r.Unreadable);
            idb.Set("orphanBlobs", r.OrphanBlobs);
            m.Set("indexedDb", idb);
            OrderedMap counts = new OrderedMap();
            counts.Set("idFieldRecordsIndexedDb", r.IdFieldRecords);
            counts.Set("idFieldRecordsTraces", r.IdFieldTraces);
            counts.Set("parentRecordsIndexedDb", r.ParentRecords);
            counts.Set("parentRecordsTraces", r.ParentTraces);
            counts.Set("conversationRecordsIndexedDb", r.ConversationRecords);
            counts.Set("conversationRecordsTraces", r.ConversationTraces);
            counts.Set("wordHitsIndexedDb", r.WordHitsIndexedDb);
            counts.Set("wordHitsTraces", r.WordHitsTraces);
            counts.Set("autoHitsIndexedDb", r.AutoHitsIndexedDb);
            counts.Set("autoHitsTraces", r.AutoHitsTraces);
            counts.Set("timeFieldMatches", r.TimeFieldMatches);
            counts.Set("textsDropped", r.TextsDropped);
            counts.Set("wordHitCapReached", r.WordHitCap);
            m.Set("counts", counts);
            if (includeTexts)
            {
                OrderedMap tally = new OrderedMap();
                List<string> tallyKeys = new List<string>(r.FieldTally.Keys);
                tallyKeys.Sort(StringComparer.Ordinal);
                foreach (string k in tallyKeys) tally.Set(k, r.FieldTally[k]);
                m.Set("idLocations", tally);
            }
            OrderedMap texts = new OrderedMap();
            string[] names = new string[] { "word", "idField", "auto", "parent", "conversation", "bodyItself" };
            for (int section = 0; section <= 4; section++)
            {
                OrderedMap s = new OrderedMap();
                s.Set("texts", r.CountTexts(section, false));
                s.Set("notInBodies", r.CountTexts(section, true));
                texts.Set(names[section], s);
            }
            m.Set("distinctTexts", texts);
            m.Set("detailFile", r.DetailFile);
            m.Set("textsFile", r.TextsFile);
            if (!includeTexts) return m;
            List<object> list = new List<object>();
            foreach (ThreadText t in r.Texts)
            {
                OrderedMap tm = new OrderedMap();
                tm.Set("number", t.Number);
                tm.Set("section", names[t.Section]);
                tm.Set("sameAsBody", t.SameAsBody > 0 ? (object)t.SameAsBody : null);
                tm.Set("partOfBody", t.PartOfBody > 0 ? (object)t.PartOfBody : null);
                tm.Set("sameAsTraceBody", t.SameAsTraceBody > 0 ? (object)t.SameAsTraceBody : null);
                tm.Set("text", t.Text);
                tm.Set("textCut", t.Cut);
                tm.Set("idField", t.IdField);
                tm.Set("parent", t.Parent);
                tm.Set("conversation", t.Conversation);
                tm.Set("word", t.Word);
                tm.Set("fromIndexedDb", t.FromIndexedDb);
                tm.Set("fromTraces", t.FromTraces);
                tm.Set("occurrenceCount", t.OccurrenceCount);
                tm.Set("matchIndex", t.MatchIndex >= 0 ? (object)t.MatchIndex : null);
                List<object> tl = new List<object>();
                foreach (SearchTerm term in t.Terms) tl.Add((term.Auto ? "auto:" : "user:") + term.Text);
                tm.Set("terms", tl);
                List<object> occ = new List<object>();
                foreach (ThreadOccurrence o in t.Occurrences)
                {
                    OrderedMap om = new OrderedMap();
                    om.Set("category", o.Category);
                    om.Set("origin", o.Origin);
                    om.Set("where", o.Where);
                    om.Set("field", o.Field);
                    om.Set("idField", o.IdField);
                    om.Set("term", o.Term != null ? o.Term.Text : null);
                    occ.Add(om);
                }
                tm.Set("occurrences", occ);
                list.Add(tm);
            }
            m.Set("texts", list);
            return m;
        }

        // ---------------------------------------------------------------- screen (counts and fixed labels only)

        private static string N(long n)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Three lines for the one-screen summary: counts and fixed labels, no text, no word, no id, no path.</summary>
        public static List<string> ScreenLines(ThreadSearchResult r)
        {
            List<string> lines = new List<string>();
            if (r == null || !r.Ran)
            {
                lines.Add("[スレッド探索] 行っていない");
                return lines;
            }
            lines.Add("[スレッド探索/未確認] 別項目のID: 記録 " + N(r.IdFieldRecords + r.IdFieldTraces) + " / 文面 " + N(r.CountTexts(1, false)) + " (本文候補に無い " + N(r.CountTexts(1, true)) + ")"
                + " | 打切り " + N(r.TextsDropped + r.WordHitCap) + (r.Error != null ? " | 出力エラー" : ""));
            lines.Add("  言葉 " + N(r.UserTerms) + " 個: 一致 " + N(r.WordHitsIndexedDb + r.WordHitsTraces) + " / 文面 " + N(r.CountTexts(0, false)) + " (本文候補に無い " + N(r.CountTexts(0, true)) + ")"
                + " | 本文と同じ並びの別文面 " + N(r.CountTexts(2, true)));
            lines.Add("  親投稿ID: 記録 " + N(r.ParentRecords + r.ParentTraces) + " / 文面 " + N(r.CountTexts(3, false)) + " | 会話IDだけ: 記録 " + N(r.ConversationRecords + r.ConversationTraces) + " / 文面 " + N(r.CountTexts(4, false)));
            return lines;
        }

        /// <summary>One line for the ordinary console summary and the top of result.txt.</summary>
        public static string Summary(ThreadSearchResult r)
        {
            if (r == null) return "行っていない（解析が途中で終了）";
            if (!r.Ran) return "行っていない（-NoThread）";
            if (r.Error != null) return "出力に失敗（" + r.Error + "）";
            return "別項目に対象 ID を持つ記録 " + N(r.IdFieldRecords + r.IdFieldTraces) + " 件（文面 " + N(r.CountTexts(1, false)) + " 種、本文候補に無いもの " + N(r.CountTexts(1, true)) + " 種）"
                + " / 言葉 " + N(r.UserTerms) + " 個の一致 " + N(r.WordHitsIndexedDb + r.WordHitsTraces) + " 件（文面 " + N(r.CountTexts(0, false)) + " 種）"
                + " / 親投稿 ID の記録 " + N(r.ParentRecords + r.ParentTraces) + " 件 / 会話 ID だけの記録 " + N(r.ConversationRecords + r.ConversationTraces) + " 件"
                + " / 本文候補と同じ並びを含む別の文面 " + N(r.CountTexts(2, true)) + " 種（いずれも未確認。本文候補ではない）";
        }
    }
}
