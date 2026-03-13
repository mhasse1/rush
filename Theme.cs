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
    public ConsoleColor Banner { get; private set; }

    /// <summary>Metadata, timing, verbose/debug output.</summary>
    public ConsoleColor Muted { get; private set; }

    /// <summary>Highlighted text (alias names, etc.).</summary>
    public ConsoleColor Accent { get; private set; }

    /// <summary>Error messages.</summary>
    public ConsoleColor Error { get; private set; }

    /// <summary>Warning messages, "cd: no previous directory".</summary>
    public ConsoleColor Warning { get; private set; }

    /// <summary>Prompt — current working directory.</summary>
    public ConsoleColor PromptPath { get; private set; }

    /// <summary>Prompt — git branch name.</summary>
    public ConsoleColor PromptGitBranch { get; private set; }

    /// <summary>Prompt — success indicator (>).</summary>
    public ConsoleColor PromptSuccess { get; private set; }

    /// <summary>Prompt — failure indicator (✗).</summary>
    public ConsoleColor PromptFailed { get; private set; }

    /// <summary>Prompt — [ROOT] indicator (forced, non-overridable).</summary>
    public ConsoleColor PromptRoot { get; private set; }

    /// <summary>Prompt — time display (HH:mm).</summary>
    public ConsoleColor PromptTime { get; private set; }

    /// <summary>Prompt — username.</summary>
    public ConsoleColor PromptUser { get; private set; }

    /// <summary>Prompt — hostname (local session).</summary>
    public ConsoleColor PromptHost { get; private set; }

    /// <summary>Prompt — hostname when in an SSH session (emphasized).</summary>
    public ConsoleColor PromptSshHost { get; private set; }

    /// <summary>Prompt — git dirty indicator (*).</summary>
    public ConsoleColor PromptGitDirty { get; private set; }

    /// <summary>Directory names in ls output, tab completion.</summary>
    public ConsoleColor Directory { get; private set; }

    /// <summary>Executable files (.exe, .sh, etc.).</summary>
    public ConsoleColor Executable { get; private set; }

    /// <summary>Archive files (.zip, .tar, etc.).</summary>
    public ConsoleColor Archive { get; private set; }

    /// <summary>Image files (.png, .jpg, etc.).</summary>
    public ConsoleColor Image { get; private set; }

    /// <summary>Config files (.json, .yaml, etc.).</summary>
    public ConsoleColor Config { get; private set; }

    /// <summary>Documentation files (.md, .txt, etc.).</summary>
    public ConsoleColor Document { get; private set; }

    /// <summary>Source code files (.cs, .js, etc.).</summary>
    public ConsoleColor SourceCode { get; private set; }

    /// <summary>Regular files (no special extension).</summary>
    public ConsoleColor RegularFile { get; private set; }

    /// <summary>Table headers (column names).</summary>
    public ConsoleColor TableHeader { get; private set; }

    /// <summary>Table separators (─── lines).</summary>
    public ConsoleColor Separator { get; private set; }

    /// <summary>Metadata columns (PID, date, file size).</summary>
    public ConsoleColor Metadata { get; private set; }

    /// <summary>Memory display in process output.</summary>
    public ConsoleColor Memory { get; private set; }

    /// <summary>Search query text in Ctrl+R.</summary>
    public ConsoleColor SearchQuery { get; private set; }

    /// <summary>Read permission (r) in ls -l output.</summary>
    public ConsoleColor PermRead { get; private set; }

    /// <summary>Write permission (w) in ls -l output.</summary>
    public ConsoleColor PermWrite { get; private set; }

    /// <summary>Execute permission (x/s/t) in ls -l output.</summary>
    public ConsoleColor PermExec { get; private set; }

    // ── ANSI String Properties (SyntaxHighlighter) ──────────────────────

    /// <summary>ANSI reset sequence.</summary>
    public string AnsiReset { get; } = "\x1b[0m";

    /// <summary>Known commands (ls, ps, grep, etc.).</summary>
    public string AnsiKnownCommand { get; private set; }

    /// <summary>Dot-notation file paths (.property).</summary>
    public string AnsiFilePath { get; private set; }

    /// <summary>Command flags (-la, --verbose).</summary>
    public string AnsiFlag { get; private set; }

    /// <summary>Quoted strings ('hello', "world").</summary>
    public string AnsiString { get; private set; }

    /// <summary>Operators (&&, ||, >, >>).</summary>
    public string AnsiOperator { get; private set; }

    /// <summary>Pipe character (|).</summary>
    public string AnsiPipe { get; private set; }

    /// <summary>Unknown/unrecognized commands.</summary>
    public string AnsiUnknownCommand { get; private set; }

    /// <summary>Bang expansion (!!, !$).</summary>
    public string AnsiBang { get; private set; }

    /// <summary>Rush scripting keywords (if, for, def, etc.).</summary>
    public string AnsiKeyword { get; private set; }

    // ── Construction ─────────────────────────────────────────────────────

    private Theme(bool isDark, double bgR = -1, double bgG = -1, double bgB = -1)
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
        }

        // ── Contrast validation pass ──────────────────────────────────
        // When actual background RGB is available (from OSC 11), validate
        // every color against it and swap any that fail the minimum contrast.
        if (bgR >= 0)
        {
            double bgLum = TerminalBackground.RelativeLuminance(bgR, bgG, bgB);

            // ConsoleColor properties
            Banner          = EnsureContrast(Banner, bgLum, isDark);
            Muted           = EnsureContrast(Muted, bgLum, isDark);
            Accent          = EnsureContrast(Accent, bgLum, isDark);
            Error           = EnsureContrast(Error, bgLum, isDark);
            Warning         = EnsureContrast(Warning, bgLum, isDark);
            PromptPath      = EnsureContrast(PromptPath, bgLum, isDark);
            PromptGitBranch = EnsureContrast(PromptGitBranch, bgLum, isDark);
            PromptGitDirty  = EnsureContrast(PromptGitDirty, bgLum, isDark);
            PromptSuccess   = EnsureContrast(PromptSuccess, bgLum, isDark);
            PromptFailed    = EnsureContrast(PromptFailed, bgLum, isDark);
            PromptRoot      = EnsureContrast(PromptRoot, bgLum, isDark);
            PromptTime      = EnsureContrast(PromptTime, bgLum, isDark);
            PromptUser      = EnsureContrast(PromptUser, bgLum, isDark);
            PromptHost      = EnsureContrast(PromptHost, bgLum, isDark);
            PromptSshHost   = EnsureContrast(PromptSshHost, bgLum, isDark);
            Directory       = EnsureContrast(Directory, bgLum, isDark);
            Executable      = EnsureContrast(Executable, bgLum, isDark);
            Archive         = EnsureContrast(Archive, bgLum, isDark);
            Image           = EnsureContrast(Image, bgLum, isDark);
            Config          = EnsureContrast(Config, bgLum, isDark);
            Document        = EnsureContrast(Document, bgLum, isDark);
            SourceCode      = EnsureContrast(SourceCode, bgLum, isDark);
            RegularFile     = EnsureContrast(RegularFile, bgLum, isDark);
            TableHeader     = EnsureContrast(TableHeader, bgLum, isDark);
            Separator       = EnsureContrast(Separator, bgLum, isDark);
            Metadata        = EnsureContrast(Metadata, bgLum, isDark);
            Memory          = EnsureContrast(Memory, bgLum, isDark);
            SearchQuery     = EnsureContrast(SearchQuery, bgLum, isDark);
            PermRead        = EnsureContrast(PermRead, bgLum, isDark);
            PermWrite       = EnsureContrast(PermWrite, bgLum, isDark);
            PermExec        = EnsureContrast(PermExec, bgLum, isDark);

            // ANSI string colors (SyntaxHighlighter)
            AnsiKnownCommand   = EnsureAnsiContrast(AnsiKnownCommand, bgLum, isDark);
            AnsiFilePath       = EnsureAnsiContrast(AnsiFilePath, bgLum, isDark);
            AnsiFlag           = EnsureAnsiContrast(AnsiFlag, bgLum, isDark);
            AnsiString         = EnsureAnsiContrast(AnsiString, bgLum, isDark);
            AnsiOperator       = EnsureAnsiContrast(AnsiOperator, bgLum, isDark);
            AnsiPipe           = EnsureAnsiContrast(AnsiPipe, bgLum, isDark);
            AnsiUnknownCommand = EnsureAnsiContrast(AnsiUnknownCommand, bgLum, isDark);
            AnsiBang           = EnsureAnsiContrast(AnsiBang, bgLum, isDark);
            AnsiKeyword        = EnsureAnsiContrast(AnsiKeyword, bgLum, isDark);
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
        if (isDarkOverride.HasValue)
        {
            Current = new Theme(isDarkOverride.Value);
        }
        else
        {
            var bg = TerminalBackground.Detect();
            Current = new Theme(bg.IsDark, bg.BgR, bg.BgG, bg.BgB);
        }
    }

    // ── Contrast Validation ─────────────────────────────────────────────

    /// <summary>
    /// WCAG AA minimum for large text (terminal monospace qualifies).
    /// Full AA is 4.5:1 but that over-corrects for terminal use.
    /// </summary>
    private const double MinContrast = 3.0;

    /// <summary>
    /// Approximate sRGB values for the 16 ConsoleColors.
    /// Terminals vary, but these are typical values.
    /// </summary>
    private static readonly Dictionary<ConsoleColor, (double r, double g, double b)> ConsoleColorRgb = new()
    {
        [ConsoleColor.Black]       = (0.00, 0.00, 0.00),
        [ConsoleColor.DarkBlue]    = (0.00, 0.00, 0.55),
        [ConsoleColor.DarkGreen]   = (0.00, 0.55, 0.00),
        [ConsoleColor.DarkCyan]    = (0.00, 0.55, 0.55),
        [ConsoleColor.DarkRed]     = (0.55, 0.00, 0.00),
        [ConsoleColor.DarkMagenta] = (0.55, 0.00, 0.55),
        [ConsoleColor.DarkYellow]  = (0.55, 0.55, 0.00),
        [ConsoleColor.Gray]        = (0.67, 0.67, 0.67),
        [ConsoleColor.DarkGray]    = (0.33, 0.33, 0.33),
        [ConsoleColor.Blue]        = (0.33, 0.33, 1.00),
        [ConsoleColor.Green]       = (0.33, 1.00, 0.33),
        [ConsoleColor.Cyan]        = (0.33, 1.00, 1.00),
        [ConsoleColor.Red]         = (1.00, 0.33, 0.33),
        [ConsoleColor.Magenta]     = (1.00, 0.33, 1.00),
        [ConsoleColor.Yellow]      = (1.00, 1.00, 0.33),
        [ConsoleColor.White]       = (1.00, 1.00, 1.00),
    };

    /// <summary>
    /// Get the WCAG contrast ratio of a ConsoleColor against a background luminance.
    /// </summary>
    private static double GetContrastRatio(ConsoleColor color, double bgLum)
    {
        var (r, g, b) = ConsoleColorRgb[color];
        var fgLum = TerminalBackground.RelativeLuminance(r, g, b);
        return TerminalBackground.ContrastRatio(fgLum, bgLum);
    }

    /// <summary>
    /// If a color doesn't meet minimum contrast, try brighter (dark theme)
    /// or darker (light theme) alternatives. Last resort: white or black.
    /// </summary>
    private static ConsoleColor EnsureContrast(ConsoleColor color, double bgLum, bool isDark)
    {
        if (GetContrastRatio(color, bgLum) >= MinContrast)
            return color;

        // Try brighter or darker variant
        var alt = isDark ? Brighten(color) : Darken(color);
        if (alt != color && GetContrastRatio(alt, bgLum) >= MinContrast)
            return alt;

        // Try one more step
        var alt2 = isDark ? Brighten(alt) : Darken(alt);
        if (alt2 != alt && GetContrastRatio(alt2, bgLum) >= MinContrast)
            return alt2;

        // Nuclear fallback
        return isDark ? ConsoleColor.White : ConsoleColor.Black;
    }

    /// <summary>
    /// Map a color to a brighter variant (for dark backgrounds).
    /// </summary>
    private static ConsoleColor Brighten(ConsoleColor c) => c switch
    {
        ConsoleColor.Black       => ConsoleColor.DarkGray,
        ConsoleColor.DarkBlue    => ConsoleColor.Blue,
        ConsoleColor.DarkGreen   => ConsoleColor.Green,
        ConsoleColor.DarkCyan    => ConsoleColor.Cyan,
        ConsoleColor.DarkRed     => ConsoleColor.Red,
        ConsoleColor.DarkMagenta => ConsoleColor.Magenta,
        ConsoleColor.DarkYellow  => ConsoleColor.Yellow,
        ConsoleColor.DarkGray    => ConsoleColor.Gray,
        ConsoleColor.Gray        => ConsoleColor.White,
        ConsoleColor.Blue        => ConsoleColor.Cyan,
        ConsoleColor.Green       => ConsoleColor.Yellow,
        ConsoleColor.Red         => ConsoleColor.Magenta,
        _ => c, // already bright or White
    };

    /// <summary>
    /// Map a color to a darker variant (for light backgrounds).
    /// </summary>
    private static ConsoleColor Darken(ConsoleColor c) => c switch
    {
        ConsoleColor.White       => ConsoleColor.Gray,
        ConsoleColor.Gray        => ConsoleColor.DarkGray,
        ConsoleColor.DarkGray    => ConsoleColor.Black,
        ConsoleColor.Blue        => ConsoleColor.DarkBlue,
        ConsoleColor.Green       => ConsoleColor.DarkGreen,
        ConsoleColor.Cyan        => ConsoleColor.DarkCyan,
        ConsoleColor.Red         => ConsoleColor.DarkRed,
        ConsoleColor.Magenta     => ConsoleColor.DarkMagenta,
        ConsoleColor.Yellow      => ConsoleColor.DarkYellow,
        _ => c, // already dark or Black
    };

    // ── ANSI Color Contrast Validation ──────────────────────────────────

    /// <summary>
    /// Known ANSI SGR color codes → approximate sRGB for contrast checking.
    /// Covers basic 8/16 colors and the 256-color palette entries we use.
    /// </summary>
    private static readonly Dictionary<string, (double r, double g, double b)> AnsiCodeRgb = new()
    {
        // Basic foreground colors (30-37)
        ["30"] = (0.00, 0.00, 0.00), // Black
        ["31"] = (0.55, 0.00, 0.00), // DarkRed
        ["32"] = (0.00, 0.55, 0.00), // DarkGreen
        ["33"] = (0.55, 0.55, 0.00), // DarkYellow
        ["34"] = (0.00, 0.00, 0.55), // DarkBlue
        ["35"] = (0.55, 0.00, 0.55), // DarkMagenta
        ["36"] = (0.00, 0.55, 0.55), // DarkCyan
        ["37"] = (0.67, 0.67, 0.67), // Gray
        // Bright foreground colors (90-97)
        ["90"] = (0.33, 0.33, 0.33), // DarkGray
        ["91"] = (1.00, 0.33, 0.33), // Red
        ["92"] = (0.33, 1.00, 0.33), // Green
        ["93"] = (1.00, 1.00, 0.33), // Yellow
        ["94"] = (0.33, 0.33, 1.00), // Blue
        ["95"] = (1.00, 0.33, 1.00), // Magenta
        ["96"] = (0.33, 1.00, 1.00), // Cyan
        ["97"] = (1.00, 1.00, 1.00), // White
    };

    /// <summary>
    /// Validate an ANSI escape sequence against the background.
    /// If contrast is too low, substitute an appropriate fallback.
    /// </summary>
    private static string EnsureAnsiContrast(string ansi, double bgLum, bool isDark)
    {
        var rgb = ParseAnsiRgb(ansi);
        if (rgb == null) return ansi; // can't parse — leave as-is

        var (r, g, b) = rgb.Value;
        var fgLum = TerminalBackground.RelativeLuminance(r, g, b);
        if (TerminalBackground.ContrastRatio(fgLum, bgLum) >= MinContrast)
            return ansi; // already good

        // Substitute: white for dark themes, black for light
        return isDark ? "\x1b[97m" : "\x1b[30m";
    }

    /// <summary>
    /// Extract approximate RGB from an ANSI escape sequence.
    /// Handles: \x1b[Nm (basic), \x1b[38;5;Nm (256-color).
    /// </summary>
    private static (double r, double g, double b)? ParseAnsiRgb(string ansi)
    {
        if (string.IsNullOrEmpty(ansi) || !ansi.StartsWith("\x1b[") || !ansi.EndsWith("m"))
            return null;

        var body = ansi[2..^1]; // strip ESC[ and m

        // 256-color: 38;5;N
        if (body.StartsWith("38;5;") && int.TryParse(body[5..], out var idx256))
            return Palette256ToRgb(idx256);

        // Basic color code
        if (AnsiCodeRgb.TryGetValue(body, out var rgb))
            return rgb;

        return null;
    }

    /// <summary>
    /// Convert a 256-color palette index to approximate sRGB.
    /// 0-7: standard, 8-15: bright, 16-231: 6x6x6 cube, 232-255: grayscale.
    /// </summary>
    private static (double r, double g, double b)? Palette256ToRgb(int idx)
    {
        if (idx < 0 || idx > 255) return null;

        if (idx < 8)
        {
            // Standard colors — map to basic code
            var code = (30 + idx).ToString();
            return AnsiCodeRgb.TryGetValue(code, out var rgb) ? rgb : null;
        }
        if (idx < 16)
        {
            // Bright colors — map to 90+ code
            var code = (82 + idx).ToString(); // 8→90, 9→91, ...
            return AnsiCodeRgb.TryGetValue(code, out var rgb) ? rgb : null;
        }
        if (idx < 232)
        {
            // 6x6x6 color cube
            var ci = idx - 16;
            var ri = ci / 36;
            var gi = (ci % 36) / 6;
            var bi = ci % 6;
            // Each step: 0, 95, 135, 175, 215, 255 (approximately)
            double CubeVal(int v) => v == 0 ? 0.0 : (55.0 + 40.0 * v) / 255.0;
            return (CubeVal(ri), CubeVal(gi), CubeVal(bi));
        }
        // Grayscale ramp: 232-255 → 8, 18, 28, ..., 238
        var gray = (8.0 + 10.0 * (idx - 232)) / 255.0;
        return (gray, gray, gray);
    }
}
