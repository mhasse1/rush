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

    /// <summary>Background luminance (0.0–1.0). -1 = no RGB data available.</summary>
    internal double BgLuminance { get; } = -1;

    /// <summary>True if exact background RGB is known (via RUSH_BG env var or setbg).</summary>
    public bool HasDetectedRgb { get; private set; }

    /// <summary>How the background was detected.</summary>
    public TerminalBackground.DetectionMethod DetectionMethod { get; private set; } = TerminalBackground.DetectionMethod.Fallback;

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
            Muted           = ConsoleColor.DarkGray;
            Accent          = ConsoleColor.Black;
            Error           = ConsoleColor.Red;
            Warning         = ConsoleColor.DarkYellow;
            PromptPath      = ConsoleColor.DarkGreen;
            PromptGitBranch = ConsoleColor.DarkYellow;
            PromptGitDirty  = ConsoleColor.DarkYellow;
            PromptSuccess   = ConsoleColor.DarkGreen;
            PromptFailed    = ConsoleColor.Red;
            PromptRoot      = ConsoleColor.Red;
            PromptTime      = ConsoleColor.DarkGray;
            PromptUser      = ConsoleColor.Blue;
            PromptHost      = ConsoleColor.DarkGray;
            PromptSshHost   = ConsoleColor.DarkYellow;
            Directory       = ConsoleColor.Blue;
            Executable      = ConsoleColor.DarkGreen;
            Archive         = ConsoleColor.DarkRed;
            Image           = ConsoleColor.Magenta;
            Config          = ConsoleColor.DarkYellow;
            Document        = ConsoleColor.DarkCyan;
            SourceCode      = ConsoleColor.DarkBlue;
            RegularFile     = ConsoleColor.Black;
            TableHeader     = ConsoleColor.Blue;
            Separator       = ConsoleColor.DarkGray;
            Metadata        = ConsoleColor.DarkGray;
            Memory          = ConsoleColor.DarkMagenta;
            SearchQuery     = ConsoleColor.DarkYellow;
            PermRead        = ConsoleColor.DarkYellow;
            PermWrite       = ConsoleColor.Red;
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
        // When background RGB is available (from OSC 11 or assumed in
        // Initialize), validate every color and swap any that fail.
        // Skipped for the static default initializer (bgR = -1).
        if (bgR >= 0)
        {
            double bgLum = TerminalBackground.RelativeLuminance(bgR, bgG, bgB);
            BgLuminance = bgLum;

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
            double r = bg.BgR, g = bg.BgG, b = bg.BgB;
            if (r < 0)
            {
                // No exact RGB data (COLORFGBG/macOS detection) — assume
                // pure white for light themes, pure black for dark.
                // Without this, contrast validation would be skipped
                // and colors like Gray would wash out on light backgrounds.
                r = g = b = bg.IsDark ? 0.0 : 1.0;
            }
            Current = new Theme(bg.IsDark, r, g, b);
            Current.HasDetectedRgb = bg.BgR >= 0;
            Current.DetectionMethod = bg.Method;
        }
    }

    // ── Native Command Color Env Vars ──────────────────────────────────

    /// <summary>Tracks which env vars Rush set (vs user-set) so theme changes update only ours.</summary>
    private static readonly HashSet<string> _rushSetVars = new();

    /// <summary>
    /// Set LS_COLORS, LSCOLORS, GREP_COLORS, and CLICOLOR so native commands
    /// inherit theme-appropriate colors. Respects user-set values and NO_COLOR.
    /// Call after Initialize().
    /// </summary>
    public static void SetNativeColorEnvVars()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
            return;

        bool isDark = Current.IsDark;

        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.OSX))
            SmartSet("CLICOLOR", "1");

        double bgLum = Current.BgLuminance;

        // When exact background RGB is known, use 256-color palette for
        // optimal hue-aware color selection. Otherwise fall back to basic 16.
        if (Current.HasDetectedRgb)
        {
            // Get background RGB from the current theme
            var bg = TerminalBackground.Detect();
            if (bg.BgR >= 0)
            {
                SmartSet("LS_COLORS",   Build256LsColors(isDark, bg.BgR, bg.BgG, bg.BgB));
                SmartSet("GREP_COLORS", Build256GrepColors(isDark, bg.BgR, bg.BgG, bg.BgB));
            }
            else
            {
                SmartSet("LS_COLORS",   BuildLsColors(isDark, bgLum));
                SmartSet("GREP_COLORS", BuildGrepColors(isDark, bgLum));
            }
        }
        else
        {
            SmartSet("LS_COLORS",   BuildLsColors(isDark, bgLum));
            SmartSet("GREP_COLORS", BuildGrepColors(isDark, bgLum));
        }
        // BSD LSCOLORS is always basic 16 (BSD ls doesn't support 256-color)
        SmartSet("LSCOLORS",    BuildLsColorsBsd(isDark, bgLum));
    }

    /// <summary>
    /// Set env var if Rush previously set it OR if it's currently empty.
    /// Preserves user-set values from .bashrc/.zshrc.
    /// </summary>
    private static void SmartSet(string name, string value)
    {
        if (_rushSetVars.Contains(name) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
        {
            Environment.SetEnvironmentVariable(name, value);
            _rushSetVars.Add(name);
        }
    }

    // ── Contrast Validation ─────────────────────────────────────────────

    /// <summary>
    /// WCAG AA minimum for large text (terminal monospace qualifies).
    /// Full AA is 4.5:1 but that over-corrects for terminal use.
    /// </summary>
    /// <summary>
    /// Minimum WCAG contrast ratio for color validation.
    /// Configurable via "contrast" setting: standard=3.0, aa=4.5, aaa=7.0.
    /// </summary>
    internal static double MinContrast { get; set; } = 3.0;

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

        // Try primary direction (brighter for dark bg, darker for light bg)
        var alt = isDark ? Brighten(color) : Darken(color);
        if (alt != color && GetContrastRatio(alt, bgLum) >= MinContrast)
            return alt;
        var alt2 = isDark ? Brighten(alt) : Darken(alt);
        if (alt2 != alt && GetContrastRatio(alt2, bgLum) >= MinContrast)
            return alt2;

        // Try opposite direction (helps mid-luminance backgrounds like #9D836E)
        var opp = isDark ? Darken(color) : Brighten(color);
        if (opp != color && GetContrastRatio(opp, bgLum) >= MinContrast)
            return opp;
        var opp2 = isDark ? Darken(opp) : Brighten(opp);
        if (opp2 != opp && GetContrastRatio(opp2, bgLum) >= MinContrast)
            return opp2;

        // Nuclear fallback: pick whichever of White/Black has better contrast
        var whiteRatio = GetContrastRatio(ConsoleColor.White, bgLum);
        var blackRatio = GetContrastRatio(ConsoleColor.Black, bgLum);
        return whiteRatio > blackRatio ? ConsoleColor.White : ConsoleColor.Black;
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

    // ── Root Shell Background ─────────────────────────────────────────

    /// <summary>Saved original background for restore on exit. Null if not changed.</summary>
    private static string? _savedBgOsc;

    /// <summary>Saved original foreground for restore on exit. Null if not changed.</summary>
    private static string? _savedFgOsc;

    /// <summary>Current background OSC sequence. Used by ReemitBackground() after external commands.</summary>
    private static string? _currentBgOsc;

    /// <summary>Current foreground OSC sequence. Emitted alongside background.</summary>
    private static string? _currentFgOsc;

    /// <summary>
    /// If root/admin, set the terminal background via OSC 11.
    /// Call after Initialize(). Returns true if background was changed.
    /// </summary>
    public static bool ApplyRootBackground(string rootBgSetting)
    {
        if (string.Equals(rootBgSetting, "none", StringComparison.OrdinalIgnoreCase))
            return false;

        var hex = rootBgSetting;
        if (string.Equals(hex, "auto", StringComparison.OrdinalIgnoreCase))
            hex = Current.IsDark ? "#330000" : "#FFFF88";

        return SetBackground(hex);
    }

    /// <summary>
    /// Set the terminal background to a hex color via OSC 11, re-theme Rush,
    /// and update env vars (LS_COLORS, GREP_COLORS, etc.) for contrast.
    /// Saves restore state so ResetBackground() can undo it on exit.
    /// </summary>
    public static bool SetBackground(string hexColor, bool emitOsc = true)
    {
        if (!TryParseHexColor(hexColor, out var r, out var g, out var b))
            return false;

        // Build OSC 11 sequence. Use BEL (\x07) terminator for broad terminal
        // compatibility (works in iTerm2, Terminal.app, xterm, etc.)
        var osc = $"\x1b]11;rgb:{r:x4}/{g:x4}/{b:x4}\x07";
        _currentBgOsc = osc;

        // Set foreground to complement background — black on light, white on dark.
        // Without this, terminals with light default fg become illegible on light bg.
        var newLumCheck = TerminalBackground.RelativeLuminance(r / 65535.0, g / 65535.0, b / 65535.0);
        var fgOsc = newLumCheck < 0.5
            ? "\x1b]10;rgb:dddd/dddd/dddd\x07"   // light gray fg for dark backgrounds
            : "\x1b]10;rgb:1111/1111/1111\x07";   // near-black fg for light backgrounds
        _currentFgOsc = fgOsc;

        // Only emit when interactive (not rush -c) and stdout is a terminal.
        if (emitOsc && !Console.IsOutputRedirected)
        {
            _savedBgOsc ??= "\x1b]111\x07";
            _savedFgOsc ??= "\x1b]110\x07";
            Console.Write(osc);
            Console.Write(fgOsc);
        }

        // Persist for reload — child processes inherit this env var
        Environment.SetEnvironmentVariable("RUSH_BG", hexColor);

        // Re-initialize theme with the new background color for contrast validation
        var newBgR = r / 65535.0;
        var newBgG = g / 65535.0;
        var newBgB = b / 65535.0;
        var newLum = TerminalBackground.RelativeLuminance(newBgR, newBgG, newBgB);
        Current = new Theme(newLum < 0.5, newBgR, newBgG, newBgB);
        Current.HasDetectedRgb = true;

        // Update native command color env vars for new background
        SetNativeColorEnvVars();

        return true;
    }

    /// <summary>
    /// Reset the terminal background to its original state (before Rush modified it).
    /// Re-detects the theme via the normal cascade.
    /// </summary>
    public static void ResetBackground()
    {
        if (_savedBgOsc != null)
        {
            Console.Write(_savedBgOsc);
            _savedBgOsc = null;
            _currentBgOsc = null;

            if (_savedFgOsc != null)
            {
                Console.Write(_savedFgOsc);
                _savedFgOsc = null;
                _currentFgOsc = null;
            }

            // Clear RUSH_BG so detection falls through to COLORFGBG/macOS/fallback
            Environment.SetEnvironmentVariable("RUSH_BG", null);

            // Re-detect theme since we're back to the original background
            Initialize();
            SetNativeColorEnvVars();
        }
    }

    /// <summary>
    /// Restore the original terminal background on exit. Does not re-theme.
    /// </summary>
    public static void RestoreBackground()
    {
        if (_savedBgOsc != null)
        {
            Console.Write(_savedBgOsc);
            _savedBgOsc = null;
            // Clear RUSH_BG so child processes don't inherit stale value
            Environment.SetEnvironmentVariable("RUSH_BG", null);
        }
        if (_savedFgOsc != null)
        {
            Console.Write(_savedFgOsc);
            _savedFgOsc = null;
        }
    }

    /// <summary>
    /// Re-emit the current background OSC sequence without rebuilding the theme.
    /// Cheap and idempotent — call after external commands that may change terminal bg.
    /// </summary>
    public static void ReemitBackground()
    {
        if (!Console.IsOutputRedirected)
        {
            if (_currentBgOsc != null)
                Console.Write(_currentBgOsc);
            if (_currentFgOsc != null)
                Console.Write(_currentFgOsc);
        }
    }

    /// <summary>
    /// The currently active .rushbg file path, if any. Used to avoid re-applying
    /// the same color on repeated cd's within the same directory tree.
    /// </summary>
    internal static string? ActiveRushBgFile { get; set; }

    /// <summary>Parse a hex color like "#330000" or "#FF8" to 16-bit RGB components.</summary>
    internal static bool TryParseHexColor(string hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (string.IsNullOrEmpty(hex)) return false;
        hex = hex.TrimStart('#');

        if (hex.Length == 3)
        {
            // Short form: #RGB → expand to #RRGGBB
            if (!int.TryParse(hex[0..1], System.Globalization.NumberStyles.HexNumber, null, out var r4) ||
                !int.TryParse(hex[1..2], System.Globalization.NumberStyles.HexNumber, null, out var g4) ||
                !int.TryParse(hex[2..3], System.Globalization.NumberStyles.HexNumber, null, out var b4))
                return false;
            r = r4 * 0x1111; g = g4 * 0x1111; b = b4 * 0x1111;
            return true;
        }
        if (hex.Length == 6)
        {
            if (!int.TryParse(hex[0..2], System.Globalization.NumberStyles.HexNumber, null, out var r8) ||
                !int.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g8) ||
                !int.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b8))
                return false;
            r = r8 * 257; g = g8 * 257; b = b8 * 257; // scale 8-bit to 16-bit
            return true;
        }
        return false;
    }

    // ── Tiered 256-Color Palette Generation ──────────────────────────
    //
    // When exact background RGB is known (via RUSH_BG/setbg), we generate
    // optimal LS_COLORS and GREP_COLORS using the full xterm 256-color palette
    // instead of the basic 16 ANSI colors. This provides:
    //   - Hue avoidance: colors shifted away from the background hue
    //   - Three contrast tiers: Primary (7:1+), Secondary (4.5:1+), Muted (3:1+)
    //   - Inter-color distinction: each slot gets a perceptually different color
    //   - Dark mode: pastels/neons; Light mode: jewel tones

    /// <summary>
    /// Convert normalized RGB (0.0-1.0) to HSL. Hue is 0-360°.
    /// </summary>
    internal static (double H, double S, double L) RgbToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2.0;

        if (max == min)
            return (0, 0, l); // achromatic

        var d = max - min;
        var s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

        double h;
        if (max == r)
            h = ((g - b) / d + (g < b ? 6 : 0)) * 60;
        else if (max == g)
            h = ((b - r) / d + 2) * 60;
        else
            h = ((r - g) / d + 4) * 60;

        return (h, s, l);
    }

    /// <summary>
    /// Circular hue distance (0-180°). Two hues within 30° are "similar".
    /// </summary>
    internal static double HueDistance(double h1, double h2)
    {
        var d = Math.Abs(h1 - h2);
        return d > 180 ? 360 - d : d;
    }

    /// <summary>
    /// Color slot definition for palette generation.
    /// </summary>
    internal record ColorSlot(string Key, double MinContrast, double MaxContrast, double PreferredHue, double HueWeight = 1.0);

    /// <summary>
    /// LS_COLORS slots with contrast tiers and preferred hues.
    /// Dark: pastels/neons. Light: jewel tones.
    /// </summary>
    private static readonly ColorSlot[] DarkLsSlots =
    {
        new("di", 7.0, 14.0, 180, 1.5),  // directory — cyan (shifted from blue)
        new("ln", 7.0, 14.0, 300, 1.5),  // symlink — magenta/pink
        new("so", 4.5,  9.0, 280, 1.0),  // socket — purple
        new("pi", 4.5,  9.0,  50, 1.0),  // pipe — yellow
        new("ex", 7.0, 14.0, 120, 1.5),  // executable — green
        new("bd", 4.5,  9.0,  40, 1.0),  // block device — orange
        new("cd", 4.5,  9.0,  40, 1.0),  // char device — orange
    };

    private static readonly ColorSlot[] LightLsSlots =
    {
        new("di", 7.0, 14.0, 220, 1.5),  // directory — navy blue
        new("ln", 7.0, 14.0, 320, 1.5),  // symlink — deep magenta
        new("so", 4.5,  9.0, 280, 1.0),  // socket — purple
        new("pi", 4.5,  9.0,  45, 1.0),  // pipe — dark yellow/olive
        new("ex", 7.0, 14.0, 160, 1.5),  // executable — teal/dark green
        new("bd", 4.5,  9.0,  30, 1.0),  // block device — dark orange
        new("cd", 4.5,  9.0,  30, 1.0),  // char device — dark orange
    };

    /// <summary>
    /// GREP_COLORS slots with contrast tiers.
    /// </summary>
    private static readonly ColorSlot[] DarkGrepSlots =
    {
        new("fn", 7.0, 14.0, 300, 1.0),  // filename — magenta
        new("ln", 4.5,  9.0,  50, 1.0),  // line number — yellow
        new("bn", 4.5,  9.0,  50, 1.0),  // byte offset — yellow
        new("se", 3.0,  6.0,   0, 0.0),  // separator — gray (no hue pref)
    };

    private static readonly ColorSlot[] LightGrepSlots =
    {
        new("fn", 7.0, 14.0, 220, 1.0),  // filename — navy
        new("ln", 4.5,  9.0, 160, 1.0),  // line number — teal
        new("bn", 4.5,  9.0, 160, 1.0),  // byte offset — teal
        new("se", 3.0,  6.0,   0, 0.0),  // separator — gray
    };

    /// <summary>
    /// Select the best xterm 256-color code for a slot, considering:
    /// - Contrast ratio against background (must be in MinContrast-MaxContrast range)
    /// - Hue proximity to preferred hue (bonus)
    /// - Hue distance from background hue (penalty if too close)
    /// - Distance from already-used colors (penalty if too similar)
    /// </summary>
    internal static int SelectBestColor(ColorSlot slot, double bgR, double bgG, double bgB,
        double bgLum, double bgHue, double bgSat, HashSet<int> usedCodes)
    {
        int bestCode = -1;
        double bestScore = double.MinValue;

        // Scan the 6x6x6 color cube (16-231) + grayscale (232-255)
        for (int idx = 16; idx <= 255; idx++)
        {
            var rgb = Palette256ToRgb(idx);
            if (rgb == null) continue;
            var (r, g, b) = rgb.Value;

            var fgLum = TerminalBackground.RelativeLuminance(r, g, b);
            var contrast = TerminalBackground.ContrastRatio(fgLum, bgLum);

            // Must meet minimum contrast
            if (contrast < slot.MinContrast) continue;

            double score = 0;

            // Penalize exceeding max contrast (avoid halation on dark, eye strain on light)
            if (contrast > slot.MaxContrast)
                score -= (contrast - slot.MaxContrast) * 2.0;
            else
                // Prefer contrast in the upper half of the range
                score += (contrast - slot.MinContrast) / (slot.MaxContrast - slot.MinContrast) * 3.0;

            var (fgHue, fgSat, fgLightness) = RgbToHsl(r, g, b);

            // Bonus for closeness to preferred hue (only for saturated colors)
            if (fgSat > 0.2 && slot.HueWeight > 0)
            {
                var hueDist = HueDistance(fgHue, slot.PreferredHue);
                // Max bonus at 0° distance, decays linearly over 90°
                score += Math.Max(0, (90 - hueDist) / 90.0) * 5.0 * slot.HueWeight;

                // Bonus for saturation (prefer vivid colors over washed-out)
                score += fgSat * 2.0;
            }

            // Penalty for hue proximity to background (avoid blending)
            if (bgSat > 0.1 && fgSat > 0.2)
            {
                var bgHueDist = HueDistance(fgHue, bgHue);
                if (bgHueDist < 40)
                    score -= (40 - bgHueDist) / 40.0 * 8.0; // strong penalty
            }

            // Penalty for similarity to already-used colors
            foreach (var usedIdx in usedCodes)
            {
                var usedRgb = Palette256ToRgb(usedIdx);
                if (usedRgb == null) continue;
                var usedLum = TerminalBackground.RelativeLuminance(usedRgb.Value.r, usedRgb.Value.g, usedRgb.Value.b);
                var interDist = TerminalBackground.ContrastRatio(fgLum, usedLum);
                if (interDist < 1.5)
                    score -= (1.5 - interDist) * 10.0; // heavy penalty for near-identical
            }

            // Slight penalty for grayscale codes when a hue is preferred
            if (idx >= 232 && slot.HueWeight > 0)
                score -= 3.0;

            if (score > bestScore)
            {
                bestScore = score;
                bestCode = idx;
            }
        }

        return bestCode;
    }

    /// <summary>
    /// Build LS_COLORS using 256-color palette optimized for the exact background.
    /// Falls back to basic 16-color method if no suitable colors are found.
    /// </summary>
    internal static string Build256LsColors(bool isDark, double bgR, double bgG, double bgB)
    {
        var bgLum = TerminalBackground.RelativeLuminance(bgR, bgG, bgB);
        var (bgHue, bgSat, _) = RgbToHsl(bgR, bgG, bgB);
        var slots = isDark ? DarkLsSlots : LightLsSlots;
        var usedCodes = new HashSet<int>();

        var entries = new List<string>();

        foreach (var slot in slots)
        {
            var code = SelectBestColor(slot, bgR, bgG, bgB, bgLum, bgHue, bgSat, usedCodes);
            if (code >= 0)
            {
                entries.Add($"{slot.Key}=38;5;{code}");
                usedCodes.Add(code);
            }
            else
            {
                // Fallback to basic color
                var (_, darkSgr, lightSgr) = LsColorEntries.First(e => e.key == slot.Key);
                entries.Add($"{slot.Key}={EnsureSgrContrast(isDark ? darkSgr : lightSgr, bgLum, isDark)}");
            }
        }

        // Regular files — give them a muted/neutral color so they're distinct from
        // colored types (directories, symlinks, executables) but not distracting.
        // Target: 4.5:1 contrast, neutral/low-saturation.
        var fiSlot = new ColorSlot("fi", 4.5, 10.0, 0, 0.0); // no hue preference = gray/neutral
        var fiCode = SelectBestColor(fiSlot, bgR, bgG, bgB, bgLum, bgHue, bgSat, usedCodes);
        if (fiCode >= 0)
        {
            entries.Add($"fi=38;5;{fiCode}");
            usedCodes.Add(fiCode);
        }

        // Background-coded entries are always basic (su, sg, tw, ow)
        foreach (var (key, darkSgr, lightSgr) in LsColorEntries)
        {
            if (key is "su" or "sg" or "tw" or "ow")
                entries.Add($"{key}={(isDark ? darkSgr : lightSgr)}");
        }

        return string.Join(":", entries);
    }

    /// <summary>
    /// Build GREP_COLORS using 256-color palette optimized for the exact background.
    /// </summary>
    internal static string Build256GrepColors(bool isDark, double bgR, double bgG, double bgB)
    {
        var bgLum = TerminalBackground.RelativeLuminance(bgR, bgG, bgB);
        var (bgHue, bgSat, _) = RgbToHsl(bgR, bgG, bgB);
        var slots = isDark ? DarkGrepSlots : LightGrepSlots;
        var usedCodes = new HashSet<int>();

        var entries = new List<string>();

        // ms/mc (match) — always red-ish, use basic bold for visibility
        var matchSgr = isDark ? "01;31" : "31";
        matchSgr = EnsureSgrContrast(matchSgr, bgLum, isDark);
        entries.Add($"ms={matchSgr}");
        entries.Add($"mc={matchSgr}");
        entries.Add("sl="); // selected line — default
        entries.Add("cx="); // context line — default

        foreach (var slot in slots)
        {
            var code = SelectBestColor(slot, bgR, bgG, bgB, bgLum, bgHue, bgSat, usedCodes);
            if (code >= 0)
            {
                entries.Add($"{slot.Key}=38;5;{code}");
                usedCodes.Add(code);
            }
            else
            {
                var (_, darkSgr, lightSgr) = GrepColorEntries.First(e => e.key == slot.Key);
                entries.Add($"{slot.Key}={EnsureSgrContrast(isDark ? darkSgr : lightSgr, bgLum, isDark)}");
            }
        }

        return string.Join(":", entries);
    }

    // ── SGR Contrast Validation (for LS_COLORS / GREP_COLORS) ────────

    /// <summary>
    /// Validate a bare SGR parameter string (e.g., "1;34", "35") against bgLum.
    /// Skips entries that contain background codes (40-47, 100-107) since
    /// text contrasts against that background, not the terminal background.
    /// Returns the (possibly adjusted) SGR string.
    /// </summary>
    internal static string EnsureSgrContrast(string sgr, double bgLum, bool isDark)
    {
        if (string.IsNullOrEmpty(sgr)) return sgr;

        var parts = sgr.Split(';');
        string? fgCode = null;
        string prefix = "";  // bold, underline, etc.
        bool hasBgCode = false;

        foreach (var p in parts)
        {
            if (int.TryParse(p, out var code))
            {
                if ((code >= 40 && code <= 47) || (code >= 100 && code <= 107))
                {
                    hasBgCode = true;
                    break;
                }
                if ((code >= 30 && code <= 37) || (code >= 90 && code <= 97))
                    fgCode = p;
                else if (code == 1)
                    prefix = p + ";";  // preserve "01;" vs "1;"
            }
        }

        // Has its own background — text contrasts against that, not terminal bg
        if (hasBgCode) return sgr;
        // No foreground color found — nothing to validate
        if (fgCode == null) return sgr;

        bool isBold = prefix.Length > 0;
        if (!int.TryParse(fgCode, out var fc)) return sgr;

        // When bold + basic color (30-37), terminals render as bright (90-97).
        // Check contrast using the effective display color.
        var effectiveCode = (isBold && fc >= 30 && fc <= 37) ? (fc + 60).ToString() : fgCode;
        if (AnsiCodeRgb.TryGetValue(effectiveCode, out var rgb))
        {
            var fgLum = TerminalBackground.RelativeLuminance(rgb.r, rgb.g, rgb.b);
            if (TerminalBackground.ContrastRatio(fgLum, bgLum) >= MinContrast)
                return sgr; // already good
        }

        // Try toggling bright/normal: 30-37 ↔ 90-97
        // When bold+basic (e.g. 1;34), effective is bright (94) — try basic (34) without bold
        // When bright (e.g. 94), try basic (34)
        // When basic (e.g. 34), try bright (94)
        string altCode;
        string altPrefix;
        if (isBold && fc >= 30 && fc <= 37)
        {
            // Bold+basic: effective was fc+60 (bright). Try fc itself (basic, no bold).
            altCode = fc.ToString();
            altPrefix = ""; // drop bold
        }
        else if (fc >= 90)
        {
            altCode = (fc - 60).ToString();
            altPrefix = prefix;
        }
        else
        {
            altCode = (fc + 60).ToString();
            altPrefix = prefix;
        }

        if (AnsiCodeRgb.TryGetValue(altCode, out var altRgb))
        {
            var altLum = TerminalBackground.RelativeLuminance(altRgb.r, altRgb.g, altRgb.b);
            if (TerminalBackground.ContrastRatio(altLum, bgLum) >= MinContrast)
                return altPrefix + altCode;
        }

        // Nuclear fallback
        return prefix + (isDark ? "97" : "30");
    }

    // ── Inter-Color Distinction ────────────────────────────────────────

    /// <summary>
    /// Get the effective ANSI foreground code from an SGR string.
    /// Accounts for bold+basic → bright mapping.
    /// Returns null if no foreground code found or entry has its own background.
    /// </summary>
    internal static string? GetEffectiveFgCode(string sgr)
    {
        if (string.IsNullOrEmpty(sgr)) return null;
        var parts = sgr.Split(';');
        string? fgCode = null;
        bool isBold = false;

        foreach (var p in parts)
        {
            if (int.TryParse(p, out var code))
            {
                if ((code >= 40 && code <= 47) || (code >= 100 && code <= 107))
                    return null; // has its own background — skip
                if ((code >= 30 && code <= 37) || (code >= 90 && code <= 97))
                    fgCode = code.ToString();
                else if (code == 1)
                    isBold = true;
            }
        }

        if (fgCode == null) return null;
        int fc = int.Parse(fgCode);
        // Bold + basic (30-37) renders as bright (90-97)
        if (isBold && fc >= 30 && fc <= 37)
            return (fc + 60).ToString();
        return fc.ToString();
    }

    /// <summary>
    /// Compute perceptual distance between two SGR foreground colors.
    /// Returns contrast ratio between the two colors (1.0 = identical, higher = more distinct).
    /// </summary>
    internal static double SgrDistance(string sgrA, string sgrB)
    {
        var codeA = GetEffectiveFgCode(sgrA);
        var codeB = GetEffectiveFgCode(sgrB);
        if (codeA == null || codeB == null) return 21.0; // can't compare — assume distinct
        if (codeA == codeB) return 1.0; // identical

        if (!AnsiCodeRgb.TryGetValue(codeA, out var rgbA) ||
            !AnsiCodeRgb.TryGetValue(codeB, out var rgbB))
            return 21.0; // unknown — assume distinct

        var lumA = TerminalBackground.RelativeLuminance(rgbA.r, rgbA.g, rgbA.b);
        var lumB = TerminalBackground.RelativeLuminance(rgbB.r, rgbB.g, rgbB.b);
        return TerminalBackground.ContrastRatio(lumA, lumB);
    }

    /// <summary>
    /// Minimum contrast ratio between two foreground colors in the same group
    /// to consider them "distinguishable". Lower than MinContrast since we
    /// just need visual distinction, not readability against each other.
    /// </summary>
    private const double MinDistinction = 1.3;

    /// <summary>
    /// Available ANSI foreground codes to try as alternatives, ordered by preference.
    /// Bright colors first (more saturated), then basic.
    /// </summary>
    private static readonly string[] AlternativeFgCodes =
    {
        "91", "92", "93", "94", "95", "96", "97",  // bright colors (including white)
        "31", "32", "33", "34", "35", "36", "37",  // basic colors (including gray)
    };

    /// <summary>
    /// Ensure all fg-only entries in a group are visually distinct from each other.
    /// Entries with background codes (su, sg, tw, ow) are skipped.
    /// If two entries collide, the second one is shifted to an alternative that
    /// passes both background contrast and inter-color distinction.
    /// </summary>
    internal static void EnsureGroupDistinction((string key, string sgr)[] entries, double bgLum, bool isDark)
    {
        if (bgLum < 0) return; // no RGB data — skip distinction check

        for (int i = 0; i < entries.Length; i++)
        {
            var codeI = GetEffectiveFgCode(entries[i].sgr);
            if (codeI == null) continue; // has bg or no fg — skip

            for (int j = i + 1; j < entries.Length; j++)
            {
                var codeJ = GetEffectiveFgCode(entries[j].sgr);
                if (codeJ == null) continue;

                if (SgrDistance(entries[i].sgr, entries[j].sgr) >= MinDistinction)
                    continue; // already distinct

                // Find a replacement for entry j that's distinct from all others
                foreach (var alt in AlternativeFgCodes)
                {
                    // Check contrast against background
                    if (!AnsiCodeRgb.TryGetValue(alt, out var altRgb)) continue;
                    var altLum = TerminalBackground.RelativeLuminance(altRgb.r, altRgb.g, altRgb.b);
                    if (TerminalBackground.ContrastRatio(altLum, bgLum) < MinContrast)
                        continue; // doesn't pass background contrast

                    // Compute the actual SGR that would be stored, to ensure
                    // distinction check matches the final representation
                    int altInt = int.Parse(alt);
                    string storedSgr = (isDark && altInt >= 90)
                        ? $"1;{altInt - 60}"   // dark theme convention: bold+basic
                        : alt;                  // use as-is

                    // Check distinction from all other entries in the group
                    bool distinctFromAll = true;
                    for (int k = 0; k < entries.Length; k++)
                    {
                        if (k == j) continue;
                        if (GetEffectiveFgCode(entries[k].sgr) == null) continue;

                        if (SgrDistance(storedSgr, entries[k].sgr) < MinDistinction)
                        {
                            distinctFromAll = false;
                            break;
                        }
                    }

                    if (distinctFromAll)
                    {
                        entries[j] = (entries[j].key, storedSgr);
                        break;
                    }
                }
            }
        }
    }

    // ── Dynamic LS_COLORS / LSCOLORS / GREP_COLORS Builders ──────────

    /// <summary>Base LS_COLORS entries: (key, dark SGR, light SGR).</summary>
    private static readonly (string key, string darkSgr, string lightSgr)[] LsColorEntries =
    {
        ("di", "1;34", "34"),       // directory
        ("ln", "1;36", "35"),       // symlink
        ("so", "1;35", "35"),       // socket
        ("pi", "33",   "33"),       // pipe
        ("ex", "1;32", "32"),       // executable
        ("bd", "1;33", "33"),       // block device
        ("cd", "1;33", "33"),       // char device
        ("su", "37;41", "37;41"),   // setuid (has bg)
        ("sg", "30;43", "30;43"),   // setgid (has bg)
        ("tw", "30;42", "30;42"),   // sticky+other-writable (has bg)
        ("ow", "34;42", "34;42"),   // other-writable (has bg)
    };

    /// <summary>
    /// Primary LS_COLORS keys that must be visually distinct from each other.
    /// These are the most commonly visible file types in `ls` output.
    /// Secondary types (pi, bd, cd) may share colors — they're rare.
    /// </summary>
    private static readonly HashSet<string> PrimaryLsKeys = new() { "di", "ln", "so", "ex" };

    /// <summary>Build LS_COLORS with per-entry contrast validation and inter-color distinction.</summary>
    internal static string BuildLsColors(bool isDark, double bgLum)
    {
        // Phase 1: Per-entry contrast validation against background
        var validated = new (string key, string sgr)[LsColorEntries.Length];
        for (int i = 0; i < LsColorEntries.Length; i++)
        {
            var (key, darkSgr, lightSgr) = LsColorEntries[i];
            var sgr = isDark ? darkSgr : lightSgr;
            if (bgLum >= 0)
                sgr = EnsureSgrContrast(sgr, bgLum, isDark);
            validated[i] = (key, sgr);
        }

        // Phase 2: Inter-color distinction for primary entries only
        // Extract primary entries, run distinction, put back
        var primaryIndices = new List<int>();
        var primaryGroup = new List<(string key, string sgr)>();
        for (int i = 0; i < validated.Length; i++)
        {
            if (PrimaryLsKeys.Contains(validated[i].key) && GetEffectiveFgCode(validated[i].sgr) != null)
            {
                primaryIndices.Add(i);
                primaryGroup.Add(validated[i]);
            }
        }

        if (primaryGroup.Count > 1)
        {
            var groupArray = primaryGroup.ToArray();
            EnsureGroupDistinction(groupArray, bgLum, isDark);
            for (int k = 0; k < primaryIndices.Count; k++)
                validated[primaryIndices[k]] = groupArray[k];
        }

        var result = new string[validated.Length];
        for (int i = 0; i < validated.Length; i++)
            result[i] = $"{validated[i].key}={validated[i].sgr}";
        return string.Join(":", result);
    }

    /// <summary>Base GREP_COLORS entries: (key, dark SGR, light SGR).</summary>
    private static readonly (string key, string darkSgr, string lightSgr)[] GrepColorEntries =
    {
        ("ms", "01;31", "31"),    // match (selected)
        ("mc", "01;31", "31"),    // match (context)
        ("sl", "",      ""),      // selected line
        ("cx", "",      ""),      // context line
        ("fn", "35",    "34"),    // filename
        ("ln", "32",    "32"),    // line number
        ("bn", "32",    "32"),    // byte offset
        ("se", "36",    "36"),    // separator
    };

    /// <summary>Build GREP_COLORS with per-entry contrast validation.</summary>
    /// <remarks>
    /// No distinction pass here — GREP_COLORS has intentional duplicates
    /// (ms/mc are both "match", ln/bn are related metadata).
    /// </remarks>
    internal static string BuildGrepColors(bool isDark, double bgLum)
    {
        var entries = new string[GrepColorEntries.Length];
        for (int i = 0; i < GrepColorEntries.Length; i++)
        {
            var (key, darkSgr, lightSgr) = GrepColorEntries[i];
            var sgr = isDark ? darkSgr : lightSgr;
            if (bgLum >= 0 && sgr.Length > 0)
                sgr = EnsureSgrContrast(sgr, bgLum, isDark);
            entries[i] = $"{key}={sgr}";
        }
        return string.Join(":", entries);
    }

    // ── BSD LSCOLORS Builder ─────────────────────────────────────────

    /// <summary>BSD letter → ANSI foreground code mapping.</summary>
    private static readonly Dictionary<char, string> BsdToAnsi = new()
    {
        ['a'] = "30", ['b'] = "31", ['c'] = "32", ['d'] = "33",
        ['e'] = "34", ['f'] = "35", ['g'] = "36", ['h'] = "37",
    };

    /// <summary>ANSI foreground code → BSD letter mapping.</summary>
    private static readonly Dictionary<string, char> AnsiToBsd = new()
    {
        ["30"] = 'a', ["31"] = 'b', ["32"] = 'c', ["33"] = 'd',
        ["34"] = 'e', ["35"] = 'f', ["36"] = 'g', ["37"] = 'h',
        ["90"] = 'a', ["91"] = 'b', ["92"] = 'c', ["93"] = 'd',
        ["94"] = 'e', ["95"] = 'f', ["96"] = 'g', ["97"] = 'h',
    };

    /// <summary>
    /// Base LSCOLORS: 11 fg+bg pairs. Uppercase = bold (bright).
    /// Positions: dir, symlink, socket, pipe, exec, block, char, setuid, setgid, sticky+ow, ow.
    /// </summary>
    private static readonly (char darkFg, char darkBg, char lightFg, char lightBg)[] BsdColorEntries =
    {
        ('E', 'x', 'e', 'x'),  // 1: directory
        ('G', 'x', 'f', 'x'),  // 2: symlink
        ('F', 'x', 'f', 'x'),  // 3: socket
        ('d', 'x', 'd', 'x'),  // 4: pipe
        ('C', 'x', 'c', 'x'),  // 5: executable
        ('e', 'g', 'e', 'g'),  // 6: block device (has bg)
        ('e', 'd', 'e', 'd'),  // 7: char device (has bg)
        ('a', 'b', 'a', 'b'),  // 8: setuid (has bg)
        ('a', 'g', 'a', 'g'),  // 9: setgid (has bg)
        ('a', 'c', 'a', 'c'),  // 10: sticky+other-writable (has bg)
        ('a', 'd', 'a', 'd'),  // 11: other-writable (has bg)
    };

    /// <summary>Build BSD LSCOLORS with per-entry contrast validation.</summary>
    internal static string BuildLsColorsBsd(bool isDark, double bgLum)
    {
        var chars = new char[BsdColorEntries.Length * 2];
        for (int i = 0; i < BsdColorEntries.Length; i++)
        {
            var (darkFg, darkBg, lightFg, lightBg) = BsdColorEntries[i];
            var fg = isDark ? darkFg : lightFg;
            var bg = isDark ? darkBg : lightBg;

            // Only validate fg-only entries (bg == 'x' means default/terminal bg)
            if (bg == 'x' && bgLum >= 0)
            {
                bool isBold = char.IsUpper(fg);
                var baseLetter = char.ToLower(fg);
                if (BsdToAnsi.TryGetValue(baseLetter, out var ansiCode))
                {
                    // If bold, use bright ANSI code for contrast check
                    var checkCode = isBold ? (int.Parse(ansiCode) + 60).ToString() : ansiCode;
                    var validated = EnsureSgrContrast(checkCode, bgLum, isDark);

                    // Map result back to BSD letter
                    if (AnsiToBsd.TryGetValue(validated, out var newLetter))
                    {
                        // Determine if result is a bright code (90-97)
                        bool resultBright = int.TryParse(validated, out var vc) && vc >= 90;
                        fg = resultBright ? char.ToUpper(newLetter) : newLetter;
                    }
                    else if (validated == "97")
                    {
                        fg = 'H'; // bright white → bold light grey (closest BSD equivalent)
                    }
                    else if (validated == "30")
                    {
                        fg = 'a'; // black
                    }
                }
            }

            chars[i * 2] = fg;
            chars[i * 2 + 1] = bg;
        }
        return new string(chars);
    }
}
