using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Rush;

/// <summary>
/// Detects the terminal background color to enable contrast-aware theming.
/// Uses a cascade of detection methods: RUSH_BG env var → COLORFGBG env var →
/// macOS appearance → fallback to dark.
///
/// For precise RGB control, users set backgrounds via `setbg "#hex"` or `.rushbg`
/// files, which set the RUSH_BG environment variable for persistence across reloads.
/// </summary>
public static class TerminalBackground
{
    public enum DetectionMethod { RushBg, ColorFgBg, MacOsAppearance, Fallback }

    public record TerminalBg(bool IsDark, double BgLuminance, double BgR = -1, double BgG = -1, double BgB = -1,
        DetectionMethod Method = DetectionMethod.Fallback);

    /// <summary>
    /// Detect the terminal background. Returns dark/light classification
    /// and the background luminance (0.0 = black, 1.0 = white).
    /// All methods are fast (env var reads + one optional process spawn).
    /// </summary>
    public static TerminalBg Detect()
    {
        try
        {
            // 1. RUSH_BG env var — set by setbg/dirbg, persists across reload
            if (TryRushBg(out var lum, out var bgR, out var bgG, out var bgB))
                return new TerminalBg(lum < 0.5, lum, bgR, bgG, bgB, DetectionMethod.RushBg);
        }
        catch { }

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

    // ── RUSH_BG Environment Variable ─────────────────────────────────────

    /// <summary>
    /// Parse RUSH_BG env var (hex color like "#222733") to exact RGB.
    /// Set by setbg command and .rushbg file processing.
    /// </summary>
    private static bool TryRushBg(out double luminance, out double bgR, out double bgG, out double bgB)
    {
        luminance = 0;
        bgR = bgG = bgB = 0;

        var value = Environment.GetEnvironmentVariable("RUSH_BG");
        if (string.IsNullOrEmpty(value)) return false;

        if (!Theme.TryParseHexColor(value, out var r16, out var g16, out var b16))
            return false;

        bgR = r16 / 65535.0;
        bgG = g16 / 65535.0;
        bgB = b16 / 65535.0;
        luminance = RelativeLuminance(bgR, bgG, bgB);
        return true;
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
}
