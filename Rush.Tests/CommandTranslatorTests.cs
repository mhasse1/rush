using Rush;
using Xunit;

namespace Rush.Tests;

/// <summary>
/// Tests for CommandTranslator — Unix-to-PowerShell command translation.
/// On *nix, standard Unix commands run natively (return null from Translate).
/// Rush pipeline operators (where, select, count, etc.) and isAfterPipe
/// special cases (grep→Where-Object, sort→Sort-Object) always work.
/// </summary>
public class CommandTranslatorTests
{
    private readonly CommandTranslator _translator = new();

    // ── Basic Translation ───────────────────────────────────────────────

    [Fact]
    public void Echo_AlwaysTranslates()
    {
        // echo → Write-Output on all platforms (for PS variable expansion)
        var result = _translator.Translate("echo hello");
        Assert.Equal("Write-Output \"hello\"", result);
    }

    [Fact]
    public void NativeCommands_ReturnNull()
    {
        // Standard Unix commands run natively — no translation
        Assert.Null(_translator.Translate("ls"));
        Assert.Null(_translator.Translate("grep -i users file.txt"));
        Assert.Null(_translator.Translate("cp -r src/ dest/"));
        Assert.Null(_translator.Translate("rm -rf /tmp/junk"));
        Assert.Null(_translator.Translate("head -n 5 file.txt"));
        Assert.Null(_translator.Translate("tail -n 10 file.txt"));
        Assert.Null(_translator.Translate("sort file.txt"));
        Assert.Null(_translator.Translate("ps aux"));
        Assert.Null(_translator.Translate("kill 1234"));
        Assert.Null(_translator.Translate("pwd"));
        Assert.Null(_translator.Translate("env"));
        Assert.Null(_translator.Translate("which dotnet"));
    }

    // ── Pipe Context — Special After-Pipe Translations ──────────────────
    // isAfterPipe translations work regardless of platform — they're hardcoded
    // in TranslateSegment, independent of Register() calls.

