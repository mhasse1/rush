using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Rush;

/// <summary>
/// Detects the terminal background color to enable contrast-aware theming.
/// Uses a cascade of detection methods: OSC 11 query → COLORFGBG env var →
/// macOS appearance → fallback to dark.
///
/// All detection methods are time-bounded to prevent hangs in nested shells,
/// broken ptys, or unresponsive terminal environments.
/// </summary>
public static class TerminalBackground
{
    public enum DetectionMethod { Osc11, ColorFgBg, MacOsAppearance, Fallback }

    public record TerminalBg(bool IsDark, double BgLuminance, double BgR = -1, double BgG = -1, double BgB = -1,
        DetectionMethod Method = DetectionMethod.Fallback);

    /// <summary>
    /// Saved stty settings for emergency restore if detection times out.
    /// Set before modifying terminal state, cleared after restore.
    /// </summary>
    private static volatile string? _savedStty;

    /// <summary>
    /// Detect the terminal background. Returns dark/light classification
    /// and the background luminance (0.0 = black, 1.0 = white).
    ///
    /// Guaranteed to return within ~2 seconds — never hangs, even in
    /// nested shells, broken ptys, or over SSH with lag.
    /// </summary>
    public static TerminalBg Detect()
    {
        _savedStty = null;

        try
        {
            // Run detection on a background thread with a hard timeout.
            // This is the safety net: even if stty or read() blocks in
            // some unexpected way, we bail and return the fallback.
            var task = Task.Run(DetectCore);
            if (task.Wait(TimeSpan.FromMilliseconds(2000)))
                return task.Result;
        }
        catch { }

        // Timeout — try to restore terminal settings if they were modified
        // before the timeout fired (e.g., stty set raw mode, then read() blocked)
        var saved = _savedStty;
        if (saved != null)
        {
            _savedStty = null;
            RunSttySafe(saved);
            Console.Write("\r\x1b[K");
        }

        // Default to dark (most developer terminals)
        return new TerminalBg(true, 0.0);
    }

    /// <summary>
    /// Core detection cascade, runs on background thread.
    /// </summary>
    private static TerminalBg DetectCore()
    {
        try
        {
            // 1. OSC 11 — query terminal directly for background RGB
            if (TryOsc11(out var luminance, out var bgR, out var bgG, out var bgB))
                return new TerminalBg(luminance < 0.5, luminance, bgR, bgG, bgB, DetectionMethod.Osc11);
        }
        catch { /* OSC 11 failed — try next method */ }

        try
        {
            // 2. COLORFGBG env var (set by some terminals: iTerm2, Konsole, urxvt)
            if (TryColorFgBg(out var isDark))
                return new TerminalBg(isDark, isDark ? 0.0 : 1.0, Method: DetectionMethod.ColorFgBg);
        }
        catch { }

        try
        {
            // 3. macOS system appearance
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && TryMacOsAppearance(out var isDark))
                return new TerminalBg(isDark, isDark ? 0.0 : 1.0, Method: DetectionMethod.MacOsAppearance);
        }
        catch { }

