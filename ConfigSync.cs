using System.Diagnostics;
using System.Text.Json;

namespace Rush;

/// <summary>
/// Syncs Rush config files to/from a private GitHub repo via the gh CLI.
/// Config dir: ~/.config/rush/
/// Sync metadata: ~/.config/rush/sync.json
///
/// Commands:
///   rush sync init    — create private rush-config repo and link
///   rush sync push    — commit and push config changes
///   rush sync pull    — pull latest config from GitHub
///   rush sync status  — show sync status
///   rush sync         — alias for status
/// </summary>
public class ConfigSync
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "rush");

    private static readonly string SyncMetaPath = Path.Combine(ConfigDir, "sync.json");

    /// <summary>Files tracked by sync (relative to ConfigDir).</summary>
    private static readonly string[] SyncFiles = {
        "config.json",
        "config.rush",
        "init.rush"
    };

    /// <summary>
    /// Handle `sync` subcommand from the REPL.
    /// </summary>
    public static bool HandleSync(string args)
    {
        var subcommand = args.Trim().ToLowerInvariant();

        switch (subcommand)
        {
            case "init":
                return Init();
            case "push":
                return Push();
            case "pull":
                return Pull();
            case "status":
            case "":
                return Status();
            default:
                PrintError($"Unknown sync command: {subcommand}");
                PrintMuted("  Usage: sync init | push | pull | status");
                return false;
        }
    }

    /// <summary>
    /// Initialize: create a private GitHub repo and set up the config dir as a git repo.
    /// </summary>
    private static bool Init()
    {
        // Check gh CLI is available and authenticated
        if (!CheckGh()) return false;

        // Check if already initialized
        if (IsGitRepo())
        {
            var meta = LoadMeta();
            if (meta != null)
            {
                PrintMuted($"  Already syncing to: {meta.Repo}");
                PrintMuted("  Use 'sync push' or 'sync pull'");
                return true;
            }
        }

        // Get GitHub username
        var username = RunGh("api user -q .login")?.Trim();
        if (string.IsNullOrEmpty(username))
        {
            PrintError("Could not determine GitHub username. Run 'gh auth login' first.");
            return false;
        }

        var repoName = "rush-config";
        var fullRepo = $"{username}/{repoName}";

        // Check if repo already exists on GitHub
        var repoCheck = RunGh($"repo view {fullRepo} --json name 2>&1");
        bool repoExists = repoCheck != null && repoCheck.Contains("\"name\"");

        if (!repoExists)
        {
            // Create private repo
            PrintMuted($"  Creating private repo: {fullRepo}");
            var result = RunGh($"repo create {repoName} --private --description \"Rush shell configuration\" --confirm");
            if (result == null)
            {
                PrintError("Failed to create GitHub repo.");
                return false;
            }
        }
        else
        {
            PrintMuted($"  Found existing repo: {fullRepo}");
        }

        // Initialize git in config dir (if not already)
        if (!IsGitRepo())
        {
            RunGit("init");
            RunGit("branch -M main");
        }

        // Set remote
        RunGit("remote remove origin 2>/dev/null");
        RunGit($"remote add origin git@github.com:{fullRepo}.git");

        // Create .gitignore for sensitive files
        var gitignorePath = Path.Combine(ConfigDir, ".gitignore");
        if (!File.Exists(gitignorePath))
        {
            File.WriteAllText(gitignorePath, "# Sensitive files\napi-keys\nsecrets.*\n*.key\n*.pem\nsync.json\n");
        }

        // Try to pull existing content first
        var pullResult = RunGit("pull origin main --allow-unrelated-histories 2>&1");

        // Stage existing config files
        foreach (var file in SyncFiles)
        {
            var fullPath = Path.Combine(ConfigDir, file);
            if (File.Exists(fullPath))
                RunGit($"add {file}");
        }
        RunGit("add .gitignore");

        // Initial commit if there are changes
        RunGit("diff --cached --quiet 2>&1");
        // Always try to commit — git will no-op if nothing to commit
        RunGit($"commit -m \"Initial rush config sync\" --allow-empty 2>&1");
        RunGit("push -u origin main 2>&1");

        // Save sync metadata
        SaveMeta(new SyncMeta
        {
            Repo = fullRepo,
            LastSync = DateTime.UtcNow.ToString("o"),
            Initialized = true
        });

        PrintAccent($"  Syncing to: github.com/{fullRepo}");
        PrintMuted("  Use 'sync push' to upload changes, 'sync pull' to download");
        return true;
    }

    /// <summary>
    /// Push local config changes to GitHub.
    /// </summary>
    private static bool Push()
    {
        if (!EnsureInitialized()) return false;

        // Stage tracked config files
        foreach (var file in SyncFiles)
        {
            var fullPath = Path.Combine(ConfigDir, file);
            if (File.Exists(fullPath))
                RunGit($"add {file}");
        }
        RunGit("add .gitignore");

        // Check if there are changes to commit
        var status = RunGit("status --porcelain")?.Trim();
        if (string.IsNullOrEmpty(status))
        {
            PrintMuted("  Already up to date, nothing to push");
            return true;
        }

        // Commit with timestamp
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var hostname = Environment.MachineName.ToLowerInvariant();
        RunGit($"commit -m \"sync from {hostname} at {timestamp}\"");

        // Push
        var result = RunGit("push origin main 2>&1");
        if (result != null && result.Contains("error"))
        {
            PrintError("Push failed. Try 'sync pull' first to merge remote changes.");
            return false;
        }

        SaveMeta(LoadMeta()! with { LastSync = DateTime.UtcNow.ToString("o") });

        PrintAccent("  Config pushed to GitHub");
        if (!string.IsNullOrEmpty(status))
        {
            foreach (var line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                PrintMuted($"    {line.Trim()}");
        }
        return true;
    }

    /// <summary>
    /// Pull latest config from GitHub.
    /// </summary>
    private static bool Pull()
    {
        if (!EnsureInitialized()) return false;

        var result = RunGit("pull origin main 2>&1");

        if (result != null && result.Contains("Already up to date"))
        {
            PrintMuted("  Already up to date");
        }
        else if (result != null && result.Contains("CONFLICT"))
        {
            PrintError("Merge conflict detected. Resolve manually in ~/.config/rush/");
            return false;
        }
        else
        {
            PrintAccent("  Config pulled from GitHub");
            PrintMuted("  Run 'reload' to apply changes");
        }

        SaveMeta(LoadMeta()! with { LastSync = DateTime.UtcNow.ToString("o") });
        return true;
    }

    /// <summary>
    /// Show sync status.
    /// </summary>
    private static bool Status()
    {
        var meta = LoadMeta();
        if (meta == null || !meta.Initialized)
        {
            PrintMuted("  Not synced. Run 'sync init' to set up GitHub sync.");
            return true;
        }

        PrintAccent($"  Repo: github.com/{meta.Repo}");

        // Show last sync time
        if (DateTime.TryParse(meta.LastSync, out var lastSync))
        {
            var ago = DateTime.UtcNow - lastSync;
            var agoStr = ago.TotalDays >= 1 ? $"{(int)ago.TotalDays}d ago" :
                         ago.TotalHours >= 1 ? $"{(int)ago.TotalHours}h ago" :
                         $"{(int)ago.TotalMinutes}m ago";
            PrintMuted($"  Last sync: {agoStr}");
        }

        // Show local changes
        var status = RunGit("status --porcelain")?.Trim();
        if (!string.IsNullOrEmpty(status))
        {
            PrintMuted("  Local changes:");
            foreach (var line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                PrintMuted($"    {line.Trim()}");
        }
        else
        {
            PrintMuted("  No local changes");
        }

        // Show tracked files
        PrintMuted("  Tracked files:");
        foreach (var file in SyncFiles)
        {
            var fullPath = Path.Combine(ConfigDir, file);
            var exists = File.Exists(fullPath);
            PrintMuted($"    {(exists ? "✓" : "·")} {file}");
        }

        return true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static bool EnsureInitialized()
    {
        var meta = LoadMeta();
        if (meta == null || !meta.Initialized)
        {
            PrintError("Not initialized. Run 'sync init' first.");
            return false;
        }
        if (!CheckGh()) return false;
        return true;
    }

    private static bool CheckGh()
    {
        try
        {
            var result = RunProcess("gh", "auth status");
            if (result == null || !result.Contains("Logged in"))
            {
                PrintError("GitHub CLI not authenticated. Run 'gh auth login' first.");
                return false;
            }
            return true;
        }
        catch
        {
            PrintError("GitHub CLI (gh) not found. Install with: brew install gh");
            return false;
        }
    }

    private static bool IsGitRepo()
    {
        return Directory.Exists(Path.Combine(ConfigDir, ".git"));
    }

    private static string? RunGh(string args)
    {
        return RunProcess("gh", args);
    }

    private static string? RunGit(string args)
    {
        return RunProcess("git", $"-C \"{ConfigDir}\" {args}");
    }

    private static string? RunProcess(string command, string args)
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

    private static SyncMeta? LoadMeta()
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

    private static void SaveMeta(SyncMeta meta)
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

    private static void PrintMuted(string msg)
    {
        Console.ForegroundColor = Theme.Current.Muted;
        Console.WriteLine(msg);
        Console.ResetColor();
    }

    private static void PrintAccent(string msg)
    {
        Console.ForegroundColor = Theme.Current.Accent;
        Console.WriteLine(msg);
        Console.ResetColor();
    }

    private static void PrintError(string msg)
    {
        Console.ForegroundColor = Theme.Current.Error;
        Console.Error.WriteLine($"  {msg}");
        Console.ResetColor();
    }
}

/// <summary>Sync metadata stored in sync.json.</summary>
public record SyncMeta
{
    public string Repo { get; init; } = "";
    public string LastSync { get; init; } = "";
    public bool Initialized { get; init; }
}
