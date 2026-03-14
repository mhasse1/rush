using Rush;
using Xunit;

namespace Rush.Tests;

/// <summary>
/// Tests for Theme.SetNativeColorEnvVars() — theme-aware LS_COLORS, LSCOLORS, GREP_COLORS.
/// </summary>
public class ThemeColorEnvVarTests : IDisposable
{
    private readonly string? _origLsColors;
    private readonly string? _origLsColorsBsd;
    private readonly string? _origGrepColors;
    private readonly string? _origNoColor;
    private readonly string? _origCliColor;

    public ThemeColorEnvVarTests()
    {
        // Save originals
        _origLsColors    = Environment.GetEnvironmentVariable("LS_COLORS");
        _origLsColorsBsd = Environment.GetEnvironmentVariable("LSCOLORS");
        _origGrepColors  = Environment.GetEnvironmentVariable("GREP_COLORS");
        _origNoColor     = Environment.GetEnvironmentVariable("NO_COLOR");
        _origCliColor    = Environment.GetEnvironmentVariable("CLICOLOR");

        // Clear all for a clean slate
        ClearColorVars();
    }

    public void Dispose()
    {
        // Restore originals
        Environment.SetEnvironmentVariable("LS_COLORS",   _origLsColors);
        Environment.SetEnvironmentVariable("LSCOLORS",    _origLsColorsBsd);
        Environment.SetEnvironmentVariable("GREP_COLORS", _origGrepColors);
        Environment.SetEnvironmentVariable("NO_COLOR",    _origNoColor);
        Environment.SetEnvironmentVariable("CLICOLOR",    _origCliColor);
    }

    private static void ClearColorVars()
    {
        Environment.SetEnvironmentVariable("LS_COLORS",   null);
        Environment.SetEnvironmentVariable("LSCOLORS",    null);
        Environment.SetEnvironmentVariable("GREP_COLORS", null);
        Environment.SetEnvironmentVariable("NO_COLOR",    null);
        Environment.SetEnvironmentVariable("CLICOLOR",    null);
    }

    [Fact]
    public void DarkTheme_SetsBoldColors()
    {
        Theme.Initialize(isDarkOverride: true);
        Theme.SetNativeColorEnvVars();

        var ls = Environment.GetEnvironmentVariable("LS_COLORS");
        Assert.NotNull(ls);
        Assert.Contains("di=1;34", ls);   // bold blue directories
        Assert.Contains("ln=1;36", ls);   // bold cyan symlinks
    }

    [Fact]
    public void LightTheme_SetsNonBoldColors()
    {
        Theme.Initialize(isDarkOverride: false);
        Theme.SetNativeColorEnvVars();

        var ls = Environment.GetEnvironmentVariable("LS_COLORS");
        Assert.NotNull(ls);
        Assert.Contains("di=34", ls);     // plain blue (not bold)
        Assert.DoesNotContain("di=1;34", ls);
    }

    [Fact]
    public void UserSetVar_NotOverwritten()
    {
        var custom = "di=1;33:ln=1;35";
        Environment.SetEnvironmentVariable("LS_COLORS", custom);

        Theme.Initialize(isDarkOverride: true);
        Theme.SetNativeColorEnvVars();

        Assert.Equal(custom, Environment.GetEnvironmentVariable("LS_COLORS"));
    }

    [Fact]
    public void NoColor_SuppressesAllVars()
    {
        Environment.SetEnvironmentVariable("NO_COLOR", "1");

        Theme.Initialize(isDarkOverride: true);
        Theme.SetNativeColorEnvVars();

        Assert.Null(Environment.GetEnvironmentVariable("LS_COLORS"));
        Assert.Null(Environment.GetEnvironmentVariable("LSCOLORS"));
        Assert.Null(Environment.GetEnvironmentVariable("GREP_COLORS"));
    }

    [Fact]
    public void ThemeSwitch_UpdatesRushSetVars()
    {
        // Start dark
        Theme.Initialize(isDarkOverride: true);
        Theme.SetNativeColorEnvVars();
        var darkLs = Environment.GetEnvironmentVariable("LS_COLORS");
        Assert.Contains("di=1;34", darkLs);

        // Switch to light
        Theme.Initialize(isDarkOverride: false);
        Theme.SetNativeColorEnvVars();
        var lightLs = Environment.GetEnvironmentVariable("LS_COLORS");
        Assert.Contains("di=34", lightLs);
        Assert.DoesNotContain("di=1;34", lightLs);
    }

