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

    /// <summary>Current buffer contents (for edit-in-editor handoff).</summary>
    public string CurrentBuffer => new string(_buffer?.ToArray() ?? Array.Empty<char>());

    /// <summary>Hint text showing the keybinding for edit-in-editor.</summary>
    public string EditInEditorHint => Mode == EditMode.Vi
        ? "esc v → $EDITOR"
        : "C-x C-e → $EDITOR";

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

    // Vi search (/ and ?)
    private string _lastSearchQuery = "";
    private bool _lastSearchForward; // true = / (oldest→newest), false = ? (newest→oldest)
    private int _lastSearchMatchIndex = -1;

    // Vi yank/paste/undo
    private string _yankBuffer = "";
    private readonly Stack<(List<char> buffer, int cursor)> _undoStack = new();
    private char _pendingOperator; // 'd', 'c', 'y', or '\0'
    private int _pendingCount;

    // Vi U (undo-all) baseline — initial buffer state at ReadLine entry
    private (List<char> buffer, int cursor)? _lineBaseline;

    // Vi . (dot-repeat) — closure capturing last edit command
    private Action? _dotRepeatAction;

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
    private volatile bool _resized;

    // Autosuggestion (fish-style ghost text from history)
    private string? _suggestion;

    /// <summary>
    /// Get a read-only view of the history (for the 'history' built-in).
    /// </summary>
    public IReadOnlyList<string> History => _history;

    /// <summary>
    /// Signal that the terminal was resized (SIGWINCH).
    /// The next keypress will recapture the cursor position.
    /// </summary>
    public void NotifyResize() => _resized = true;

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
            _suggestion = null;
            _viCount = 0;
            _viCountActive = false;
            _lineBaseline = (new List<char>(_buffer), _cursor);

            if (Mode == EditMode.Vi)
            {
                _viMode = ViMode.Insert;
                SetCursorShape(insert: true);
            }

            while (true)
            {
                var key = Console.ReadKey(intercept: true);

                // Terminal resized while waiting — recapture cursor position
                if (_resized)
                {
                    _resized = false;
                    _startLeft = Console.CursorLeft;
                    _startTop = Console.CursorTop;
                }

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
                if (_cursor >= _buffer.Count - 1 && _suggestion != null)
                    AcceptSuggestion();
                else
                {
                    for (int i = 0; i < count && _cursor < _buffer.Count - 1; i++) _cursor++;
                    SetCursorPos();
                }
                return null;
            case '0':
            case '^':
                _cursor = 0;
                SetCursorPos();
                return null;
            case '$':
                if (_cursor >= Math.Max(0, _buffer.Count - 1) && _suggestion != null)
                    AcceptSuggestion();
                else
                {
                    _cursor = Math.Max(0, _buffer.Count - 1);
                    SetCursorPos();
                }
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
            case 'W':
                for (int i = 0; i < count; i++)
                {
                    _cursor = FindWORDBoundaryRight(_buffer, _cursor);
                    if (_cursor >= _buffer.Count) { _cursor = Math.Max(0, _buffer.Count - 1); break; }
                }
                SetCursorPos();
                return null;
            case 'B':
                for (int i = 0; i < count; i++)
                    _cursor = FindWORDBoundaryLeft(_buffer, _cursor);
                SetCursorPos();
                return null;
            case 'E':
                for (int i = 0; i < count; i++)
                    _cursor = FindWORDEnd(_buffer, _cursor);
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
            case ',': // Reverse repeat last find
                if (_lastFindChar != 0)
                {
                    _lastFindForward = !_lastFindForward;
                    RepeatFindChar(count);
                    _lastFindForward = !_lastFindForward; // restore for next ;
                }
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
                    int xCount = count;
                    _dotRepeatAction = () =>
                    {
                        if (_buffer.Count > 0)
                        {
                            PushUndo();
                            var sb = new System.Text.StringBuilder();
                            for (int i = 0; i < xCount && _cursor < _buffer.Count; i++)
                            {
                                sb.Append(_buffer[_cursor]);
                                _buffer.RemoveAt(_cursor);
                            }
                            _yankBuffer = sb.ToString();
                            if (_cursor >= _buffer.Count && _cursor > 0) _cursor--;
                            Redraw();
                        }
                    };
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
                    int bigXCount = count;
                    _dotRepeatAction = () =>
                    {
                        if (_cursor > 0)
                        {
                            PushUndo();
                            var sb = new System.Text.StringBuilder();
                            for (int i = 0; i < bigXCount && _cursor > 0; i++)
                            {
                                sb.Insert(0, _buffer[_cursor - 1]);
                                _buffer.RemoveAt(_cursor - 1);
                                _cursor--;
                            }
                            _yankBuffer = sb.ToString();
                            Redraw();
                        }
                    };
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
                    _dotRepeatAction = () =>
                    {
                        if (_cursor < _buffer.Count)
                        {
                            PushUndo();
                            _yankBuffer = new string(_buffer.GetRange(_cursor, _buffer.Count - _cursor).ToArray());
                            _buffer.RemoveRange(_cursor, _buffer.Count - _cursor);
                            if (_cursor > 0) _cursor--;
                            Redraw();
                        }
                    };
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
            case 'U': // Undo all — restore line to original state
                if (_lineBaseline != null)
                {
                    PushUndo();
                    var (baseBuf, baseCur) = _lineBaseline.Value;
                    _buffer = new List<char>(baseBuf);
                    _cursor = Math.Min(baseCur, Math.Max(0, _buffer.Count - 1));
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
                    _dotRepeatAction = () =>
                    {
                        if (_cursor < _buffer.Count)
                        {
                            PushUndo();
                            _buffer[_cursor] = rKey.KeyChar;
                            Redraw();
                        }
                    };
                }
                return null;
            }

            // -- Toggle case --
            case '~':
                if (_buffer.Count > 0)
                {
                    PushUndo();
                    int tildeCount = count;
                    for (int i = 0; i < tildeCount && _cursor < _buffer.Count; i++)
                    {
                        char ch = _buffer[_cursor];
                        _buffer[_cursor] = char.IsUpper(ch) ? char.ToLower(ch) : char.ToUpper(ch);
                        if (_cursor < _buffer.Count - 1) _cursor++;
                    }
                    Redraw();
                    _dotRepeatAction = () =>
                    {
                        if (_buffer.Count > 0)
                        {
                            PushUndo();
                            for (int i = 0; i < tildeCount && _cursor < _buffer.Count; i++)
                            {
                                char ch = _buffer[_cursor];
                                _buffer[_cursor] = char.IsUpper(ch) ? char.ToLower(ch) : char.ToUpper(ch);
                                if (_cursor < _buffer.Count - 1) _cursor++;
                            }
                            Redraw();
                        }
                    };
                }
                return null;

            // -- Comment line and submit --
            case '#':
                _buffer.Insert(0, '#');
                _buffer.Insert(1, ' ');
                Redraw();
                Console.WriteLine();
                return new string(_buffer.ToArray());

            // -- Dot-repeat last modification --
            case '.':
                _dotRepeatAction?.Invoke();
                return null;

            // -- Vi search --
            case '/':
                HandleViSearch(forward: true);
                return null;
            case '?':
                HandleViSearch(forward: false);
                return null;
            case 'n':
                if (!string.IsNullOrEmpty(_lastSearchQuery))
                    RepeatViSearch(_lastSearchForward);
                return null;
            case 'N':
                if (!string.IsNullOrEmpty(_lastSearchQuery))
                    RepeatViSearch(!_lastSearchForward);
                return null;

            // -- Edit in $EDITOR --
            case 'v':
                Console.WriteLine();
                return "\x16";  // sentinel: open in editor

            default:
                break;
        }

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                ClearGhostText();
                Console.WriteLine();
                return new string(_buffer.ToArray());

            case ConsoleKey.UpArrow:
                HistoryUp();
                // In vi normal mode, cursor must be ON last char, not past it
                if (_buffer.Count > 0 && _cursor >= _buffer.Count)
                    _cursor = _buffer.Count - 1;
                SetCursorPos();
                return null;
            case ConsoleKey.DownArrow:
                HistoryDown();
                if (_buffer.Count > 0 && _cursor >= _buffer.Count)
                    _cursor = _buffer.Count - 1;
                SetCursorPos();
                return null;

            case ConsoleKey.RightArrow:
                if (_cursor >= _buffer.Count - 1 && _suggestion != null)
                    AcceptSuggestion();
                else if (_cursor < _buffer.Count - 1)
                    _cursor++;
                SetCursorPos();
                return null;

            // Backspace in normal mode: delete char before cursor, stay in normal mode.
            // Matches GNU readline rl_vi_rubout: move left + delete, no mode change.
            case ConsoleKey.Backspace:
                if (_cursor > 0)
                {
                    PushUndo();
                    _buffer.RemoveAt(_cursor - 1);
                    _cursor--;
                    Redraw();
                }
                return null;

            case ConsoleKey.D when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                if (_buffer.Count == 0) { Console.WriteLine(); return "\x04"; }
                return null;
            case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                Console.WriteLine("^C");
                return "";
        }

        // j/k for history in normal mode
        if (key.KeyChar == 'j')
        {
            HistoryDown();
            if (_buffer.Count > 0 && _cursor >= _buffer.Count)
                _cursor = _buffer.Count - 1;
            SetCursorPos();
            return null;
        }
        if (key.KeyChar == 'k')
        {
            HistoryUp();
            if (_buffer.Count > 0 && _cursor >= _buffer.Count)
                _cursor = _buffer.Count - 1;
            SetCursorPos();
            return null;
        }

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
            if (op == 'd')
            {
                _dotRepeatAction = () =>
                {
                    PushUndo();
                    _yankBuffer = new string(_buffer.ToArray());
                    _buffer.Clear();
                    _cursor = 0;
                    Redraw();
                };
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
                // Capture dot-repeat for d+motion
                char motionChar = key.KeyChar;
                int motionCount = count;
                _dotRepeatAction = () =>
                {
                    var rng = ComputeMotionRange(new ConsoleKeyInfo(motionChar, 0, false, false, false), motionCount);
                    if (rng == null) return;
                    var (s, e) = rng.Value;
                    if (s == e) return;
                    PushUndo();
                    _yankBuffer = new string(_buffer.GetRange(s, e - s).ToArray());
                    _buffer.RemoveRange(s, e - s);
                    _cursor = Math.Min(s, Math.Max(0, _buffer.Count - 1));
                    Redraw();
                };
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

            case 'W':
                to = from;
                for (int i = 0; i < count; i++)
                    to = FindWORDBoundaryRight(_buffer, to);
                return (from, Math.Min(to, _buffer.Count));

            case 'B':
                to = from;
                for (int i = 0; i < count; i++)
                    to = FindWORDBoundaryLeft(_buffer, to);
                return (Math.Min(to, from), Math.Max(to, from));

            case 'E':
                to = from;
                for (int i = 0; i < count; i++)
                    to = FindWORDEnd(_buffer, to);
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
                ClearGhostText();
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
                    _cursor = FindWORDBoundaryLeft(_buffer, _cursor);
                else if (_cursor > 0)
                    _cursor--;
                SetCursorPos();
                return null;

            case ConsoleKey.RightArrow:
                if (key.Modifiers.HasFlag(ConsoleModifiers.Alt))
                {
                    if (_cursor == _buffer.Count && _suggestion != null)
                        AcceptSuggestionWord();
                    else
                        _cursor = FindWORDBoundaryRight(_buffer, _cursor);
                }
                else if (_cursor == _buffer.Count && _suggestion != null)
                    AcceptSuggestion();
                else if (_cursor < _buffer.Count)
                    _cursor++;
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
                if (_cursor == _buffer.Count && _suggestion != null)
                    AcceptSuggestion();
                else
                {
                    _cursor = _buffer.Count;
                    SetCursorPos();
                }
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

            case ConsoleKey.X when key.Modifiers.HasFlag(ConsoleModifiers.Control):
            {
                var nextKey = Console.ReadKey(intercept: true);
                if (nextKey.Key == ConsoleKey.E && nextKey.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    Console.WriteLine();
                    return "\x16";  // sentinel: open in editor
                }
                return null;  // Ctrl+X followed by something else — ignore
            }

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
        // Tab = filesystem/command completion only. Never accepts suggestions.
        // Suggestions are accepted via: Right-arrow, End, or `l` at EOL (vi mode).
        if (CompleteHandler == null) return;

        var input = new string(_buffer.ToArray());
        var result = CompleteHandler(input, _cursor);

        if (result != null)
        {
            var (newInput, newCursor) = result.Value;
            _buffer.Clear();
            _buffer.AddRange(newInput);
            _cursor = Math.Min(newCursor, _buffer.Count);
            Redraw();
            return;
        }

        // No single completion — show list if multiple exist
        ShowCompletionsHandler?.Invoke();
        // Recapture cursor position after completions redrew the prompt
        _startLeft = Console.CursorLeft;
        _startTop = Console.CursorTop;
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

    // ── Vi Search (/ and ?) ─────────────────────────────────────────────

    private void HandleViSearch(bool forward)
    {
        var searchBuffer = new List<char>();
        int matchIndex = -1;

        // Save current state
        var savedBuffer = new List<char>(_buffer);
        var savedCursor = _cursor;

        var prompt = forward ? "(/)'" : "(?)'";

        void UpdateDisplay(string? match)
        {
            Console.SetCursorPosition(0, _startTop);
            int width;
            try { width = Console.WindowWidth; }
            catch { width = 120; }
            Console.Write(new string(' ', width));
            Console.SetCursorPosition(0, _startTop);

            Console.ForegroundColor = Theme.Current.Muted;
            Console.Write(prompt);
            Console.ForegroundColor = Theme.Current.SearchQuery;
            Console.Write(new string(searchBuffer.ToArray()));
            Console.ForegroundColor = Theme.Current.Muted;
            Console.Write("': ");
            Console.ResetColor();

            if (match != null)
                Console.Write(match);

            _startLeft = Console.CursorLeft;
        }

        void DoSearch()
        {
            var query = new string(searchBuffer.ToArray());
            matchIndex = -1;

            if (forward)
            {
                // Search from oldest to newest
                for (int i = 0; i < _history.Count; i++)
                {
                    if (_history[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        matchIndex = i;
                        break;
                    }
                }
            }
            else
            {
                // Search from newest to oldest
                for (int i = _history.Count - 1; i >= 0; i--)
                {
                    if (_history[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        matchIndex = i;
                        break;
                    }
                }
            }

            if (matchIndex >= 0)
            {
                _buffer.Clear();
                _buffer.AddRange(_history[matchIndex]);
                _cursor = _buffer.Count;
            }
        }

        UpdateDisplay(null);

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Escape)
            {
                // Cancel — restore original
                _buffer = savedBuffer;
                _cursor = savedCursor;

                Console.SetCursorPosition(0, _startTop);
                int width2;
                try { width2 = Console.WindowWidth; }
                catch { width2 = 120; }
                Console.Write(new string(' ', width2));
                Console.SetCursorPosition(0, _startTop);
                _startLeft = 0;
                Redraw();
                return;
            }

            if (key.Key == ConsoleKey.Enter)
            {
                // Accept current match
                if (matchIndex >= 0)
                {
                    _buffer.Clear();
                    _buffer.AddRange(_history[matchIndex]);
                    _cursor = _buffer.Count;

                    // Save search state for n/N
                    _lastSearchQuery = new string(searchBuffer.ToArray());
                    _lastSearchForward = forward;
                    _lastSearchMatchIndex = matchIndex;
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

            // Cycle to next match with the same key that started the search
            if ((forward && key.KeyChar == '/') || (!forward && key.KeyChar == '?'))
            {
                if (matchIndex >= 0)
                {
                    var query = new string(searchBuffer.ToArray());
                    if (forward)
                    {
                        for (int i = matchIndex + 1; i < _history.Count; i++)
                        {
                            if (_history[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                            {
                                matchIndex = i;
                                _buffer.Clear();
                                _buffer.AddRange(_history[i]);
                                _cursor = _buffer.Count;
                                UpdateDisplay(_history[i]);
                                break;
                            }
                        }
                    }
                    else
                    {
                        for (int i = matchIndex - 1; i >= 0; i--)
                        {
                            if (_history[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                            {
                                matchIndex = i;
                                _buffer.Clear();
                                _buffer.AddRange(_history[i]);
                                _cursor = _buffer.Count;
                                UpdateDisplay(_history[i]);
                                break;
                            }
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

            DoSearch();
            UpdateDisplay(matchIndex >= 0 ? _history[matchIndex] : null);
        }
    }

    private void RepeatViSearch(bool forward)
    {
        if (string.IsNullOrEmpty(_lastSearchQuery)) return;

        int startFrom = _lastSearchMatchIndex;
        if (forward)
        {
            for (int i = startFrom + 1; i < _history.Count; i++)
            {
                if (_history[i].Contains(_lastSearchQuery, StringComparison.OrdinalIgnoreCase))
                {
                    _lastSearchMatchIndex = i;
                    ReplaceBuffer(_history[i]);
                    return;
                }
            }
        }
        else
        {
            for (int i = startFrom - 1; i >= 0; i--)
            {
                if (_history[i].Contains(_lastSearchQuery, StringComparison.OrdinalIgnoreCase))
                {
                    _lastSearchMatchIndex = i;
                    ReplaceBuffer(_history[i]);
                    return;
                }
            }
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

        // Ghost text (autosuggestion from history)
        string ghost = GetGhostText();
        int ghostLen = ghost.Length;
        if (ghostLen > 0)
        {
            Console.Write("\x1b[90m"); // dim gray
            Console.Write(ghost);
            Console.Write("\x1b[0m");  // reset
        }

        // Clear trailing chars
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

        // Bounds check: clamp row to valid buffer range
        try
        {
            var bufferHeight = Console.BufferHeight;
            if (row >= bufferHeight) row = bufferHeight - 1;
            if (row < 0) row = 0;
        }
        catch { }

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

    // ── Vi word classification ────────────────────────────────────────
    // Vi distinguishes 3 char classes: word (alnum/_), punctuation, whitespace.
    // w/b/e use class boundaries; W/B/E use whitespace-only boundaries (WORD).

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // ── w/b/e — char-class word boundaries (vi "word") ──────────────

    private static int FindWordBoundaryRight(List<char> buffer, int cursor)
    {
        if (cursor >= buffer.Count) return buffer.Count;
        int pos = cursor;
        // Skip current class
        if (pos < buffer.Count && IsWordChar(buffer[pos]))
            while (pos < buffer.Count && IsWordChar(buffer[pos])) pos++;
        else if (pos < buffer.Count && !char.IsWhiteSpace(buffer[pos]))
            while (pos < buffer.Count && !char.IsWhiteSpace(buffer[pos]) && !IsWordChar(buffer[pos])) pos++;
        // Skip whitespace
        while (pos < buffer.Count && char.IsWhiteSpace(buffer[pos])) pos++;
        return pos;
    }

    private static int FindWordBoundaryLeft(List<char> buffer, int cursor)
    {
        if (cursor == 0) return 0;
        int pos = cursor - 1;
        // Skip whitespace
        while (pos > 0 && char.IsWhiteSpace(buffer[pos])) pos--;
        // Skip current class backward
        if (pos >= 0 && IsWordChar(buffer[pos]))
            while (pos > 0 && IsWordChar(buffer[pos - 1])) pos--;
        else if (pos >= 0 && !char.IsWhiteSpace(buffer[pos]))
            while (pos > 0 && !char.IsWhiteSpace(buffer[pos - 1]) && !IsWordChar(buffer[pos - 1])) pos--;
        return pos;
    }

    private static int FindWordEnd(List<char> buffer, int cursor)
    {
        if (cursor >= buffer.Count - 1) return Math.Max(0, buffer.Count - 1);
        int pos = cursor + 1;
        // Skip whitespace
        while (pos < buffer.Count && char.IsWhiteSpace(buffer[pos])) pos++;
        // Skip current class forward to end
        if (pos < buffer.Count && IsWordChar(buffer[pos]))
            while (pos < buffer.Count - 1 && IsWordChar(buffer[pos + 1])) pos++;
        else if (pos < buffer.Count && !char.IsWhiteSpace(buffer[pos]))
            while (pos < buffer.Count - 1 && !char.IsWhiteSpace(buffer[pos + 1]) && !IsWordChar(buffer[pos + 1])) pos++;
        return pos;
    }

    // ── W/B/E — whitespace-only WORD boundaries (vi "WORD") ─────────

    private static int FindWORDBoundaryRight(List<char> buffer, int cursor)
    {
        if (cursor >= buffer.Count) return buffer.Count;
        int pos = cursor;
        while (pos < buffer.Count && !char.IsWhiteSpace(buffer[pos])) pos++;
        while (pos < buffer.Count && char.IsWhiteSpace(buffer[pos])) pos++;
        return pos;
    }

    private static int FindWORDBoundaryLeft(List<char> buffer, int cursor)
    {
        if (cursor == 0) return 0;
        int pos = cursor - 1;
        while (pos > 0 && char.IsWhiteSpace(buffer[pos])) pos--;
        while (pos > 0 && !char.IsWhiteSpace(buffer[pos - 1])) pos--;
        return pos;
    }

    private static int FindWORDEnd(List<char> buffer, int cursor)
    {
        if (cursor >= buffer.Count - 1) return Math.Max(0, buffer.Count - 1);
        int pos = cursor + 1;
        while (pos < buffer.Count && char.IsWhiteSpace(buffer[pos])) pos++;
        while (pos < buffer.Count - 1 && !char.IsWhiteSpace(buffer[pos + 1])) pos++;
        return pos;
    }

    // ── Autosuggestion (fish-style ghost text from history) ──────────

    /// <summary>
    /// Find the most recent history entry that starts with the given prefix
    /// and is strictly longer than it. Returns null if no match.
    /// </summary>
    internal static string? FindSuggestion(IReadOnlyList<string> history, string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return null;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Length > prefix.Length
                && history[i].StartsWith(prefix, StringComparison.Ordinal))
                return history[i];
        }
        return null;
    }

    private void UpdateSuggestion()
    {
        var prefix = new string(_buffer.ToArray());
        _suggestion = FindSuggestion(_history, prefix);
    }

    private string GetGhostText()
    {
        if (_suggestion == null || _cursor != _buffer.Count || _buffer.Count == 0)
            return "";
        if (_suggestion.Length <= _buffer.Count)
            return "";
        return _suggestion[_buffer.Count..];
    }

    /// <summary>
    /// Erase ghost text from screen before Enter/newline so it doesn't linger.
    /// </summary>
    private void ClearGhostText()
    {
        var ghost = GetGhostText();
        if (ghost.Length > 0)
        {
            _suggestion = null;
            Redraw();
        }
    }

    private void AcceptSuggestion()
    {
        if (_suggestion == null || _suggestion.Length <= _buffer.Count) return;
        _buffer.AddRange(_suggestion[_buffer.Count..]);
        _cursor = _buffer.Count;
        _suggestion = null;
        Redraw();
    }

    private void AcceptSuggestionWord()
    {
        if (_suggestion == null || _suggestion.Length <= _buffer.Count) return;

        // Find next WORD boundary in the suggestion text
        int start = _buffer.Count;
        int pos = start;
        // Skip non-whitespace (the current word)
        while (pos < _suggestion.Length && !char.IsWhiteSpace(_suggestion[pos])) pos++;
        // Skip whitespace after the word
        while (pos < _suggestion.Length && char.IsWhiteSpace(_suggestion[pos])) pos++;

        // Append from start to pos
        for (int i = start; i < pos; i++)
            _buffer.Add(_suggestion[i]);
        _cursor = _buffer.Count;

        // Re-evaluate suggestion for remaining ghost text
        UpdateSuggestion();
        Redraw();
    }
}
