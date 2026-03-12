using System.Diagnostics;

namespace Rush.Tests;

/// <summary>
/// Shared test utilities for integration tests.
/// Provides cross-platform Rush binary discovery and command execution.
/// </summary>
public static class TestHelper
{
    private static string? _cachedBinary;

    /// <summary>
    /// Path to the Rush binary, discovered by walking up from the test output directory.
    /// Supports macOS (osx-arm64), Linux (linux-x64, linux-arm64), and Windows (win-x64).
    /// </summary>
    public static string RushBinary
    {
        get
        {
            if (_cachedBinary != null) return _cachedBinary;

            var dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Rush.csproj")))
                dir = Path.GetDirectoryName(dir);

            if (dir == null)
                throw new InvalidOperationException("Could not find Rush project root");

            var exeName = OperatingSystem.IsWindows() ? "rush.exe" : "rush";

            // Try platform-specific RID directories, then generic
            string[] rids = OperatingSystem.IsWindows()
                ? new[] { "win-x64", "win-arm64" }
                : OperatingSystem.IsMacOS()
                    ? new[] { "osx-arm64", "osx-x64" }
                    : new[] { "linux-x64", "linux-arm64" };

            foreach (var rid in rids)
            {
                var candidate = Path.Combine(dir, "bin", "Debug", "net8.0", rid, exeName);
                if (File.Exists(candidate))
                {
                    _cachedBinary = candidate;
                    return candidate;
                }
            }

            // Generic fallback (no RID subfolder)
            var generic = Path.Combine(dir, "bin", "Debug", "net8.0", exeName);
            if (File.Exists(generic))
            {
                _cachedBinary = generic;
                return generic;
            }

            throw new InvalidOperationException(
                $"Could not find rush binary. Tried RIDs: {string.Join(", ", rids)} and generic path.");
        }
    }

    /// <summary>
    /// Path to the Rush.Tests fixtures directory.
    /// </summary>
    public static string FixturesDir
    {
        get
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Rush.Tests.csproj")))
                dir = Path.GetDirectoryName(dir);

            return dir != null
                ? Path.Combine(dir, "Fixtures")
                : throw new InvalidOperationException("Could not find Rush.Tests project root");
        }
    }

    /// <summary>
    /// Run a rush command via `rush -c` using ArgumentList (safe for special characters).
    /// Returns trimmed stdout, trimmed stderr, and exit code.
    /// </summary>
    public static (string stdout, string stderr, int exitCode) RunRush(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = RushBinary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        return (stdout.Trim(), stderr.Trim(), proc.ExitCode);
    }

    /// <summary>
    /// Run a .rush script file and capture output.
    /// Returns raw stdout (not trimmed), stderr, and exit code.
    /// </summary>
    public static (string stdout, string stderr, int exitCode) RunRushScript(string scriptPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = RushBinary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(scriptPath);

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        return (stdout, stderr, proc.ExitCode);
    }
}
