namespace Rush;

/// <summary>
/// Cross-platform path utilities.
/// Normalizes paths for display (backslash → forward slash on Windows)
/// and provides platform-aware path list separators.
/// </summary>
internal static class PathUtils
{
    /// <summary>
    /// Normalize a single path for display: backslash → forward slash on Windows.
    /// No-op on Unix where paths already use forward slashes.
    /// </summary>
    internal static string Normalize(string path)
        => OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;

    /// <summary>
    /// Platform-aware separator for PATH, CDPATH, and similar colon/semicolon-separated lists.
    /// Windows uses ';', Unix uses ':'.
    /// </summary>
    internal static char PathListSeparator
        => OperatingSystem.IsWindows() ? ';' : ':';

    // ── PATH Normalization (Windows ↔ Rush) ─────────────────────────

    /// <summary>
    /// Import a native PATH string into Rush-normalized format.
    /// Windows: C:\Program Files\Git\cmd;C:\bin → C:/Program\ Files/Git/cmd:C:/bin
    /// Unix: no-op (already in the right format).
    /// </summary>
    internal static string ImportPath(string nativePath)
    {
        if (!OperatingSystem.IsWindows()) return nativePath;

        var entries = nativePath.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var normalized = entries.Select(e =>
        {
            var p = e.TrimEnd('\\', '/').Replace('\\', '/');
            if (p.Contains(' '))
                p = p.Replace(" ", "\\ ");
            return p;
        });
        return string.Join(":", normalized);
    }

    /// <summary>
    /// Export a Rush-normalized PATH string back to native format.
    /// Rush: C:/Program\ Files/Git/cmd:C:/bin → C:\Program Files\Git\cmd;C:\bin
    /// Unix: no-op.
    /// </summary>
    internal static string ExportPath(string rushPath)
    {
        if (!OperatingSystem.IsWindows()) return rushPath;

        var entries = SplitRushPath(rushPath);
        var native = entries.Select(e =>
        {
            var p = e.Replace("\\ ", " ").Replace('/', '\\');
            return p;
        });
        return string.Join(";", native);
    }

    /// <summary>
    /// Split a Rush-normalized PATH on colons, respecting drive letters.
    /// C:/Program\ Files:C:/bin → ["C:/Program\ Files", "C:/bin"]
    /// Handles drive letters (C:/) which contain colons but aren't separators.
    /// </summary>
    internal static string[] SplitRushPath(string rushPath)
    {
        if (!OperatingSystem.IsWindows())
            return rushPath.Split(':', StringSplitOptions.RemoveEmptyEntries);

        // On Windows, colons after drive letters (C:/) aren't separators
        var entries = new List<string>();
        var current = new System.Text.StringBuilder();

        for (int i = 0; i < rushPath.Length; i++)
        {
            var ch = rushPath[i];
            if (ch == ':')
            {
                // Check if this is a drive letter colon (single letter before, / after)
                if (current.Length == 1 && char.IsLetter(current[0])
                    && i + 1 < rushPath.Length && rushPath[i + 1] == '/')
                {
                    current.Append(ch); // part of drive letter, not a separator
                }
                else
                {
                    if (current.Length > 0)
                    {
                        entries.Add(current.ToString());
                        current.Clear();
                    }
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            entries.Add(current.ToString());

        return entries.ToArray();
    }

    /// <summary>
    /// Format a single path entry for Rush display (forward slashes, escaped spaces, trimmed).
    /// </summary>
    internal static string FormatForDisplay(string path)
    {
        var result = Normalize(path).TrimEnd('/');
        if (result.Contains(' '))
            result = result.Replace(" ", "\\ ");
        return result;
    }
}
