namespace Rush;

public enum EditMode { Emacs, Vi }
public enum ViMode { Insert, Normal }

/// <summary>
/// Custom line editor with cursor movement, history, vi mode, tab completion,
/// reverse search, and basic editing. Replaces Console.ReadLine() for the REPL.
/// </summary>
public class LineEditor
{
    private readonly List<string> _history = new();
    private int _historyIndex;
    private int _maxHistory = 500;

    /// <summary>Maximum history entries (default 500, min 10). Set via config.</summary>
    public int MaxHistory { get => _maxHistory; set => _maxHistory = Math.Max(10, value); }

    public EditMode Mode { get; set; } = EditMode.Vi;
    private ViMode _viMode = ViMode.Insert;

    // Tab completion callback
    public Func<string, int, (string newInput, int newCursor)?>? CompleteHandler { get; set; }
    public Action? ShowCompletionsHandler { get; set; }

    // Syntax highlighting
    public SyntaxHighlighter? Highlighter { get; set; }

    // Vi count prefix
    private int _viCount;
    private bool _viCountActive;

    // Vi find char
    private char _lastFindChar;
    private bool _lastFindForward;

    // Vi yank/paste/undo
    private string _yankBuffer = "";
    private readonly Stack<(List<char> buffer, int cursor)> _undoStack = new();
    private char _pendingOperator; // 'd', 'c', 'y', or '\0'
    private int _pendingCount;

    // Fish-style autosuggestion
    private string? _suggestion;

