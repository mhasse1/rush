using System.Diagnostics;

namespace Rush;

/// <summary>
/// UNC Phase 1 — SSH file operations via //ssh:[user@]host/path syntax.
/// Assumes rush is installed on the remote host (rush-on-both-ends).
/// All remote commands execute via: ssh target 'rush -c "command"'
///
/// Supported operations: ls, cat, cp, rm, mkdir, rmdir, mv
/// </summary>
public static class UncHandler
{
    private const string SshPrefix = "//ssh:";

    /// <summary>
    /// Try to handle a command segment containing UNC paths.
    /// Returns true if the segment was handled (contains //ssh: paths),
    /// false if it should fall through to normal dispatch.
    /// </summary>
    public static bool TryHandle(string segment, out bool failed)
    {
        failed = false;

        var parts = CommandTranslator.SplitCommandLine(segment);
        if (parts.Length == 0) return false;

        var verb = parts[0].ToLowerInvariant();
        var args = parts[1..];

        // Check if any argument is a UNC path
        bool hasUnc = false;
        foreach (var arg in args)
        {
            if (arg.StartsWith(SshPrefix, StringComparison.OrdinalIgnoreCase))
            {
                hasUnc = true;
                break;
            }
        }
        if (!hasUnc) return false;

        switch (verb)
        {
            case "ls":
            case "dir":
                failed = !HandleLs(args);
                return true;

            case "cat":
            case "type":
                failed = !HandleCat(args);
                return true;

            case "cp":
            case "copy":
                failed = !HandleCp(args);
                return true;

            case "mv":
            case "move":
            case "ren":
            case "rename":
                failed = !HandleMv(args);
                return true;

            case "rm":
            case "del":
            case "delete":
                failed = !HandleRm(args);
                return true;

            case "mkdir":
            case "md":
                failed = !HandleMkdir(args);
                return true;

            case "rmdir":
            case "rd":
                failed = !HandleRmdir(args);
                return true;

            default:
                // UNC path detected but verb not supported
                Console.Error.WriteLine($"rush: UNC paths not supported with '{verb}'");
                failed = true;
                return true;
        }
    }

    // ── Parse ────────────────────────────────────────────────────────────

