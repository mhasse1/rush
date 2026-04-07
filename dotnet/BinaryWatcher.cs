namespace Rush;

/// <summary>
/// Tracks whether the installed rush binary has been updated since this
/// process started.  Used by the prompt to show a [stale] indicator
/// so the user knows to run 'reload --hard'.
///
/// Detection: compares File.GetLastWriteTimeUtc of the resolved binary
/// at startup vs now.  Throttled to one stat() call every 30 seconds.
/// </summary>
public class BinaryWatcher
{
    private readonly string? _processPath;    // original path (may be symlink)
    private readonly string? _resolvedPath;    // resolved target at startup
    private readonly DateTime _startupMtime;
    private DateTime _lastCheckTime;
    private bool _isStale;
    private bool _hintShown;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    public BinaryWatcher()
    {
        try
        {
            _processPath = Environment.ProcessPath;
            if (_processPath == null)
            {
                _resolvedPath = null;
                return;
            }

            _resolvedPath = ResolveSymlinks(_processPath);
            _startupMtime = File.GetLastWriteTimeUtc(_resolvedPath);
            _lastCheckTime = DateTime.UtcNow;
        }
        catch
        {
            // Permission error, file not found, etc. — disable watching.
            _resolvedPath = null;
        }
    }

    /// <summary>
    /// Internal constructor for testability.
    /// </summary>
    internal BinaryWatcher(string resolvedPath, DateTime startupMtime)
    {
        _resolvedPath = resolvedPath;
        _startupMtime = startupMtime;
        _lastCheckTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Whether the binary on disk has been modified since startup.
    /// Throttled: only stats the file every 30 seconds.
    /// Once stale, stays stale (sticky) — no further stat calls.
    /// </summary>
    public bool IsStale
    {
        get
        {
            if (_resolvedPath == null || _isStale)
                return _isStale;

            var now = DateTime.UtcNow;
            if (now - _lastCheckTime < CheckInterval)
                return false;

            _lastCheckTime = now;

            try
            {
                // Check if the symlink now points to a different file
                // (e.g., install.sh changed the target after a framework upgrade)
                if (_processPath != null)
                {
                    var currentResolved = ResolveSymlinks(_processPath);
                    if (currentResolved != _resolvedPath)
                    {
                        _isStale = true;
                        return _isStale;
                    }
                }

                var currentMtime = File.GetLastWriteTimeUtc(_resolvedPath);
                if (currentMtime != _startupMtime)
                    _isStale = true;
            }
            catch
            {
                // File disappeared or permission error — stop checking
                // but don't falsely report stale.
            }

            return _isStale;
        }
    }

    /// <summary>
    /// Returns true exactly once — the first time staleness is detected.
    /// Used to print a one-time explanatory hint in the prompt.
    /// </summary>
    public bool ShouldShowHint()
    {
        if (_isStale && !_hintShown)
        {
            _hintShown = true;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Resolve a path through its full symlink chain to the final target.
    /// Handles both standard install (/usr/local/bin/rush → /usr/local/lib/rush/rush)
    /// and dev mode (→ bin/Release/.../publish/rush).
    /// </summary>
    private static string ResolveSymlinks(string path)
    {
        var info = new FileInfo(path);
        var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
        return resolved?.FullName ?? Path.GetFullPath(path);
    }
}
