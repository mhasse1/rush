namespace Rush;

/// <summary>
/// SSH/SCP transport for config sync.
/// Uses scp for file transfer, ssh for remote commands.
/// Security: BatchMode=yes (key-based auth only, never prompts for password).
/// Target format: user@host:/path/to/rush-config
/// </summary>
internal static class SyncSsh
{
    private const string SshOpts = "-o BatchMode=yes -o ConnectTimeout=5 -o StrictHostKeyChecking=accept-new";

    /// <summary>Initialize SSH sync target.</summary>
    internal static bool Init(string target)
    {
        // Prompt for target if not provided
        if (string.IsNullOrEmpty(target))
        {
            ConfigSync.PrintMuted("  Enter SSH target (user@host:/path/to/rush-config):");
            Console.ForegroundColor = Theme.Current.Accent;
            Console.Write("  Target: ");
            Console.ResetColor();
            target = Console.ReadLine()?.Trim() ?? "";
        }

        if (string.IsNullOrEmpty(target) || !target.Contains(':') || !target.Contains('@'))
        {
            ConfigSync.PrintError("Invalid SSH target. Format: user@host:/path/to/rush-config");
            return false;
        }

        var (hostPart, remotePath) = ParseTarget(target);

        // Test SSH connectivity
        ConfigSync.PrintMuted($"  Testing SSH connection to {hostPart}...");
        var testResult = RunSsh(hostPart, "echo ok");
        if (testResult == null || !testResult.Trim().Contains("ok"))
        {
            ConfigSync.PrintError($"SSH connection failed. Ensure SSH keys are set up:");
            ConfigSync.PrintMuted("    ssh-keygen -t ed25519");
            ConfigSync.PrintMuted($"    ssh-copy-id {hostPart}");
            return false;
        }

        // Create remote directory
        ConfigSync.PrintMuted($"  Creating remote directory: {remotePath}");
        RunSsh(hostPart, $"mkdir -p {remotePath}");

        // Push initial config files
        ConfigSync.PrintMuted("  Pushing initial config...");
        foreach (var file in ConfigSync.SyncFiles)
        {
            var localPath = Path.Combine(ConfigSync.ConfigDir, file);
            if (File.Exists(localPath))
            {
                if (!ScpTo(localPath, $"{hostPart}:{remotePath}/{file}"))
                {
                    ConfigSync.PrintError($"Failed to push {file}");
                    return false;
                }
            }
        }

        // Write and push manifest
        var manifest = ConfigSync.BuildManifest();
        var localManifest = Path.Combine(ConfigSync.ConfigDir, ConfigSync.ManifestFile);
        ConfigSync.SaveManifest(localManifest, manifest);
        ScpTo(localManifest, $"{hostPart}:{remotePath}/{ConfigSync.ManifestFile}");

        // Save sync metadata
        ConfigSync.SaveMeta(new SyncMeta
        {
            Transport = "ssh",
            Target = target,
            LastSync = DateTime.UtcNow.ToString("o"),
            LastSyncHost = Environment.MachineName.ToLowerInvariant(),
            SyncHash = manifest.Hash,
            Initialized = true
        });

        ConfigSync.PrintAccent($"  Syncing via SSH to: {target}");
        ConfigSync.PrintMuted("  Use 'sync push' to upload changes, 'sync pull' to download");
        return true;
    }

    /// <summary>Push local config to remote SSH server.</summary>
    internal static bool Push(bool force)
    {
        var meta = ConfigSync.LoadMeta()!;
        var (hostPart, remotePath) = ParseTarget(meta.Target);

        // Fetch remote manifest for conflict detection
        if (!force)
        {
            var remoteManifest = FetchRemoteManifest(hostPart, remotePath);
            var conflict = ConfigSync.CheckConflict(remoteManifest, meta);

            switch (conflict)
            {
                case ConflictResult.NoChange:
                    ConfigSync.PrintMuted("  Already up to date, nothing to push");
                    return true;
                case ConflictResult.Conflict:
                    ConfigSync.PrintConflict(remoteManifest!);
                    return false;
                case ConflictResult.SafeToPull:
                    ConfigSync.PrintMuted("  Remote has newer changes. Run 'sync pull' first.");
                    return false;
            }
        }

        // Push each tracked file
        ConfigSync.PrintMuted("  Pushing config via SCP...");
        int pushed = 0;
        foreach (var file in ConfigSync.SyncFiles)
        {
            var localPath = Path.Combine(ConfigSync.ConfigDir, file);
            if (File.Exists(localPath))
            {
                if (!ScpTo(localPath, $"{hostPart}:{remotePath}/{file}"))
                {
                    ConfigSync.PrintError($"Failed to push {file}");
                    return false;
                }
                pushed++;
            }
        }

        // Update and push manifest
        var manifest = ConfigSync.BuildManifest();
        var localManifest = Path.Combine(ConfigSync.ConfigDir, ConfigSync.ManifestFile);
        ConfigSync.SaveManifest(localManifest, manifest);
        ScpTo(localManifest, $"{hostPart}:{remotePath}/{ConfigSync.ManifestFile}");

        // Update local metadata
        ConfigSync.SaveMeta(meta with
        {
            LastSync = DateTime.UtcNow.ToString("o"),
            LastSyncHost = Environment.MachineName.ToLowerInvariant(),
            SyncHash = manifest.Hash
        });

        ConfigSync.PrintAccent($"  Config pushed via SSH ({pushed} file{(pushed != 1 ? "s" : "")})");
        return true;
    }

