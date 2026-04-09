using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rush;

/// <summary>
/// Multi-transport config sync dispatcher and shared utilities.
/// Config dir: ~/.config/rush/
/// Sync metadata: ~/.config/rush/sync.json
///
/// Transports:
///   github — Private GitHub repo via gh CLI + git
///   ssh    — Remote server via SCP (key-based auth)
///   path   — Filesystem path (UNC share, USB, mounted drive)
///
/// Commands:
///   sync init [transport] [target]  — set up sync
///   sync push [--force]             — push config changes
///   sync pull [--force]             — pull config changes
///   sync status                     — show sync status
/// </summary>
public class ConfigSync
{
    internal static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "rush");

    internal static readonly string SyncMetaPath = Path.Combine(ConfigDir, "sync.json");

    /// <summary>Files tracked by sync (relative to ConfigDir).</summary>
    internal static readonly string[] SyncFiles = {
        "config.json",
        "init.rush"
    };

    internal static readonly string ManifestFile = ".sync-manifest";

    /// <summary>
    /// Handle `sync` subcommand from the REPL.
    /// </summary>
    public static bool HandleSync(string args)
    {
        var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var subcommand = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var rest = parts.Length > 1 ? string.Join(' ', parts[1..]) : "";
        bool force = rest.Contains("--force", StringComparison.OrdinalIgnoreCase);
        if (force) rest = rest.Replace("--force", "", StringComparison.OrdinalIgnoreCase).Trim();

        switch (subcommand)
        {
            case "init":
                return HandleInit(rest);
            case "push":
                return DispatchCommand("push", force);
            case "pull":
                return DispatchCommand("pull", force);
            case "status":
            case "":
                return DispatchCommand("status", false);
            default:
                PrintError($"Unknown sync command: {subcommand}");
                PrintMuted("  Usage: sync init [github|ssh|path] | push [--force] | pull [--force] | status");
                return false;
        }
    }

    /// <summary>
    /// Handle `sync init` — select transport and initialize.
    /// </summary>
    private static bool HandleInit(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var transport = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var target = parts.Length > 1 ? parts[1].Trim() : "";

        // If no transport specified, show interactive picker
        if (string.IsNullOrEmpty(transport))
        {
            PrintMuted("  Select sync method:");
            PrintMuted("    1. github — Private GitHub repo (requires gh + git)");
            PrintMuted("    2. ssh    — Remote server via SCP (requires SSH keys)");
            PrintMuted("    3. path   — Filesystem path (UNC share, USB, mounted drive)");
            Console.ForegroundColor = Theme.Current.Accent;
            Console.Write("  Choice [1-3]: ");
            Console.ResetColor();

            var choice = Console.ReadLine()?.Trim();
            transport = choice switch
            {
                "1" or "github" => "github",
                "2" or "ssh" => "ssh",
                "3" or "path" => "path",
                _ => ""
            };

            if (string.IsNullOrEmpty(transport))
            {
                PrintError("Invalid choice. Use: sync init github | sync init ssh | sync init path");
                return false;
            }
        }

        // Ensure config dir exists
        Directory.CreateDirectory(ConfigDir);

        return transport switch
        {
            "github" => SyncGitHub.Init(target),
            "ssh" => SyncSsh.Init(target),
            "path" => SyncPath.Init(target),
            _ => HandleUnknownTransport(transport)
        };
    }

    /// <summary>
    /// Dispatch push/pull/status to the correct transport based on saved metadata.
    /// </summary>
    private static bool DispatchCommand(string command, bool force)
    {
        var meta = LoadMeta();

        // Status is special — always works even if not initialized
        if (command == "status")
        {
            if (meta == null || !meta.Initialized)
            {
                PrintMuted("  Not synced. Run 'sync init' to set up config sync.");
                return true;
            }
        }
        else
        {
            if (meta == null || !meta.Initialized)
            {
                PrintError("Not initialized. Run 'sync init' first.");
                return false;
            }
        }

        return meta!.Transport switch
        {
            "github" => command switch
            {
                "push" => SyncGitHub.Push(force),
                "pull" => SyncGitHub.Pull(force),
                "status" => SyncGitHub.Status(),
                _ => false
            },
            "ssh" => command switch
            {
                "push" => SyncSsh.Push(force),
                "pull" => SyncSsh.Pull(force),
                "status" => SyncSsh.Status(),
                _ => false
            },
            "path" => command switch
            {
                "push" => SyncPath.Push(force),
                "pull" => SyncPath.Pull(force),
                "status" => SyncPath.Status(),
                _ => false
            },
            _ => HandleUnknownTransport(meta.Transport)
        };
    }

    private static bool HandleUnknownTransport(string transport)
    {
        PrintError($"Unknown sync transport: {transport}");
        PrintMuted("  Valid transports: github, ssh, path");
        return false;
    }

    // ── Shared Utilities (used by all transports) ───────────────────────

    /// <summary>Compute SHA256 hash of all sync files concatenated.</summary>
    internal static string ComputeSyncHash()
    {
        using var sha = SHA256.Create();
        var builder = new StringBuilder();
        foreach (var file in SyncFiles)
        {
            var fullPath = Path.Combine(ConfigDir, file);
            if (File.Exists(fullPath))
                builder.Append(File.ReadAllText(fullPath));
        }
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Load the remote sync manifest from a local file path.</summary>
    internal static SyncManifest? LoadManifest(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SyncManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch { return null; }
    }

    /// <summary>Save a sync manifest to a local file path.</summary>
    internal static void SaveManifest(string path, SyncManifest manifest)
    {
        try
        {
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(path, json);
        }
        catch { }
    }

    /// <summary>Build a manifest for the current local state.</summary>
    internal static SyncManifest BuildManifest()
    {
        return new SyncManifest
        {
            Hash = ComputeSyncHash(),
            Host = Environment.MachineName.ToLowerInvariant(),
            Timestamp = DateTime.UtcNow.ToString("o")
        };
    }

    /// <summary>
    /// Check for conflicts between local and remote state.
    /// Returns: NoChange, SafeToPull, SafeToPush, or Conflict.
    /// </summary>
    internal static ConflictResult CheckConflict(SyncManifest? remote, SyncMeta local)
    {
        var localHash = ComputeSyncHash();

        // No remote manifest — first push or missing manifest
        if (remote == null)
            return ConflictResult.SafeToPush;

        bool localChanged = localHash != local.SyncHash;
        bool remoteChanged = remote.Hash != local.SyncHash;

        if (!localChanged && !remoteChanged)
            return ConflictResult.NoChange;

        if (remoteChanged && !localChanged)
            return ConflictResult.SafeToPull;

        if (localChanged && !remoteChanged)
            return ConflictResult.SafeToPush;

        // Both changed — conflict
        return ConflictResult.Conflict;
    }

    /// <summary>Print conflict information.</summary>
    internal static void PrintConflict(SyncManifest remote)
    {
        PrintError("Conflict detected!");
        PrintMuted($"  Remote was last updated by '{remote.Host}'");
        if (DateTime.TryParse(remote.Timestamp, out var ts))
        {
            var ago = DateTime.UtcNow - ts;
            var agoStr = ago.TotalDays >= 1 ? $"{(int)ago.TotalDays}d ago" :
                         ago.TotalHours >= 1 ? $"{(int)ago.TotalHours}h ago" :
                         $"{(int)ago.TotalMinutes}m ago";
            PrintMuted($"  Remote last sync: {agoStr}");
        }
        PrintMuted("  Use 'sync push --force' to overwrite remote");
        PrintMuted("  Use 'sync pull --force' to overwrite local");
    }

    /// <summary>Format a "time ago" string from a UTC timestamp.</summary>
    internal static string FormatTimeAgo(string utcTimestamp)
    {
        if (!DateTime.TryParse(utcTimestamp, out var ts))
            return "unknown";
        var ago = DateTime.UtcNow - ts;
        return ago.TotalDays >= 1 ? $"{(int)ago.TotalDays}d ago" :
               ago.TotalHours >= 1 ? $"{(int)ago.TotalHours}h ago" :
               $"{(int)ago.TotalMinutes}m ago";
    }

    // ── Process Execution ───────────────────────────────────────────────

    internal static string? RunProcess(string command, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(command, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);
            return string.IsNullOrEmpty(output) ? stderr : output;
        }
        catch
        {
            return null;
        }
    }

    internal static string? RunGit(string args)
    {
        return RunProcess("git", $"-C \"{ConfigDir}\" {args}");
    }

    internal static string? RunGh(string args)
    {
        return RunProcess("gh", args);
    }

    // ── Metadata ────────────────────────────────────────────────────────

    internal static SyncMeta? LoadMeta()
    {
        try
        {
            if (!File.Exists(SyncMetaPath)) return null;
            var json = File.ReadAllText(SyncMetaPath);
            return JsonSerializer.Deserialize<SyncMeta>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch { return null; }
    }

    internal static void SaveMeta(SyncMeta meta)
    {
        try
        {
            var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(SyncMetaPath, json);
        }
        catch { }
    }

    // ── Print Helpers ───────────────────────────────────────────────────

    internal static void PrintMuted(string msg)
    {
        Console.ForegroundColor = Theme.Current.Muted;
        Console.WriteLine(msg);
        Console.ResetColor();
    }

    internal static void PrintAccent(string msg)
    {
        Console.ForegroundColor = Theme.Current.Accent;
        Console.WriteLine(msg);
        Console.ResetColor();
    }

    internal static void PrintError(string msg)
    {
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine($"  {msg}");
        Console.ResetColor();
    }

    /// <summary>Print tracked files with existence markers.</summary>
    internal static void PrintTrackedFiles()
    {
        PrintMuted("  Tracked files:");
        foreach (var file in SyncFiles)
        {
            var fullPath = Path.Combine(ConfigDir, file);
            var exists = File.Exists(fullPath);
            PrintMuted($"    {(exists ? "✓" : "·")} {file}");
        }
    }
}

// ── Data Models ─────────────────────────────────────────────────────────

/// <summary>Sync metadata stored in sync.json (local-only, not synced).</summary>
public record SyncMeta
{
    public string Transport { get; init; } = "github";
    public string Target { get; init; } = "";
    public string LastSync { get; init; } = "";
    public string LastSyncHost { get; init; } = "";
    public string SyncHash { get; init; } = "";
    public bool Initialized { get; init; }

    // Backward compat: old sync.json with "repo" field
    [System.Text.Json.Serialization.JsonInclude]
    public string Repo
    {
        get => Target;
        init => Target = value;
    }
}

/// <summary>Sync manifest — travels WITH the config files on the remote.</summary>
public record SyncManifest
{
    public string Hash { get; init; } = "";
    public string Host { get; init; } = "";
    public string Timestamp { get; init; } = "";
}

/// <summary>Result of conflict detection.</summary>
public enum ConflictResult
{
    NoChange,
    SafeToPull,
    SafeToPush,
    Conflict
}
