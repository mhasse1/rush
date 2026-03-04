using Rush;
using Xunit;

namespace Rush.Tests;

/// <summary>
/// Tests for JSONC comment stripping and config value handling.
/// </summary>
public class ConfigTests
{
    // ── JSONC Comment Stripping ──────────────────────────────────────

    [Fact]
    public void StripJsonComments_RemovesSingleLineComments()
    {
        var input = """
            {
              // This is a comment
              "key": "value"
            }
            """;
        var result = RushConfig.StripJsonComments(input);
        Assert.DoesNotContain("//", result);
        Assert.Contains("\"key\": \"value\"", result);
    }

    [Fact]
    public void StripJsonComments_PreservesStringsWithSlashes()
    {
        var input = """
            {
              "path": "http://example.com"
            }
            """;
        var result = RushConfig.StripJsonComments(input);
        Assert.Contains("http://example.com", result);
    }

    [Fact]
    public void StripJsonComments_PreservesEscapedQuotes()
    {
        var input = """
            {
              "val": "say \"hello\"" // comment here
            }
            """;
        var result = RushConfig.StripJsonComments(input);
        Assert.Contains("say \\\"hello\\\"", result);
        Assert.DoesNotContain("comment here", result);
    }

    [Fact]
    public void StripJsonComments_HandlesMultipleComments()
    {
        var input = """
            {
              // Comment 1
              "a": 1,
              // Comment 2
              "b": 2
            }
            """;
        var result = RushConfig.StripJsonComments(input);
        Assert.DoesNotContain("Comment 1", result);
        Assert.DoesNotContain("Comment 2", result);
        Assert.Contains("\"a\": 1", result);
        Assert.Contains("\"b\": 2", result);
    }

    [Fact]
    public void StripJsonComments_EmptyInput()
    {
        Assert.Equal("", RushConfig.StripJsonComments(""));
    }

    [Fact]
    public void StripJsonComments_NoComments()
    {
        var input = """{"key": "value"}""";
        var result = RushConfig.StripJsonComments(input);
        Assert.Equal(input, result);
    }

    // ── Setting Metadata ────────────────────────────────────────────

    [Fact]
    public void FindSetting_CaseInsensitive()
    {
        var info = RushConfig.FindSetting("editMode");
        Assert.NotNull(info);
        Assert.Equal("editMode", info.Key);
        Assert.Equal("Editing", info.Category);
    }

    [Fact]
    public void FindSetting_UnknownReturnsNull()
    {
        var info = RushConfig.FindSetting("nonexistent");
        Assert.Null(info);
    }

    // ── GetValue / SetValue ──────────────────────────────────────────

    [Fact]
    public void GetValue_ReturnsDefaults()
    {
        var config = new RushConfig();
        Assert.Equal("vi", config.GetValue("editMode"));
        Assert.Equal("500", config.GetValue("historySize"));
        Assert.Equal("auto", config.GetValue("theme"));
        Assert.Equal("false", config.GetValue("stopOnError"));
        Assert.Equal("true", config.GetValue("completionIgnoreCase"));
        Assert.Equal("true", config.GetValue("showTips"));
    }

    [Fact]
    public void SetValue_ValidBooleanSetting()
    {
        var config = new RushConfig();
        Assert.True(config.SetValue("stopOnError", "true"));
        Assert.True(config.StopOnError);
        Assert.Equal("true", config.GetValue("stopOnError"));
    }

    [Fact]
    public void SetValue_ValidStringSetting()
    {
        var config = new RushConfig();
        Assert.True(config.SetValue("editMode", "emacs"));
        Assert.Equal("emacs", config.EditMode);
    }

    [Fact]
    public void SetValue_InvalidValue_ReturnsFalse()
    {
        var config = new RushConfig();
        Assert.False(config.SetValue("editMode", "nano"));
        Assert.Equal("vi", config.EditMode); // Unchanged
    }

    [Fact]
    public void SetValue_InvalidKey_ReturnsFalse()
    {
        var config = new RushConfig();
        Assert.False(config.SetValue("nonexistent", "value"));
    }

    [Fact]
    public void SetValue_HistorySize()
    {
        var config = new RushConfig();
        Assert.True(config.SetValue("historySize", "1000"));
        Assert.Equal(1000, config.HistorySize);
        Assert.False(config.SetValue("historySize", "abc"));
    }

    [Fact]
    public void SetValue_Theme()
    {
        var config = new RushConfig();
        Assert.True(config.SetValue("theme", "dark"));
        Assert.Equal("dark", config.Theme);
        Assert.False(config.SetValue("theme", "neon"));
    }

    // ── AllSettings Consistency ──────────────────────────────────────

    [Fact]
    public void AllSettings_AllHaveDefaultValues()
    {
        foreach (var setting in RushConfig.AllSettings)
        {
            Assert.False(string.IsNullOrEmpty(setting.Key), $"Setting has empty key");
            Assert.False(string.IsNullOrEmpty(setting.DefaultValue), $"{setting.Key} has empty default");
            Assert.False(string.IsNullOrEmpty(setting.Description), $"{setting.Key} has empty description");
            Assert.False(string.IsNullOrEmpty(setting.Category), $"{setting.Key} has empty category");
        }
    }

    [Fact]
    public void AllSettings_DefaultsMatchNewConfig()
    {
        var config = new RushConfig();
        foreach (var setting in RushConfig.AllSettings)
        {
            var actualValue = config.GetValue(setting.Key);
            Assert.True(setting.DefaultValue == actualValue,
                $"Setting {setting.Key}: default in metadata is '{setting.DefaultValue}' but config has '{actualValue}'");
        }
    }
}
