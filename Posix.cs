using System.Runtime.InteropServices;

namespace Rush;

/// <summary>
/// POSIX signal helpers for job control (macOS).
/// </summary>
static class Posix
{
    [DllImport("libc")]
    private static extern int kill(int pid, int sig);

    // macOS signal numbers
    private const int SIGCONT = 19;

    /// <summary>Send SIGCONT to resume a stopped process.</summary>
    public static void SendCONT(int pid) => kill(pid, SIGCONT);
}
