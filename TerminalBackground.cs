using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Rush;

/// <summary>
/// Detects the terminal background color to enable contrast-aware theming.
/// Uses a cascade of detection methods: OSC 11 query → COLORFGBG env var →
/// macOS appearance → fallback to dark.
/// </summary>
public static class TerminalBackground
{
    public record TerminalBg(bool IsDark, double BgLuminance);

    /// <summary>
    /// Detect the terminal background. Returns dark/light classification
    /// and the background luminance (0.0 = black, 1.0 = white).
    /// </summary>
    public static TerminalBg Detect()
    {
        try
        {
            // 1. OSC 11 — query terminal directly for background RGB
            if (TryOsc11(out var luminance))
                return new TerminalBg(luminance < 0.5, luminance);
        }
        catch { /* OSC 11 failed — try next method */ }

        try
        {
            // 2. COLORFGBG env var (set by some terminals: iTerm2, Konsole, urxvt)
            if (TryColorFgBg(out var isDark))
                return new TerminalBg(isDark, isDark ? 0.0 : 1.0);
        }
        catch { }

        try
        {
            // 3. macOS system appearance
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && TryMacOsAppearance(out var isDark))
                return new TerminalBg(isDark, isDark ? 0.0 : 1.0);
        }
        catch { }

        // 4. Fallback: assume dark (most developer terminals)
        return new TerminalBg(true, 0.0);
    }

    // ── OSC 11 Terminal Query ──────────────────────────────────────────

    private static bool TryOsc11(out double luminance)
    {
        luminance = 0;

        // Only attempt on Unix when connected to a real terminal
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        if (Console.IsInputRedirected || Console.IsOutputRedirected) return false;

        // Open /dev/tty directly to avoid interfering with stdin/stdout
        const string ttyPath = "/dev/tty";
        if (!File.Exists(ttyPath)) return false;

        // Save terminal settings via stty (avoids termios struct layout
        // differences between macOS arm64 and Linux — tcflag_t is 8 bytes
        // on macOS but 4 on Linux, making P/Invoke fragile)
        var saved = CaptureStty("-g");
        if (string.IsNullOrEmpty(saved)) return false;

        try
        {
            // Disable echo and canonical mode, set read timeout
            // min 0 + time 1 = return immediately with whatever is available,
            // or after 100ms with nothing
            RunStty("-echo -icanon min 0 time 1");

            // Send OSC 11 query: ESC ] 11 ; ? ESC backslash
            var query = "\x1b]11;?\x1b\\";
            var queryBytes = Encoding.ASCII.GetBytes(query);

            using var ttyWrite = new FileStream(ttyPath, FileMode.Open, FileAccess.Write);
            ttyWrite.Write(queryBytes);
            ttyWrite.Flush();

            // Read response using native read() on fd 0
            var response = new StringBuilder();
            var deadline = DateTime.UtcNow.AddMilliseconds(200);
            var buf = new byte[1];

            while (DateTime.UtcNow < deadline)
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
                    Thread.Sleep(5);
                }
            }

            // Drain any remaining bytes before restoring echo
            var drainDeadline = DateTime.UtcNow.AddMilliseconds(50);
            while (DateTime.UtcNow < drainDeadline)
            {
                if (read(0, buf, 1) <= 0) break;
            }

            return ParseOsc11Response(response.ToString(), out luminance);
        }
        finally
        {
            // Restore terminal settings (re-enables echo)
            RunStty(saved.Trim());

            // Clear any response artifacts that leaked to the display
            Console.Write("\r\x1b[K");
        }
    }

    /// <summary>Capture stty output (e.g., stty -g for saved settings).</summary>
    private static string? CaptureStty(string args)
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
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(1000);
            return proc.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }

    /// <summary>Run stty to change terminal settings (no output capture needed).</summary>
    private static void RunStty(string args)
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
            proc?.WaitForExit(1000);
        }
        catch { }
    }

    /// <summary>
    /// Parse an OSC 11 response like: ESC]11;rgb:RRRR/GGGG/BBBB ESC\
    /// The hex values can be 1-4 digits each.
    /// </summary>
    private static bool ParseOsc11Response(string response, out double luminance)
    {
        luminance = 0;

        // Match rgb:XXXX/XXXX/XXXX pattern
        var match = Regex.Match(response, @"rgb:([0-9a-fA-F]+)/([0-9a-fA-F]+)/([0-9a-fA-F]+)");
        if (!match.Success) return false;

        var rHex = match.Groups[1].Value;
        var gHex = match.Groups[2].Value;
        var bHex = match.Groups[3].Value;

        // Normalize to 0.0-1.0 range based on hex digit count
        // 1 digit = /15, 2 digits = /255, 3 digits = /4095, 4 digits = /65535
        double r = NormalizeHex(rHex);
        double g = NormalizeHex(gHex);
        double b = NormalizeHex(bHex);

        luminance = RelativeLuminance(r, g, b);
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

            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(500);

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
}