    /// <summary>Pull config from remote SSH server.</summary>
    internal static bool Pull(bool force)
    {
        var meta = ConfigSync.LoadMeta()!;
        var (hostPart, remotePath) = ParseTarget(meta.Target);

        // Fetch remote manifest for conflict detection
        if (!force)
        {
            var remoteManifest = FetchRemoteManifest(hostPart, remotePath);
            var conflict = ConfigSync.CheckConflict(remoteManifest, meta);

            switch (conflict)
            {
                case ConflictResult.NoChange:
                    ConfigSync.PrintMuted("  Already up to date");
                    return true;
                case ConflictResult.Conflict:
                    ConfigSync.PrintConflict(remoteManifest!);
                    return false;
                case ConflictResult.SafeToPush:
                    ConfigSync.PrintMuted("  No remote changes. Use 'sync push' to upload local changes.");
                    return true;
            }
        }

        // Pull each tracked file
        ConfigSync.PrintMuted("  Pulling config via SCP...");
        int pulled = 0;
        foreach (var file in ConfigSync.SyncFiles)
        {
            var remoteSrc = $"{hostPart}:{remotePath}/{file}";
            var localDest = Path.Combine(ConfigSync.ConfigDir, file);
            // Check if file exists on remote first
            var check = RunSsh(hostPart, $"test -f {remotePath}/{file} && echo exists");
            if (check != null && check.Contains("exists"))
            {
                if (!ScpFrom(remoteSrc, localDest))
                {
                    ConfigSync.PrintError($"Failed to pull {file}");
                    return false;
                }
                pulled++;
            }
        }

        // Pull and update manifest
        ScpFrom($"{hostPart}:{remotePath}/{ConfigSync.ManifestFile}",
                Path.Combine(ConfigSync.ConfigDir, ConfigSync.ManifestFile));

        // Update local metadata
        ConfigSync.SaveMeta(meta with
        {
            LastSync = DateTime.UtcNow.ToString("o"),
            SyncHash = ConfigSync.ComputeSyncHash()
        });

        if (force)
            ConfigSync.PrintAccent($"  Config force-pulled via SSH ({pulled} file{(pulled != 1 ? "s" : "")})");
        else
            ConfigSync.PrintAccent($"  Config pulled via SSH ({pulled} file{(pulled != 1 ? "s" : "")})");
        ConfigSync.PrintMuted("  Run 'reload' to apply changes");
        return true;
    }

    /// <summary>Show SSH sync status.</summary>
    internal static bool Status()
    {
        var meta = ConfigSync.LoadMeta()!;
        var (hostPart, remotePath) = ParseTarget(meta.Target);

        ConfigSync.PrintAccent("  Transport: ssh");
        ConfigSync.PrintAccent($"  Target: {meta.Target}");
        ConfigSync.PrintMuted($"  Last sync: {ConfigSync.FormatTimeAgo(meta.LastSync)}");
        if (!string.IsNullOrEmpty(meta.LastSyncHost))
            ConfigSync.PrintMuted($"  Last sync from: {meta.LastSyncHost}");

        // Quick connectivity check
        var test = RunSsh(hostPart, "echo ok");
        if (test != null && test.Contains("ok"))
            ConfigSync.PrintMuted("  Remote: reachable ✓");
        else
            ConfigSync.PrintMuted("  Remote: unreachable ✗");

        ConfigSync.PrintTrackedFiles();
        return true;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static (string hostPart, string remotePath) ParseTarget(string target)
    {
        var colonIdx = target.IndexOf(':');
        var hostPart = target[..colonIdx];
        var remotePath = target[(colonIdx + 1)..];
        return (hostPart, remotePath);
    }

    private static SyncManifest? FetchRemoteManifest(string hostPart, string remotePath)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"rush-manifest-{Guid.NewGuid():N}");
        try
        {
            if (ScpFrom($"{hostPart}:{remotePath}/{ConfigSync.ManifestFile}", tempPath))
                return ConfigSync.LoadManifest(tempPath);
            return null;
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private static string? RunSsh(string hostPart, string remoteCmd)
    {
        return ConfigSync.RunProcess("ssh", $"{SshOpts} {hostPart} \"{remoteCmd}\"");
    }

    private static bool ScpTo(string localPath, string remoteTarget)
    {
        var result = ConfigSync.RunProcess("scp", $"{SshOpts} -q \"{localPath}\" \"{remoteTarget}\"");
        return result != null;
    }

    private static bool ScpFrom(string remoteSource, string localPath)
    {
        var result = ConfigSync.RunProcess("scp", $"{SshOpts} -q \"{remoteSource}\" \"{localPath}\"");
        return result != null;
    }
}
