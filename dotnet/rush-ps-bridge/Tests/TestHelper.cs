// Shared test utilities for rush-ps-bridge tests.
// Discovers the built binary (platform-aware) and spawns subprocess
// calls for the protocol-level integration tests.

using System.Diagnostics;

namespace Rush.PsBridge.Tests;

internal static class TestHelper
{
    private static string? _cachedBinary;

    /// <summary>
    /// Path to the built rush-ps-bridge binary. Walks up from the test
    /// assembly's output directory to find the project root, then
    /// locates the binary under bin/Debug/net10.0/ (RID-specific or
    /// generic). Cached after first lookup.
    /// </summary>
    public static string BridgeBinary
    {
        get
        {
            if (_cachedBinary != null) return _cachedBinary;

            var dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "rush-ps-bridge.csproj")))
            {
                dir = Path.GetDirectoryName(dir);
            }
            if (dir == null)
            {
                throw new InvalidOperationException(
                    "Could not locate rush-ps-bridge.csproj from test output dir");
            }

            var exe = OperatingSystem.IsWindows() ? "rush-ps.exe" : "rush-ps";
            string[] rids = OperatingSystem.IsWindows()
                ? new[] { "win-x64", "win-arm64" }
                : OperatingSystem.IsMacOS()
                    ? new[] { "osx-arm64", "osx-x64" }
                    : new[] { "linux-x64", "linux-arm64" };

            foreach (var rid in rids)
            {
                var p = Path.Combine(dir, "bin", "Debug", "net10.0", rid, exe);
                if (File.Exists(p)) { _cachedBinary = p; return p; }
            }
            var generic = Path.Combine(dir, "bin", "Debug", "net10.0", exe);
            if (File.Exists(generic)) { _cachedBinary = generic; return generic; }

            throw new FileNotFoundException(
                $"rush-ps-bridge binary not found under {dir}/bin/Debug/net10.0/");
        }
    }

    /// <summary>
    /// Spawn the bridge binary in a given mode with stdin/stdout piped.
    /// Caller interacts with the returned Process via Process.StandardInput
    /// and Process.StandardOutput. Call Process.WaitForExit or Kill when done.
    /// </summary>
    public static Process SpawnBridge(string mode)
    {
        var psi = new ProcessStartInfo
        {
            FileName = BridgeBinary,
            Arguments = mode,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var p = new Process { StartInfo = psi };
        p.Start();
        return p;
    }
}
