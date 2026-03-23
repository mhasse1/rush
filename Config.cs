using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rush;

/// <summary>
/// Rush configuration loaded from ~/.config/rush/config.json
/// Supports JSONC (JSON with // comments) for self-documenting config.
/// </summary>
public class RushConfig
{
    // ── Editing ────────────────────────────────────────────────────────
    public string EditMode { get; set; } = "vi";
    public int HistorySize { get; set; } = 500;

    // ── Display ────────────────────────────────────────────────────────
    public string Theme { get; set; } = "auto";
    public string PromptFormat { get; set; } = "default";
    public bool ShowTiming { get; set; } = true;
    public bool ShowTips { get; set; } = true;
    public bool ShowHints { get; set; } = true;

    /// <summary>
    /// Terminal background color for palette generation. Hex like "#222733".
    /// "auto" = detect from env/OS. When set, enables 256-color palette with
    /// WCAG contrast validation. Applied before banner — no visible glitch.
    /// </summary>
    public string Bg { get; set; } = "off";

    // ── Accessibility ────────────────────────────────────────────────────
    /// <summary>
    /// WCAG contrast level for color validation.
    /// "standard" = 3:1 (default, WCAG large text minimum).
    /// "aa" = 4.5:1 (WCAG AA normal text). "aaa" = 7:1 (WCAG AAA).
    /// </summary>
    public string Contrast { get; set; } = "standard";

    // ── Root Shell ──────────────────────────────────────────────────────
    /// <summary>
    /// Terminal background color for root/admin shells.
    /// "auto" = dark red on dark themes, dark yellow on light.
    /// "none" = disabled. Hex color like "#330000" for custom.
    /// </summary>
    public string RootBackground { get; set; } = "auto";

    // ── Error Handling ─────────────────────────────────────────────────
    public bool StopOnError { get; set; } = false;
    public bool PipefailMode { get; set; } = false;

    // ── Debugging ──────────────────────────────────────────────────────
    public bool TraceCommands { get; set; } = false;

    // ── Globbing ───────────────────────────────────────────────────────
    public bool StrictGlobs { get; set; } = false;

    // ── Completion ─────────────────────────────────────────────────────
    public bool CompletionIgnoreCase { get; set; } = true;

    // ── AI ───────────────────────────────────────────────────────────
    public string AiProvider { get; set; } = "anthropic";
    public string AiModel { get; set; } = "auto";

    // ── Aliases ────────────────────────────────────────────────────────
    public Dictionary<string, string> Aliases { get; set; } = new();

