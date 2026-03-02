using Rush;
using Xunit;

namespace Rush.Tests;

/// <summary>
/// Tests for CommandTranslator — Unix-to-PowerShell command translation.
/// Covers: basic commands, flag translation, pipe context, special commands,
/// dot-notation, passthrough, and command/flag queries.
/// </summary>
public class CommandTranslatorTests
{
    private readonly CommandTranslator _translator = new();

    // ── Basic Translation ───────────────────────────────────────────────

    [Theory]
    [InlineData("ls", "Get-ChildItem")]
    [InlineData("cat file.txt", "Get-Content file.txt")]
    [InlineData("pwd", "Get-Location")]
    [InlineData("ps", "Get-Process")]
    [InlineData("echo hello", "Write-Output 'hello'")]
    [InlineData("env", "Get-ChildItem Env:")]
    [InlineData("which dotnet", "Get-Command dotnet")]
    [InlineData("clear", "Clear-Host")]
    public void BasicCommands_TranslateCorrectly(string input, string expected)
    {
        var result = _translator.Translate(input);
        Assert.Equal(expected, result);
    }

    // ── Flag Translation ────────────────────────────────────────────────

    [Fact]
    public void Ls_DashA_TranslatesToForce()
    {
        var result = _translator.Translate("ls -a");
        Assert.Equal("Get-ChildItem -Force", result);
    }

    [Fact]
    public void Ls_DashR_TranslatesToRecurse()
    {
        var result = _translator.Translate("ls -R");
        Assert.Equal("Get-ChildItem -Recurse", result);
    }

    [Fact]
    public void Rm_DashRf_TranslatesRecurseForce()
    {
        var result = _translator.Translate("rm -rf /tmp/junk");
        Assert.Equal("Remove-Item -Recurse -Force /tmp/junk", result);
    }

    [Fact]
    public void Cp_DashR_TranslatesToRecurse()
    {
        var result = _translator.Translate("cp -r src/ dest/");
        Assert.Equal("Copy-Item -Recurse src/ dest/", result);
    }

    // ── Pipe Context — Special After-Pipe Translations ──────────────────

    [Theory]
    [InlineData("ls | grep test", "Get-ChildItem | Where-Object { $_ -cmatch 'test' }")]
    [InlineData("ls | sort", "Get-ChildItem | Sort-Object")]
    [InlineData("ls | sort -r", "Get-ChildItem | Sort-Object -Descending")]
    [InlineData("ls | wc -l", "Get-ChildItem | Measure-Object -Line")]
    [InlineData("ls | uniq", "Get-ChildItem | Select-Object -Unique")]
    [InlineData("ls | count", "Get-ChildItem | Measure-Object | ForEach-Object { $_.Count }")]
    [InlineData("ls | first", "Get-ChildItem | Select-Object -First 1")]
    [InlineData("ls | first 5", "Get-ChildItem | Select-Object -First 5")]
    [InlineData("ls | last", "Get-ChildItem | Select-Object -Last 1")]
    [InlineData("ls | last 3", "Get-ChildItem | Select-Object -Last 3")]
    [InlineData("ls | skip 2", "Get-ChildItem | Select-Object -Skip 2")]
    public void PipeContext_SpecialCommands_TranslateCorrectly(string input, string expected)
    {
        var result = _translator.Translate(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void HeadAfterPipe_DefaultsTo10()
    {
        var result = _translator.Translate("ls | head");
        Assert.Equal("Get-ChildItem | Select-Object -First 10", result);
    }

    [Fact]
    public void HeadAfterPipe_WithCount()
    {
        var result = _translator.Translate("ls | head -5");
        Assert.Equal("Get-ChildItem | Select-Object -First 5", result);
    }

    [Fact]
    public void TailAfterPipe_DefaultsTo10()
    {
        var result = _translator.Translate("ls | tail");
        Assert.Equal("Get-ChildItem | Select-Object -Last 10", result);
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
        var result = _translator.Translate("ps | .ProcessName");
        Assert.Equal("Get-Process | ForEach-Object { $_.ProcessName }", result);
    }

    [Fact]
    public void DotNotation_ArrayExpansion()
    {
        var result = _translator.Translate("data | .items[].id");
        Assert.Equal("data | ForEach-Object { $_.items } | ForEach-Object { $_.id }", result);
    }

    // ── Where (pipe filter) ─────────────────────────────────────────────

    [Theory]
    [InlineData("ps | where CPU > 10", "Get-Process | Where-Object { $_.CPU -gt 10 }")]
    [InlineData("ps | where Name == chrome", "Get-Process | Where-Object { $_.Name -eq 'chrome' }")]
    [InlineData("ps | where CPU < 5", "Get-Process | Where-Object { $_.CPU -lt 5 }")]
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

    // ── Command/Flag Query APIs ─────────────────────────────────────────

    [Fact]
    public void GetCommandNames_ContainsBasicCommands()
    {
        var names = _translator.GetCommandNames().ToList();
        Assert.Contains("ls", names);
        Assert.Contains("grep", names);
        Assert.Contains("cat", names);
        Assert.Contains("rm", names);
    }

    [Fact]
    public void GetFlagsForCommand_Ls_ContainsExpectedFlags()
    {
        var flags = _translator.GetFlagsForCommand("ls").ToList();
        Assert.Contains("-a", flags);
        Assert.Contains("-l", flags);
        Assert.Contains("-R", flags);
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
        Assert.True(_translator.IsKnownCommand("ls"));
        Assert.True(_translator.IsKnownCommand("grep"));
    }

    [Fact]
    public void IsKnownCommand_CaseInsensitive()
    {
        Assert.True(_translator.IsKnownCommand("LS"));
        Assert.True(_translator.IsKnownCommand("Grep"));
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
        // echo combines args into single quoted output
        Assert.Equal("Write-Output 'hello world'", result);
    }

    [Fact]
    public void Echo_PreservesExistingQuotes()
    {
        var result = _translator.Translate("echo 'hello world'");
        // Pre-quoted args get wrapped again by quoting logic
        Assert.Equal("Write-Output ''hello world''", result);
    }

    // ── Tee ─────────────────────────────────────────────────────────────

    [Fact]
    public void TeeAfterPipe_TranslatesToTeeObject()
    {
        var result = _translator.Translate("ls | tee output.txt");
        Assert.Equal("Get-ChildItem | Tee-Object -FilePath output.txt", result);
    }

    [Fact]
    public void TeeAfterPipe_Append()
    {
        var result = _translator.Translate("ls | tee -a output.txt");
        Assert.Equal("Get-ChildItem | Tee-Object -FilePath output.txt -Append", result);
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
}
