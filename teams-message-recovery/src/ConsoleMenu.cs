// Teams Message History - choices made with the arrow keys and Enter.
// The option that used to be the default (plain Enter) starts selected. When the console cannot take
// single keys (input or output redirected, no console), the same question is asked as a numbered list
// read with ReadLine, so piped and scripted runs keep working.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace TeamsMessageHistory
{
    public static class ConsoleMenu
    {
        /// <summary>Development harness only: keys are taken from here instead of the console.</summary>
        public static Func<ConsoleKeyInfo> KeyReader;

        private static bool KeysAvailable
        {
            get
            {
                if (KeyReader != null) return true;
                try { return !Console.IsInputRedirected && !Console.IsOutputRedirected; }
                catch (Exception) { return false; }
            }
        }

        /// <summary>One of the options. Returns its index, or -1 when the input ended.</summary>
        public static int Choose(string title, IList<string> options, int defaultIndex)
        {
            if (options == null || options.Count == 0) return -1;
            if (defaultIndex < 0 || defaultIndex >= options.Count) defaultIndex = 0;
            if (KeysAvailable)
            {
                try
                {
                    bool[] none = null;
                    return Run(title, "↑↓ で選び、Enter で決定します。", options, defaultIndex, none);
                }
                catch (Exception) { }   // the console refused key input or cursor moves: ask by number instead
            }
            Console.WriteLine(title);
            for (int i = 0; i < options.Count; i++) Console.WriteLine("  " + (i + 1).ToString(CultureInfo.InvariantCulture) + "  " + options[i]);
            Console.WriteLine("番号を入力して Enter（Enter だけ = " + (defaultIndex + 1).ToString(CultureInfo.InvariantCulture) + "）:");
            Console.Write("> ");
            string typed = ReadLine();
            if (typed == null) return -1;
            int n;
            if (int.TryParse(typed.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 1 && n <= options.Count) return n - 1;
            return defaultIndex;
        }

        /// <summary>Any number of the options (space toggles). Returns the marks, or null when the input ended.</summary>
        public static bool[] ChooseMany(string title, IList<string> options, bool[] defaults)
        {
            bool[] marks = new bool[options.Count];
            for (int i = 0; i < marks.Length; i++) marks[i] = defaults == null || (i < defaults.Length && defaults[i]);
            if (options.Count == 0) return marks;
            if (KeysAvailable)
            {
                try
                {
                    bool[] working = (bool[])marks.Clone();
                    if (Run(title, "↑↓ で移動、スペースで選択/解除、Enter で決定します（最初はすべて選択済み）。", options, 0, working) < 0) return null;
                    return working;
                }
                catch (Exception) { }
            }
            Console.WriteLine(title);
            for (int i = 0; i < options.Count; i++) Console.WriteLine("  " + (i + 1).ToString(CultureInfo.InvariantCulture) + "  " + options[i]);
            Console.WriteLine("番号をカンマ区切りで指定（Enter だけ = すべて）:");
            Console.Write("> ");
            string typed = ReadLine();
            if (typed == null) return null;
            typed = typed.Trim();
            if (typed.Length == 0) return marks;
            bool[] picked = new bool[options.Count];
            foreach (string part in typed.Split(','))
            {
                int n;
                if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 1 && n <= options.Count) picked[n - 1] = true;
            }
            return picked;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ReadConsoleW(IntPtr hConsoleInput, [Out] char[] lpBuffer, int nNumberOfCharsToRead, out int lpNumberOfCharsRead, IntPtr pInputControl);

        /// <summary>One typed line (link, word, folder). On a Windows console it is read as UTF-16 straight from the
        /// console, so Japanese typed or pasted does not depend on the console's input code page; everywhere else
        /// (redirected input, other systems, any failure) it is Console.ReadLine. Null when the input ended.</summary>
        public static string ReadLine()
        {
            if (KeyReader == null && Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                try
                {
                    if (!Console.IsInputRedirected)
                    {
                        IntPtr handle = GetStdHandle(-10);   // STD_INPUT_HANDLE
                        StringBuilder sb = new StringBuilder();
                        char[] buffer = new char[4096];
                        bool ok = true;
                        while (true)
                        {
                            int read;
                            if (!ReadConsoleW(handle, buffer, buffer.Length, out read, IntPtr.Zero)) { ok = false; break; }
                            if (read <= 0) break;
                            sb.Append(buffer, 0, read);
                            if (buffer[read - 1] == '\n') break;
                        }
                        if (ok)
                        {
                            if (sb.Length == 0) return null;
                            string line = sb.ToString();
                            int cut = line.IndexOf('\x1A');   // Ctrl+Z
                            if (cut == 0) return null;
                            if (cut > 0) line = line.Substring(0, cut);
                            return line.TrimEnd('\r', '\n');
                        }
                    }
                }
                catch (Exception) { }
            }
            return Console.ReadLine();
        }

        private static ConsoleKeyInfo ReadKey()
        {
            return KeyReader != null ? KeyReader() : Console.ReadKey(true);
        }

        /// <summary>marks == null: single choice (returns the index). Otherwise a check list (returns 0, marks updated).</summary>
        private static int Run(string title, string hint, IList<string> options, int selected, bool[] marks)
        {
            Console.WriteLine(title);
            Console.WriteLine("  " + hint);
            // Redrawing needs a real screen buffer. Without one (harness, odd hosts) the list is printed once.
            bool redraw = false;
            int width = 80;
            if (KeyReader == null)
            {
                try
                {
                    width = Math.Max(20, Console.WindowWidth);
                    redraw = Console.CursorTop >= 0;
                }
                catch (Exception) { redraw = false; }
            }
            bool cursorWasVisible = true;
            if (redraw)
            {
                try { cursorWasVisible = Console.CursorVisible; Console.CursorVisible = false; } catch (Exception) { }
            }
            try
            {
                for (int i = 0; i < options.Count; i++) WriteLine(options, marks, i, i == selected, width, redraw);
                // after the lines are written the buffer may have scrolled: the list ends just above the cursor
                int top = redraw ? Console.CursorTop - options.Count : 0;
                while (true)
                {
                    ConsoleKeyInfo key = ReadKey();
                    int before = selected;
                    bool toggled = false;
                    if (key.Key == ConsoleKey.Enter) break;
                    if (key.Key == ConsoleKey.UpArrow) selected = selected > 0 ? selected - 1 : options.Count - 1;
                    else if (key.Key == ConsoleKey.DownArrow) selected = selected < options.Count - 1 ? selected + 1 : 0;
                    else if (key.Key == ConsoleKey.Home || key.Key == ConsoleKey.PageUp) selected = 0;
                    else if (key.Key == ConsoleKey.End || key.Key == ConsoleKey.PageDown) selected = options.Count - 1;
                    else if (key.Key == ConsoleKey.Spacebar && marks != null) { marks[selected] = !marks[selected]; toggled = true; }
                    else if (key.KeyChar >= '1' && key.KeyChar <= '9' && key.KeyChar - '1' < options.Count) selected = key.KeyChar - '1';   // the old number still moves the mark
                    if (!redraw || top < 0) continue;
                    if (before != selected) Redraw(options, marks, before, false, width, top);
                    if (before != selected || toggled) Redraw(options, marks, selected, true, width, top);
                    Console.SetCursorPosition(0, top + options.Count);
                }
            }
            finally
            {
                if (redraw)
                {
                    try { Console.CursorVisible = cursorWasVisible; } catch (Exception) { }
                }
            }
            if (marks == null)
            {
                Console.WriteLine("  → " + options[selected]);
                return selected;
            }
            int count = 0;
            foreach (bool m in marks) if (m) count++;
            Console.WriteLine("  → " + count.ToString(CultureInfo.InvariantCulture) + " 件を選択");
            return 0;
        }

        private static void Redraw(IList<string> options, bool[] marks, int index, bool selected, int width, int top)
        {
            Console.SetCursorPosition(0, top + index);
            WriteLine(options, marks, index, selected, width, true);
        }

        private static void WriteLine(IList<string> options, bool[] marks, int index, bool selected, int width, bool pad)
        {
            string text = (selected ? " > " : "   ") + (marks != null ? (marks[index] ? "[x] " : "[ ] ") : "") + options[index];
            text = Fit(text, width - 1, pad);
            if (selected && pad)
            {
                ConsoleColor fg = Console.ForegroundColor, bg = Console.BackgroundColor;
                Console.ForegroundColor = bg;
                Console.BackgroundColor = fg;
                Console.Write(text);
                Console.ForegroundColor = fg;
                Console.BackgroundColor = bg;
                Console.WriteLine();
            }
            else Console.WriteLine(text);
        }

        /// <summary>One screen line: cut to the width (a wrapped line would break the redraw) and padded to it.</summary>
        private static string Fit(string text, int columns, bool pad)
        {
            text = text.Replace("\r", " ").Replace("\n", " ");
            if (DiagnosticsCollector.DisplayWidth(text) > columns)
            {
                StringBuilder sb = new StringBuilder();
                int w = 0;
                foreach (char c in text)
                {
                    int cw = DiagnosticsCollector.DisplayWidth(c.ToString());
                    if (w + cw > columns - 3) break;
                    sb.Append(c);
                    w += cw;
                }
                text = sb.ToString() + "...";
            }
            if (!pad) return text;
            int missing = columns - DiagnosticsCollector.DisplayWidth(text);
            return missing > 0 ? text + new string(' ', missing) : text;
        }
    }
}
