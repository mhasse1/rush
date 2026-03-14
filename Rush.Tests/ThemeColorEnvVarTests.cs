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
        Assert.Equal("exfxcxdxbxegedabagacad", light);
    }
}
