// PowerShell invocation layer shared by --bridge and --mcp modes.
//
// Holds a single Runspace for the process lifetime so variables,
// function definitions, and imported modules persist across calls.
// This matches the "session" semantics of the old dotnet Rush's
// `plugin.ps ... end` plugin protocol.
//
// Output streams are captured separately (success, error, warning,
// verbose, information) and returned as a structured result. Callers
// decide how to serialize — the plugin mode flattens to text, the
// MCP mode preserves structure.

using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace Rush.PsBridge;

/// <summary>
/// One PS invocation call's outputs, separated by stream. Consumers
/// pick which streams matter — `plugin.ps ... end` typically wants
/// Success on stdout + Error on stderr; MCP clients may want all
/// streams as structured content.
/// </summary>
public sealed record PsResult(
    IReadOnlyList<PSObject> Success,
    IReadOnlyList<ErrorRecord> Errors,
    IReadOnlyList<WarningRecord> Warnings,
    IReadOnlyList<InformationRecord> Information,
    IReadOnlyList<VerboseRecord> Verbose,
    bool HadErrors);

internal sealed class PsRunner : IDisposable
{
    private Runspace? _runspace;
    private readonly object _lock = new();

    /// <summary>
    /// Execute a PowerShell script in the persistent runspace. Blocks
    /// until the script finishes; not thread-safe within a single
    /// runner — callers are expected to serialize invocations.
    /// </summary>
    public PsResult Invoke(string script)
    {
        lock (_lock)
        {
            EnsureRunspace();
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace!;
            ps.AddScript(script);

            // Invoke() returns the Success stream; other streams come
            // from ps.Streams after invocation.
            IReadOnlyList<PSObject> success;
            try
            {
                success = ps.Invoke();
            }
            catch (RuntimeException rex)
            {
                // Script threw — still return a PsResult with the error
                // so callers can diagnose, rather than bubbling.
                return new PsResult(
                    Success: Array.Empty<PSObject>(),
                    Errors: new[] { rex.ErrorRecord },
                    Warnings: Array.Empty<WarningRecord>(),
                    Information: Array.Empty<InformationRecord>(),
                    Verbose: Array.Empty<VerboseRecord>(),
                    HadErrors: true);
            }

            return new PsResult(
                Success: success,
                Errors: ps.Streams.Error.ToArray(),
                Warnings: ps.Streams.Warning.ToArray(),
                Information: ps.Streams.Information.ToArray(),
                Verbose: ps.Streams.Verbose.ToArray(),
                HadErrors: ps.HadErrors);
        }
    }

    private void EnsureRunspace()
    {
        if (_runspace != null) return;
        var state = InitialSessionState.CreateDefault();
        // ExecutionPolicy: Bypass so scripts run without per-file sign-off.
        // The bridge runs under user control, not as a downloaded artifact.
        state.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        _runspace = RunspaceFactory.CreateRunspace(state);
        _runspace.Open();
    }

    public void Dispose()
    {
        _runspace?.Dispose();
        _runspace = null;
    }
}
