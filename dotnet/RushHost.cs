using System.Globalization;
using System.Management.Automation.Host;

namespace Rush;

/// <summary>
/// Custom PSHost implementation that gives Rush control over the PowerShell runtime.
/// </summary>
public class RushHost : PSHost
{
    private readonly Guid _instanceId = Guid.NewGuid();
    private readonly RushHostUI _ui;

    public RushHost(RushHostUI ui)
    {
        _ui = ui;
    }

    public override string Name => "RushShell";
    public override Version Version => new(0, 1, 0);
    public override Guid InstanceId => _instanceId;
    public override CultureInfo CurrentCulture => CultureInfo.CurrentCulture;
    public override CultureInfo CurrentUICulture => CultureInfo.CurrentUICulture;
    public override PSHostUserInterface UI => _ui;

    /// <summary>
    /// Set by PowerShell when a script calls exit. The REPL loop checks this
    /// flag and breaks gracefully (saving history, resetting cursor).
    /// </summary>
    public bool ShouldExit { get; private set; }
    public int ExitCode { get; private set; }

    public override void SetShouldExit(int exitCode)
    {
        ShouldExit = true;
        ExitCode = exitCode;
    }

    public override void EnterNestedPrompt() { }
    public override void ExitNestedPrompt() { }
    public override void NotifyBeginApplication() { }
    public override void NotifyEndApplication() { }
}
