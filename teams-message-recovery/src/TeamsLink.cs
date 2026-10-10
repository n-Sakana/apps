// Teams Message History - parse a message link ("Copy link" in Teams) or a bare message id.
// Link formats (Microsoft Learn, "Deep link to a Teams chat"):
//   chat:    https://teams.microsoft.com/l/message/<chatId>/<messageId>?context={"contextType":"chat"}
//   channel: https://teams.microsoft.com/l/message/<channelId>/<messageId>?tenantId=..&groupId=..&parentMessageId=..&teamName=..&channelName=..&createdTime=..
// Nothing here touches the network; the text is only split into its parts.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TeamsMessageHistory
{
    public sealed class MessageReference
    {
        public string Input;
        public string InputKind;        // "link", "id", "conversation+id", "conversation-only", "invalid"
        public string ConversationId;   // e.g. 19:...@thread.v2, 19:...@thread.tacv2, 48:notes
        public string MessageId;        // the message the link points at (for a channel reply: the reply itself)
        public string ParentMessageId;  // channel links: the root post of the reply chain (never used as the target)
        public string TenantId;
        public string GroupId;
        public string CreatedTime;
        public string ContextType;
        public string Host;
        public readonly List<string> Notes = new List<string>();

        public bool IsValid { get { return !string.IsNullOrEmpty(MessageId); } }

        public bool LooksLikeChannel
        {
            get
            {
                if (ConversationId == null) return false;
                string c = ConversationId.ToLowerInvariant();
                return c.EndsWith("@thread.tacv2") || c.EndsWith("@thread.skype");
            }
        }

        public bool IsReplyLink
        {
            get { return ParentMessageId != null && MessageId != null && ParentMessageId != MessageId; }
        }

        private static readonly Regex DigitsOnly = new Regex(@"^[0-9]{6,25}$", RegexOptions.Compiled);
        private static readonly Regex ConversationAndId = new Regex(@"^(?<conv>[0-9]+:[^\s/]+)\s+(?<id>[0-9]{6,25})$", RegexOptions.Compiled);
        private static readonly Regex ConversationOnly = new Regex(@"^[0-9]+:[^\s/]+$", RegexOptions.Compiled);

        public static MessageReference Parse(string input)
        {
            MessageReference r = new MessageReference();
            r.Input = input ?? "";
            string text = r.Input.Trim().Trim('<', '>', '"', '\'');
            if (text.Length == 0)
            {
                r.InputKind = "invalid";
                r.Notes.Add("入力が空です。");
                return r;
            }
            int marker = text.IndexOf("/l/message/", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                ParseMessageLink(r, text, marker);
                return r;
            }
            if (text.IndexOf("/l/chat/", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("/l/channel/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                r.InputKind = "conversation-only";
                r.Notes.Add("このリンクは会話へのリンクで、メッセージIDを含みません。メッセージの「…」→「リンクをコピー」で取ったリンクを貼ってください。");
                int start = text.IndexOf("/l/", StringComparison.OrdinalIgnoreCase) + 3;
                int slash = text.IndexOf('/', start);
                if (slash > start)
                {
                    int next = text.IndexOfAny(new char[] { '/', '?', '#' }, slash + 1);
                    string conv = next > slash ? text.Substring(slash + 1, next - slash - 1) : text.Substring(slash + 1);
                    r.ConversationId = Uri.UnescapeDataString(conv);
                }
                return r;
            }
            if (DigitsOnly.IsMatch(text))
            {
                r.InputKind = "id";
                r.MessageId = text;
                r.Notes.Add("メッセージIDだけが指定されました。会話IDは照合せず、同じIDを持つ候補をすべて示します。");
                return r;
            }
            Match m = ConversationAndId.Match(text);
            if (m.Success)
            {
                r.InputKind = "conversation+id";
                r.ConversationId = m.Groups["conv"].Value;
                r.MessageId = m.Groups["id"].Value;
                return r;
            }
            if (ConversationOnly.IsMatch(text))
            {
                r.InputKind = "conversation-only";
                r.ConversationId = text;
                r.Notes.Add("会話IDだけが指定されました。メッセージIDが必要です。");
                return r;
            }
            r.InputKind = "invalid";
            r.Notes.Add("メッセージへのリンク（.../l/message/<会話ID>/<メッセージID>...）か、メッセージID（数字）を入力してください。");
            return r;
        }

        private static void ParseMessageLink(MessageReference r, string text, int marker)
        {
            r.InputKind = "link";
            int schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd > 0 && schemeEnd < marker)
            {
                int hostEnd = text.IndexOf('/', schemeEnd + 3);
                r.Host = hostEnd > 0 ? text.Substring(schemeEnd + 3, hostEnd - schemeEnd - 3) : text.Substring(schemeEnd + 3);
            }
            int pos = marker + "/l/message/".Length;
            int convEnd = text.IndexOf('/', pos);
            if (convEnd < 0)
            {
                r.Notes.Add("リンクにメッセージIDの部分がありません。");
                r.ConversationId = Uri.UnescapeDataString(StripQuery(text.Substring(pos)));
                return;
            }
            r.ConversationId = Uri.UnescapeDataString(text.Substring(pos, convEnd - pos));
            int idStart = convEnd + 1;
            int idEnd = text.IndexOfAny(new char[] { '?', '#', '/' }, idStart);
            string id = idEnd > 0 ? text.Substring(idStart, idEnd - idStart) : text.Substring(idStart);
            id = Uri.UnescapeDataString(id).Trim();
            if (id.Length == 0)
            {
                r.Notes.Add("リンクのメッセージIDが空です。");
                return;
            }
            r.MessageId = id;
            if (!DigitsOnly.IsMatch(id)) r.Notes.Add("メッセージIDが数字だけではありません（そのまま照合します）。");
            int q = text.IndexOf('?', convEnd);
            if (q > 0)
            {
                string query = text.Substring(q + 1);
                int hash = query.IndexOf('#');
                if (hash >= 0) query = query.Substring(0, hash);
                foreach (string part in query.Split('&'))
                {
                    if (part.Length == 0) continue;
                    int eq = part.IndexOf('=');
                    string key = eq >= 0 ? part.Substring(0, eq) : part;
                    string value = eq >= 0 ? Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' ')) : "";
                    switch (key.ToLowerInvariant())
                    {
                        case "parentmessageid": r.ParentMessageId = value; break;
                        case "tenantid": r.TenantId = value; break;
                        case "groupid": r.GroupId = value; break;
                        case "createdtime": r.CreatedTime = value; break;
                        case "context":
                            r.ContextType = value.IndexOf("chat", StringComparison.OrdinalIgnoreCase) >= 0 ? "chat" : value;
                            break;
                    }
                }
            }
            if (r.IsReplyLink)
            {
                r.Notes.Add("チャネルの返信へのリンクです。対象は返信そのもの（messageId）で、parentMessageId は親投稿の目印としてだけ使います。");
            }
        }

        private static string StripQuery(string s)
        {
            int q = s.IndexOfAny(new char[] { '?', '#' });
            return q >= 0 ? s.Substring(0, q) : s;
        }

        public OrderedMap ToMap()
        {
            OrderedMap map = new OrderedMap();
            map.Set("input", Input);
            map.Set("inputKind", InputKind);
            map.Set("host", Host);
            map.Set("conversationId", ConversationId);
            map.Set("messageId", MessageId);
            map.Set("parentMessageId", ParentMessageId);
            map.Set("isReplyLink", IsReplyLink);
            map.Set("looksLikeChannel", LooksLikeChannel);
            map.Set("tenantId", TenantId);
            map.Set("groupId", GroupId);
            map.Set("createdTime", CreatedTime);
            map.Set("contextType", ContextType);
            map.Set("notes", new List<object>(Notes.ToArray()));
            return map;
        }
    }
}
