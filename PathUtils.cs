namespace Rush;

/// <summary>
/// Cross-platform path utilities.
/// Normalizes paths for display (backslash → forward slash on Windows)
/// and provides platform-aware path list separators.
/// </summary>
internal static class PathUtils
{
    /// <summary>
    /// Normalize a path for display: backslash → forward slash on Windows.
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
}
