namespace Rush;

/// <summary>
/// Read/write ~/.config/rush/secrets.rush — plain Rush `export KEY="value"` lines.
/// Used by `set --secret` to persist API keys and tokens.
/// The file is sourced at startup by RunStartupScripts.
/// </summary>
public static class SecretsFile
{
    private static readonly string SecretsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "rush", "secrets.rush");

    /// <summary>
    /// Set or update an export in secrets.rush.
    /// If the key already exists, updates the value in-place.
    /// Otherwise appends a new export line.
    /// </summary>
    public static void SetExport(string key, string value)
    {
        var dir = Path.GetDirectoryName(SecretsPath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var escapedValue = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var exportLine = $"export {key}=\"{escapedValue}\"";

        if (!File.Exists(SecretsPath))
        {
            // Create with header comment
            File.WriteAllText(SecretsPath,
                $"# ~/.config/rush/secrets.rush\n" +
                $"# API keys and tokens — never synced.\n" +
                $"# Managed by: set --secret KEY \"value\"\n\n" +
                $"{exportLine}\n");
            return;
        }

        // Read existing lines, update or append
        var lines = File.ReadAllLines(SecretsPath).ToList();
        var prefix = $"export {key}=";
        var found = false;

        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith(prefix, StringComparison.Ordinal))
            {
                lines[i] = exportLine;
                found = true;
                break;
            }
        }

        if (!found)
        {
            // Ensure trailing newline before appending
            if (lines.Count > 0 && !string.IsNullOrEmpty(lines[^1]))
                lines.Add("");
            lines.Add(exportLine);
        }

        File.WriteAllText(SecretsPath, string.Join("\n", lines) + "\n");
    }

    /// <summary>
    /// Get the path to the secrets file (for display in help/error messages).
    /// </summary>
    public static string GetPath() => SecretsPath;
}
