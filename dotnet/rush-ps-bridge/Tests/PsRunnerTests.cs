// Unit tests for PsRunner — the shared PowerShell-invocation layer.
// Exercises the runspace directly (no subprocess), so these are
// fast and don't depend on the bridge binary being built.

using Xunit;

namespace Rush.PsBridge.Tests;

public class PsRunnerTests
{
    [Fact]
    public void Invoke_SimpleExpression_ReturnsValue()
    {
        using var runner = new PsRunner();
        var result = runner.Invoke("2 + 3");
        Assert.False(result.HadErrors);
        Assert.Single(result.Success);
        Assert.Equal("5", result.Success[0].ToString());
    }

    [Fact]
    public void Invoke_WriteOutput_CapturesSuccessStream()
    {
        using var runner = new PsRunner();
        var result = runner.Invoke("Write-Output 'hello'");
        Assert.False(result.HadErrors);
        Assert.Contains(result.Success, o => o != null && o.ToString() == "hello");
    }

    [Fact]
    public void Invoke_WriteError_CapturesErrorStream()
    {
        using var runner = new PsRunner();
        var result = runner.Invoke("Write-Error 'broken'");
        Assert.True(result.HadErrors);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Invoke_ThrownException_ReturnsErrorResultNotThrow()
    {
        using var runner = new PsRunner();
        // Throwing from PS should not bubble up into .NET — the runner
        // traps it so one bad script doesn't kill the bridge process.
        var result = runner.Invoke("throw 'boom'");
        Assert.True(result.HadErrors);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Invoke_VariablesPersistAcrossCalls()
    {
        using var runner = new PsRunner();
        runner.Invoke("$x = 42");
        var result = runner.Invoke("$x * 2");
        Assert.False(result.HadErrors);
        Assert.Single(result.Success);
        Assert.Equal("84", result.Success[0].ToString());
    }

    [Fact]
    public void Invoke_FunctionDefinitionsPersistAcrossCalls()
    {
        using var runner = new PsRunner();
        runner.Invoke("function Double { param($n) $n * 2 }");
        var result = runner.Invoke("Double 7");
        Assert.False(result.HadErrors);
        Assert.Single(result.Success);
        Assert.Equal("14", result.Success[0].ToString());
    }

    [Fact]
    public void Invoke_WriteWarning_CapturesWarningStream()
    {
        using var runner = new PsRunner();
        var result = runner.Invoke("Write-Warning 'heads up'");
        // Warnings don't set HadErrors — the script completed.
        Assert.False(result.HadErrors);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Message == "heads up");
    }

    [Fact]
    public void Invoke_EmptyScript_NoErrorNoOutput()
    {
        using var runner = new PsRunner();
        var result = runner.Invoke("");
        Assert.False(result.HadErrors);
        Assert.Empty(result.Success);
    }
}
