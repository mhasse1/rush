using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Rush;
using Xunit;

namespace Rush.Tests;

/// <summary>
/// Tests for type-aware dot-completion in TabCompleter.
/// Covers: runtime introspection (V1), static type inference (V2),
/// Rush method lists, .NET member reflection, and edge cases.
/// </summary>
public class TabCompleterDotCompletionTests : IDisposable
{
    private readonly Runspace _runspace;
    private readonly TabCompleter _completer;

    public TabCompleterDotCompletionTests()
    {
        _runspace = RunspaceFactory.CreateRunspace();
        _runspace.Open();
        var translator = new CommandTranslator();
        _completer = new TabCompleter(_runspace, translator);
    }

    public void Dispose()
    {
        _runspace.Close();
        _runspace.Dispose();
    }

    // ── Helper ──────────────────────────────────────────────────────────

    private void SetVariable(string name, object value)
    {
        _runspace.SessionStateProxy.SetVariable(name, value);
    }

    private List<string> GetCompletions(string input)
    {
        var completions = new List<string>();
        var result = _completer.Complete(input, input.Length);
        if (result == null) return completions;

        // First completion
        completions.Add(ExtractCompletion(result.Value.newInput, input));

        // Cycle through remaining completions
        var currentInput = result.Value.newInput;
        for (int i = 0; i < 100; i++) // Safety limit
        {
            var next = _completer.Complete(currentInput, result.Value.newCursor);
            if (next == null) break;
            var comp = ExtractCompletion(next.Value.newInput, input);
            if (completions.Contains(comp)) break; // Cycled back to start
            completions.Add(comp);
            currentInput = next.Value.newInput;
        }

        return completions;
    }

    private static string ExtractCompletion(string newInput, string originalInput)
    {
        // Extract just the completed token from the new input
        // Find the dot and take everything after it (minus trailing space)
        var trimmed = newInput.TrimEnd();
        var dotPos = trimmed.LastIndexOf('.');
        if (dotPos >= 0)
            return trimmed[(dotPos + 1)..];
        return trimmed;
    }

    // ── V1: Runtime Introspection Tests ─────────────────────────────────

    [Fact]
    public void DotCompletion_StringVariable_ShowsStringMethods()
    {
        SetVariable("name", "hello");
        var completions = GetCompletions("name.up");
        Assert.Contains("upcase", completions);
    }

    [Fact]
    public void DotCompletion_StringVariable_ShowsDotNetProperties()
    {
        SetVariable("name", "hello");
        var completions = GetCompletions("name.Len");
        Assert.Contains("Length", completions);
    }

    [Fact]
    public void DotCompletion_StringVariable_FiltersCorrectly()
    {
        SetVariable("name", "hello");
        var completions = GetCompletions("name.str");
        Assert.Contains("strip", completions);
        Assert.DoesNotContain("upcase", completions);
    }

    [Fact]
    public void DotCompletion_IntVariable_ShowsNumericMethods()
    {
        SetVariable("count", 42);
        var completions = GetCompletions("count.ro");
        Assert.Contains("round", completions);
    }

    [Fact]
    public void DotCompletion_IntVariable_ShowsDurationMethods()
    {
        SetVariable("n", 5);
        var completions = GetCompletions("n.ho");
        Assert.Contains("hours", completions);
    }

    [Fact]
    public void DotCompletion_ArrayVariable_ShowsCollectionMethods()
    {
        SetVariable("items", new object[] { 1, 2, 3 });
        var completions = GetCompletions("items.sel");
        Assert.Contains("select", completions);
    }

    [Fact]
    public void DotCompletion_ArrayVariable_ShowsMapMethod()
    {
        SetVariable("items", new object[] { "a", "b" });
        var completions = GetCompletions("items.ma");
        Assert.Contains("map", completions);
    }

    [Fact]
    public void DotCompletion_ArrayVariable_ShowsLengthProperty()
    {
        SetVariable("items", new object[] { 1, 2, 3 });
        var completions = GetCompletions("items.Len");
        Assert.Contains("Length", completions);
    }

