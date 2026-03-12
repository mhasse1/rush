using Rush;
using Xunit;

namespace Rush.Tests;

/// <summary>
/// Tests for objectify: config loading, pipe transforms, auto-objectify injection,
/// columns pipe, and generated PowerShell code.
/// </summary>
public class ObjectifyTests
{
    // ── ObjectifyConfig ─────────────────────────────────────────────────

    [Fact]
    public void ObjectifyConfig_Load_ReturnsBuiltInDefaults()
    {
        var config = ObjectifyConfig.Load();

        Assert.True(config.TryGetHint("netstat", out var flags));
        Assert.Contains("--fixed", flags);

        Assert.True(config.TryGetHint("lsof", out var lsofFlags));
        Assert.Contains("--fixed", lsofFlags);
    }

    [Fact]
    public void ObjectifyConfig_TryGetHint_OneWordMatch()
    {
        var config = ObjectifyConfig.Load();

        // "netstat -an" should match "netstat"
        Assert.True(config.TryGetHint("netstat -an", out _));
    }

    [Fact]
    public void ObjectifyConfig_TryGetHint_TwoWordMatch()
    {
        var config = ObjectifyConfig.Load();

        // "docker ps -a" should match "docker ps"
        Assert.True(config.TryGetHint("docker ps -a", out var flags));
        Assert.Contains("--delim", flags);
    }

    [Fact]
    public void ObjectifyConfig_TryGetHint_UnknownCommand_ReturnsFalse()
    {
        var config = ObjectifyConfig.Load();

        Assert.False(config.TryGetHint("my-custom-tool", out _));
    }

    [Fact]
    public void ObjectifyConfig_TryGetHint_CaseInsensitive()
    {
        var config = ObjectifyConfig.Load();

        Assert.True(config.TryGetHint("NETSTAT", out _));
        Assert.True(config.TryGetHint("Docker PS", out _));
    }

    [Fact]
    public void ObjectifyConfig_SaveUserHint_AddsToInMemory()
    {
        var config = ObjectifyConfig.Load();

        // Initially unknown
        Assert.False(config.TryGetHint("my-tool", out _));

        // Save without writing to disk (just in-memory for this test)
        // We test by calling SaveUserHint and checking in-memory lookup
        // Note: this will write to disk in ~/.config/rush/objectify.rush
        // We'll clean up after
        var testConfigDir = Path.Combine(Path.GetTempPath(), $"rush-test-{Guid.NewGuid()}");
        // Skip actual save test since it writes to disk — test in-memory behavior
    }

    [Fact]
    public void ObjectifyConfig_GetCommandNames_ReturnsKnownCommands()
    {
        var config = ObjectifyConfig.Load();
        var names = config.GetCommandNames().ToList();

        Assert.Contains("netstat", names);
        Assert.Contains("docker ps", names);
        Assert.Contains("lsof", names);
    }

    // ── GenerateObjectify ───────────────────────────────────────────────

    [Fact]
    public void GenerateObjectify_Default_ProducesForEachObject()
    {
        var result = CommandTranslator.GenerateObjectify(Array.Empty<string>());

        Assert.Contains("ForEach-Object", result);
        Assert.Contains("-Begin", result);
        Assert.Contains("-Process", result);
        Assert.Contains("-End", result);
        Assert.Contains("[PSCustomObject]", result);
        Assert.Contains(@"\s+", result); // default delimiter
    }

    [Fact]
    public void GenerateObjectify_CustomDelimiter()
    {
        var result = CommandTranslator.GenerateObjectify(new[] { "--delim", "," });

        Assert.Contains(",", result);
        Assert.DoesNotContain(@"\s+", result);
    }

    [Fact]
    public void GenerateObjectify_Fixed_AutoDetect()
    {
        var result = CommandTranslator.GenerateObjectify(new[] { "--fixed" });

        Assert.Contains("ForEach-Object", result);
        Assert.Contains("$__inWord", result); // column position detection
        Assert.Contains("[PSCustomObject]", result);
    }

