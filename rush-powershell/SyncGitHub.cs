namespace Rush;

/// <summary>
/// GitHub transport for config sync.
/// Uses gh CLI + git for push/pull.
/// </summary>
internal static class SyncGitHub
{
    /// <summary>
    /// Initialize: create a private GitHub repo and set up the config dir as a git repo.
    /// </summary>
    internal static bool Init(string target)
    {
        // Check gh CLI is available and authenticated
        if (!CheckGh()) return false;

        // Check if already initialized
        if (IsGitRepo())
        {
            var existing = ConfigSync.LoadMeta();
            if (existing != null && existing.Initialized)
            {
                ConfigSync.PrintMuted($"  Already syncing to: github.com/{existing.Target}");
                ConfigSync.PrintMuted("  Use 'sync push' or 'sync pull'");
                return true;
            }
        }

        // Get GitHub username
        var username = ConfigSync.RunGh("api user -q .login")?.Trim();
        if (string.IsNullOrEmpty(username))
        {
            ConfigSync.PrintError("Could not determine GitHub username. Run 'gh auth login' first.");
            return false;
        }

        var repoName = "rush-config";
        var fullRepo = $"{username}/{repoName}";

        // Check if repo already exists on GitHub
        var repoCheck = ConfigSync.RunGh($"repo view {fullRepo} --json name");
        bool repoExists = repoCheck != null && repoCheck.Contains("\"name\"");

        if (!repoExists)
        {
            ConfigSync.PrintMuted($"  Creating private repo: {fullRepo}");
            var result = ConfigSync.RunGh($"repo create {repoName} --private --description \"Rush shell configuration\" --confirm");
            if (result == null)
            {
                ConfigSync.PrintError("Failed to create GitHub repo.");
                return false;
            }
        }
        else
        {
            ConfigSync.PrintMuted($"  Found existing repo: {fullRepo}");
        }

        // Initialize git in config dir (if not already)
        if (!IsGitRepo())
        {
            ConfigSync.RunGit("init");
            ConfigSync.RunGit("branch -M main");
        }

        // Set remote
        ConfigSync.RunGit("remote remove origin");
        ConfigSync.RunGit($"remote add origin git@github.com:{fullRepo}.git");

        // Create .gitignore for sensitive files
        var gitignorePath = Path.Combine(ConfigSync.ConfigDir, ".gitignore");
        if (!File.Exists(gitignorePath))
        {
            File.WriteAllText(gitignorePath,
                "# Sensitive files\nsecrets.rush\nsecrets.*\napi-keys\n*.key\n*.pem\nsync.json\n.sync-manifest\n");
        }

        // Try to pull existing content first
        ConfigSync.RunGit("pull origin main --allow-unrelated-histories");

        // Stage existing config files
        foreach (var file in ConfigSync.SyncFiles)
        {
            var fullPath = Path.Combine(ConfigSync.ConfigDir, file);
            if (File.Exists(fullPath))
                ConfigSync.RunGit($"add {file}");
        }
        ConfigSync.RunGit("add .gitignore");

        // Write initial manifest
        var manifest = ConfigSync.BuildManifest();
        var manifestPath = Path.Combine(ConfigSync.ConfigDir, ConfigSync.ManifestFile);
        ConfigSync.SaveManifest(manifestPath, manifest);

        // Initial commit
        ConfigSync.RunGit($"commit -m \"Initial rush config sync\" --allow-empty");
        ConfigSync.RunGit("push -u origin main");

        // Save sync metadata
        ConfigSync.SaveMeta(new SyncMeta
        {
            Transport = "github",
            Target = fullRepo,
            LastSync = DateTime.UtcNow.ToString("o"),
            LastSyncHost = Environment.MachineName.ToLowerInvariant(),
            SyncHash = manifest.Hash,
            Initialized = true
        });

        ConfigSync.PrintAccent($"  Syncing to: github.com/{fullRepo}");
        ConfigSync.PrintMuted("  Use 'sync push' to upload changes, 'sync pull' to download");
        return true;
    }

