using System.Collections.Concurrent;
using System.Diagnostics;

namespace Rush;

/// <summary>
/// SSH connection pooling via OpenSSH ControlMaster.
/// First connection to a host creates a master socket; subsequent connections
/// multiplex over it (~0ms connect vs ~200-500ms fresh handshake).
/// Unix only — ControlMaster requires Unix domain sockets.
/// </summary>
internal static class SshPool
{
    // %C = hash of %l%h%p%r — short, deterministic, unique per connection
    private const string ControlPath = "/tmp/rush-cm-%C";

    // Track hosts we've connected to, for cleanup on exit
    private static readonly ConcurrentDictionary<string, byte> _hosts = new();

    private static readonly bool _enabled = !OperatingSystem.IsWindows();

    /// <summary>
    /// Inject ControlMaster options into an ssh/scp ProcessStartInfo.
    /// Call after other -o options, before host/command args. No-op on Windows.
    /// </summary>
    internal static void Apply(ProcessStartInfo psi)
    {
        if (!_enabled) return;

        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ControlMaster=auto");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add($"ControlPath={ControlPath}");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ControlPersist=60");
    }

    /// <summary>
    /// Record a host for cleanup. Call after successful Process.Start.
    /// </summary>
    internal static void Track(string sshTarget)
    {
        if (!_enabled) return;
        _hosts.TryAdd(sshTarget, 0);
    }

    /// <summary>
    /// Tear down ControlMaster sockets for all tracked hosts.
    /// Best-effort — ControlPersist=60 handles eventual cleanup even if this fails.
    /// </summary>
    internal static void Cleanup()
    {
        if (!_enabled) return;

        foreach (var host in _hosts.Keys)
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
                psi.ArgumentList.Add("-O");
                psi.ArgumentList.Add("exit");
                psi.ArgumentList.Add("-o");
                psi.ArgumentList.Add($"ControlPath={ControlPath}");
                psi.ArgumentList.Add(host);

                using var proc = Process.Start(psi);
                proc?.WaitForExit(3000);
            }
            catch { /* best-effort */ }
        }

        _hosts.Clear();
    }
}