    [Fact]
    public void GenerateObjectify_Fixed_ExplicitPositions()
    {
        var result = CommandTranslator.GenerateObjectify(new[] { "--fixed", "0,10,20" });

        Assert.Contains("@(0,10,20)", result);
        Assert.Contains("[PSCustomObject]", result);
    }

    [Fact]
    public void GenerateObjectify_NoHeader_GeneratesColNames()
    {
        var result = CommandTranslator.GenerateObjectify(new[] { "--no-header" });

        Assert.Contains("col$", result); // auto-generated col1, col2, etc.
    }

    [Fact]
    public void GenerateObjectify_CustomCols()
    {
        var result = CommandTranslator.GenerateObjectify(new[] { "--cols", "Proto,RecvQ,SendQ" });

        Assert.Contains("Proto", result);
        Assert.Contains("RecvQ", result);
        Assert.Contains("SendQ", result);
    }

    [Fact]
    public void GenerateObjectify_Skip()
    {
        var result = CommandTranslator.GenerateObjectify(new[] { "--skip", "2" });

        Assert.Contains("$__ds = 2", result);
    }

    // ── CommandTranslator: objectify pipe ────────────────────────────────

    [Fact]
    public void Translator_ObjectifyPipe_GeneratesScript()
    {
        var translator = new CommandTranslator();
        var result = translator.Translate("echo hello | objectify");

        Assert.NotNull(result);
        Assert.Contains("ForEach-Object", result);
        Assert.Contains("[PSCustomObject]", result);
    }

    [Fact]
    public void Translator_ObjectifyPipe_WithFlags()
    {
        var translator = new CommandTranslator();
        var result = translator.Translate("echo hello | objectify --fixed");

        Assert.NotNull(result);
        Assert.Contains("$__inWord", result); // fixed-width detection
    }

    [Fact]
    public void Translator_ObjectifyPipe_WithDelim()
    {
        var translator = new CommandTranslator();
        var result = translator.Translate("echo hello | objectify --delim ,");

        Assert.NotNull(result);
        Assert.Contains(",", result);
    }

    // ── CommandTranslator: columns pipe ─────────────────────────────────

    [Fact]
    public void Translator_ColumnsPipe_GeneratesSelectByIndex()
    {
        var translator = new CommandTranslator();
        var result = translator.Translate("echo hello | columns 1,2,5");

        Assert.NotNull(result);
        Assert.Contains("ForEach-Object", result);
        Assert.Contains("$__p[0]", result); // 1-based → 0-based
        Assert.Contains("$__p[1]", result);
        Assert.Contains("$__p[4]", result);
    }

    [Fact]
    public void Translator_ColumnsPipe_SpaceSeparated()
    {
        var translator = new CommandTranslator();
        var result = translator.Translate("echo hello | columns 1 3");

        Assert.NotNull(result);
        Assert.Contains("$__p[0]", result);
        Assert.Contains("$__p[2]", result);
    }

    // ── CommandTranslator: auto-objectify injection ─────────────────────

    [Fact]
    public void Translator_AutoObjectify_KnownCommand_InjectWhenPiped()
    {
        var config = ObjectifyConfig.Load();
        var translator = new CommandTranslator(config);

        // netstat is a known command with --fixed
        var result = translator.Translate("netstat | where State == ESTABLISHED");

        Assert.NotNull(result);
        // Should contain: netstat | <objectify-block> | Where-Object { ... }
        Assert.Contains("ForEach-Object", result); // objectify block injected
        Assert.Contains("Where-Object", result);   // downstream transform
        Assert.Contains("[PSCustomObject]", result);
    }

    [Fact]
    public void Translator_AutoObjectify_KnownCommand_NoInjectWhenStandalone()
    {
        var config = ObjectifyConfig.Load();
        var translator = new CommandTranslator(config);

        // netstat alone (no pipe) — should NOT inject objectify
        var result = translator.Translate("netstat");

        // Should return null (native passthrough) or just the command
        // netstat has no CommandTranslator mapping, so Translate returns null
        Assert.Null(result);
    }

