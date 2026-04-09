using Xunit;
using Rush;

namespace Rush.Tests;

/// <summary>
/// Tests for SecretsFile — reading/writing secrets.rush.
/// </summary>
public class SecretsFileTests
{
    [Fact]
    public void SecretsPath_IsValid()
    {
        var path = SecretsFile.GetPath();
        Assert.Contains("secrets.rush", path);
        Assert.Contains(".config", path);
        Assert.Contains("rush", path);
    }
}