    /// <summary>
    /// Convert the theme string to a nullable bool for Theme.Initialize().
    /// "dark" → true, "light" → false, "auto"/anything else → null (auto-detect).
    /// </summary>
    public bool? GetThemeOverride() => Theme?.ToLowerInvariant() switch
    {
        "dark" => true,
        "light" => false,
        _ => null
    };

    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "rush");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    /// <summary>
    /// All settings metadata for self-documenting config and `set` command.
    /// </summary>
    public static readonly SettingInfo[] AllSettings = new[]
    {
        new SettingInfo("editMode",            "Editing",       "vi",    "vi, emacs",       "Editing mode. Vi: modal (Esc=normal, i=insert, /, ?, n, N=search). Emacs: always inserting, Ctrl+R=search."),
        new SettingInfo("historySize",         "Editing",       "500",   "number",          "Max commands saved to ~/.config/rush/history across sessions. Duplicates are collapsed."),
        new SettingInfo("bg",                   "Display",       "off",   "#hex, off",         "Terminal background color. Enables precise 256-color palette. Use: set bg \"#222733\""),
        new SettingInfo("theme",               "Display",       "auto",  "auto, dark, light","Color theme. \"auto\" detects terminal background. Force dark/light if detection is wrong."),
        new SettingInfo("promptFormat",        "Display",       "default","default",         "Prompt style. Override by defining rush_prompt() in init.rush for full control."),
        new SettingInfo("showTiming",          "Display",       "true",  "true, false",     "Show elapsed time for commands taking longer than 500ms."),
        new SettingInfo("showTips",            "Display",       "true",  "true, false",     "Show a rotating tip on shell startup. Disable with: set --save showTips false"),
        new SettingInfo("showHints",           "Display",       "true",  "true, false",     "Contextual hints that surface lesser-known features (e.g. esc v → $EDITOR). Not training wheels."),
        new SettingInfo("stopOnError",         "Error Handling","false", "true, false",      "Stop executing on first error (like bash set -e). Interactive sessions exit; scripts abort."),
        new SettingInfo("pipefailMode",        "Error Handling","false", "true, false",      "Fail a pipeline if ANY command fails, not just the last one (like bash set -o pipefail)."),
        new SettingInfo("traceCommands",       "Debugging",     "false", "true, false",      "Print each command before executing (like bash set -x). Shows: + command. Useful for debugging scripts."),
        new SettingInfo("strictGlobs",         "Globbing",      "false", "true, false",      "Error when a glob pattern (*.txt) matches nothing. false = pass the pattern through as literal text."),
        new SettingInfo("completionIgnoreCase","Completion",    "true",  "true, false",      "Case-insensitive Tab completion for paths and commands."),
        new SettingInfo("contrast",            "Accessibility", "standard","standard, aa, aaa","WCAG contrast level. standard=3:1 (large text), aa=4.5:1 (normal text), aaa=7:1 (enhanced)."),
        new SettingInfo("aiProvider",          "AI",            "anthropic","anthropic, openai, gemini, ollama","AI provider for the `ai` command. Custom providers via ~/.config/rush/ai-providers/"),
        new SettingInfo("aiModel",             "AI",            "auto","model name","Override the default model for your AI provider. \"auto\" = use provider default."),
    };

    /// <summary>
    /// Load config from disk. Returns defaults if no config file exists.
    /// Creates a self-documenting config on first run.
    /// </summary>
    public static RushConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var raw = File.ReadAllText(ConfigPath);
                var json = StripJsonComments(raw);
                var config = JsonSerializer.Deserialize<RushConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                return config ?? new RushConfig();
            }
        }
        catch
        {
            // Corrupt config — use defaults
        }

        // Create self-documenting config on first run
        var defaultConfig = new RushConfig();
        defaultConfig.Save();
        EnsureStartupScripts();
        return defaultConfig;
    }

    /// <summary>
    /// Save current config to disk as self-documenting JSONC.
    /// </summary>
    public void Save()
    {
        try
        {
            if (!Directory.Exists(ConfigDir))
                Directory.CreateDirectory(ConfigDir);

            var jsonc = GenerateJsonc();
            File.WriteAllText(ConfigPath, jsonc);
        }
        catch
        {
            // Can't write config — silently continue
        }
    }

    /// <summary>
    /// Apply config settings to the shell components.
    /// Returns (setE, setX, setPipefail) for the REPL loop.
    /// </summary>
    public (bool stopOnError, bool traceCommands, bool pipefailMode) Apply(LineEditor? editor, CommandTranslator translator)
    {
        // Edit mode + history size (skip if no editor, e.g. LLM mode)
        if (editor != null)
        {
            editor.Mode = EditMode.Equals("emacs", StringComparison.OrdinalIgnoreCase)
                ? Rush.EditMode.Emacs
                : Rush.EditMode.Vi;
            editor.MaxHistory = HistorySize;
        }

        // Custom aliases
        foreach (var (alias, command) in Aliases)
        {
            translator.RegisterAlias(alias, command);
        }

        return (StopOnError, TraceCommands, PipefailMode);
    }

    /// <summary>
    /// Get the current value of a setting by its camelCase key.
    /// </summary>
    public string GetValue(string key) => key.ToLowerInvariant() switch
    {
        "editmode" => EditMode,
        "historysize" => HistorySize.ToString(),
        "bg" => Bg,
        "theme" => Theme,
        "promptformat" => PromptFormat,
        "showtiming" => ShowTiming.ToString().ToLowerInvariant(),
        "showtips" => ShowTips.ToString().ToLowerInvariant(),
        "showhints" => ShowHints.ToString().ToLowerInvariant(),
        "stoponerror" => StopOnError.ToString().ToLowerInvariant(),
        "pipefailmode" => PipefailMode.ToString().ToLowerInvariant(),
        "tracecommands" => TraceCommands.ToString().ToLowerInvariant(),
        "strictglobs" => StrictGlobs.ToString().ToLowerInvariant(),
        "completionignorecase" => CompletionIgnoreCase.ToString().ToLowerInvariant(),
        "contrast" => Contrast,
        "aiprovider" => AiProvider,
        "aimodel" => AiModel,
        _ => ""
    };

    /// <summary>
    /// Set a config value by key. Returns true if recognized and valid.
    /// </summary>
    public bool SetValue(string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "editmode":
                if (value != "vi" && value != "emacs") return false;
                EditMode = value;
                return true;
            case "historysize":
                if (!int.TryParse(value, out var hs) || hs < 0) return false;
                HistorySize = hs;
                return true;
            case "bg":
                var bgVal = value.Trim('"', '\'');
                if (string.Equals(bgVal, "off", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(bgVal, "auto", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(bgVal, "reset", StringComparison.OrdinalIgnoreCase))
                { Bg = "off"; return true; }
                // Validate hex color
                if (!Rush.Theme.TryParseHexColor(bgVal, out _, out _, out _)) return false;
                Bg = bgVal;
                return true;
            case "theme":
                if (value != "auto" && value != "dark" && value != "light") return false;
                Theme = value;
                return true;
            case "promptformat":
                PromptFormat = value;
                return true;
            case "showtiming":
                if (!bool.TryParse(value, out var st)) return false;
                ShowTiming = st;
                return true;
            case "showtips":
                if (!bool.TryParse(value, out var tips)) return false;
                ShowTips = tips;
                return true;
            case "showhints":
                if (!bool.TryParse(value, out var hints)) return false;
                ShowHints = hints;
                return true;
            case "stoponerror":
                if (!bool.TryParse(value, out var se)) return false;
                StopOnError = se;
                return true;
            case "pipefailmode":
                if (!bool.TryParse(value, out var pf)) return false;
                PipefailMode = pf;
                return true;
            case "tracecommands":
                if (!bool.TryParse(value, out var tc)) return false;
                TraceCommands = tc;
                return true;
            case "strictglobs":
                if (!bool.TryParse(value, out var sg)) return false;
                StrictGlobs = sg;
                return true;
            case "completionignorecase":
                if (!bool.TryParse(value, out var ci)) return false;
                CompletionIgnoreCase = ci;
                return true;
            case "contrast":
                var lv = value.ToLowerInvariant();
                if (lv != "standard" && lv != "aa" && lv != "aaa") return false;
                Contrast = lv;
                return true;
            case "aiprovider":
                AiProvider = value;
                return true;
            case "aimodel":
                AiModel = value;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Get the minimum contrast ratio for the current WCAG setting.
    /// </summary>
    public double GetContrastRatio() => Contrast.ToLowerInvariant() switch
    {
        "aa" => 4.5,
        "aaa" => 7.0,
        _ => 3.0  // "standard"
    };

    public static string GetConfigPath() => ConfigPath;
    public static string GetConfigDir() => ConfigDir;

    /// <summary>
    /// Find a setting by key (case-insensitive).
    /// </summary>
    public static SettingInfo? FindSetting(string key)
        => AllSettings.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    // ── JSONC Support ────────────────────────────────────────────────────

    /// <summary>
    /// Strip // comments from JSONC text, respecting quoted strings.
    /// </summary>
    internal static string StripJsonComments(string jsonc)
    {
        var result = new System.Text.StringBuilder(jsonc.Length);
        bool inString = false;
        bool escaped = false;

        for (int i = 0; i < jsonc.Length; i++)
        {
            char c = jsonc[i];

            if (escaped)
            {
                result.Append(c);
                escaped = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                result.Append(c);
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                result.Append(c);
                continue;
            }

            if (!inString && c == '/' && i + 1 < jsonc.Length && jsonc[i + 1] == '/')
            {
                // Skip to end of line
                while (i < jsonc.Length && jsonc[i] != '\n') i++;
                if (i < jsonc.Length) result.Append('\n');
                continue;
            }

            result.Append(c);
        }

        return result.ToString();
    }

    /// <summary>
    /// Generate self-documenting JSONC config with all settings,
    /// grouped by category with descriptions.
    /// </summary>
    private string GenerateJsonc()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");

        string? lastCategory = null;

        for (int i = 0; i < AllSettings.Length; i++)
        {
            var s = AllSettings[i];
            var value = GetValue(s.Key);
            var isDefault = value == s.DefaultValue;

            // Category header
            if (s.Category != lastCategory)
            {
                if (lastCategory != null) sb.AppendLine();
                sb.AppendLine($"  // ── {s.Category} ──────────────────────────────────────────");
                lastCategory = s.Category;
            }

            // Description + valid values
            sb.AppendLine($"  // {s.Description}");

            // Format the value
            var jsonValue = FormatJsonValue(s.Key, value);
            sb.Append($"  \"{s.Key}\": {jsonValue}");

            // Comma after every setting — aliases section always follows
            sb.AppendLine(",");
        }

        // Aliases section
        if (Aliases.Count > 0 || true) // Always show aliases section
        {
            sb.AppendLine();
            sb.AppendLine("  // ── Aliases ──────────────────────────────────────────────");
            sb.AppendLine("  // Managed by: alias --save, unalias --save");
            sb.Append("  \"aliases\": ");
            if (Aliases.Count == 0)
            {
                sb.AppendLine("{}");
            }
            else
            {
                sb.AppendLine("{");
                var aliasList = Aliases.ToList();
                for (int i = 0; i < aliasList.Count; i++)
                {
                    var (key, cmd) = aliasList[i];
                    var escapedCmd = cmd.Replace("\\", "\\\\").Replace("\"", "\\\"");
                    sb.Append($"    \"{key}\": \"{escapedCmd}\"");
                    sb.AppendLine(i < aliasList.Count - 1 ? "," : "");
                }
                sb.AppendLine("  }");
            }
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string FormatJsonValue(string key, string value)
    {
        // Boolean values
        if (value == "true" || value == "false") return value;
        // Numeric values
        if (int.TryParse(value, out _)) return value;
        // String values
        return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    /// <summary>
    /// Create default init.rush if it doesn't exist.
    /// This is the startup script — exports, functions, prompt customization.
    /// </summary>
    private static void EnsureStartupScripts()
    {
        try
        {
            var initRush = Path.Combine(ConfigDir, "init.rush");
            if (!File.Exists(initRush))
            {
                File.WriteAllText(initRush, """
                    # ~/.config/rush/init.rush
                    # Startup script — runs on every shell launch.
                    # Full Rush syntax: exports, functions, control flow.
                    #
                    # Rush splits configuration across three layers:
                    #   config.json  — settings, saved aliases (managed by set/alias --save)
                    #   init.rush    — PATH, exports, functions, prompt (this file)
                    #   secrets.rush — API keys and tokens (never synced)
                    #
                    # Aliases:  use `alias ll='ls -la'` at the REPL (session-only)
                    #           or `alias --save ll='ls -la'` to persist to config.json.
                    #           Don't put alias commands here — they belong in config.json.

                    # ── PATH ─────────────────────────────────────────────────
                    # path add
                    #   /opt/homebrew/bin
                    #   ~/.local/bin
                    #   /usr/local/go/bin
                    # end

                    # ── Environment ──────────────────────────────────────────
                    # export EDITOR=vim
                    # export PAGER=less

                    # ── Secrets ──────────────────────────────────────────────
                    # API keys and tokens belong in secrets.rush (never synced).
                    # Create it:  touch ~/.config/rush/secrets.rush
                    #   export OPENAI_API_KEY="sk-..."
                    #   export GITHUB_TOKEN="ghp_..."

                    # ── OS-Specific ──────────────────────────────────────────
                    # if os == "macos"
                    #   export PATH="/opt/homebrew/bin:$PATH"
                    # end
                    # if os == "linux"
                    #   export PATH="/home/linuxbrew/.linuxbrew/bin:$PATH"
                    # end

                    # ── Functions ─────────────────────────────────────────────
                    # def mkcd(dir)
                    #   mkdir -p $dir
                    #   cd $dir
                    # end

                    # ── Custom Prompt ────────────────────────────────────────
                    # Override the default info line by defining rush_prompt().
                    # Default: ✓ 14:32  mark@macbook  rush/src  main*
                    #
                    # Available variables:
                    #   $exit_code    — numeric exit code of last command
                    #   $exit_failed  — true if last command failed
                    #   $is_ssh       — true if connected via SSH
                    #   $is_root      — true if running as root/admin
                    #
                    # Simple example (return a string):
                    # def rush_prompt()
                    #   "#{Time.now.ToString("HH:mm")} #{pwd} > "
                    # end
                    #
                    # Full example — replicates the default prompt:
                    # def rush_prompt()
                    #   if $exit_failed
                    #     if $exit_code > 1
                    #       Write-Host "✗ #{$exit_code}" -NoNewline -ForegroundColor Red
                    #     else
                    #       Write-Host "✗" -NoNewline -ForegroundColor Red
                    #     end
                    #   else
                    #     Write-Host "✓" -NoNewline -ForegroundColor Green
                    #   end
                    #
                    #   time = Time.now.ToString("HH:mm")
                    #   Write-Host " #{time}" -NoNewline -ForegroundColor DarkGray
                    #
                    #   user = env.USER
                    #   host = hostname
                    #   Write-Host "  #{user}" -NoNewline -ForegroundColor Cyan
                    #   Write-Host "@" -NoNewline -ForegroundColor DarkGray
                    #   if $is_ssh
                    #     Write-Host "#{host}" -NoNewline -ForegroundColor Yellow
                    #   else
                    #     Write-Host "#{host}" -NoNewline -ForegroundColor Blue
                    #   end
                    #
                    #   dir = $(pwd)
                    #   home = env.HOME
                    #   if dir.StartsWith(home)
                    #     dir = "~" + dir.Substring(home.Length)
                    #   end
                    #   parts = dir.Split("/")
                    #   if parts.Length > 3
                    #     dir = parts[-2] + "/" + parts[-1]
                    #   end
                    #   Write-Host "  #{dir}" -NoNewline -ForegroundColor White
                    #
                    #   branch = $(git branch --show-current 2>/dev/null).Trim()
                    #   unless branch.empty?
                    #     Write-Host "  #{branch}" -NoNewline -ForegroundColor Magenta
                    #     dirty = $(git status --porcelain 2>/dev/null).Trim()
                    #     unless dirty.empty?
                    #       Write-Host "*" -NoNewline -ForegroundColor Yellow
                    #     end
                    #   end
                    # end
                    """.Replace("                    ", ""));
            }
        }
        catch
        {
            // Best-effort — don't fail startup over sample files
        }
    }
}

/// <summary>
/// Metadata for a single configuration setting.
/// </summary>
public record SettingInfo(
    string Key,
    string Category,
    string DefaultValue,
    string ValidValues,
    string Description
);