        // 4. Fallback: assume dark (most developer terminals)
        return new TerminalBg(true, 0.0);
    }

    // ── OSC 11 Terminal Query ──────────────────────────────────────────

    private static bool TryOsc11(out double luminance, out double bgR, out double bgG, out double bgB)
    {
        luminance = 0;
        bgR = bgG = bgB = 0;

        // Only attempt on Unix when connected to a real terminal
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        if (Console.IsInputRedirected || Console.IsOutputRedirected) return false;

        // Open /dev/tty directly to avoid interfering with stdin/stdout
        const string ttyPath = "/dev/tty";
        if (!File.Exists(ttyPath)) return false;

        // Save terminal settings via stty (avoids termios struct layout
        // differences between macOS arm64 and Linux — tcflag_t is 8 bytes
        // on macOS but 4 on Linux, making P/Invoke fragile)
        var saved = CaptureSttyWithTimeout("-g");
        if (string.IsNullOrEmpty(saved)) return false;

        // Store for emergency restore if the outer timeout fires while
        // we're in raw mode (e.g., read() blocks in a nested shell)
        _savedStty = saved.Trim();

        try
        {
            // Disable echo and canonical mode, set read timeout
            // min 0 + time 1 = return immediately with whatever is available,
            // or after 100ms with nothing
            if (!RunSttyWithTimeout("-echo -icanon min 0 time 1"))
            {
                // Couldn't change terminal settings — can't safely query
                return false;
            }

            // Send OSC 11 query: ESC ] 11 ; ? ESC backslash
            var query = "\x1b]11;?\x1b\\";
            var queryBytes = Encoding.ASCII.GetBytes(query);

            using var ttyWrite = new FileStream(ttyPath, FileMode.Open, FileAccess.Write);
            ttyWrite.Write(queryBytes);
            ttyWrite.Flush();

            // Read response — use poll() before each read() to guarantee
            // we never block, even if stty settings didn't take effect
            // (which can happen in nested shells or broken ptys)
            var response = new StringBuilder();
            var deadline = DateTime.UtcNow.AddMilliseconds(300);
            var buf = new byte[1];

            while (DateTime.UtcNow < deadline)
            {
                // poll() with 50ms timeout — returns >0 if data available
                var pfd = new Pollfd { fd = 0, events = POLLIN };
                var ready = poll(ref pfd, 1, 50);

                if (ready > 0 && (pfd.revents & POLLIN) != 0)
                {
                    var bytesRead = read(0, buf, 1);
                    if (bytesRead > 0)
                    {
                        response.Append((char)buf[0]);

                        // Response ends with ST (ESC \) or BEL (\x07)
                        var s = response.ToString();
                        if (s.EndsWith("\x1b\\") || s.EndsWith("\x07"))
                            break;
                    }
                    else
                    {
                        break; // EOF or error
                    }
                }
                // poll returned 0 (timeout) or -1 (error) — loop checks deadline
            }

            // Drain any remaining bytes before restoring echo
            var drainDeadline = DateTime.UtcNow.AddMilliseconds(50);
            while (DateTime.UtcNow < drainDeadline)
            {
                var pfd2 = new Pollfd { fd = 0, events = POLLIN };
                if (poll(ref pfd2, 1, 10) <= 0) break;
                if (read(0, buf, 1) <= 0) break;
            }

            return ParseOsc11Response(response.ToString(), out luminance, out bgR, out bgG, out bgB);
        }
        finally
        {
            // Restore terminal settings (re-enables echo)
            RunSttySafe(saved.Trim());
            _savedStty = null; // Successfully restored

            // Clear any response artifacts that leaked to the display
            Console.Write("\r\x1b[K");
        }
    }

    // ── Process Helpers (with proper timeouts) ──────────────────────────

    /// <summary>
    /// Capture stty output with a hard timeout. Kills the process if it hangs.
    /// The old version used ReadToEnd() which blocks forever if stty hangs
    /// (e.g., in nested shells where the controlling terminal is unavailable).
    /// </summary>
    private static string? CaptureSttyWithTimeout(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("stty", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;

            // Read output asynchronously so WaitForExit can actually timeout.
            // ReadToEnd() blocks until the process closes stdout — if stty
            // hangs, ReadToEnd() hangs too and WaitForExit never runs.
            var outputTask = proc.StandardOutput.ReadToEndAsync();

            if (!proc.WaitForExit(500))
            {
                try { proc.Kill(); } catch { }
                return null;
            }

            // Process exited — output should be available almost immediately
            if (!outputTask.Wait(100)) return null;
            return proc.ExitCode == 0 ? outputTask.Result : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Run stty with a timeout. Returns false if stty hangs or fails.
    /// </summary>
    private static bool RunSttyWithTimeout(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("stty", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;

            if (!proc.WaitForExit(500))
            {
                try { proc.Kill(); } catch { }
                return false;
            }

            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Best-effort stty restore — used in finally blocks and timeout recovery.
    /// Doesn't throw, doesn't return status.
    /// </summary>
    private static void RunSttySafe(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("stty", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null && !proc.WaitForExit(500))
            {
                try { proc.Kill(); } catch { }
            }
        }
        catch { }
    }

    // ── OSC 11 Response Parser ──────────────────────────────────────────

    /// <summary>
    /// Parse an OSC 11 response like: ESC]11;rgb:RRRR/GGGG/BBBB ESC\
    /// The hex values can be 1-4 digits each.
    /// </summary>
    private static bool ParseOsc11Response(string response, out double luminance,
        out double bgR, out double bgG, out double bgB)
    {
        luminance = 0;
        bgR = bgG = bgB = 0;

        // Match rgb:XXXX/XXXX/XXXX pattern
        var match = Regex.Match(response, @"rgb:([0-9a-fA-F]+)/([0-9a-fA-F]+)/([0-9a-fA-F]+)");
        if (!match.Success) return false;

        var rHex = match.Groups[1].Value;
        var gHex = match.Groups[2].Value;
        var bHex = match.Groups[3].Value;

        // Normalize to 0.0-1.0 range based on hex digit count
        // 1 digit = /15, 2 digits = /255, 3 digits = /4095, 4 digits = /65535
        bgR = NormalizeHex(rHex);
        bgG = NormalizeHex(gHex);
        bgB = NormalizeHex(bHex);

        luminance = RelativeLuminance(bgR, bgG, bgB);
        return true;
    }

    private static double NormalizeHex(string hex)
    {
        int value = Convert.ToInt32(hex, 16);
        double maxValue = hex.Length switch
        {
            1 => 15.0,
            2 => 255.0,
            3 => 4095.0,
            4 => 65535.0,
            _ => Math.Pow(16, hex.Length) - 1
        };
        return value / maxValue;
    }

    // ── COLORFGBG Environment Variable ──────────────────────────────────

    private static bool TryColorFgBg(out bool isDark)
    {
        isDark = true;
        var value = Environment.GetEnvironmentVariable("COLORFGBG");
        if (string.IsNullOrEmpty(value)) return false;

        // Format: "fg;bg" e.g. "15;0" (white on black = dark) or "0;15" (black on white = light)
        var parts = value.Split(';');
        if (parts.Length < 2) return false;

        if (int.TryParse(parts[^1], out var bg))
        {
            // Standard 16-color terminal: 0-6 = dark colors, 7-15 = light colors
            isDark = bg < 8;
            return true;
        }
        return false;
    }

    // ── macOS System Appearance ──────────────────────────────────────────

    private static bool TryMacOsAppearance(out bool isDark)
    {
        isDark = true;

        try
        {
            var psi = new ProcessStartInfo("defaults", "read -g AppleInterfaceStyle")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(500))
            {
                try { proc.Kill(); } catch { }
                return false;
            }

            if (!outputTask.Wait(100)) return false;
            var output = outputTask.Result.Trim();

            if (proc.ExitCode == 0)
            {
                isDark = output.Equals("Dark", StringComparison.OrdinalIgnoreCase);
                return true;
            }
            else
            {
                // Command fails when not in dark mode → system is in light mode
                isDark = false;
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    // ── WCAG Contrast Helpers (public for Theme validation) ──────────────

    /// <summary>
    /// Calculate WCAG relative luminance from sRGB values (0.0-1.0).
    /// </summary>
    public static double RelativeLuminance(double r, double g, double b)
    {
        static double Linearize(double v) =>
            v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

        return 0.2126 * Linearize(r) + 0.7152 * Linearize(g) + 0.0722 * Linearize(b);
    }

    /// <summary>
    /// Calculate WCAG contrast ratio between two luminance values.
    /// Returns a value from 1.0 (identical) to 21.0 (black vs white).
    /// 4.5:1 is the minimum for readable text (WCAG AA).
    /// </summary>
    public static double ContrastRatio(double l1, double l2)
    {
        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    // ── Native P/Invoke (Unix only) ─────────────────────────────────────

    [DllImport("libc", SetLastError = true)]
    private static extern int read(int fd, byte[] buf, int count);

    /// <summary>
    /// poll() — check if file descriptors have data available without blocking.
    /// </summary>
    [DllImport("libc", SetLastError = true)]
    private static extern int poll(ref Pollfd fds, int nfds, int timeout);

    [StructLayout(LayoutKind.Sequential)]
    private struct Pollfd
    {
        public int fd;
        public short events;
        public short revents;
    }

    private const short POLLIN = 0x0001;
}
