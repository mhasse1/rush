using Xunit;
using Rush;

namespace Rush.Tests;

/// <summary>
/// Tests for ReloadState — session state serialization and restoration.
/// </summary>
public class ReloadStateTests
{
    [Fact]
    public void SessionState_DefaultsAreValid()
    {
        var state = new ReloadState.SessionState();
        Assert.Equal(1, state.Version);
        Assert.Equal("", state.Cwd);
        Assert.Empty(state.Env);
        Assert.Empty(state.Variables);
        Assert.Empty(state.Aliases);
        Assert.Null(state.PreviousDirectory);
        Assert.False(state.SetE);
        Assert.False(state.SetX);
        Assert.False(state.SetPipefail);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var state = new ReloadState.SessionState
        {
            Cwd = "/tmp/test",
            PreviousDirectory = "/home/user",
            SetE = true,
            SetX = false,
            SetPipefail = true,
            Env = { ["CUSTOM_VAR"] = "hello" },
            Variables = { ["myvar"] = "world", ["count"] = 42 },
            Aliases = { ["ll"] = "ls -la" }
        };

        ReloadState.Save(state);
        var loaded = ReloadState.Load();

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded!.Version);
        Assert.Equal("/tmp/test", loaded.Cwd);
        Assert.Equal("/home/user", loaded.PreviousDirectory);
        Assert.True(loaded.SetE);
        Assert.False(loaded.SetX);
        Assert.True(loaded.SetPipefail);
        Assert.Equal("hello", loaded.Env["CUSTOM_VAR"]);
        Assert.Equal("ll", loaded.Aliases.Keys.First());
        Assert.Equal("ls -la", loaded.Aliases["ll"]);
    }

    [Fact]
    public void Load_DeletesFileAfterRead()
    {
        var state = new ReloadState.SessionState { Cwd = "/tmp" };
        ReloadState.Save(state);

        // First load succeeds
        var loaded = ReloadState.Load();
        Assert.NotNull(loaded);

        // Second load returns null (file deleted)
        var second = ReloadState.Load();
        Assert.Null(second);
    }

    [Fact]
    public void Load_ReturnsNull_WhenNoFile()
    {
        // Make sure no state file exists
        var result = ReloadState.Load();
        // Can be null (no file) or non-null (leftover from previous test)
        // Either way, load again should be null
        var again = ReloadState.Load();
        Assert.Null(again);
    }
}