    /// <summary>
    /// Parse "//ssh:[user@]host/path" into (sshTarget, remotePath).
    /// sshTarget includes user@ if present (passed straight to ssh).
    /// Returns null if the string is not a UNC SSH path.
    /// </summary>
    internal static (string sshTarget, string remotePath)? TryParse(string arg)
    {
        if (!arg.StartsWith(SshPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var rest = arg[SshPrefix.Length..]; // after "//ssh:"
        var slashIdx = rest.IndexOf('/');
        if (slashIdx < 0)
        {
            // //ssh:host with no path — treat as root
            return (rest, "/");
        }

        var sshTarget = rest[..slashIdx];
        var remotePath = rest[slashIdx..]; // includes leading /

        if (string.IsNullOrEmpty(sshTarget))
            return null;

        // Strip leading / for Windows drive paths: /C:/foo → C:/foo
        if (remotePath.Length >= 4 && remotePath[0] == '/' &&
            char.IsLetter(remotePath[1]) && remotePath[2] == ':' && remotePath[3] == '/')
        {
            remotePath = remotePath[1..];
        }

        return (sshTarget, remotePath);
    }

    // ── Operations ───────────────────────────────────────────────────────

    private static bool HandleLs(string[] args)
    {
        // Collect flags and UNC paths
        var flags = new List<string>();
        var uncPaths = new List<(string target, string path)>();

        foreach (var arg in args)
        {
            var parsed = TryParse(arg);
            if (parsed != null)
                uncPaths.Add(parsed.Value);
            else if (arg.StartsWith("-"))
                flags.Add(arg);
            // ignore non-UNC, non-flag args (shouldn't happen in valid usage)
        }

        if (uncPaths.Count == 0) return false;

        // ls each UNC path
        foreach (var (target, path) in uncPaths)
        {
            var flagStr = flags.Count > 0 ? " " + string.Join(" ", flags) : "";
            var cmd = $"ls{flagStr} {path}";
            var (stdout, stderr, exitCode) = RunRemote(target, cmd);

            if (!string.IsNullOrEmpty(stdout))
                Console.WriteLine(stdout);
            if (exitCode != 0 && !string.IsNullOrEmpty(stderr))
            {
                Console.Error.WriteLine(stderr);
                return false;
            }
        }

        return true;
    }

    private static bool HandleCat(string[] args)
    {
        foreach (var arg in args)
        {
            var parsed = TryParse(arg);
            if (parsed == null) continue;

            var (target, path) = parsed.Value;
            var (stdout, stderr, exitCode) = RunRemote(target, $"cat {path}");

            if (!string.IsNullOrEmpty(stdout))
                Console.Write(stdout);
            if (exitCode != 0 && !string.IsNullOrEmpty(stderr))
            {
                Console.Error.WriteLine(stderr);
                return false;
            }
        }

        return true;
    }

    private static bool HandleCp(string[] args)
    {
        // Filter out flags (like -r, -f) and collect positional args
        var flags = new List<string>();
        var positional = new List<string>();

        foreach (var arg in args)
        {
            if (arg.StartsWith("-"))
                flags.Add(arg);
            else
                positional.Add(arg);
        }

        if (positional.Count < 2)
        {
            Console.Error.WriteLine("rush: cp requires source and destination");
            return false;
        }

        var src = positional[0];
        var dst = positional[^1]; // last positional is destination

        var srcParsed = TryParse(src);
        var dstParsed = TryParse(dst);

        if (srcParsed != null && dstParsed != null)
        {
            // Server-to-server: relay via local temp
            return HandleServerToServerCp(srcParsed.Value, dstParsed.Value);
        }
        else if (srcParsed != null)
        {
            // Remote → local: scp remote:path local
            var (target, path) = srcParsed.Value;
            return RunScp($"{target}:{path}", dst) == 0;
        }
        else if (dstParsed != null)
        {
            // Local → remote: scp local remote:path
            var (target, path) = dstParsed.Value;
            return RunScp(src, $"{target}:{path}") == 0;
        }

        return false; // shouldn't reach here
    }

    private static bool HandleServerToServerCp((string target, string path) src, (string target, string path) dst)
    {
        // Relay: scp src to temp, then scp temp to dst
        var tmpFile = Path.Combine(Path.GetTempPath(), $"rush-unc-{Guid.NewGuid():N}");
        try
        {
            var rc1 = RunScp($"{src.target}:{src.path}", tmpFile);
            if (rc1 != 0) return false;

            var rc2 = RunScp(tmpFile, $"{dst.target}:{dst.path}");
            return rc2 == 0;
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { }
        }
    }

    private static bool HandleMv(string[] args)
    {
        var positional = args.Where(a => !a.StartsWith("-")).ToArray();
        if (positional.Length < 2)
        {
            Console.Error.WriteLine("rush: mv requires source and destination");
            return false;
        }

        var src = positional[0];
        var dst = positional[^1];

        var srcParsed = TryParse(src);
        var dstParsed = TryParse(dst);

        if (srcParsed != null && dstParsed != null && srcParsed.Value.sshTarget == dstParsed.Value.sshTarget)
        {
            // Same host: remote mv
            var (target, _) = srcParsed.Value;
            var cmd = $"mv {srcParsed.Value.remotePath} {dstParsed.Value.remotePath}";
            var (stdout, stderr, exitCode) = RunRemote(target, cmd);
            if (exitCode != 0 && !string.IsNullOrEmpty(stderr))
                Console.Error.WriteLine(stderr);
            return exitCode == 0;
        }
        else if (srcParsed != null && dstParsed != null)
        {
            // Different hosts: cp + rm source
            if (!HandleServerToServerCp(srcParsed.Value, dstParsed.Value))
                return false;
            // Remove source
            var (target, path) = srcParsed.Value;
            var (_, stderr, exitCode) = RunRemote(target, $"rm {path}");
            if (exitCode != 0 && !string.IsNullOrEmpty(stderr))
                Console.Error.WriteLine(stderr);
            return exitCode == 0;
        }
        else
        {
            // Mixed local/remote: cp then rm source
            if (!HandleCp(args)) return false;
            // Remove source
            if (srcParsed != null)
            {
                var (target, path) = srcParsed.Value;
                var (_, stderr, exitCode) = RunRemote(target, $"rm {path}");
                if (exitCode != 0 && !string.IsNullOrEmpty(stderr))
                    Console.Error.WriteLine(stderr);
                return exitCode == 0;
            }
            else
            {
                // Source is local
                try { File.Delete(src); return true; }
                catch (Exception ex) { Console.Error.WriteLine($"rush: {ex.Message}"); return false; }
            }
        }
    }

    private static bool HandleRm(string[] args)
    {
        bool recursive = args.Any(a => a == "-r" || a == "-rf" || a == "-R");

        foreach (var arg in args)
        {
            if (arg.StartsWith("-")) continue;
            var parsed = TryParse(arg);
            if (parsed == null) continue;

            var (target, path) = parsed.Value;
            var rFlag = recursive ? "-r " : "";
            var (stdout, stderr, exitCode) = RunRemote(target, $"rm {rFlag}{path}");
            if (exitCode != 0)
            {
                if (!string.IsNullOrEmpty(stderr)) Console.Error.WriteLine(stderr);
                return false;
            }
        }

        return true;
    }

    private static bool HandleMkdir(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("-")) continue;
            var parsed = TryParse(arg);
            if (parsed == null) continue;

            var (target, path) = parsed.Value;
            var (stdout, stderr, exitCode) = RunRemote(target, $"mkdir {path}");
            if (exitCode != 0)
            {
                if (!string.IsNullOrEmpty(stderr)) Console.Error.WriteLine(stderr);
                return false;
            }
        }

        return true;
    }

