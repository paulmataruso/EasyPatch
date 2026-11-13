using EasyPatch.Agent.Core.Abstractions;
using EasyPatch.Agent.Core.Scanning;
using EasyPatch.Agent.Core.State;
using EasyPatch.Agent.Core.Util;
using EasyPatch.Contracts;
using EasyPatch.Contracts.Agent;
using EasyPatch.Contracts.Updates;
using Microsoft.Extensions.Logging;

namespace EasyPatch.Agent.Core.Jobs;

/// <summary>
/// Holds the agent's jobs in durable state and executes them one at a time. Only the explicitly supported job
/// types are executed; anything else is rejected.
/// </summary>
public sealed class JobRunner
{
    private readonly AgentPlatform _platform;
    private readonly AgentStateStore _state;
    private readonly JobReporter _reporter;
    private readonly ScanService _scans;
    private readonly InventoryService _inventory;
    private readonly InstallUpdatesHandler _install;
    private readonly WuaOperationGate _gate;
    private readonly AgentRuntimeOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private Guid? _executingJobId;
    private string? _activity;

    public JobRunner(
        AgentPlatform platform,
        AgentStateStore state,
        JobReporter reporter,
        ScanService scans,
        InventoryService inventory,
        InstallUpdatesHandler install,
        WuaOperationGate gate,
        AgentRuntimeOptions options,
        TimeProvider time,
        ILogger<JobRunner> logger)
    {
        _platform = platform;
        _state = state;
        _reporter = reporter;
        _scans = scans;
        _inventory = inventory;
        _install = install;
        _gate = gate;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public bool HasRunnableOrActiveJob => _state.Read(s => s.Jobs.Exists(j => j.RunState != JobRunState.WaitingForReboot));

    public string CurrentActivity
    {
        get
        {
            lock (_lock)
            {
                if (_executingJobId is null)
                {
                    return _gate.IsBusy ? "Scanning" : "Idle";
                }

                return _install.Activity ?? _activity ?? "Running job";
            }
        }
    }

    /// <summary>IDs of every job the agent holds (reported in check-ins so the server does not re-deliver them).</summary>
    public IReadOnlyList<Guid> HeldJobIds() => _state.Read(s => s.Jobs.Select(j => j.Job.JobId).ToList());

    /// <summary>Persists newly delivered jobs. Returns the jobs that still need to be acknowledged.</summary>
    public IReadOnlyList<Guid> Accept(IEnumerable<AgentJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var now = _time.GetUtcNow();
        var list = jobs.ToList();
        var unsupported = new List<AgentJob>();
        var pending = _state.Update(s =>
        {
            foreach (var job in list)
            {
                if (s.FindJob(job.JobId) is not null)
                {
                    continue;
                }

                s.Jobs.Add(new JobRecord { Job = job, ReceivedAt = now, RunState = JobRunState.Received });
                if (IsSupported(job))
                {
                    _logger.LogInformation("Received {Type} job {JobId} ({Trigger}).", job.Type, job.JobId, job.Trigger);
                }
                else
                {
                    unsupported.Add(job);
                }
            }

            return s.Jobs.Where(j => j.RunState == JobRunState.Received && !unsupported.Contains(j.Job)).Select(j => j.Job.JobId).ToList();
        });

        // Report unsupported jobs as failed (never executed) so the server stops re-delivering them.
        foreach (var job in unsupported)
        {
            _logger.LogError("Rejecting job {JobId}: unsupported type {Type} or missing payload.", job.JobId, job.Type);
            _reporter.Report(job.JobId, JobStatus.Failed, "Failed", null, null,
                errorCode: "unsupported_job", errorMessage: $"This agent cannot execute {job.Type} jobs with the supplied payload.");
            Remove(job.JobId);
        }

        return pending;
    }

    public void MarkAcknowledged(Guid jobId) => _state.Update(s =>
    {
        var record = s.FindJob(jobId);
        if (record is { RunState: JobRunState.Received })
        {
            record.RunState = JobRunState.Acknowledged;
        }
    });

    /// <summary>Forgets a job the server no longer wants executed (e.g. cancelled before acknowledgement).</summary>
    public void Drop(Guid jobId, string reason)
    {
        if (_state.Update(s => s.Jobs.RemoveAll(j => j.Job.JobId == jobId) > 0))
        {
            _logger.LogInformation("Dropped job {JobId}: {Reason}", jobId, reason);
        }
    }

    /// <summary>Handles an administrator cancellation. Not-yet-started jobs are cancelled immediately;
    /// a running install stops before the next update (an in-progress WUA install cannot be interrupted safely).</summary>
    public void RequestCancel(Guid jobId)
    {
        var record = _state.Read(s => s.FindJob(jobId) is { } r ? new { r.RunState } : null);
        if (record is null)
        {
            return;
        }

        bool executing;
        lock (_lock)
        {
            executing = _executingJobId == jobId;
        }

        _state.Update(s =>
        {
            var r = s.FindJob(jobId);
            if (r is not null)
            {
                r.CancelRequested = true;
            }
        });

        if (executing)
        {
            _logger.LogInformation("Cancellation requested for running job {JobId}; it will stop before the next update.", jobId);
            return;
        }

        _reporter.Report(jobId, JobStatus.Cancelled, "Cancelled", null, "Cancelled by an administrator before it started.");
        _state.Update(s => s.Jobs.RemoveAll(j => j.Job.JobId == jobId));
        _logger.LogInformation("Job {JobId} cancelled before it started.", jobId);
    }

    /// <summary>Executes the next runnable job, if any. Returns true when a job was processed.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        ExpireStaleJobs();
        var next = PickNext();
        if (next is null)
        {
            return false;
        }

        var jobId = next.Job.JobId;
        var attempts = _state.Update(s =>
        {
            var r = s.FindJob(jobId)!;
            r.Attempts++;
            r.RunState = JobRunState.Running;
            r.StartedAt ??= _time.GetUtcNow();
            return r.Attempts;
        });

        if (attempts > _options.MaxJobAttempts)
        {
            _reporter.Report(jobId, JobStatus.Failed, "Failed", null, null, next.Items,
                errorCode: "agent_interrupted",
                errorMessage: $"The job was interrupted {attempts - 1} times (agent restarts or crashes) and has been abandoned.");
            Remove(jobId);
            return true;
        }

        lock (_lock)
        {
            _executingJobId = jobId;
            _activity = next.Job.Type.ToString();
        }

        await _gate.EnterAsync(cancellationToken);
        try
        {
            var record = _state.Read(s => s.FindJob(jobId))!;
            var outcome = next.Job.Type switch
            {
                JobType.InstallUpdates => await _install.ExecuteAsync(record, cancellationToken),
                JobType.UpdateScan => await RunScanJobAsync(jobId, cancellationToken),
                JobType.InventoryRefresh => await RunInventoryJobAsync(jobId, cancellationToken),
                _ => throw new NotSupportedException($"Job type {next.Job.Type} is not supported."),
            };

            if (outcome == JobOutcome.Finished)
            {
                Remove(jobId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Service is stopping: keep the job; it resumes (idempotently) on next start.
            _logger.LogInformation("Job {JobId} interrupted by service shutdown; it will resume on the next start.", jobId);
            throw;
        }
        catch (Exception ex)
        {
            var code = ex is WindowsUpdateException || (ex.HResult & 0xFFFF0000) == 0x80240000 ? WindowsUpdateErrors.Format(ex.HResult) : "agent_error";
            _logger.LogError(ex, "Job {JobId} failed.", jobId);
            var items = _state.Read(s => s.FindJob(jobId)?.Items.ToList() ?? []);
            _reporter.Report(jobId, JobStatus.Failed, "Failed", null, null, items,
                errorCode: code,
                errorMessage: ex is WindowsUpdateException || code != "agent_error"
                    ? $"{WindowsUpdateErrors.Describe(ex.HResult)} {ex.Message}".Trim()
                    : ex.Message);
            Remove(jobId);
        }
        finally
        {
            _gate.Exit();
            lock (_lock)
            {
                _executingJobId = null;
                _activity = null;
            }
        }

        return true;
    }

    private async Task<JobOutcome> RunScanJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        _reporter.Report(jobId, JobStatus.Running, "Scanning", 10, "Scanning Windows Update.");
        var report = await _scans.RunScanAsync(ScanTrigger.Job, jobId, gateHeld: true, cancellationToken);
        if (report is { Succeeded: true })
        {
            _reporter.Report(jobId, JobStatus.Completed, "Completed", 100,
                $"Scan completed: {report.Missing.Count(m => !m.RebootRequired)} update(s) available, {report.Installed.Count} installed.");
        }
        else
        {
            _reporter.Report(jobId, JobStatus.Failed, "Failed", 100, "Windows Update scan failed.",
                errorCode: report?.ErrorCode, errorMessage: report?.ErrorMessage ?? "The scan did not run.");
        }

        return JobOutcome.Finished;
    }

    private async Task<JobOutcome> RunInventoryJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        _reporter.Report(jobId, JobStatus.Running, "Collecting", 10, "Collecting inventory.");
        await _inventory.SendInventoryAsync(cancellationToken);
        _reporter.Report(jobId, JobStatus.Completed, "Completed", 100, "Inventory collected.");
        return JobOutcome.Finished;
    }

