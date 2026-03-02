namespace Rush;

/// <summary>
/// Centralized color palette for the shell. Provides dark and light variants
/// with guaranteed contrast against the terminal background.
/// Set once at startup via Initialize(), readable everywhere via Current.
/// </summary>
public class Theme
{
    /// <summary>
    /// The active theme. Set by Initialize() at startup.
    /// </summary>
    public static Theme Current { get; private set; } = new Theme(isDark: true);

    public bool IsDark { get; }

    // ── ConsoleColor Properties (OutputRenderer, Program, Prompt, etc.) ──

    /// <summary>Banner text, help section headers.</summary>
    public ConsoleColor Banner { get; }

    /// <summary>Metadata, timing, autosuggestion prefix, verbose/debug output.</summary>
    public ConsoleColor Muted { get; }

    /// <summary>Highlighted text (alias names, etc.).</summary>
    public ConsoleColor Accent { get; }

    /// <summary>Error messages.</summary>
    public ConsoleColor Error { get; }

    /// <summary>Warning messages, "cd: no previous directory".</summary>
    public ConsoleColor Warning { get; }

    /// <summary>Prompt — current working directory.</summary>
    public ConsoleColor PromptPath { get; }

    /// <summary>Prompt — git branch name.</summary>
    public ConsoleColor PromptGitBranch { get; }

    /// <summary>Prompt — success indicator (>).</summary>
    public ConsoleColor PromptSuccess { get; }

    /// <summary>Prompt — failure indicator (✗).</summary>
    public ConsoleColor PromptFailed { get; }

    /// <summary>Prompt — [ROOT] indicator (forced, non-overridable).</summary>
    public ConsoleColor PromptRoot { get; }

    /// <summary>Prompt — time display (HH:mm).</summary>
    public ConsoleColor PromptTime { get; }

    /// <summary>Prompt — username.</summary>
    public ConsoleColor PromptUser { get; }

    /// <summary>Prompt — hostname (local session).</summary>
    public ConsoleColor PromptHost { get; }

    /// <summary>Prompt — hostname when in an SSH session (emphasized).</summary>
    public ConsoleColor PromptSshHost { get; }

    /// <summary>Prompt — git dirty indicator (*).</summary>
    public ConsoleColor PromptGitDirty { get; }

    /// <summary>Directory names in ls output, tab completion.</summary>
    public ConsoleColor Directory { get; }

    /// <summary>Executable files (.exe, .sh, etc.).</summary>
    public ConsoleColor Executable { get; }

    /// <summary>Archive files (.zip, .tar, etc.).</summary>
    public ConsoleColor Archive { get; }

    /// <summary>Image files (.png, .jpg, etc.).</summary>
    public ConsoleColor Image { get; }

    /// <summary>Config files (.json, .yaml, etc.).</summary>
    public ConsoleColor Config { get; }

    /// <summary>Documentation files (.md, .txt, etc.).</summary>
    public ConsoleColor Document { get; }

    /// <summary>Source code files (.cs, .js, etc.).</summary>
    public ConsoleColor SourceCode { get; }

    /// <summary>Regular files (no special extension).</summary>
    public ConsoleColor RegularFile { get; }

    /// <summary>Table headers (column names).</summary>
    public ConsoleColor TableHeader { get; }

    /// <summary>Table separators (─── lines).</summary>
    public ConsoleColor Separator { get; }

    /// <summary>Metadata columns (PID, date, file size).</summary>
    public ConsoleColor Metadata { get; }

    /// <summary>Memory display in process output.</summary>
    public ConsoleColor Memory { get; }

    /// <summary>Search query text in Ctrl+R.</summary>
    public ConsoleColor SearchQuery { get; }

    /// <summary>Read permission (r) in ls -l output.</summary>
    public ConsoleColor PermRead { get; }

    /// <summary>Write permission (w) in ls -l output.</summary>
    public ConsoleColor PermWrite { get; }

    /// <summary>Execute permission (x/s/t) in ls -l output.</summary>
    public ConsoleColor PermExec { get; }

    // ── ANSI String Properties (SyntaxHighlighter) ──────────────────────

    /// <summary>ANSI reset sequence.</summary>
    public string AnsiReset { get; } = "\x1b[0m";

    /// <summary>Known commands (ls, ps, grep, etc.).</summary>
    public string AnsiKnownCommand { get; }

    /// <summary>Dot-notation file paths (.property).</summary>
    public string AnsiFilePath { get; }

    /// <summary>Command flags (-la, --verbose).</summary>
    public string AnsiFlag { get; }

    /// <summary>Quoted strings ('hello', "world").</summary>
    public string AnsiString { get; }

    /// <summary>Operators (&&, ||, >, >>).</summary>
    public string AnsiOperator { get; }

    /// <summary>Pipe character (|).</summary>
    public string AnsiPipe { get; }

    /// <summary>Unknown/unrecognized commands.</summary>
    public string AnsiUnknownCommand { get; }

    /// <summary>Bang expansion (!!, !$).</summary>
    public string AnsiBang { get; }

    /// <summary>Rush scripting keywords (if, for, def, etc.).</summary>
    public string AnsiKeyword { get; }

    /// <summary>Autosuggestion ghost text — must be clearly dimmer than any typed text.</summary>
    public string AnsiSuggestion { get; }

    // ── Construction ─────────────────────────────────────────────────────

