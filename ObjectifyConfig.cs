namespace Rush;

/// <summary>
/// Manages objectify parse hints for known commands.
/// Three-layer config: built-in defaults &lt; /etc/rush/objectify.rush &lt; ~/.config/rush/objectify.rush
/// When a known command is piped, Rush auto-injects an objectify block to convert text → PS objects.
/// </summary>
public class ObjectifyConfig
{
    private readonly Dictionary<string, string[]> _commands = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Built-in defaults — ships with Rush.</summary>
    private static readonly Dictionary<string, string[]> BuiltInDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["netstat"] = new[] { "--fixed" },
        ["ss"] = Array.Empty<string>(),
        ["lsof"] = new[] { "--fixed" },
        ["free"] = new[] { "--skip", "1" },
        ["docker ps"] = new[] { "--delim", @"\s{2,}" },
        ["docker images"] = new[] { "--delim", @"\s{2,}" },
        ["kubectl get"] = Array.Empty<string>(),
        ["mount"] = Array.Empty<string>(),
    };

    private static readonly string UserConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "rush");

    private static readonly string UserConfigPath = Path.Combine(UserConfigDir, "objectify.rush");
    private const string SystemConfigPath = "/etc/rush/objectify.rush";

    private ObjectifyConfig() { }

    /// <summary>
    /// Load config from all three layers, merging in order:
    /// built-in defaults → /etc/rush/objectify.rush → ~/.config/rush/objectify.rush
    /// Later layers override earlier ones for the same command.
    /// </summary>
    internal static ObjectifyConfig Load()
    {
        var config = new ObjectifyConfig();

        // Layer 1: built-in defaults
        foreach (var kv in BuiltInDefaults)
            config._commands[kv.Key] = kv.Value;

        // Layer 2: system config
        LoadFile(SystemConfigPath, config._commands);

        // Layer 3: user config
        LoadFile(UserConfigPath, config._commands);

        return config;
    }

    /// <summary>
    /// Check if a command has a known objectify hint.
    /// Tries 2-word match first ("docker ps"), then 1-word ("netstat").
    /// </summary>
    internal bool TryGetHint(string commandLine, out string[] flags)
    {
        // Try 2-word match: "docker ps" from "docker ps -a"
        var parts = commandLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            var twoWord = $"{parts[0]} {parts[1]}";
            if (_commands.TryGetValue(twoWord, out var twoFlags))
            {
                flags = twoFlags;
                return true;
            }
        }

        // Try 1-word match: "netstat" from "netstat -an"
        if (parts.Length >= 1 && _commands.TryGetValue(parts[0], out var oneFlags))
        {
            flags = oneFlags;
            return true;
        }

        flags = Array.Empty<string>();
        return false;
    }

    /// <summary>
    /// Save a command hint to the user's config file (~/.config/rush/objectify.rush).
    /// Creates the file and directory if they don't exist.
    /// If the command already exists, replaces its entry.
    /// </summary>
    internal void SaveUserHint(string command, string[] flags)
    {
        // Update in-memory
        _commands[command] = flags;

        // Ensure directory exists
        Directory.CreateDirectory(UserConfigDir);

        var flagStr = flags.Length > 0 ? string.Join(' ', flags) : "";
        var entry = $"{command}    {flagStr}".TrimEnd();

        // Read existing file, replace or append
        var lines = new List<string>();
        if (File.Exists(UserConfigPath))
        {
            foreach (var line in File.ReadAllLines(UserConfigPath))
            {
                var parsed = ParseLine(line);
                if (parsed.HasValue && parsed.Value.command.Equals(command, StringComparison.OrdinalIgnoreCase))
                    continue; // Skip old entry — we'll append the new one
                lines.Add(line);
            }
        }
        lines.Add(entry);

        File.WriteAllLines(UserConfigPath, lines);
    }

    /// <summary>Get all configured commands (for tab completion or help).</summary>
    internal IEnumerable<string> GetCommandNames() => _commands.Keys.OrderBy(k => k);

    // ── File Parsing ─────────────────────────────────────────────────────

    private static void LoadFile(string path, Dictionary<string, string[]> target)
    {
        if (!File.Exists(path)) return;

        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var parsed = ParseLine(line);
                if (parsed.HasValue)
                    target[parsed.Value.command] = parsed.Value.flags;
            }
        }
        catch
        {
            // Best-effort — don't crash on malformed config
        }
    }

    /// <summary>
    /// Parse a config line into command + flags.
    /// Format: "command    flags..."  or  "docker ps    --delim \s{2,}"
    /// Lines starting with # are comments. Empty lines are skipped.
    /// </summary>
    private static (string command, string[] flags)? ParseLine(string line)
    {
        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
            return null;

        // Split on first run of 2+ spaces or tab — separates command from flags
        // This allows "docker ps" (single space) as a command name
        var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^(.+?)(?:\s{2,}|\t)(.*)$");
        if (match.Success)
        {
            var command = match.Groups[1].Value.Trim();
            var flagStr = match.Groups[2].Value.Trim();
            var flags = string.IsNullOrEmpty(flagStr)
                ? Array.Empty<string>()
                : SplitFlags(flagStr);
            return (command, flags);
        }

        // No flags — just a command name (use default objectify behavior)
        return (trimmed, Array.Empty<string>());
    }

    /// <summary>Split flag string respecting quotes: --delim "\s{2,}" → ["--delim", "\s{2,}"]</summary>
    private static string[] SplitFlags(string input)
    {
        var flags = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuote = false;
        char quoteChar = '"';

        foreach (var ch in input)
        {
            if (!inQuote && (ch == '"' || ch == '\''))
            {
                inQuote = true;
                quoteChar = ch;
                continue; // Don't include the quote character
            }
            if (inQuote && ch == quoteChar)
            {
                inQuote = false;
                continue;
            }
            if (ch == ' ' && !inQuote)
            {
                if (current.Length > 0)
                {
                    flags.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(ch);
        }

        if (current.Length > 0)
            flags.Add(current.ToString());

        return flags.ToArray();
    }
}