    private JobRecord? PickNext()
    {
        var boot = _platform.BootTime.GetLastBootTime();
        return _state.Read(s =>
        {
            // 1. A parked job whose restart has happened.
            var resumable = s.Jobs.Find(j => j.RunState == JobRunState.WaitingForReboot &&
                                            j.WaitingBootTime is { } waitedOn &&
                                            (boot - waitedOn).Duration() > _options.BootTimeTolerance);
            if (resumable is not null)
            {
                return resumable;
            }

            // 2. An interrupted job (was Running when the agent stopped), then acknowledged jobs in order.
            return s.Jobs.Find(j => j.RunState == JobRunState.Running)
                ?? s.Jobs.Find(j => j.RunState == JobRunState.Acknowledged);
        });
    }

    private void ExpireStaleJobs()
    {
        var now = _time.GetUtcNow();
        var stale = _state.Read(s => s.Jobs
            .Where(j => (j.RunState == JobRunState.WaitingForReboot && j.WaitingSince is { } since && now - since > _options.WaitingForRebootMaxAge) ||
                        (j.RunState is JobRunState.Received or JobRunState.Acknowledged && j.Job.ExpiresAt != default && j.Job.ExpiresAt < now))
            .Select(j => (j.Job.JobId, j.RunState))
            .ToList());

        foreach (var (jobId, runState) in stale)
        {
            if (runState == JobRunState.WaitingForReboot)
            {
                var items = _state.Read(s => s.FindJob(jobId)?.Items.ToList() ?? []);
                _reporter.Report(jobId, JobStatus.Failed, "Failed", null, null, items,
                    errorCode: "reboot_not_performed",
                    errorMessage: $"The computer was not restarted within {_options.WaitingForRebootMaxAge.TotalDays:0} days, so the remaining updates were not installed.",
                    rebootRequired: true);
            }
            else
            {
                _logger.LogWarning("Job {JobId} expired before it could start.", jobId);
                _reporter.Report(jobId, JobStatus.Expired, "Expired", null, "The job expired before the agent could start it.",
                    errorCode: "job_expired", errorMessage: "The job expired before the agent could start it.");
            }

            Remove(jobId);
        }
    }

    private void Remove(Guid jobId) => _state.Update(s => s.Jobs.RemoveAll(j => j.Job.JobId == jobId));

    private static bool IsSupported(AgentJob job) => job.Type switch
    {
        JobType.UpdateScan or JobType.InventoryRefresh => true,
        JobType.InstallUpdates => job.Install is not null,
        _ => false,
    };
}