    private Theme(bool isDark)
    {
        IsDark = isDark;

        if (isDark)
        {
            // Dark background — use bright/light colors for contrast
            Banner          = ConsoleColor.Cyan;
            Muted           = ConsoleColor.DarkGray;
            Accent          = ConsoleColor.White;
            Error           = ConsoleColor.Red;
            Warning         = ConsoleColor.Yellow;
            PromptPath      = ConsoleColor.Green;
            PromptGitBranch = ConsoleColor.DarkYellow;
            PromptGitDirty  = ConsoleColor.Yellow;
            PromptSuccess   = ConsoleColor.Green;
            PromptFailed    = ConsoleColor.Red;
            PromptRoot      = ConsoleColor.Red;
            PromptTime      = ConsoleColor.DarkGray;
            PromptUser      = ConsoleColor.Cyan;
            PromptHost      = ConsoleColor.DarkGray;
            PromptSshHost   = ConsoleColor.Yellow;
            Directory       = ConsoleColor.Blue;
            Executable      = ConsoleColor.Green;
            Archive         = ConsoleColor.Red;
            Image           = ConsoleColor.Magenta;
            Config          = ConsoleColor.Yellow;
            Document        = ConsoleColor.Cyan;
            SourceCode      = ConsoleColor.White;
            RegularFile     = ConsoleColor.Gray;
            TableHeader     = ConsoleColor.Cyan;
            Separator       = ConsoleColor.DarkGray;
            Metadata        = ConsoleColor.DarkGray;
            Memory          = ConsoleColor.DarkYellow;
            SearchQuery     = ConsoleColor.Yellow;
            PermRead        = ConsoleColor.Yellow;
            PermWrite       = ConsoleColor.Red;
            PermExec        = ConsoleColor.Green;

            AnsiKnownCommand   = "\x1b[36m";   // Cyan
            AnsiFilePath       = "\x1b[96m";   // BrightCyan
            AnsiFlag           = "\x1b[33m";   // Yellow
            AnsiString         = "\x1b[32m";   // Green
            AnsiOperator       = "\x1b[35m";   // Magenta
            AnsiPipe           = "\x1b[90m";   // DarkGray
            AnsiUnknownCommand = "\x1b[37m";   // White
            AnsiBang           = "\x1b[35m";   // Magenta
            AnsiKeyword        = "\x1b[38;5;204m"; // Pink/rose — distinct from cyan commands
            AnsiSuggestion     = "\x1b[38;5;240m"; // 256-color dark gray — dimmer than DarkGray/bright-black
        }
        else
        {
            // Light background — use dark/saturated colors for contrast
            Banner          = ConsoleColor.DarkCyan;
            Muted           = ConsoleColor.Gray;
            Accent          = ConsoleColor.Black;
            Error           = ConsoleColor.DarkRed;
            Warning         = ConsoleColor.DarkYellow;
            PromptPath      = ConsoleColor.DarkGreen;
            PromptGitBranch = ConsoleColor.DarkYellow;
            PromptGitDirty  = ConsoleColor.DarkYellow;
            PromptSuccess   = ConsoleColor.DarkGreen;
            PromptFailed    = ConsoleColor.DarkRed;
            PromptRoot      = ConsoleColor.DarkRed;
            PromptTime      = ConsoleColor.Gray;
            PromptUser      = ConsoleColor.DarkCyan;
            PromptHost      = ConsoleColor.Gray;
            PromptSshHost   = ConsoleColor.DarkYellow;
            Directory       = ConsoleColor.DarkBlue;
            Executable      = ConsoleColor.DarkGreen;
            Archive         = ConsoleColor.DarkRed;
            Image           = ConsoleColor.DarkMagenta;
            Config          = ConsoleColor.DarkYellow;
            Document        = ConsoleColor.DarkCyan;
            SourceCode      = ConsoleColor.DarkGray;
            RegularFile     = ConsoleColor.DarkGray;
            TableHeader     = ConsoleColor.DarkCyan;
            Separator       = ConsoleColor.Gray;
            Metadata        = ConsoleColor.Gray;
            Memory          = ConsoleColor.DarkYellow;
            SearchQuery     = ConsoleColor.DarkYellow;
            PermRead        = ConsoleColor.DarkYellow;
            PermWrite       = ConsoleColor.DarkRed;
            PermExec        = ConsoleColor.DarkGreen;

            AnsiKnownCommand   = "\x1b[34m";         // Blue
            AnsiFilePath       = "\x1b[34m";         // Blue
            AnsiFlag           = "\x1b[38;5;130m";   // dark orange (256-color)
            AnsiString         = "\x1b[32m";         // Green
            AnsiOperator       = "\x1b[35m";         // Magenta
            AnsiPipe           = "\x1b[90m";         // DarkGray
            AnsiUnknownCommand = "\x1b[30m";         // Black
            AnsiBang           = "\x1b[35m";         // Magenta
            AnsiKeyword        = "\x1b[38;5;161m";   // Dark pink
            AnsiSuggestion     = "\x1b[38;5;246m";   // 256-color mid-gray (~3:1 contrast on white)
        }
    }

    // ── Initialization ───────────────────────────────────────────────────

    /// <summary>
    /// Initialize the theme from explicit dark/light override or auto-detection.
    /// Call once at startup, before any output.
    /// </summary>
    /// <param name="isDarkOverride">
    /// true = force dark, false = force light, null = auto-detect.
    /// </param>
    public static void Initialize(bool? isDarkOverride = null)
    {
        bool isDark;
        if (isDarkOverride.HasValue)
        {
            isDark = isDarkOverride.Value;
        }
        else
        {
            var bg = TerminalBackground.Detect();
            isDark = bg.IsDark;
        }

        Current = new Theme(isDark);
    }
}