    [Fact]
    public void GrepColors_SetForBothThemes()
    {
        Theme.Initialize(isDarkOverride: true);
        Theme.SetNativeColorEnvVars();
        var darkGrep = Environment.GetEnvironmentVariable("GREP_COLORS");
        Assert.NotNull(darkGrep);
        Assert.Contains("ms=01;31", darkGrep);  // bold red

        ClearColorVars();

        Theme.Initialize(isDarkOverride: false);
        Theme.SetNativeColorEnvVars();
        var lightGrep = Environment.GetEnvironmentVariable("GREP_COLORS");
        Assert.NotNull(lightGrep);
        Assert.Contains("ms=31", lightGrep);     // plain red
        Assert.DoesNotContain("ms=01;31", lightGrep);
    }

    [Fact]
    public void BsdLsColors_SetForBothThemes()
    {
        Theme.Initialize(isDarkOverride: true);
        Theme.SetNativeColorEnvVars();
        var dark = Environment.GetEnvironmentVariable("LSCOLORS");
        Assert.Equal("ExGxFxdxCxegedabagacad", dark);

        ClearColorVars();

        Theme.Initialize(isDarkOverride: false);
        Theme.SetNativeColorEnvVars();
        var light = Environment.GetEnvironmentVariable("LSCOLORS");
        // socket=magenta(f), exec=green(c) — consistent with LS_COLORS
        Assert.Equal("exfxfxdxcxegedabagacad", light);
    }

    // ── Contrast Validation Tests (direct builder methods) ───────────

    [Fact]
    public void EnsureSgrContrast_BlueOnTeal_Substituted()
    {
        // Teal background: RGB(0, 0.55, 0.55) — blue (34) is invisible
        double bgLum = TerminalBackground.RelativeLuminance(0, 0.55, 0.55);
        var result = Theme.EnsureSgrContrast("1;34", bgLum, isDark: true);
        // Should NOT contain dark blue (34) — should substitute
        Assert.DoesNotContain("34", result);
    }

    [Fact]
    public void EnsureSgrContrast_MagentaOnPurple_Substituted()
    {
        // Deep purple background: RGB(0.25, 0, 0.35) — bright magenta (95) is too close
        double bgLum = TerminalBackground.RelativeLuminance(0.25, 0.0, 0.35);
        var result = Theme.EnsureSgrContrast("35", bgLum, isDark: true);
        // Dark magenta (35) should be substituted — not enough contrast
        Assert.NotEqual("35", result);
    }

    [Fact]
    public void EnsureSgrContrast_WithBgCode_Unchanged()
    {
        // Entry with background code (37;41 = white on red) should not be modified
        double bgLum = TerminalBackground.RelativeLuminance(0, 0.55, 0.55);
        var result = Theme.EnsureSgrContrast("37;41", bgLum, isDark: true);
        Assert.Equal("37;41", result);
    }

    [Fact]
    public void EnsureSgrContrast_GoodContrastUnchanged()
    {
        // White (97) on black background — excellent contrast, should stay
        double bgLum = TerminalBackground.RelativeLuminance(0, 0, 0);
        var result = Theme.EnsureSgrContrast("1;97", bgLum, isDark: true);
        Assert.Equal("1;97", result);
    }

    [Fact]
    public void BuildLsColors_PureBlack_MatchesDarkDefaults()
    {
        // Pure black background — should produce the original dark LS_COLORS
        double bgLum = TerminalBackground.RelativeLuminance(0, 0, 0);
        var result = Theme.BuildLsColors(isDark: true, bgLum);
        Assert.Contains("di=1;34", result);   // bold blue
        Assert.Contains("ln=1;36", result);   // bold cyan
        Assert.Contains("su=37;41", result);  // bg entries unchanged
    }

