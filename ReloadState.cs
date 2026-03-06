using System.Management.Automation.Runspaces;
using System.Text.Json;

namespace Rush;

/// <summary>
/// Captures, serializes, and restores session state for hot-reload.
/// Used by `reload --hard` to preserve user state across binary restarts.
/// </summary>
public static class ReloadState
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "rush", ".reload-state.json");

    /// <summary>
    /// Session state snapshot — everything needed to restore a session.
    /// </summary>
    public class SessionState
    {
        public int Version { get; set; } = 1;
        public string Cwd { get; set; } = "";
        public Dictionary<string, string> Env { get; set; } = new();
        public Dictionary<string, object?> Variables { get; set; } = new();
        public Dictionary<string, string> Aliases { get; set; } = new();
        public string? PreviousDirectory { get; set; }
        public bool SetE { get; set; }
        public bool SetX { get; set; }
        public bool SetPipefail { get; set; }
    }

    // ── Baseline Snapshots ─────────────────────────────────────────────
    // Captured at startup to distinguish user variables from system ones.

    private static HashSet<string>? _baselineVars;
    private static HashSet<string>? _baselineEnv;

    /// <summary>
    /// Call after init.rush/secrets.rush to snapshot the baseline state.
    /// Variables created after this point are considered "user-defined".
    /// </summary>
    public static void CaptureBaseline(Runspace runspace)
    {
        try
        {
            // Snapshot PowerShell variable names
            using var ps = System.Management.Automation.PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript("Get-Variable | ForEach-Object { $_.Name }");
            var results = ps.Invoke();
            _baselineVars = new HashSet<string>(
                results.Select(r => r?.ToString() ?? ""),
                StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _baselineVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // Snapshot environment variable names
        _baselineEnv = new HashSet<string>(
            Environment.GetEnvironmentVariables().Keys.Cast<string>(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Capture current session state for serialization.
    /// Only captures user-defined variables (those not in the baseline).
    /// </summary>
    public static SessionState Capture(Runspace runspace, RushConfig config,
        string? previousDirectory, bool setE, bool setX, bool setPipefail)
    {
        var state = new SessionState
        {
            Cwd = Environment.CurrentDirectory,
            PreviousDirectory = previousDirectory,
            SetE = setE,
            SetX = setX,
            SetPipefail = setPipefail,
            Aliases = new Dictionary<string, string>(config.Aliases)
        };

        // Capture user-defined environment variables (diff against baseline)
        if (_baselineEnv != null)
        {
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                var key = entry.Key.ToString()!;
                if (!_baselineEnv.Contains(key))
                {
                    state.Env[key] = entry.Value?.ToString() ?? "";
                }
            }
        }

        // Capture user-defined PowerShell variables (diff against baseline)
        if (_baselineVars != null)
        {
            try
            {
                using var ps = System.Management.Automation.PowerShell.Create();
                ps.Runspace = runspace;
                ps.AddScript("Get-Variable | ForEach-Object { @{ Name = $_.Name; Value = $_.Value } }");
                var results = ps.Invoke();

                foreach (var result in results)
                {
                    if (result?.BaseObject is System.Collections.Hashtable ht)
                    {
                        var name = ht["Name"]?.ToString();
                        var value = ht["Value"];
                        if (name != null && !_baselineVars.Contains(name))
                        {
                            // Only serialize simple types
                            state.Variables[name] = value switch
                            {
                                string s => s,
                                int i => i,
                                long l => l,
                                double d => d,
                                bool b => b,
                                _ => value?.ToString()
                            };
                        }
                    }
                }
            }
            catch { }
        }

        return state;
    }

    /// <summary>
    /// Serialize and save state to disk.
    /// </summary>
    public static void Save(SessionState state)
    {
        var dir = Path.GetDirectoryName(StatePath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(StatePath, json);
    }

    /// <summary>
    /// Load state from disk. Returns null if no state file exists.
    /// Deletes the state file after reading (one-shot).
    /// </summary>
    public static SessionState? Load()
    {
        if (!File.Exists(StatePath)) return null;

        try
        {
            var json = File.ReadAllText(StatePath);
            var state = JsonSerializer.Deserialize<SessionState>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            });
            return state;
        }
        finally
        {
            // Always clean up — one-shot file
            try { File.Delete(StatePath); } catch { }
        }
    }

    /// <summary>
    /// Restore session state from a saved snapshot.
    /// </summary>
    public static void Restore(SessionState state, Runspace runspace,
        ref string? previousDirectory, ref bool setE, ref bool setX, ref bool setPipefail)
    {
        // Restore cwd
        if (!string.IsNullOrEmpty(state.Cwd) && Directory.Exists(state.Cwd))
        {
            Environment.CurrentDirectory = state.Cwd;
            try
            {
                using var ps = System.Management.Automation.PowerShell.Create();
                ps.Runspace = runspace;
                ps.AddScript($"Set-Location '{state.Cwd.Replace("'", "''")}'");
                ps.Invoke();
            }
            catch { }
        }

        // Restore previous directory
        previousDirectory = state.PreviousDirectory;

        // Restore shell flags
        setE = state.SetE;
        setX = state.SetX;
        setPipefail = state.SetPipefail;

        // Restore environment variables
        foreach (var (key, value) in state.Env)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        // Restore PowerShell variables
        foreach (var (name, value) in state.Variables)
        {
            try
            {
                runspace.SessionStateProxy.SetVariable(name, value);
            }
            catch { }
        }
    }
}
