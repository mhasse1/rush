namespace Rush;

/// <summary>
/// Filesystem path transport for config sync.
/// Works with any accessible path: UNC shares (\\server\share), mounted drives,
/// USB drives, NFS mounts, Dropbox/OneDrive folders, etc.
/// Security: relies on OS-level file permissions and authentication.
/// </summary>
internal static class SyncPath
{
    /// <summary>Initialize filesystem path sync target.</summary>
    internal static bool Init(string target)
    {
        // Prompt for target if not provided
        if (string.IsNullOrEmpty(target))
        {
            ConfigSync.PrintMuted("  Enter sync path (UNC share, mounted drive, or local path):");
            ConfigSync.PrintMuted("  Examples: \\\\server\\share\\rush-config  /Volumes/usb/rush-config");
            Console.ForegroundColor = Theme.Current.Accent;
            Console.Write("  Path: ");
            Console.ResetColor();
            target = Console.ReadLine()?.Trim() ?? "";
        }

        if (string.IsNullOrEmpty(target))
        {
            ConfigSync.PrintError("No path specified.");
            return false;
        }

        // Normalize path
        target = Path.GetFullPath(target);

        // Create directory if it doesn't exist
        try
        {
            if (!Directory.Exists(target))
            {
                ConfigSync.PrintMuted($"  Creating directory: {target}");
                Directory.CreateDirectory(target);
            }
        }
        catch (Exception ex)
        {
            ConfigSync.PrintError($"Cannot create directory: {ex.Message}");
            return false;
        }

        // Test write access
        var testFile = Path.Combine(target, ".rush-write-test");
        try
        {
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
        }
        catch (Exception ex)
        {
            ConfigSync.PrintError($"Path not writable: {ex.Message}");
            return false;
        }

        // Copy initial config files
        ConfigSync.PrintMuted("  Copying initial config...");
        foreach (var file in ConfigSync.SyncFiles)
        {
            var localPath = Path.Combine(ConfigSync.ConfigDir, file);
            if (File.Exists(localPath))
            {
                try
                {
                    File.Copy(localPath, Path.Combine(target, file), overwrite: true);
                }
                catch (Exception ex)
                {
                    ConfigSync.PrintError($"Failed to copy {file}: {ex.Message}");
                    return false;
                }
            }
        }

        // Write manifest
        var manifest = ConfigSync.BuildManifest();
        ConfigSync.SaveManifest(Path.Combine(target, ConfigSync.ManifestFile), manifest);

        // Also save local copy of manifest
        ConfigSync.SaveManifest(
            Path.Combine(ConfigSync.ConfigDir, ConfigSync.ManifestFile), manifest);

        // Save sync metadata
        ConfigSync.SaveMeta(new SyncMeta
        {
            Transport = "path",
            Target = target,
            LastSync = DateTime.UtcNow.ToString("o"),
            LastSyncHost = Environment.MachineName.ToLowerInvariant(),
            SyncHash = manifest.Hash,
            Initialized = true
        });

        ConfigSync.PrintAccent($"  Syncing to: {target}");
        ConfigSync.PrintMuted("  Use 'sync push' to upload changes, 'sync pull' to download");
        return true;
    }