    [Fact]
    public void BuildLsColors_PureWhite_MatchesLightDefaults()
    {
        double bgLum = TerminalBackground.RelativeLuminance(1, 1, 1);
        var result = Theme.BuildLsColors(isDark: false, bgLum);
        Assert.Contains("di=34", result);
        Assert.Contains("su=37;41", result);
    }

    [Fact]
    public void BuildLsColors_NoBgLum_SkipsValidation()
    {
        // bgLum = -1 (no RGB data) — should return unmodified defaults
        var dark = Theme.BuildLsColors(isDark: true, bgLum: -1);
        Assert.Contains("di=1;34", dark);
        var light = Theme.BuildLsColors(isDark: false, bgLum: -1);
        Assert.Contains("di=34", light);
    }

    [Fact]
    public void BuildLsColors_TealBg_AdjustsBlueAndCyan()
    {
        // Teal background — blue (34) and cyan (36) should be adjusted
        double bgLum = TerminalBackground.RelativeLuminance(0, 0.55, 0.55);
        var result = Theme.BuildLsColors(isDark: true, bgLum);
        // Directory should no longer be bold blue
        Assert.DoesNotContain("di=1;34", result);
        // But entries with bg codes should be untouched
        Assert.Contains("su=37;41", result);
        Assert.Contains("sg=30;43", result);
    }

    [Fact]
    public void BuildLsColorsBsd_PureBlack_MatchesDarkDefaults()
    {
        double bgLum = TerminalBackground.RelativeLuminance(0, 0, 0);
        var result = Theme.BuildLsColorsBsd(isDark: true, bgLum);
        Assert.Equal("ExGxFxdxCxegedabagacad", result);
    }

    [Fact]
    public void BuildLsColorsBsd_PureWhite_MatchesLightDefaults()
    {
        double bgLum = TerminalBackground.RelativeLuminance(1, 1, 1);
        var result = Theme.BuildLsColorsBsd(isDark: false, bgLum);
        // Consistent with LS_COLORS: socket=magenta(f), exec=green(c)
        Assert.Equal("exfxfxdxcxegedabagacad", result);
    }

    [Fact]
    public void BuildGrepColors_PureBlack_MatchesDarkDefaults()
    {
        double bgLum = TerminalBackground.RelativeLuminance(0, 0, 0);
        var result = Theme.BuildGrepColors(isDark: true, bgLum);
        Assert.Contains("ms=01;31", result);
        // fn=35 (dark magenta) fails contrast on black → bumped to fn=95
        Assert.Contains("fn=95", result);
    }

    [Fact]
    public void BuildGrepColors_NoBgLum_SkipsValidation()
    {
        var result = Theme.BuildGrepColors(isDark: true, bgLum: -1);
        Assert.Contains("ms=01;31", result);
    }

    [Fact]
    public void EnsureSgrContrast_EmptyString_ReturnsEmpty()
    {
        var result = Theme.EnsureSgrContrast("", 0.5, isDark: true);
        Assert.Equal("", result);
    }

    // ── Hex Color Parsing Tests ──────────────────────────────────────

    [Fact]
    public void TryParseHexColor_6Digit()
    {
        Assert.True(Theme.TryParseHexColor("#330000", out var r, out var g, out var b));
        Assert.Equal(0x33 * 257, r);  // 8-bit → 16-bit
        Assert.Equal(0, g);
        Assert.Equal(0, b);
    }

    [Fact]
    public void TryParseHexColor_3Digit()
    {
        Assert.True(Theme.TryParseHexColor("#F80", out var r, out var g, out var b));
        Assert.Equal(0xF * 0x1111, r);
        Assert.Equal(0x8 * 0x1111, g);
        Assert.Equal(0, b);
    }

    [Fact]
    public void TryParseHexColor_NoHash()
    {
        Assert.True(Theme.TryParseHexColor("FF0000", out var r, out _, out _));
        Assert.Equal(0xFF * 257, r);
    }

    [Fact]
    public void TryParseHexColor_Invalid()
    {
        Assert.False(Theme.TryParseHexColor("none", out _, out _, out _));
        Assert.False(Theme.TryParseHexColor("", out _, out _, out _));
        Assert.False(Theme.TryParseHexColor("#GG0000", out _, out _, out _));
    }
}