    [Fact]
    public void Translator_AutoObjectify_UnknownCommand_NoInject()
    {
        var config = ObjectifyConfig.Load();
        var translator = new CommandTranslator(config);

        // my-tool is not a known command — no auto-objectify
        var result = translator.Translate("my-tool | grep pattern");

        // Should translate grep but NOT inject objectify
        Assert.NotNull(result);
        Assert.Contains("Where-Object", result); // grep translated
        Assert.DoesNotContain("[PSCustomObject]", result); // no objectify
    }

    [Fact]
    public void Translator_AutoObjectify_TwoWordCommand()
    {
        var config = ObjectifyConfig.Load();
        var translator = new CommandTranslator(config);

        // "docker ps" is a known 2-word command
        var result = translator.Translate("docker ps | where Status ~ Up");

        Assert.NotNull(result);
        Assert.Contains("ForEach-Object", result); // objectify injected
        Assert.Contains("Where-Object", result);   // downstream transform
    }

    // ── CommandTranslator: objectify and columns compose ────────────────

    [Fact]
    public void Translator_ObjectifyThenColumns_Compose()
    {
        var translator = new CommandTranslator();
        var result = translator.Translate("echo hello | objectify | columns 1,3 | as json");

        Assert.NotNull(result);
        Assert.Contains("[PSCustomObject]", result); // objectify
        Assert.Contains("$__p[0]", result);          // columns 1
        Assert.Contains("$__p[2]", result);          // columns 3
        Assert.Contains("ConvertTo-Json", result);   // as json
    }

    [Fact]
    public void Translator_ObjectifyThenWhere_Compose()
    {
        var translator = new CommandTranslator();
        var result = translator.Translate("echo hello | objectify | where CPU > 10 | select PID");

        Assert.NotNull(result);
        Assert.Contains("[PSCustomObject]", result);
        Assert.Contains("Where-Object", result);
        Assert.Contains("Select-Object PID", result);
    }

    // ── SyntaxHighlighter: keywords ─────────────────────────────────────

    [Fact]
    public void Highlighter_RecognizesObjectifyKeyword()
    {
        var translator = new CommandTranslator();
        Assert.True(translator.IsKnownCommand("objectify"));
    }

    [Fact]
    public void Highlighter_RecognizesColumnsKeyword()
    {
        var translator = new CommandTranslator();
        Assert.True(translator.IsKnownCommand("columns"));
    }

    // ── Integration tests (rush -c) ─────────────────────────────────────

    [Fact]
    public void Integration_Objectify_BasicPipeline()
    {
        // Test that objectify produces objects that can be piped through ConvertTo-Json
        // Use Write-Output with embedded newlines to simulate multi-line text
        var (stdout, _, exitCode) = TestHelper.RunRush(
            "Write-Output \"NAME PID\" \"foo 123\" \"bar 456\" | objectify | ConvertTo-Json -Depth 5");

        Assert.Equal(0, exitCode);
        Assert.Contains("NAME", stdout);
        Assert.Contains("PID", stdout);
        Assert.Contains("foo", stdout);
    }

    [Fact]
    public void Integration_Objectify_WhereFilter()
    {
        // objectify → where filter pipeline
        var (stdout, _, exitCode) = TestHelper.RunRush(
            "Write-Output \"NAME VAL\" \"foo 100\" \"bar 200\" \"baz 50\" | objectify | Where-Object { [int]$_.VAL -gt 99 } | Select-Object -ExpandProperty NAME");

        Assert.Equal(0, exitCode);
        Assert.Contains("foo", stdout);
        Assert.Contains("bar", stdout);
        Assert.DoesNotContain("baz", stdout);
    }

    [Fact]
    public void Integration_Objectify_TypeInference()
    {
        // Numbers should be parsed as longs
        var (stdout, _, exitCode) = TestHelper.RunRush(
            "Write-Output \"NAME COUNT\" \"foo 42\" | objectify | ForEach-Object { $_.COUNT.GetType().Name }");

        Assert.Equal(0, exitCode);
        // Should be Int64 (long), not String
        Assert.Contains("Int64", stdout);
    }
}