    /// <summary>Push local config to filesystem path.</summary>
    internal static bool Push(bool force)
    {
        var meta = ConfigSync.LoadMeta()!;
        var target = meta.Target;

        // Verify path is accessible
        if (!Directory.Exists(target))
        {
            ConfigSync.PrintError($"Sync path not accessible: {target}");
            ConfigSync.PrintMuted("  Mount the share or connect the drive, then try again.");
            return false;
        }

        // Conflict detection
        if (!force)
        {
            var remoteManifestPath = Path.Combine(target, ConfigSync.ManifestFile);
            var remoteManifest = ConfigSync.LoadManifest(remoteManifestPath);
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

        // Copy files to target
        int pushed = 0;
        foreach (var file in ConfigSync.SyncFiles)
        {
            var localPath = Path.Combine(ConfigSync.ConfigDir, file);
            if (File.Exists(localPath))
            {
                try
                {
                    File.Copy(localPath, Path.Combine(target, file), overwrite: true);
                    pushed++;
                }
                catch (Exception ex)
                {
                    ConfigSync.PrintError($"Failed to copy {file}: {ex.Message}");
                    return false;
                }
            }
        }

        // Update manifest on remote
        var manifest = ConfigSync.BuildManifest();
        ConfigSync.SaveManifest(Path.Combine(target, ConfigSync.ManifestFile), manifest);

        // Also update local manifest
        ConfigSync.SaveManifest(
            Path.Combine(ConfigSync.ConfigDir, ConfigSync.ManifestFile), manifest);

        // Update metadata
        ConfigSync.SaveMeta(meta with
        {
            LastSync = DateTime.UtcNow.ToString("o"),
            LastSyncHost = Environment.MachineName.ToLowerInvariant(),
            SyncHash = manifest.Hash
        });

        ConfigSync.PrintAccent($"  Config pushed ({pushed} file{(pushed != 1 ? "s" : "")})");
        return true;
    }

    /// <summary>Pull config from filesystem path.</summary>
    internal static bool Pull(bool force)
    {
        var meta = ConfigSync.LoadMeta()!;
        var target = meta.Target;

        // Verify path is accessible
        if (!Directory.Exists(target))
        {
            ConfigSync.PrintError($"Sync path not accessible: {target}");
            ConfigSync.PrintMuted("  Mount the share or connect the drive, then try again.");
            return false;
        }

        // Conflict detection
        if (!force)
        {
            var remoteManifestPath = Path.Combine(target, ConfigSync.ManifestFile);
            var remoteManifest = ConfigSync.LoadManifest(remoteManifestPath);
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

        // Copy files from target
        int pulled = 0;
        foreach (var file in ConfigSync.SyncFiles)
        {
            var remotePath = Path.Combine(target, file);
            if (File.Exists(remotePath))
            {
                try
                {
                    File.Copy(remotePath, Path.Combine(ConfigSync.ConfigDir, file), overwrite: true);
                    pulled++;
                }
                catch (Exception ex)
                {
                    ConfigSync.PrintError($"Failed to copy {file}: {ex.Message}");
                    return false;
                }
            }
        }

        // Copy manifest from remote
        var remoteManifest2 = Path.Combine(target, ConfigSync.ManifestFile);
        if (File.Exists(remoteManifest2))
        {
            try
            {
                File.Copy(remoteManifest2,
                    Path.Combine(ConfigSync.ConfigDir, ConfigSync.ManifestFile),
                    overwrite: true);
            }
            catch { }
        }

        // Update metadata
        ConfigSync.SaveMeta(meta with
        {
            LastSync = DateTime.UtcNow.ToString("o"),
            SyncHash = ConfigSync.ComputeSyncHash()
        });

        if (force)
            ConfigSync.PrintAccent($"  Config force-pulled ({pulled} file{(pulled != 1 ? "s" : "")})");
        else
            ConfigSync.PrintAccent($"  Config pulled ({pulled} file{(pulled != 1 ? "s" : "")})");
        ConfigSync.PrintMuted("  Run 'reload' to apply changes");
        return true;
    }

    /// <summary>Show filesystem path sync status.</summary>
    internal static bool Status()
    {
        var meta = ConfigSync.LoadMeta()!;

        ConfigSync.PrintAccent("  Transport: path");
        ConfigSync.PrintAccent($"  Target: {meta.Target}");
        ConfigSync.PrintMuted($"  Last sync: {ConfigSync.FormatTimeAgo(meta.LastSync)}");
        if (!string.IsNullOrEmpty(meta.LastSyncHost))
            ConfigSync.PrintMuted($"  Last sync from: {meta.LastSyncHost}");

        // Check accessibility
        if (Directory.Exists(meta.Target))
            ConfigSync.PrintMuted("  Path: accessible ✓");
        else
            ConfigSync.PrintMuted("  Path: not accessible ✗");

        ConfigSync.PrintTrackedFiles();
        return true;
    }
}