    /// <summary>Push local config changes to GitHub.</summary>
    internal static bool Push(bool force)
    {
        if (!EnsureReady()) return false;
        var meta = ConfigSync.LoadMeta()!;

        // Stage tracked config files
        foreach (var file in ConfigSync.SyncFiles)
        {
            var fullPath = Path.Combine(ConfigSync.ConfigDir, file);
            if (File.Exists(fullPath))
                ConfigSync.RunGit($"add {file}");
        }
        ConfigSync.RunGit("add .gitignore");

        // Check if there are changes to commit
        var status = ConfigSync.RunGit("status --porcelain")?.Trim();
        if (string.IsNullOrEmpty(status))
        {
            ConfigSync.PrintMuted("  Already up to date, nothing to push");
            return true;
        }

        // Update manifest before committing
        var manifest = ConfigSync.BuildManifest();
        var manifestPath = Path.Combine(ConfigSync.ConfigDir, ConfigSync.ManifestFile);
        ConfigSync.SaveManifest(manifestPath, manifest);

        // Commit with timestamp
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var hostname = Environment.MachineName.ToLowerInvariant();
        ConfigSync.RunGit($"commit -m \"sync from {hostname} at {timestamp}\"");

        // Push (force if requested)
        var pushCmd = force ? "push origin main --force" : "push origin main";
        var result = ConfigSync.RunGit(pushCmd);
        if (result != null && result.Contains("error") && !force)
        {
            ConfigSync.PrintError("Push failed. Try 'sync pull' first or use 'sync push --force'.");
            return false;
        }

        ConfigSync.SaveMeta(meta with
        {
            LastSync = DateTime.UtcNow.ToString("o"),
            LastSyncHost = hostname,
            SyncHash = manifest.Hash
        });

        ConfigSync.PrintAccent("  Config pushed to GitHub");
        foreach (var line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            ConfigSync.PrintMuted($"    {line.Trim()}");
        return true;
    }

    /// <summary>Pull latest config from GitHub.</summary>
    internal static bool Pull(bool force)
    {
        if (!EnsureReady()) return false;

        var pullCmd = force ? "reset --hard origin/main" : "pull origin main";
        if (force)
        {
            // Fetch first, then reset
            ConfigSync.RunGit("fetch origin main");
        }
        var result = force ? ConfigSync.RunGit(pullCmd) : ConfigSync.RunGit(pullCmd);

        if (!force && result != null && result.Contains("Already up to date"))
        {
            ConfigSync.PrintMuted("  Already up to date");
            return true;
        }
        else if (!force && result != null && result.Contains("CONFLICT"))
        {
            ConfigSync.PrintError("Merge conflict detected.");
            ConfigSync.PrintMuted("  Use 'sync pull --force' to overwrite local with remote");
            ConfigSync.PrintMuted("  Or resolve manually in ~/.config/rush/");
            return false;
        }

        var meta = ConfigSync.LoadMeta()!;
        ConfigSync.SaveMeta(meta with
        {
            LastSync = DateTime.UtcNow.ToString("o"),
            SyncHash = ConfigSync.ComputeSyncHash()
        });

        if (force)
            ConfigSync.PrintAccent("  Config force-pulled from GitHub (local overwritten)");
        else
            ConfigSync.PrintAccent("  Config pulled from GitHub");
        ConfigSync.PrintMuted("  Run 'reload' to apply changes");
        return true;
    }

    /// <summary>Show GitHub sync status.</summary>
    internal static bool Status()
    {
        var meta = ConfigSync.LoadMeta()!;

        ConfigSync.PrintAccent($"  Transport: github");
        ConfigSync.PrintAccent($"  Repo: github.com/{meta.Target}");
        ConfigSync.PrintMuted($"  Last sync: {ConfigSync.FormatTimeAgo(meta.LastSync)}");
        if (!string.IsNullOrEmpty(meta.LastSyncHost))
            ConfigSync.PrintMuted($"  Last sync from: {meta.LastSyncHost}");

        // Show local changes
        var status = ConfigSync.RunGit("status --porcelain")?.Trim();
        if (!string.IsNullOrEmpty(status))
        {
            ConfigSync.PrintMuted("  Local changes:");
            foreach (var line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                ConfigSync.PrintMuted($"    {line.Trim()}");
        }
        else
        {
            ConfigSync.PrintMuted("  No local changes");
        }

        ConfigSync.PrintTrackedFiles();
        return true;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static bool EnsureReady()
    {
        var meta = ConfigSync.LoadMeta();
        if (meta == null || !meta.Initialized)
        {
            ConfigSync.PrintError("Not initialized. Run 'sync init' first.");
            return false;
        }
        return CheckGh();
    }

    private static bool CheckGh()
    {
        try
        {
            var result = ConfigSync.RunProcess("gh", "auth status");
            if (result == null || !result.Contains("Logged in"))
            {
                ConfigSync.PrintError("GitHub CLI not authenticated. Run 'gh auth login' first.");
                return false;
            }
            return true;
        }
        catch
        {
            ConfigSync.PrintError("GitHub CLI (gh) not found. Install with: brew install gh");
            return false;
        }
    }

    private static bool IsGitRepo()
    {
        return Directory.Exists(Path.Combine(ConfigSync.ConfigDir, ".git"));
    }
}