    [Theory]
    [InlineData("data | grep test", "data | Where-Object { $_ -cmatch 'test' }")]
    [InlineData("data | grep -i test", "data | Where-Object { $_ -match 'test' }")]
    [InlineData("data | sort", "data | Sort-Object")]
    [InlineData("data | sort Name", "data | Sort-Object -Property Name")]
    [InlineData("data | sort -r", "data | Sort-Object -Descending")]
    // wc falls through to native/coreutils — no Measure-Object translation
    [InlineData("data | uniq", "data | Select-Object -Unique")]
    [InlineData("data | count", "data | Measure-Object | ForEach-Object { $_.Count }")]
    [InlineData("data | first", "data | Select-Object -First 1")]
    [InlineData("data | first 5", "data | Select-Object -First 5")]
    [InlineData("data | last", "data | Select-Object -Last 1")]
    [InlineData("data | last 3", "data | Select-Object -Last 3")]
    [InlineData("data | skip 2", "data | Select-Object -Skip 2")]
    public void PipeContext_SpecialCommands_TranslateCorrectly(string input, string expected)
    {
        var result = _translator.Translate(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void HeadAfterPipe_DefaultsTo10()
    {
        var result = _translator.Translate("data | head");
        Assert.Equal("data | Select-Object -First 10", result);
    }

    [Fact]
    public void HeadAfterPipe_WithCount()
    {
        var result = _translator.Translate("data | head -5");
        Assert.Equal("data | Select-Object -First 5", result);
    }

    [Fact]
    public void TailAfterPipe_DefaultsTo10()
    {
        var result = _translator.Translate("data | tail");
        Assert.Equal("data | Select-Object -Last 10", result);
    }

    // ── Format Conversion (as/from) ─────────────────────────────────────

    [Theory]
    [InlineData("data | as json", "data | ConvertTo-Json -Depth 5")]
    [InlineData("data | as csv", "data | ConvertTo-Csv -NoTypeInformation")]
    [InlineData("data | as table", "data | Format-Table -AutoSize")]
    [InlineData("data | as list", "data | Format-List")]
    [InlineData("data | from json", "data | ConvertFrom-Json")]
    [InlineData("data | from csv", "data | ConvertFrom-Csv")]
    public void FormatConversion_TranslatesCorrectly(string input, string expected)
    {
        var result = _translator.Translate(input);
        Assert.Equal(expected, result);
    }

    // ── Dot Notation (pipe property access) ─────────────────────────────

    [Fact]
    public void DotNotation_SimpleProperty()
    {
        var result = _translator.Translate("data | .Name");
        Assert.Equal("data | ForEach-Object { $_.Name }", result);
    }

    [Fact]
    public void DotNotation_ArrayExpansion()
    {
        var result = _translator.Translate("data | .items[].id");
        Assert.Equal("data | ForEach-Object { $_.items } | ForEach-Object { $_.id }", result);
    }

    // ── Where (pipe filter) ─────────────────────────────────────────────

    [Theory]
    [InlineData("data | where CPU > 10", "data | Where-Object { $_.CPU -gt 10 }")]
    [InlineData("data | where Name == chrome", "data | Where-Object { $_.Name -eq 'chrome' }")]
    [InlineData("data | where CPU < 5", "data | Where-Object { $_.CPU -lt 5 }")]
    public void WhereAfterPipe_TranslatesOperators(string input, string expected)
    {
        var result = _translator.Translate(input);
        Assert.Equal(expected, result);
    }

    // ── Math Aggregations ───────────────────────────────────────────────

    [Theory]
    [InlineData("data | sum Count", "data | Measure-Object -Property Count -Sum | ForEach-Object { $_.Sum }")]
    [InlineData("data | avg Score", "data | Measure-Object -Property Score -Average | ForEach-Object { $_.Average }")]
    [InlineData("data | min Size", "data | Measure-Object -Property Size -Minimum | ForEach-Object { $_.Minimum }")]
    [InlineData("data | max Size", "data | Measure-Object -Property Size -Maximum | ForEach-Object { $_.Maximum }")]
    public void MathAggregations_TranslateCorrectly(string input, string expected)
    {
        var result = _translator.Translate(input);
        Assert.Equal(expected, result);
    }

    // ── JSON Command ────────────────────────────────────────────────────

    [Fact]
    public void JsonStandalone_ReadsAndParses()
    {
        var result = _translator.Translate("json config.json");
        Assert.Equal("Get-Content config.json | ConvertFrom-Json", result);
    }

    [Fact]
    public void JsonAfterPipe_ConvertsFromJson()
    {
        var result = _translator.Translate("curl api | json");
        Assert.Contains("ConvertFrom-Json", result);
    }

    // ── Passthrough ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("git status")]
    [InlineData("docker ps")]
    [InlineData("npm install")]
    [InlineData("make build")]
    public void UnknownCommands_ReturnNull(string input)
    {
        var result = _translator.Translate(input);
        Assert.Null(result);
    }

    [Fact]
    public void NullPassthrough_CurlRegisteredButNoCmdlet()
    {
        // curl is registered with null cmdlet → passthrough
        var result = _translator.Translate("curl https://example.com");
        Assert.Null(result);
    }

    [Fact]
    public void Find_NotTranslated_RunsNatively()
    {
        // find should pass through to native execution (not translated to PS)
        var result = _translator.Translate("find . -iname '*.txt'");
        Assert.Null(result);
    }

    // ── Command/Flag Query APIs ─────────────────────────────────────────

    [Fact]
    public void GetCommandNames_ContainsPipelineOperators()
    {
        var names = _translator.GetCommandNames().ToList();
        Assert.Contains("echo", names);
        Assert.Contains("where", names);
        Assert.Contains("select", names);
    }

    [Fact]
    public void GetFlagsForCommand_UnknownCommand_ReturnsEmpty()
    {
        var flags = _translator.GetFlagsForCommand("nonexistent").ToList();
        Assert.Empty(flags);
    }

    [Fact]
    public void IsKnownCommand_ReturnsTrueForRegistered()
    {
        Assert.True(_translator.IsKnownCommand("echo"));
        Assert.True(_translator.IsKnownCommand("where"));
    }

    [Fact]
    public void IsKnownCommand_CaseInsensitive()
    {
        Assert.True(_translator.IsKnownCommand("ECHO"));
        Assert.True(_translator.IsKnownCommand("Where"));
    }

    [Fact]
    public void IsKnownCommand_ReturnsFalseForUnknown()
    {
        Assert.False(_translator.IsKnownCommand("git"));
        Assert.False(_translator.IsKnownCommand("docker"));
    }

    // ── Alias Registration ──────────────────────────────────────────────

    [Fact]
    public void RegisterAlias_AddsNewCommand()
    {
        _translator.RegisterAlias("ll", "Get-ChildItem -Force");
        Assert.True(_translator.IsKnownCommand("ll"));
    }

    // ── Echo Quoting ────────────────────────────────────────────────────

    [Fact]
    public void Echo_QuotesPositionalArgs()
    {
        var result = _translator.Translate("echo hello world");
        Assert.Equal("Write-Output \"hello world\"", result);
    }

    [Fact]
    public void Echo_StripsAndRewrapsQuotedArgs()
    {
        var result = _translator.Translate("echo 'hello world'");
        Assert.Equal("Write-Output \"hello world\"", result);
    }

    [Fact]
    public void Echo_ExpandsVariableReference()
    {
        var result = _translator.Translate("echo $x");
        Assert.Equal("Write-Output \"$x\"", result);
    }

    [Fact]
    public void Echo_MixedTextAndVariable()
    {
        var result = _translator.Translate("echo hello $x world");
        Assert.Equal("Write-Output \"hello $x world\"", result);
    }

    [Fact]
    public void Echo_DoubleQuotedInput()
    {
        var result = _translator.Translate("echo \"hello world\"");
        Assert.Equal("Write-Output \"hello world\"", result);
    }

    // ── Tee ─────────────────────────────────────────────────────────────

    [Fact]
    public void TeeAfterPipe_TranslatesToTeeObject()
    {
        var result = _translator.Translate("data | tee output.txt");
        Assert.Equal("data | Tee-Object -FilePath output.txt", result);
    }

    [Fact]
    public void TeeAfterPipe_Append()
    {
        var result = _translator.Translate("data | tee -a output.txt");
        Assert.Equal("data | Tee-Object -FilePath output.txt -Append", result);
    }

    // ── Distinct ────────────────────────────────────────────────────────

    [Fact]
    public void DistinctAfterPipe_Bare()
    {
        var result = _translator.Translate("data | distinct");
        Assert.Equal("data | Sort-Object -Unique", result);
    }

    [Fact]
    public void DistinctAfterPipe_WithProperty()
    {
        var result = _translator.Translate("data | distinct Name");
        Assert.Equal("data | Sort-Object -Property Name -Unique", result);
    }

    // ── User Alias Tests ────────────────────────────────────────────

    [Fact]
    public void UserAlias_IsUserAlias_ReturnsTrue()
    {
        _translator.RegisterAlias("gp", "git push");
        Assert.True(_translator.IsUserAlias("gp"));
    }

    [Fact]
    public void UserAlias_IsUserAlias_ReturnsFalseForBuiltins()
    {
        // echo is a built-in command mapping, not a user alias
        Assert.False(_translator.IsUserAlias("echo"));
    }

    [Fact]
    public void UserAlias_IsUserAlias_ReturnsFalseForUnknown()
    {
        Assert.False(_translator.IsUserAlias("nonexistent"));
    }

    [Fact]
    public void UserAlias_Translates()
    {
        _translator.RegisterAlias("gp", "git push");
        var result = _translator.Translate("gp");
        Assert.Equal("git push", result);
    }

    [Fact]
    public void UserAlias_TranslatesWithArgs()
    {
        _translator.RegisterAlias("gc", "git commit");
        var result = _translator.Translate("gc -m \"test\"");
        Assert.Equal("git commit -m \"test\"", result);
    }
}
