using Xunit;

namespace Rush.Tests;

/// <summary>
/// Tests for fish-style autosuggestion logic in LineEditor.
/// Tests the static FindSuggestion() method which is the core matching logic.
/// </summary>
public class AutosuggestionTests
{
    [Fact]
    public void FindSuggestion_MatchesMostRecent()
    {
        var history = new[] { "git commit -m \"old\"", "git push", "git commit -m \"new\"" };
        var result = LineEditor.FindSuggestion(history, "git c");
        Assert.Equal("git commit -m \"new\"", result);
    }

    [Fact]
    public void FindSuggestion_ReturnsNullForEmptyPrefix()
    {
        var history = new[] { "foo", "bar" };
        Assert.Null(LineEditor.FindSuggestion(history, ""));
    }

    [Fact]
    public void FindSuggestion_ReturnsNullForNullPrefix()
    {
        var history = new[] { "foo", "bar" };
        Assert.Null(LineEditor.FindSuggestion(history, null!));
    }

    [Fact]
    public void FindSuggestion_ReturnsNullWhenOnlyExactMatch()
    {
        // Must be strictly longer than prefix — no point suggesting what's already typed
        var history = new[] { "foo" };
        Assert.Null(LineEditor.FindSuggestion(history, "foo"));
    }

    [Fact]
    public void FindSuggestion_CaseSensitive()
    {
        var history = new[] { "Git commit" };
        Assert.Null(LineEditor.FindSuggestion(history, "git"));
    }

    [Fact]
    public void FindSuggestion_ReturnsNullForNoMatch()
    {
        var history = new[] { "ls -la", "cd /tmp" };
        Assert.Null(LineEditor.FindSuggestion(history, "git"));
    }

    [Fact]
    public void FindSuggestion_EmptyHistory()
    {
        Assert.Null(LineEditor.FindSuggestion(Array.Empty<string>(), "foo"));
    }

    [Fact]
    public void FindSuggestion_SingleCharPrefix()
    {
        var history = new[] { "echo hello", "exit" };
        var result = LineEditor.FindSuggestion(history, "e");
        // Most recent match: "exit"
        Assert.Equal("exit", result);
    }

    [Fact]
    public void FindSuggestion_FullPrefixMatch()
    {
        var history = new[] { "docker compose up -d", "docker compose down" };
        var result = LineEditor.FindSuggestion(history, "docker compose ");
        // Most recent: "docker compose down"
        Assert.Equal("docker compose down", result);
    }

    [Fact]
    public void FindSuggestion_SkipsExactMatchFindsLonger()
    {
        // "foo" exact match exists, but should find "foobar" instead
        var history = new[] { "foobar", "foo" };
        var result = LineEditor.FindSuggestion(history, "foo");
        // "foo" at index 1 is exact (skipped), "foobar" at index 0 matches
        Assert.Equal("foobar", result);
    }
}