    // History persistence
    private static readonly string HistoryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "rush");
    private static readonly string HistoryPath = Path.Combine(HistoryDir, "history");

    // Shared state for a single ReadLine call
    private List<char> _buffer = null!;
    private int _cursor;
    private int _startLeft;
    private int _startTop;
    private string? _savedInput;

    /// <summary>
    /// Get a read-only view of the history (for the 'history' built-in).
    /// </summary>
    public IReadOnlyList<string> History => _history;

    /// <summary>
    /// Read a line of input with full editing support.
    /// Returns null on Ctrl+D (EOF).
    /// </summary>
    public string? ReadLine()
    {
        // Fallback for redirected input (piped stdin, testing)
        if (Console.IsInputRedirected)
            return Console.ReadLine();

        // Treat Ctrl+C as a regular keystroke so Console.ReadKey can capture it.
        // Restored in finally so CancelKeyPress still works for running commands.
        Console.TreatControlCAsInput = true;
        try
        {
            _buffer = new List<char>();
            _cursor = 0;
            _startLeft = Console.CursorLeft;
            _startTop = Console.CursorTop;
            _historyIndex = _history.Count;
            _savedInput = null;
            _viCount = 0;
            _viCountActive = false;

            if (Mode == EditMode.Vi)
            {
                _viMode = ViMode.Insert;
                SetCursorShape(insert: true);
            }

            while (true)
            {
                var key = Console.ReadKey(intercept: true);

                string? result;
                if (Mode == EditMode.Vi)
                    result = HandleViKey(key);
                else
                    result = HandleEmacsKey(key);

                if (result != null)
                {
                    SetCursorShape(insert: true); // Reset cursor on exit
                    var line = result;
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        if (_history.Count == 0 || _history[^1] != line)
                        {
                            _history.Add(line);
                            if (_history.Count > MaxHistory)
                                _history.RemoveAt(0);
                        }
                    }
                    return result == "\x04" ? null : result;
                }
            }
        }
        finally
        {
            Console.TreatControlCAsInput = false;
        }
    }

    // ── Vi Mode ──────────────────────────────────────────────────────────

    private string? HandleViKey(ConsoleKeyInfo key)
    {
        if (_viMode == ViMode.Insert)
            return HandleViInsertKey(key);
        else
            return HandleViNormalKey(key);
    }

    private string? HandleViInsertKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _viMode = ViMode.Normal;
                if (_cursor > 0) _cursor--;
                SetCursorPos();
                SetCursorShape(insert: false);
                return null;

            default:
                return HandleCommonKey(key);
        }
    }

    private string? HandleViNormalKey(ConsoleKeyInfo key)
    {
        // Count prefix: accumulate digits
        if (key.KeyChar >= '1' && key.KeyChar <= '9' && !_viCountActive)
        {
            _viCount = key.KeyChar - '0';
            _viCountActive = true;
            return null;
        }
        if (key.KeyChar >= '0' && key.KeyChar <= '9' && _viCountActive)
        {
            _viCount = _viCount * 10 + (key.KeyChar - '0');
            return null;
        }

        int count = _viCountActive ? _viCount : 1;
        _viCount = 0;
        _viCountActive = false;

        // Operator+motion state machine: if an operator (d/c/y) is pending,
        // this keystroke is the motion — compute range and apply.
        if (_pendingOperator != '\0')
        {
            var op = _pendingOperator;
            var opCount = _pendingCount;
            _pendingOperator = '\0';
            _pendingCount = 0;
            ApplyOperatorMotion(op, key, opCount * count);
            return null;
        }

        switch (key.KeyChar)
        {
            // -- Movement (with count support) --
            case 'h':
                for (int i = 0; i < count && _cursor > 0; i++) _cursor--;
                SetCursorPos();
                return null;
            case 'l':
                for (int i = 0; i < count && _cursor < _buffer.Count - 1; i++) _cursor++;
                SetCursorPos();
                return null;
            case '0':
            case '^':
                _cursor = 0;
                SetCursorPos();
                return null;
            case '$':
                _cursor = Math.Max(0, _buffer.Count - 1);
                SetCursorPos();
                return null;
            case 'w':
                for (int i = 0; i < count; i++)
                {
                    _cursor = FindWordBoundaryRight(_buffer, _cursor);
                    if (_cursor >= _buffer.Count) { _cursor = Math.Max(0, _buffer.Count - 1); break; }
                }
                SetCursorPos();
                return null;
            case 'b':
                for (int i = 0; i < count; i++)
                    _cursor = FindWordBoundaryLeft(_buffer, _cursor);
                SetCursorPos();
                return null;
            case 'e':
                for (int i = 0; i < count; i++)
                    _cursor = FindWordEnd(_buffer, _cursor);
                SetCursorPos();
                return null;

            // -- Find char (f/F/t/T) --
            case 'f':
            case 'F':
            case 't':
            case 'T':
                HandleFindChar(key.KeyChar, count);
                return null;
            case ';': // Repeat last find
                RepeatFindChar(count);
                return null;

            // -- Operators (wait for motion) --
            case 'd':
            case 'c':
            case 'y':
                _pendingOperator = key.KeyChar;
                _pendingCount = count;
                return null;

            // -- Enter insert mode (with undo snapshot) --
            case 'i':
                PushUndo();
                _viMode = ViMode.Insert;
                SetCursorShape(insert: true);
                return null;
            case 'a':
                PushUndo();
                if (_buffer.Count > 0) _cursor++;
                _viMode = ViMode.Insert;
                SetCursorPos();
                SetCursorShape(insert: true);
                return null;
            case 'I':
                PushUndo();
                _cursor = 0;
                _viMode = ViMode.Insert;
                SetCursorPos();
                SetCursorShape(insert: true);
                return null;
            case 'A':
                PushUndo();
                _cursor = _buffer.Count;
                _viMode = ViMode.Insert;
                SetCursorPos();
                SetCursorShape(insert: true);
                return null;

            // -- Editing (with count, undo, yank) --
            case 'x':
                if (_buffer.Count > 0)
                {
                    PushUndo();
                    var xChars = new System.Text.StringBuilder();
                    for (int i = 0; i < count && _cursor < _buffer.Count; i++)
                    {
                        xChars.Append(_buffer[_cursor]);
                        _buffer.RemoveAt(_cursor);
                    }
                    _yankBuffer = xChars.ToString();
                    if (_cursor >= _buffer.Count && _cursor > 0) _cursor--;
                    Redraw();
                }
                return null;
            case 'X':
                if (_cursor > 0)
                {
                    PushUndo();
                    var xbChars = new System.Text.StringBuilder();
                    for (int i = 0; i < count && _cursor > 0; i++)
                    {
                        xbChars.Insert(0, _buffer[_cursor - 1]);
                        _buffer.RemoveAt(_cursor - 1);
                        _cursor--;
                    }
                    _yankBuffer = xbChars.ToString();
                    Redraw();
                }
                return null;
            case 'D':
                if (_cursor < _buffer.Count)
                {
                    PushUndo();
                    _yankBuffer = new string(_buffer.GetRange(_cursor, _buffer.Count - _cursor).ToArray());
                    _buffer.RemoveRange(_cursor, _buffer.Count - _cursor);
                    if (_cursor > 0) _cursor--;
                    Redraw();
                }
                return null;
            case 'C':
                PushUndo();
                if (_cursor < _buffer.Count)
                {
                    _yankBuffer = new string(_buffer.GetRange(_cursor, _buffer.Count - _cursor).ToArray());
                    _buffer.RemoveRange(_cursor, _buffer.Count - _cursor);
                }
                _viMode = ViMode.Insert;
                Redraw();
                SetCursorShape(insert: true);
                return null;
            case 'S':
                PushUndo();
                _yankBuffer = new string(_buffer.ToArray());
                _buffer.Clear();
                _cursor = 0;
                _viMode = ViMode.Insert;
                Redraw();
                SetCursorShape(insert: true);
                return null;
            case 's':
                if (_buffer.Count > 0)
                {
                    PushUndo();
                    var sChars = new System.Text.StringBuilder();
                    for (int i = 0; i < count && _cursor < _buffer.Count; i++)
                    {
                        sChars.Append(_buffer[_cursor]);
                        _buffer.RemoveAt(_cursor);
                    }
                    _yankBuffer = sChars.ToString();
                }
                _viMode = ViMode.Insert;
                Redraw();
                SetCursorShape(insert: true);
                return null;

            // -- Yank/Paste/Undo --
            case 'p':
                if (!string.IsNullOrEmpty(_yankBuffer))
                {
                    PushUndo();
                    int insertPos = Math.Min(_cursor + 1, _buffer.Count);
                    _buffer.InsertRange(insertPos, _yankBuffer);
                    _cursor = insertPos + _yankBuffer.Length - 1;
                    Redraw();
                }
                return null;
            case 'P':
                if (!string.IsNullOrEmpty(_yankBuffer))
                {
                    PushUndo();
                    _buffer.InsertRange(_cursor, _yankBuffer);
                    _cursor += _yankBuffer.Length - 1;
                    Redraw();
                }
                return null;
            case 'Y':
                if (_cursor < _buffer.Count)
                    _yankBuffer = new string(_buffer.GetRange(_cursor, _buffer.Count - _cursor).ToArray());
                return null;
            case 'u':
                if (_undoStack.Count > 0)
                {
                    var (buf, cur) = _undoStack.Pop();
                    _buffer = buf;
                    _cursor = Math.Min(cur, Math.Max(0, _buffer.Count - 1));
                    Redraw();
                }
                return null;

            // -- Replace character --
            case 'r':
            {
                var rKey = Console.ReadKey(intercept: true);
                if (rKey.KeyChar >= 32 && _cursor < _buffer.Count)
                {
                    PushUndo();
                    _buffer[_cursor] = rKey.KeyChar;
                    Redraw();
                }
                return null;
            }

            default:
                break;
        }

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                ClearSuggestionDisplay();
                Console.WriteLine();
                return new string(_buffer.ToArray());

            case ConsoleKey.UpArrow:
                HistoryUp();
                return null;
            case ConsoleKey.DownArrow:
                HistoryDown();
                return null;

            case ConsoleKey.RightArrow:
                if (_suggestion != null && _cursor >= Math.Max(0, _buffer.Count - 1))
                {
                    AcceptSuggestion();
                    return null;
                }
                if (_cursor < _buffer.Count - 1) _cursor++;
                SetCursorPos();
                return null;

            case ConsoleKey.D when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                if (_buffer.Count == 0) { Console.WriteLine(); return "\x04"; }
                return null;
            case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                Console.WriteLine("^C");
                return "";
        }

        // j/k for history in normal mode
        if (key.KeyChar == 'j') { HistoryDown(); return null; }
        if (key.KeyChar == 'k') { HistoryUp(); return null; }

        return null;
    }

    private void HandleFindChar(char cmd, int count)
    {
        // Read the next character to find
        var nextKey = Console.ReadKey(intercept: true);
        var target = nextKey.KeyChar;
        if (target < 32) return; // Not a printable char

        _lastFindChar = target;
        _lastFindForward = cmd == 'f' || cmd == 't';
        bool isTill = cmd == 't' || cmd == 'T';

        for (int i = 0; i < count; i++)
            DoFindChar(target, _lastFindForward, isTill);
    }

    private void RepeatFindChar(int count)
    {
        if (_lastFindChar == 0) return;
        bool isTill = false; // ; always does f-style repeat
        for (int i = 0; i < count; i++)
            DoFindChar(_lastFindChar, _lastFindForward, isTill);
    }

    private void DoFindChar(char target, bool forward, bool till)
    {
        if (forward)
        {
            for (int pos = _cursor + 1; pos < _buffer.Count; pos++)
            {
                if (_buffer[pos] == target)
                {
                    _cursor = till ? pos - 1 : pos;
                    SetCursorPos();
                    return;
                }
            }
        }
        else
        {
            for (int pos = _cursor - 1; pos >= 0; pos--)
            {
                if (_buffer[pos] == target)
                {
                    _cursor = till ? pos + 1 : pos;
                    SetCursorPos();
                    return;
                }
            }
        }
    }

    // ── Vi Operator+Motion ─────────────────────────────────────────────

    private void PushUndo()
    {
        _undoStack.Push((new List<char>(_buffer), _cursor));
    }

    private void ApplyOperatorMotion(char op, ConsoleKeyInfo key, int count)
    {
        // Doubled operator (dd, cc, yy) → whole line
        if (key.KeyChar == op)
        {
            PushUndo();
            _yankBuffer = new string(_buffer.ToArray());
            if (op == 'y')
            {
                _cursor = 0;
                SetCursorPos();
            }
            else
            {
                _buffer.Clear();
                _cursor = 0;
                if (op == 'c') { _viMode = ViMode.Insert; SetCursorShape(insert: true); }
                Redraw();
            }
            return;
        }

        // Compute the range the motion covers
        var range = ComputeMotionRange(key, count);
        if (range == null) return;
        var (start, end) = range.Value;
        if (start == end) return;

        PushUndo();
        _yankBuffer = new string(_buffer.GetRange(start, end - start).ToArray());

        switch (op)
        {
            case 'd':
                _buffer.RemoveRange(start, end - start);
                _cursor = Math.Min(start, Math.Max(0, _buffer.Count - 1));
                Redraw();
                break;
            case 'c':
                _buffer.RemoveRange(start, end - start);
                _cursor = start;
                _viMode = ViMode.Insert;
                Redraw();
                SetCursorShape(insert: true);
                break;
            case 'y':
                _cursor = start;
                SetCursorPos();
                break;
        }
    }

    private (int start, int end)? ComputeMotionRange(ConsoleKeyInfo key, int count)
    {
        int from = _cursor;
        int to;

        switch (key.KeyChar)
        {
            case 'w':
                to = from;
                for (int i = 0; i < count; i++)
                    to = FindWordBoundaryRight(_buffer, to);
                return (from, Math.Min(to, _buffer.Count));

            case 'b':
                to = from;
                for (int i = 0; i < count; i++)
                    to = FindWordBoundaryLeft(_buffer, to);
                return (Math.Min(to, from), Math.Max(to, from));

            case 'e':
                to = from;
                for (int i = 0; i < count; i++)
                    to = FindWordEnd(_buffer, to);
                return (from, Math.Min(to + 1, _buffer.Count));

            case '$':
                return (from, _buffer.Count);

            case '0':
            case '^':
                return (0, from);

            case 'h':
                to = Math.Max(0, from - count);
                return (to, from);

            case 'l':
                to = Math.Min(_buffer.Count, from + count);
                return (from, to);

            case 'f':
            case 't':
            {
                var fKey = Console.ReadKey(intercept: true);
                if (fKey.KeyChar < 32) return null;
                to = from;
                for (int i = 0; i < count; i++)
                {
                    int pos = to + 1;
                    while (pos < _buffer.Count && _buffer[pos] != fKey.KeyChar) pos++;
                    if (pos < _buffer.Count) to = pos;
                    else return null;
                }
                int rangeEnd = key.KeyChar == 't' ? to : to + 1;
                return (from, Math.Min(rangeEnd, _buffer.Count));
            }

            default:
                return null;
        }
    }

    // ── Emacs Mode ───────────────────────────────────────────────────────

    private string? HandleEmacsKey(ConsoleKeyInfo key)
    {
        return HandleCommonKey(key);
    }

    // ── Common Key Handling ──────────────────────────────────────────────

    private string? HandleCommonKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                ClearSuggestionDisplay();
                Console.WriteLine();
                return new string(_buffer.ToArray());

            case ConsoleKey.Tab:
                HandleTab();
                return null;

            case ConsoleKey.Backspace:
                if (_cursor > 0)
                {
                    _buffer.RemoveAt(_cursor - 1);
                    _cursor--;
                    Redraw();
                }
                return null;

            case ConsoleKey.Delete:
                if (_cursor < _buffer.Count)
                {
                    _buffer.RemoveAt(_cursor);
                    Redraw();
                }
                return null;

            case ConsoleKey.LeftArrow:
                if (key.Modifiers.HasFlag(ConsoleModifiers.Alt))
                    _cursor = FindWordBoundaryLeft(_buffer, _cursor);
                else if (_cursor > 0)
                    _cursor--;
                SetCursorPos();
                return null;

            case ConsoleKey.RightArrow:
                if (key.Modifiers.HasFlag(ConsoleModifiers.Alt))
                    _cursor = FindWordBoundaryRight(_buffer, _cursor);
                else if (_cursor < _buffer.Count)
                    _cursor++;
                else if (_suggestion != null)
                {
                    AcceptSuggestion();
                    return null;
                }
                SetCursorPos();
                return null;

            case ConsoleKey.Home:
                _cursor = 0;
                SetCursorPos();
                return null;

            case ConsoleKey.End:
                if (_cursor == _buffer.Count && _suggestion != null)
                {
                    AcceptSuggestion();
                    return null;
                }
                _cursor = _buffer.Count;
                SetCursorPos();
                return null;

            case ConsoleKey.A when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                _cursor = 0;
                SetCursorPos();
                return null;

            case ConsoleKey.E when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                _cursor = _buffer.Count;
                SetCursorPos();
                return null;

            case ConsoleKey.UpArrow:
                HistoryUp();
                return null;

            case ConsoleKey.DownArrow:
                HistoryDown();
                return null;

            case ConsoleKey.D when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                if (_buffer.Count == 0) { Console.WriteLine(); return "\x04"; }
                return null;

            case ConsoleKey.U when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                if (_cursor > 0)
                {
                    _buffer.RemoveRange(0, _cursor);
                    _cursor = 0;
                    Redraw();
                }
                return null;

            case ConsoleKey.K when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                if (_cursor < _buffer.Count)
                {
                    _buffer.RemoveRange(_cursor, _buffer.Count - _cursor);
                    Redraw();
                }
                return null;

            case ConsoleKey.W when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                if (_cursor > 0)
                {
                    int wordStart = FindWordBoundaryLeft(_buffer, _cursor);
                    _buffer.RemoveRange(wordStart, _cursor - wordStart);
                    _cursor = wordStart;
                    Redraw();
                }
                return null;

            case ConsoleKey.R when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                HandleReverseSearch();
                return null;

            case ConsoleKey.L when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                Console.Clear();
                _startLeft = 0;
                _startTop = 0;
                Redraw();
                return null;

            case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                Console.WriteLine("^C");
                return "";

            default:
                if (key.KeyChar >= 32)
                {
                    _buffer.Insert(_cursor, key.KeyChar);
                    _cursor++;
                    Redraw();
                }
                return null;
        }
    }

    // ── Tab Completion ───────────────────────────────────────────────────

    private void HandleTab()
    {
        // In vi mode, Tab accepts the autosuggestion when one is visible
        // and cursor is at end of input. This keeps hands on home row —
        // the primary ergonomic advantage of vi mode.
        // In emacs mode, Tab always does completion (use → or End to accept suggestions).
        if (Mode == EditMode.Vi && _suggestion != null && _cursor == _buffer.Count)
        {
            AcceptSuggestion();
            return;
        }

        if (CompleteHandler == null) return;

        var input = new string(_buffer.ToArray());
        var result = CompleteHandler(input, _cursor);

        if (result == null)
        {
            // No completions — show list if available
            ShowCompletionsHandler?.Invoke();
            return;
        }

        var (newInput, newCursor) = result.Value;
        _buffer.Clear();
        _buffer.AddRange(newInput);
        _cursor = Math.Min(newCursor, _buffer.Count);
        Redraw();
    }

    // ── Reverse Search (Ctrl+R) ──────────────────────────────────────────

    private void HandleReverseSearch()
    {
        var searchBuffer = new List<char>();
        int matchIndex = -1;

        // Save current state
        var savedBuffer = new List<char>(_buffer);
        var savedCursor = _cursor;

        void UpdateSearchDisplay(string? match)
        {
            // Clear current line and show search prompt
            Console.SetCursorPosition(0, _startTop);
            int width;
            try { width = Console.WindowWidth; }
            catch { width = 120; }
            Console.Write(new string(' ', width));
            Console.SetCursorPosition(0, _startTop);

            Console.ForegroundColor = Theme.Current.Muted;
            Console.Write("(reverse-i-search)`");
            Console.ForegroundColor = Theme.Current.SearchQuery;
            Console.Write(new string(searchBuffer.ToArray()));
            Console.ForegroundColor = Theme.Current.Muted;
            Console.Write("': ");
            Console.ResetColor();

            if (match != null)
                Console.Write(match);

            _startLeft = Console.CursorLeft;
        }

        UpdateSearchDisplay(null);

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Escape)
            {
                // Cancel — restore original
                _buffer = savedBuffer;
                _cursor = savedCursor;

                // Redraw prompt
                Console.SetCursorPosition(0, _startTop);
                int width2;
                try { width2 = Console.WindowWidth; }
                catch { width2 = 120; }
                Console.Write(new string(' ', width2));
                Console.SetCursorPosition(0, _startTop);

                // Caller will redraw prompt
                _startLeft = 0;
                Redraw();
                return;
            }

            if (key.Key == ConsoleKey.Enter)
            {
                // Accept current match — put it in the buffer
                if (matchIndex >= 0)
                {
                    _buffer.Clear();
                    _buffer.AddRange(_history[matchIndex]);
                    _cursor = _buffer.Count;
                }

                Console.SetCursorPosition(0, _startTop);
                int width3;
                try { width3 = Console.WindowWidth; }
                catch { width3 = 120; }
                Console.Write(new string(' ', width3));
                Console.SetCursorPosition(0, _startTop);
                _startLeft = 0;
                Redraw();
                return;
            }

            if (key.Key == ConsoleKey.R && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                // Find next match (further back in history)
                if (matchIndex > 0)
                {
                    var searchStr = new string(searchBuffer.ToArray());
                    for (int i = matchIndex - 1; i >= 0; i--)
                    {
                        if (_history[i].Contains(searchStr, StringComparison.OrdinalIgnoreCase))
                        {
                            matchIndex = i;
                            _buffer.Clear();
                            _buffer.AddRange(_history[i]);
                            _cursor = _buffer.Count;
                            UpdateSearchDisplay(_history[i]);
                            break;
                        }
                    }
                }
                continue;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (searchBuffer.Count > 0)
                    searchBuffer.RemoveAt(searchBuffer.Count - 1);
            }
            else if (key.KeyChar >= 32)
            {
                searchBuffer.Add(key.KeyChar);
            }
            else
            {
                continue;
            }

            // Search history for match
            var query = new string(searchBuffer.ToArray());
            matchIndex = -1;
            for (int i = _history.Count - 1; i >= 0; i--)
            {
                if (_history[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    matchIndex = i;
                    _buffer.Clear();
                    _buffer.AddRange(_history[i]);
                    _cursor = _buffer.Count;
                    break;
                }
            }

            UpdateSearchDisplay(matchIndex >= 0 ? _history[matchIndex] : null);
        }
    }

    // ── History ──────────────────────────────────────────────────────────

    private void HistoryUp()
    {
        if (_historyIndex > 0)
        {
            if (_historyIndex == _history.Count)
                _savedInput = new string(_buffer.ToArray());
            _historyIndex--;
            ReplaceBuffer(_history[_historyIndex]);
        }
    }

    private void HistoryDown()
    {
        if (_historyIndex < _history.Count)
        {
            _historyIndex++;
            ReplaceBuffer(_historyIndex == _history.Count
                ? _savedInput ?? ""
                : _history[_historyIndex]);
        }
    }

    // ── Drawing ──────────────────────────────────────────────────────────

    private void Redraw()
    {
        // Update autosuggestion
        UpdateSuggestion();

        Console.SetCursorPosition(_startLeft, _startTop);
        var text = new string(_buffer.ToArray());

        if (Highlighter != null)
        {
            Console.Write(Highlighter.Colorize(text));
            Console.ResetColor(); // Sync .NET state after ANSI codes
        }
        else
        {
            Console.Write(text);
        }

        // Draw autosuggestion ghost text (fish-style)
        int ghostLen = 0;
        if (_suggestion != null && _cursor == _buffer.Count)
        {
            Console.Write(Theme.Current.AnsiSuggestion);
            Console.Write(_suggestion);
            Console.Write(Theme.Current.AnsiReset);
            ghostLen = _suggestion.Length;
        }

        // Clear trailing chars (including old ghost text)
        try
        {
            int totalWritten = (_startLeft + _buffer.Count + ghostLen) % Console.WindowWidth;
            int clearCount = Console.WindowWidth - totalWritten;
            if (clearCount > 0 && clearCount <= Console.WindowWidth)
                Console.Write(new string(' ', clearCount));
        }
        catch { }

        SetCursorPos();
    }

    private void SetCursorPos()
    {
        int totalPos = _startLeft + _cursor;
        int width;
        try { width = Console.WindowWidth; }
        catch { width = 120; }

        int row = _startTop + totalPos / width;
        int col = totalPos % width;

        try { Console.SetCursorPosition(col, row); }
        catch { }
    }

    private void ReplaceBuffer(string newContent)
    {
        _buffer.Clear();
        _buffer.AddRange(newContent);
        _cursor = _buffer.Count;
        Redraw();
    }

    // ── Autosuggestion ──────────────────────────────────────────────────

    private void UpdateSuggestion()
    {
        _suggestion = null;
        if (_buffer.Count == 0) return;
        if (_cursor != _buffer.Count) return; // Only suggest when cursor at end

        var currentInput = new string(_buffer.ToArray());

        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].StartsWith(currentInput, StringComparison.OrdinalIgnoreCase)
                && _history[i].Length > currentInput.Length)
            {
                _suggestion = _history[i][currentInput.Length..];
                return;
            }
        }
    }

    /// <summary>
    /// Erase ghost suggestion text from the display so it doesn't
    /// linger in scrollback when Enter is pressed.
    /// Cursor is already positioned at end of typed text (after last Redraw).
    /// ESC[J erases from cursor to end of display, handling wrapped ghosts too.
    /// </summary>
    private void ClearSuggestionDisplay()
    {
        if (_suggestion == null) return;
        _suggestion = null;
        Console.Write("\x1b[J");
    }

    private void AcceptSuggestion()
    {
        if (_suggestion == null) return;
        _buffer.AddRange(_suggestion);
        _suggestion = null;
        if (Mode == EditMode.Vi && _viMode == ViMode.Normal)
            _cursor = Math.Max(0, _buffer.Count - 1);
        else
            _cursor = _buffer.Count;
        Redraw();
    }

    // ── History Persistence ─────────────────────────────────────────────

    /// <summary>
    /// Load history from ~/.config/rush/history
    /// </summary>
    public void LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryPath))
            {
                var lines = File.ReadAllLines(HistoryPath);
                _history.Clear();
                _history.AddRange(lines.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(MaxHistory));
            }
        }
        catch { }
    }

    /// <summary>
    /// Save history to ~/.config/rush/history
    /// </summary>
    public void SaveHistory()
    {
        try
        {
            if (!Directory.Exists(HistoryDir))
                Directory.CreateDirectory(HistoryDir);
            File.WriteAllLines(HistoryPath, _history.TakeLast(MaxHistory));
        }
        catch { }
    }

    /// <summary>
    /// Replace the most recent history entry (used for bang expansion).
    /// </summary>
    public void ReplaceLastHistory(string replacement)
    {
        if (_history.Count > 0)
            _history[^1] = replacement;
    }

    /// <summary>Clear all history entries and persist the empty state.</summary>
    public void ClearHistory()
    {
        _history.Clear();
        _historyIndex = 0;
        SaveHistory();
    }

    // ── Cursor Shape ────────────────────────────────────────────────────

    private static void SetCursorShape(bool insert)
    {
        // DECSCUSR: 5 = blinking bar (insert), 1 = blinking block (normal)
        if (insert)
            Console.Write("\x1b[5 q");
        else
            Console.Write("\x1b[1 q");
    }

    // ── Word Movement ────────────────────────────────────────────────────

    private static int FindWordBoundaryLeft(List<char> buffer, int cursor)
    {
        if (cursor == 0) return 0;
        int pos = cursor - 1;
        while (pos > 0 && char.IsWhiteSpace(buffer[pos])) pos--;
        while (pos > 0 && !char.IsWhiteSpace(buffer[pos - 1])) pos--;
        return pos;
    }

    private static int FindWordBoundaryRight(List<char> buffer, int cursor)
    {
        if (cursor >= buffer.Count) return buffer.Count;
        int pos = cursor;
        while (pos < buffer.Count && !char.IsWhiteSpace(buffer[pos])) pos++;
        while (pos < buffer.Count && char.IsWhiteSpace(buffer[pos])) pos++;
        return pos;
    }

    private static int FindWordEnd(List<char> buffer, int cursor)
    {
        if (cursor >= buffer.Count - 1) return Math.Max(0, buffer.Count - 1);
        int pos = cursor + 1;
        while (pos < buffer.Count && char.IsWhiteSpace(buffer[pos])) pos++;
        while (pos < buffer.Count - 1 && !char.IsWhiteSpace(buffer[pos + 1])) pos++;
        return pos;
    }
}
