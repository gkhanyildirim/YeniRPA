namespace YeniRPA.Web.Services.GmvNotification;

public static class JobIds
{
    public const string GmvReport = "gmv-report";
    public const string SessionKeepAlive = "session-keepalive";
}

/// <summary>What a background job last did. <see cref="Ok"/> is null before its first run.</summary>
public sealed record JobRun(DateTime? LastRunUtc, bool? Ok, string? Message);

/// <summary>
/// The last run of each background job, for the Jobs screen. In memory on purpose: it describes this
/// process's run, so a restart starting from "never run" is the truth, and it is not worth a LiteDB
/// collection.
/// </summary>
public sealed class JobStatusTracker
{
    readonly object _sync = new();
    readonly Dictionary<string, JobRun> _runs = [];

    public void Record(string jobId, bool ok, string? message)
    {
        lock (_sync)
            _runs[jobId] = new JobRun(DateTime.UtcNow, ok, message);
    }

    public JobRun Get(string jobId)
    {
        lock (_sync)
            return _runs.TryGetValue(jobId, out var run) ? run : new JobRun(null, null, null);
    }
}