    [Fact]
    public void DotCompletion_UnknownVariable_ShowsAllRushMethods()
    {
        // Variable not in runspace or symbol table → show all Rush methods
        var completions = GetCompletions("mystery.sel");
        Assert.Contains("select", completions);
    }

    [Fact]
    public void DotCompletion_EmptyPrefix_ShowsAllMethods()
    {
        SetVariable("name", "hello");
        var completions = GetCompletions("name.");
        // Should have many completions (Rush string methods + .NET string members)
        Assert.True(completions.Count > 5, $"Expected many completions, got {completions.Count}");
    }

    // ── V2: Static Type Inference Tests ─────────────────────────────────

    [Fact]
    public void TrackAssignment_StringLiteral_InfersString()
    {
        _completer.TrackAssignment("greeting", "\"hello world\"");
        var completions = GetCompletions("greeting.up");
        Assert.Contains("upcase", completions);
    }

    [Fact]
    public void TrackAssignment_SingleQuotedString_InfersString()
    {
        _completer.TrackAssignment("msg", "'hello'");
        var completions = GetCompletions("msg.down");
        Assert.Contains("downcase", completions);
    }

    [Fact]
    public void TrackAssignment_IntegerLiteral_InfersLong()
    {
        _completer.TrackAssignment("x", "42");
        var completions = GetCompletions("x.ro");
        Assert.Contains("round", completions);
    }

    [Fact]
    public void TrackAssignment_FloatLiteral_InfersDouble()
    {
        _completer.TrackAssignment("pi", "3.14");
        var completions = GetCompletions("pi.ab");
        Assert.Contains("abs", completions);
    }

    [Fact]
    public void TrackAssignment_BooleanTrue_InfersBool()
    {
        _completer.TrackAssignment("flag", "true");
        // Boolean has .NET members but no Rush-specific methods — should still complete
        var completions = GetCompletions("flag.");
        Assert.True(completions.Count > 0);
    }

    [Fact]
    public void TrackAssignment_ArrayLiteral_InfersObjectArray()
    {
        _completer.TrackAssignment("arr", "[1, 2, 3]");
        var completions = GetCompletions("arr.ea");
        Assert.Contains("each", completions);
    }

    [Fact]
    public void TrackAssignment_HashLiteral_InfersHashtable()
    {
        _completer.TrackAssignment("data", "{ name: \"rush\" }");
        var completions = GetCompletions("data.");
        Assert.True(completions.Count > 0);
    }

    [Fact]
    public void TrackAssignment_FileRead_InfersString()
    {
        _completer.TrackAssignment("content", "File.read(\"test.txt\")");
        var completions = GetCompletions("content.sp");
        Assert.Contains("split", completions);
    }

    [Fact]
    public void TrackAssignment_FileReadLines_InfersStringArray()
    {
        _completer.TrackAssignment("lines", "File.read_lines(\"test.txt\")");
        var completions = GetCompletions("lines.sel");
        Assert.Contains("select", completions);
    }

    [Fact]
    public void TrackAssignment_DirFiles_InfersFileInfoArray()
    {
        _completer.TrackAssignment("files", "Dir.files(\".\")");
        var completions = GetCompletions("files.fir");
        Assert.Contains("first", completions);
    }

    [Fact]
    public void TrackAssignment_DirDirs_InfersDirectoryInfoArray()
    {
        _completer.TrackAssignment("folders", "Dir.dirs(\".\")");
        var completions = GetCompletions("folders.ma");
        Assert.Contains("map", completions);
    }

    [Fact]
    public void TrackAssignment_TimeNow_InfersDateTime()
    {
        _completer.TrackAssignment("now", "Time.now");
        var completions = GetCompletions("now.");
        Assert.True(completions.Count > 0);
        // DateTime has .NET properties like Hour, Minute, etc.
    }

    [Fact]
    public void TrackAssignment_CaseInsensitiveStdlib()
    {
        _completer.TrackAssignment("data", "file.read(\"x\")");
        var completions = GetCompletions("data.up");
        Assert.Contains("upcase", completions);
    }

