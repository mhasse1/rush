using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;

namespace Rush;

/// <summary>
/// Manages background and suspended jobs. Uses a RunspacePool separate from
/// the main interactive runspace so background commands don't block the REPL.
/// </summary>
public class JobManager : IDisposable
{
    private readonly List<JobInfo> _jobs = new();
    private int _nextId = 1;
    private readonly RunspacePool _pool;

    public JobManager(InitialSessionState iss, PSHost host)
    {
        _pool = RunspaceFactory.CreateRunspacePool(1, 5, iss, host);
        _pool.Open();
    }

    /// <summary>
    /// Launch a command in the background. Returns the job ID.
    /// </summary>
    public int StartBackground(string displayCommand, string translatedCommand)
    {
        var ps = PowerShell.Create();
        ps.RunspacePool = _pool;
        ps.AddScript(translatedCommand);

        var output = new PSDataCollection<PSObject>();
        var asyncResult = ps.BeginInvoke<PSObject, PSObject>(null, output);

        var job = new JobInfo(
            _nextId++, displayCommand, ps, asyncResult, output, DateTime.Now);
        _jobs.Add(job);
        return job.JobId;
    }

    /// <summary>Register a suspended native process (Ctrl+Z on TUI command).</summary>
    public int RegisterSuspendedProcess(string command, Process process)
    {
        var job = new JobInfo(_nextId++, command, process, DateTime.Now);
        _jobs.Add(job);
        return job.JobId;
    }

    /// <summary>Register a suspended PS pipeline (Ctrl+Z on foreground command).</summary>
    public int RegisterSuspendedPipeline(string command, string translatedCommand)
    {
        var job = new JobInfo(_nextId++, command, translatedCommand, DateTime.Now);
        _jobs.Add(job);
        return job.JobId;
    }

    /// <summary>All jobs (active, completed, and suspended).</summary>
    public IReadOnlyList<JobInfo> GetJobs() => _jobs.AsReadOnly();

    /// <summary>Look up a single job by ID.</summary>
    public JobInfo? GetJob(int jobId) => _jobs.FirstOrDefault(j => j.JobId == jobId);

    /// <summary>
    /// Return jobs that finished since the last check. Marks them as reported.
    /// </summary>
    public List<JobInfo> GetCompletedUnreported()
    {
        var completed = _jobs.Where(j => j.IsCompleted && !j.Reported).ToList();
        foreach (var j in completed) j.Reported = true;
        return completed;
    }

    /// <summary>Stop a running job.</summary>
    public bool KillJob(int jobId)
    {
        var job = GetJob(jobId);
        if (job == null) return false;
        if (job.SuspendedProcess != null)
        {
            try { job.SuspendedProcess.Kill(); } catch { }
        }
        else if (job.Ps != null)
        {
            try { job.Ps.Stop(); } catch { }
        }
        return true;
    }

    /// <summary>
    /// Wait for a job to complete and return its collected output.
    /// </summary>
    public List<PSObject>? WaitForJob(int jobId)
    {
        var job = GetJob(jobId);
        if (job == null || job.Ps == null || job.AsyncResult == null) return null;

        try
        {
            job.Ps.EndInvoke(job.AsyncResult);
        }
        catch (PipelineStoppedException) { }
        catch { }

        job.Reported = true;
        return job.Output?.ToList() ?? new();
    }

    /// <summary>Remove completed+reported jobs from the list.</summary>
    public void RemoveCompletedJobs()
    {
        _jobs.RemoveAll(j => j.IsCompleted && j.Reported);
    }

    public void Dispose()
    {
        foreach (var job in _jobs)
        {
            try { job.Ps?.Stop(); } catch { }
            try { job.Ps?.Dispose(); } catch { }
            try { job.SuspendedProcess?.Kill(); } catch { }
            try { job.SuspendedProcess?.Dispose(); } catch { }
        }
        _pool.Close();
        _pool.Dispose();
    }
}

/// <summary>
/// Tracks a single job's state — background, suspended, or completed.
/// </summary>
public class JobInfo
{
    public int JobId { get; }
    public string Command { get; }
    public PowerShell? Ps { get; }
    public IAsyncResult? AsyncResult { get; }
    public PSDataCollection<PSObject>? Output { get; }
    public DateTime StartTime { get; }
    public bool Reported { get; set; }

    // Suspension support
    public bool IsSuspended { get; set; }
    public Process? SuspendedProcess { get; set; }
    public string? SuspendedCommand { get; set; }

    public bool IsCompleted
    {
        get
        {
            if (IsSuspended) return false;
            if (SuspendedProcess != null) return SuspendedProcess.HasExited;
            return AsyncResult?.IsCompleted ?? true;
        }
    }

    /// <summary>Background PS pipeline job.</summary>
    public JobInfo(int jobId, string command, PowerShell ps,
        IAsyncResult asyncResult, PSDataCollection<PSObject> output, DateTime startTime)
    {
        JobId = jobId;
        Command = command;
        Ps = ps;
        AsyncResult = asyncResult;
        Output = output;
        StartTime = startTime;
    }

    /// <summary>Suspended native process (Ctrl+Z on TUI command).</summary>
    public JobInfo(int jobId, string command, Process process, DateTime startTime)
    {
        JobId = jobId;
        Command = command;
        SuspendedProcess = process;
        StartTime = startTime;
        IsSuspended = true;
    }

    /// <summary>Suspended PS pipeline (Ctrl+Z on foreground command, will re-run on fg).</summary>
    public JobInfo(int jobId, string command, string suspendedCommand, DateTime startTime)
    {
        JobId = jobId;
        Command = command;
        SuspendedCommand = suspendedCommand;
        StartTime = startTime;
        IsSuspended = true;
    }
}
