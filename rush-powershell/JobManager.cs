using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;

namespace Rush;

/// <summary>
/// Manages background jobs. Uses a RunspacePool separate from
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

    /// <summary>All jobs (active and completed).</summary>
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
        try { job.Ps.Stop(); } catch { }
        return true;
    }

    /// <summary>
    /// Wait for a job to complete and return its collected output.
    /// </summary>
    public List<PSObject>? WaitForJob(int jobId)
    {
        var job = GetJob(jobId);
        if (job == null) return null;

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
            try { job.Ps.Stop(); } catch { }
            try { job.Ps.Dispose(); } catch { }
        }
        _pool.Close();
        _pool.Dispose();
    }
}

/// <summary>
/// Tracks a single background job's state.
/// </summary>
public class JobInfo
{
    public int JobId { get; }
    public string Command { get; }
    public PowerShell Ps { get; }
    public IAsyncResult AsyncResult { get; }
    public PSDataCollection<PSObject>? Output { get; }
    public DateTime StartTime { get; }
    public bool Reported { get; set; }

    public bool IsCompleted => AsyncResult.IsCompleted;

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
}