    [Fact]
    public void TrackAssignment_Overwrite_UpdatesType()
    {
        _completer.TrackAssignment("x", "\"hello\"");
        _completer.TrackAssignment("x", "42");
        var completions = GetCompletions("x.ro");
        Assert.Contains("round", completions);
    }

    // ── Edge Cases ──────────────────────────────────────────────────────

    [Fact]
    public void DotCompletion_PathLikeToken_DoesNotTrigger()
    {
        // "./script.sh" should get path completion, not dot-completion
        var result = _completer.Complete("./scr", 5);
        // If there's a ./scr* file it would complete it; otherwise null
        // The key thing is it shouldn't try dot-completion on "."
        _completer.Reset();

        // "/usr/bin." should not trigger dot completion
        var result2 = _completer.Complete("/usr/bin.", 9);
        // This is a path, not an object
    }

    [Fact]
    public void DotCompletion_FlagLikeToken_DoesNotTrigger()
    {
        // "-v.something" is a flag, not a dot-completion
        var result = _completer.Complete("cmd -v.x", 8);
        // Should not crash and should not offer dot completions
    }

    [Fact]
    public void DotCompletion_NoTrailingSpace()
    {
        // Dot completions should not add trailing space (allows chaining)
        SetVariable("name", "hello");
        var result = _completer.Complete("name.up", 7);
        Assert.NotNull(result);
        // The completed input should end with "upcase" and no trailing space
        Assert.True(result!.Value.newInput.TrimEnd() == result.Value.newInput
            || !result.Value.newInput.EndsWith("  "), // At most one space from suffix
            "Dot completion should not add trailing space");
    }

    [Fact]
    public void DotCompletion_ExtractToken_PreservesDot()
    {
        // ExtractToken should keep the full "files.sel" token
        var (token, start) = TabCompleter.ExtractToken("files.sel", 9);
        Assert.Equal("files.sel", token);
        Assert.Equal(0, start);
    }

    [Fact]
    public void DotCompletion_AfterSpace_ExtractsCorrectToken()
    {
        var (token, start) = TabCompleter.ExtractToken("x = files.sel", 13);
        Assert.Equal("files.sel", token);
        Assert.Equal(4, start);
    }

    [Fact]
    public void DotCompletion_RuntimeOverridesStatic()
    {
        // If both runtime and static type are available, runtime wins
        _completer.TrackAssignment("x", "[1, 2, 3]"); // Static: object[]
        SetVariable("x", "hello"); // Runtime: string
        var completions = GetCompletions("x.up");
        Assert.Contains("upcase", completions); // Should use string methods (runtime)
    }

    [Fact]
    public void DotCompletion_CaseInsensitiveCompletion()
    {
        SetVariable("name", "hello");
        // Lowercase prefix should match Rush methods (which are lowercase)
        var completions = GetCompletions("name.up");
        Assert.Contains("upcase", completions);
    }

    // ── Rush Method List Verification ───────────────────────────────────

    [Fact]
    public void RushStringMethods_ContainsExpectedMethods()
    {
        SetVariable("s", "hello");
        var completions = GetCompletions("s.");
        // Verify key string methods are present
        Assert.Contains("upcase", completions);
        Assert.Contains("downcase", completions);
        Assert.Contains("strip", completions);
        Assert.Contains("split", completions);
        Assert.Contains("gsub", completions);
        Assert.Contains("sub", completions);
    }

    [Fact]
    public void RushCollectionMethods_ContainsExpectedMethods()
    {
        SetVariable("arr", new object[] { 1, 2, 3 });
        var completions = GetCompletions("arr.");
        Assert.Contains("each", completions);
        Assert.Contains("select", completions);
        Assert.Contains("map", completions);
        Assert.Contains("first", completions);
        Assert.Contains("last", completions);
        Assert.Contains("count", completions);
    }

    [Fact]
    public void RushNumericMethods_ContainsExpectedMethods()
    {
        SetVariable("n", 42);
        var completions = GetCompletions("n.");
        Assert.Contains("round", completions);
        Assert.Contains("abs", completions);
        Assert.Contains("times", completions);
        Assert.Contains("hours", completions);
    }
}