    private static bool HandleRmdir(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("-")) continue;
            var parsed = TryParse(arg);
            if (parsed == null) continue;

            var (target, path) = parsed.Value;
            var (stdout, stderr, exitCode) = RunRemote(target, $"rmdir {path}");
            if (exitCode != 0)
            {
                if (!string.IsNullOrEmpty(stderr)) Console.Error.WriteLine(stderr);
                return false;
            }
        }

        return true;
    }

    // ── SSH / SCP execution ──────────────────────────────────────────────

    /// <summary>
    /// Execute a rush command on a remote host via SSH.
    /// Runs: ssh [opts] target 'rush -c "command"'
    /// </summary>
    private static (string stdout, string stderr, int exitCode) RunRemote(string sshTarget, string rushCommand)
    {
        try
        {
            var psi = new ProcessStartInfo("ssh")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("BatchMode=yes");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("ConnectTimeout=10");
            SshPool.Apply(psi);
            psi.ArgumentList.Add(sshTarget);
            psi.ArgumentList.Add($"rush -c \"{rushCommand.Replace("\"", "\\\"")}\"");

            using var proc = Process.Start(psi);
            if (proc == null)
                return ("", $"Failed to start ssh to {sshTarget}", 1);
            SshPool.Track(sshTarget);

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();

            var stdout = stdoutTask.GetAwaiter().GetResult().TrimEnd('\n', '\r');
            var stderr = stderrTask.GetAwaiter().GetResult().TrimEnd('\n', '\r');

            return (stdout, stderr, proc.ExitCode);
        }
        catch (Exception ex)
        {
            return ("", $"SSH error: {ex.Message}", 1);
        }
    }

    /// <summary>
    /// Run scp to transfer files. Returns exit code.
    /// Prints stderr on failure.
    /// </summary>
    private static int RunScp(string src, string dst)
    {
        try
        {
            var psi = new ProcessStartInfo("scp")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-r"); // recursive by default for dirs
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("BatchMode=yes");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("ConnectTimeout=10");
            SshPool.Apply(psi);
            psi.ArgumentList.Add(src);
            psi.ArgumentList.Add(dst);

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Console.Error.WriteLine($"rush: failed to start scp");
                return 1;
            }

            var stderrTask = proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();

            var stderr = stderrTask.GetAwaiter().GetResult().TrimEnd('\n', '\r');
            if (proc.ExitCode != 0 && !string.IsNullOrEmpty(stderr))
                Console.Error.WriteLine(stderr);

            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SCP error: {ex.Message}");
            return 1;
        }
    }

}
