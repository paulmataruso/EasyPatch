using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EasyPatch.Contracts;
using EasyPatch.Contracts.Agent;
using EasyPatch.Contracts.Json;
using EasyPatch.Contracts.Updates;
using EasyPatch.Core.Domain;
using EasyPatch.Core.Entities;
using EasyPatch.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EasyPatch.Infrastructure.Services;

public sealed record CreateJobRequest(
    Guid EndpointId,
    JobType Type,
    InstallMode InstallMode = InstallMode.AllEligible,
    IReadOnlyList<Guid>? UpdateGuids = null,
    bool Force = false,
    JobTrigger Trigger = JobTrigger.Manual,
    DateTimeOffset? ExpiresAt = null,
    DateTimeOffset? NotAfter = null,
    string? DedupeSuffix = null,
    RestartPayload? Restart = null,
    NotifyUserPayload? Notify = null,
    DateTimeOffset? NotBefore = null);

/// <summary>Durable PostgreSQL-backed job queue: creation, leasing to agents, acknowledgement, progress and cancellation.</summary>
public sealed class JobService(
    EasyPatchDbContext db,
    SettingsService settingsService,
    PolicyService policies,
    InstallPreviewService previews,
    AuditService audit,
    IActorContext actor,
    TimeProvider clock,
    ILogger<JobService> logger)
{
    /// <summary>Advisory lock key serializing install-slot accounting (global and per-site concurrency limits).</summary>
    public const long InstallSlotsLockKey = 0x4541_5359_0001;

    // -------------------------------------------------------------------------------------------
    // Creation (administrator or scheduler)
    // -------------------------------------------------------------------------------------------

    public async Task<Job> CreateAsync(CreateJobRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.GetUtcNow();
        var endpoint = await db.Endpoints.FirstOrDefaultAsync(e => e.Id == request.EndpointId, ct)
                       ?? throw AppException.NotFound("Endpoint");
        if (endpoint.Status != EndpointStatus.Active)
        {
            throw AppException.Conflict("The endpoint is not active (its credentials were revoked or it was decommissioned).", "endpoint_not_active");
        }

        if (AgentCapabilities.RequiredFor(request.Type) is { } capability && !endpoint.AgentCapabilities.Contains(capability))
        {
            throw AppException.Conflict(
                $"The EasyPatch agent on {endpoint.Hostname} (version {endpoint.AgentVersion ?? "unknown"}) cannot run this job. Upgrade the agent first.",
                "agent_upgrade_required");
        }

        if (request.NotBefore is { } notBefore && (notBefore < now.AddMinutes(-5) || notBefore > now.AddDays(31)))
        {
            throw AppException.BadRequest("The scheduled time must be between now and 31 days from now.", "validation_failed");
        }

        var settings = await settingsService.GetAsync(ct);
        string? payloadJson = null;
        string dedupeKey;
        List<JobUpdate> plannedItems = [];
        switch (request.Type)
        {
            case JobType.UpdateScan:
                dedupeKey = "scan";
                break;
            case JobType.InventoryRefresh:
                dedupeKey = "inventory";
                break;
            case JobType.InstallUpdates:
            {
                var selected = (request.UpdateGuids ?? []).Distinct().ToList();
                if (request.InstallMode == InstallMode.Selected && selected.Count == 0)
                {
                    throw AppException.BadRequest("Select at least one update to install.", "validation_failed");
                }

                if (selected.Count > 500)
                {
                    throw AppException.BadRequest("At most 500 updates can be selected.", "validation_failed");
                }

                var preview = await previews.BuildAsync(endpoint, request.InstallMode, selected, ct);
                if (request.InstallMode == InstallMode.Selected && preview.Items.Count == 0)
                {
                    throw AppException.Conflict("None of the selected updates can be installed (they are installed, excluded, or no longer offered).",
                        "nothing_to_install");
                }

                var payload = new InstallUpdatesPayload
                {
                    Mode = request.InstallMode,
                    SelectedUpdateGuids = selected,
                    Filter = preview.Filter,
                    NotAfter = request.NotAfter,
                };
                payloadJson = JsonSerializer.Serialize(payload, ContractJson.Options);
                dedupeKey = request.InstallMode == InstallMode.AllEligible
                    ? "install:all"
                    : "install:sel:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', selected.Order()))))[..32];
                plannedItems = preview.Items.Select(i => new JobUpdate
                {
                    Id = Guid.CreateVersion7(now),
                    UpdateGuid = i.UpdateGuid,
                    RevisionNumber = i.RevisionNumber,
                    Title = i.Title,
                    KbNumber = i.KbNumber,
                    Category = i.Category,
                    Status = JobItemStatus.Pending,
                    UpdatedAt = now,
                }).ToList();
                break;
            }

            case JobType.Restart:
            {
                var payload = request.Restart ?? throw AppException.BadRequest("Restart settings are required.", "validation_failed");
                var errors = RestartPayload.Validate(payload);
                if (errors.Count > 0)
                {
                    throw AppException.BadRequest(string.Join(" ", errors), "validation_failed");
                }

                payloadJson = JsonSerializer.Serialize(payload, ContractJson.Options);
                dedupeKey = "restart";
                break;
            }

            case JobType.NotifyUser:
            {
                var payload = request.Notify ?? throw AppException.BadRequest("Choose a message.", "validation_failed");
                if (!Enum.IsDefined(payload.Preset))
                {
                    throw AppException.BadRequest("Unknown message.", "validation_failed");
                }

                if (payload.Preset == UserMessagePreset.RestartScheduled && payload.ScheduledAt is null)
                {
                    throw AppException.BadRequest("Enter the time of the scheduled restart.", "validation_failed");
                }

                payloadJson = JsonSerializer.Serialize(payload with
                {
                    ScheduledAt = payload.Preset == UserMessagePreset.RestartScheduled ? payload.ScheduledAt : null,
                }, ContractJson.Options);
                dedupeKey = "notify:" + payload.Preset;
                break;
            }

            default:
                throw AppException.BadRequest("Unsupported job type.");
        }

        if (request.DedupeSuffix is not null)
        {
            dedupeKey += ":" + request.DedupeSuffix;
        }

        if (!request.Force)
        {
            var duplicate = await db.Jobs.AsNoTracking()
                .Where(j => j.EndpointId == endpoint.Id && j.DedupeKey == dedupeKey && JobTransitions.DedupeStatuses.Contains(j.Status))
                .Select(j => (Guid?)j.Id).FirstOrDefaultAsync(ct);
            if (duplicate is { } existingId)
            {
                throw DuplicateJob(existingId);
            }
        }

        var job = new Job
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = endpoint.OrganizationId,
            EndpointId = endpoint.Id,
            SiteId = endpoint.SiteId,
            Type = request.Type,
            Status = JobStatus.Queued,
            Trigger = request.Trigger,
            RequestedByUserId = request.Trigger == JobTrigger.Manual ? actor.UserId : null,
            RequestedBy = request.Trigger switch
            {
                JobTrigger.Scheduled when request.Type == JobType.Restart => "Restart schedule",
                JobTrigger.Scheduled => "Maintenance window",
                JobTrigger.System => "System",
                _ => actor.Username ?? "unknown",
            },
            // Scans and inventory first, then installs, then restarts (patch, then restart). Messages are leased separately.
            Priority = request.Type switch
            {
                JobType.InstallUpdates => 0,
                JobType.Restart => -10,
                _ => 10,
            },
            CreatedAt = now,
            UpdatedAt = now,
            NotBefore = request.NotBefore,
            // A restart or a message that arrives hours late would surprise the user, so they expire quickly.
            ExpiresAt = request.ExpiresAt ?? request.Type switch
            {
                JobType.Restart => Later(now, request.NotBefore).AddHours(2),
                JobType.NotifyUser => Later(now, request.NotBefore).AddHours(1),
                _ => now.AddHours(settings.JobDefaultExpiryHours),
            },
            Payload = payloadJson,
            DedupeKey = request.Force ? null : dedupeKey,
            Updates = plannedItems,
        };
        foreach (var item in plannedItems)
        {
            item.JobId = job.Id;
        }

        db.Jobs.Add(job);
        EndpointEvents.Add(db, endpoint, "JobQueued", $"{Describe(job.Type)} queued by {job.RequestedBy}.", now, EventSeverity.Info, job.Id);
        if (request.Trigger == JobTrigger.Manual)
        {
            audit.Record("job.create", "Job", job.Id.ToString(), endpoint.OrganizationId, endpoint.Id,
                new
                {
                    job.Type, mode = request.InstallMode, updates = request.UpdateGuids?.Count, request.Force, restart = request.Restart,
                    message = request.Notify?.Preset, request.NotBefore,
                });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            var existingId = await db.Jobs.AsNoTracking()
                .Where(j => j.EndpointId == endpoint.Id && j.DedupeKey == dedupeKey && JobTransitions.DedupeStatuses.Contains(j.Status))
                .Select(j => j.Id).FirstOrDefaultAsync(ct);
            throw DuplicateJob(existingId);
        }

        return job;
    }

    private static DateTimeOffset Later(DateTimeOffset now, DateTimeOffset? other) => other is { } o && o > now ? o : now;

    private static AppException DuplicateJob(Guid existingId) =>
        AppException.Conflict("An identical job is already queued or running for this endpoint.", "duplicate_job",
            new Dictionary<string, object?> { ["existingJobId"] = existingId });

    public async Task<Job> CancelAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await db.Jobs.Include(j => j.Endpoint).Include(j => j.Updates).FirstOrDefaultAsync(j => j.Id == jobId, ct) ?? throw AppException.NotFound("Job");
        if (!JobTransitions.AdminMayCancel(job.Status))
        {
            throw AppException.Conflict($"A job in status {job.Status} cannot be cancelled.", "invalid_state");
        }

        var now = clock.GetUtcNow();
        job.CancelRequestedAt = now;
        job.CancelRequestedByUserId = actor.UserId;
        job.UpdatedAt = now;
        if (job.Status is JobStatus.Queued or JobStatus.Delivered or JobStatus.WaitingForReboot)
        {
            // Not yet started on the agent (or parked): cancel immediately. The agent drops it on its next check-in/ack.
            Finish(job, JobStatus.Cancelled, now, "Cancelled by administrator.");
        }

        EndpointEvents.Add(db, job.Endpoint!, "JobCancelled",
            job.Status == JobStatus.Cancelled ? "Job cancelled by administrator." : "Cancellation requested; the agent will stop after the current update.",
            now, EventSeverity.Info, job.Id);
        audit.Record("job.cancel", "Job", job.Id.ToString(), job.OrganizationId, job.EndpointId);
        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task<Job> RetryAsync(Guid jobId, CancellationToken ct = default)
    {
        var original = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct) ?? throw AppException.NotFound("Job");
        if (!original.Status.IsTerminal())
        {
            throw AppException.Conflict("Only finished jobs can be retried.", "invalid_state");
        }

        var request = original.Type switch
        {
            JobType.Restart => new CreateJobRequest(original.EndpointId, original.Type, Restart: Payload<RestartPayload>(original)),
            JobType.NotifyUser => new CreateJobRequest(original.EndpointId, original.Type, Notify: Payload<NotifyUserPayload>(original)),
            JobType.InstallUpdates when Payload<InstallUpdatesPayload>(original) is { } install =>
                new CreateJobRequest(original.EndpointId, original.Type, install.Mode, install.SelectedUpdateGuids),
            _ => new CreateJobRequest(original.EndpointId, original.Type),
        };
        var job = await CreateAsync(request, ct);
        job.RetryOfJobId = original.Id;
        job.RetryCount = original.RetryCount + 1;
        await db.SaveChangesAsync(ct);
        return job;
    }

    // -------------------------------------------------------------------------------------------
    // Agent side: leasing, acknowledgement, progress
    // -------------------------------------------------------------------------------------------

    public sealed record LeaseResult(IReadOnlyList<AgentJob> Jobs, IReadOnlyList<Guid> CancelJobIds);

    /// <summary>Called on every check-in. Re-delivers jobs the agent lost, reports cancellations, and leases the next
    /// queued job when the endpoint is idle and concurrency limits allow.</summary>
    public async Task<LeaseResult> LeaseForCheckInAsync(Endpoint endpoint, IReadOnlyList<Guid> agentJobIds, EffectivePolicy policy,
        ServerSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(settings);
        var now = clock.GetUtcNow();
        var known = agentJobIds.ToHashSet();
        var deliver = new List<Job>();
        var cancels = new List<Guid>();

        var active = await db.Jobs.Include(j => j.Updates)
            .Where(j => j.EndpointId == endpoint.Id &&
                        (j.Status == JobStatus.Delivered || j.Status == JobStatus.Acknowledged || j.Status == JobStatus.Running ||
                         j.Status == JobStatus.WaitingForReboot))
            .ToListAsync(ct);

        foreach (var job in active)
        {
            if (known.Contains(job.Id))
            {
                if (job.CancelRequestedAt is not null)
                {
                    cancels.Add(job.Id);
                }

                continue;
            }

            // The server believes the agent holds this job but the agent does not list it (reinstall, lost state, or
            // a delivery whose response never arrived). Jobs are idempotent, so deliver again.
            if (job.CancelRequestedAt is not null)
            {
                Finish(job, JobStatus.Cancelled, now, "Cancelled by administrator.");
                continue;
            }

            if (job.DeliveryCount >= settings.MaxDeliveryAttempts)
            {
                Finish(job, JobStatus.Failed, now, $"The agent did not retain the job after {job.DeliveryCount} deliveries.");
                continue;
            }

            job.Status = JobStatus.Delivered;
            job.DeliveredAt = now;
            job.LeaseExpiresAt = now.AddMinutes(settings.DeliveryLeaseMinutes);
            job.DeliveryCount++;
            job.UpdatedAt = now;
            deliver.Add(job);
        }

        // Jobs the agent holds that the server already finished (e.g. cancelled while queued): tell it to drop them.
        var activeIds = active.Select(j => j.Id).ToHashSet();
        var orphanIds = known.Where(id => !activeIds.Contains(id)).ToList();
        if (orphanIds.Count > 0)
        {
            var finished = await db.Jobs.AsNoTracking()
                .Where(j => orphanIds.Contains(j.Id) && j.EndpointId == endpoint.Id)
                .Select(j => new { j.Id, j.Status, j.TimedOut }).ToListAsync(ct);
            cancels.AddRange(finished.Where(j => j.Status.IsTerminal() && !(j.Status == JobStatus.Failed && j.TimedOut)).Select(j => j.Id));
            // Unknown IDs (deleted jobs) are cancelled as well.
            cancels.AddRange(orphanIds.Where(id => finished.All(f => f.Id != id)));
        }

        await db.SaveChangesAsync(ct);

        // One job at a time. A job parked in WaitingForReboot does not block scans or inventory (the agent runs
        // those while it waits), but further installs stay queued until the restart has been handled. Messages to the
        // user neither block nor wait for other jobs.
        var busy = active.Any(j => j.Type != JobType.NotifyUser && j.Status is JobStatus.Delivered or JobStatus.Acknowledged or JobStatus.Running);
        var waitingForReboot = active.Any(j => j.Status == JobStatus.WaitingForReboot);
        if (!deliver.Exists(j => j.Type != JobType.NotifyUser) && !busy)
        {
            var next = await LeaseNextAsync(endpoint, policy, settings, allowInstall: !waitingForReboot,
                allowRestart: endpoint.AgentCapabilities.Contains(AgentCapabilities.Restart), ct);
            if (next is not null)
            {
                deliver.Add(next);
            }
        }

        if (endpoint.AgentCapabilities.Contains(AgentCapabilities.NotifyUser))
        {
            deliver.AddRange(await LeaseMessagesAsync(endpoint, settings, ct));
        }

        return new LeaseResult(deliver.Select(ToAgentJob).ToList(), cancels.Distinct().ToList());
    }

    /// <summary>Leases queued user messages (at most five per check-in), independently of other jobs.</summary>
    private async Task<IReadOnlyList<Job>> LeaseMessagesAsync(Endpoint endpoint, ServerSettings settings, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var queued = await db.Jobs.FromSql($"""
                SELECT *, xmin FROM jobs
                WHERE endpoint_id = {endpoint.Id} AND status = 'Queued' AND type = 'NotifyUser' AND (not_before IS NULL OR not_before <= {now})
                ORDER BY created_at
                LIMIT 5
                FOR UPDATE SKIP LOCKED
                """).ToListAsync(ct);
        var leased = new List<Job>();
        foreach (var job in queued)
        {
            if (job.ExpiresAt <= now)
            {
                Finish(job, JobStatus.Expired, now, "The message expired before the endpoint picked it up.");
                continue;
            }

            job.Status = JobStatus.Delivered;
            job.DeliveredAt = now;
            job.LeaseExpiresAt = now.AddMinutes(settings.DeliveryLeaseMinutes);
            job.DeliveryCount++;
            job.UpdatedAt = now;
            leased.Add(job);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return leased;
    }

    private async Task<Job?> LeaseNextAsync(Endpoint endpoint, EffectivePolicy policy, ServerSettings settings, bool allowInstall, bool allowRestart,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var now = clock.GetUtcNow();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var candidate = (await db.Jobs.FromSql($"""
                    SELECT *, xmin FROM jobs
                    WHERE endpoint_id = {endpoint.Id} AND status = 'Queued' AND (not_before IS NULL OR not_before <= {now})
                      AND ({allowInstall} OR type <> 'InstallUpdates')
                      AND ({allowRestart} OR type <> 'Restart') AND type <> 'NotifyUser'
                    ORDER BY priority DESC, created_at
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                    """).ToListAsync(ct)).FirstOrDefault();
            if (candidate is null)
            {
                return null;
            }

            if (candidate.ExpiresAt <= now)
            {
                Finish(candidate, JobStatus.Expired, now, "The job expired before the endpoint picked it up.");
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                continue;
            }

            if (candidate.Type == JobType.InstallUpdates)
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({InstallSlotsLockKey})", ct);
                var inFlight = db.Jobs.Where(j => j.Type == JobType.InstallUpdates && JobTransitions.ActiveOnAgent.Contains(j.Status));
                if (await inFlight.CountAsync(ct) >= settings.GlobalMaxConcurrentInstalls)
                {
                    logger.LogDebug("Global install concurrency limit reached; job {JobId} stays queued", candidate.Id);
                    return null;
                }

                var siteInFlight = endpoint.SiteId is { } siteId
                    ? inFlight.Where(j => j.SiteId == siteId)
                    : inFlight.Where(j => j.SiteId == null && j.OrganizationId == endpoint.OrganizationId);
                if (await siteInFlight.CountAsync(ct) >= policy.MaxConcurrentInstallsPerSite)
                {
                    logger.LogDebug("Site install concurrency limit reached; job {JobId} stays queued", candidate.Id);
                    return null;
                }
            }

            candidate.Status = JobStatus.Delivered;
            candidate.DeliveredAt = now;
            candidate.LeaseExpiresAt = now.AddMinutes(settings.DeliveryLeaseMinutes);
            candidate.DeliveryCount++;
            candidate.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return candidate;
        }

        return null;
    }

    public static AgentJob ToAgentJob(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new AgentJob
        {
            JobId = job.Id,
            Type = job.Type,
            Trigger = job.Trigger,
            CreatedAt = job.CreatedAt,
            ExpiresAt = job.ExpiresAt,
            Install = job.Type == JobType.InstallUpdates ? Payload<InstallUpdatesPayload>(job) : null,
            Restart = job.Type == JobType.Restart ? Payload<RestartPayload>(job) : null,
            Notify = job.Type == JobType.NotifyUser ? Payload<NotifyUserPayload>(job) : null,
        };
    }

    public static T? Payload<T>(Job job)
        where T : class =>
        job.Payload is null ? null : JsonSerializer.Deserialize<T>(job.Payload, ContractJson.Options);

    public async Task<JobAckResponse> AcknowledgeAsync(Guid endpointId, Guid jobId, CancellationToken ct = default)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId && j.EndpointId == endpointId, ct)
                  ?? throw AppException.NotFound("Job");
        var now = clock.GetUtcNow();
        if (job.CancelRequestedAt is not null && job.Status is JobStatus.Delivered or JobStatus.Queued)
        {
            Finish(job, JobStatus.Cancelled, now, "Cancelled by administrator.");
            await db.SaveChangesAsync(ct);
        }

        if (job.Status.IsTerminal())
        {
            throw AppException.Conflict("The job was cancelled or has already finished.", AgentErrorCodes.JobCancelled);
        }

        if (job.Status == JobStatus.Delivered)
        {
            job.Status = JobStatus.Acknowledged;
            job.AcknowledgedAt = now;
            job.LeaseExpiresAt = null;
            job.LastProgressAt = now;
            job.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }

        return new JobAckResponse { Accepted = true };
    }

    public async Task ReportStatusAsync(Guid endpointId, Guid jobId, JobStatusReport report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Items.Count > 1000)
        {
            throw AppException.BadRequest("Too many items in one report.", AgentErrorCodes.ValidationFailed);
        }

        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var endpoint = (await db.Endpoints.FromSql($"SELECT * FROM endpoints WHERE id = {endpointId} FOR UPDATE").ToListAsync(ct)).FirstOrDefault()
                       ?? throw AppException.NotFound("Endpoint");
        var job = await db.Jobs.Include(j => j.Updates).FirstOrDefaultAsync(j => j.Id == jobId && j.EndpointId == endpointId, ct)
                  ?? throw AppException.NotFound("Job");

        if (report.Sequence <= job.LastSequence)
        {
            return; // duplicate or stale report (idempotent)
        }

        if (!JobTransitions.AgentMayTransition(job.Status, report.Status, job.TimedOut))
        {
            if (job.Status == JobStatus.Cancelled)
            {
                throw AppException.Conflict("The job was cancelled.", AgentErrorCodes.JobCancelled);
            }

            throw AppException.Conflict($"Cannot move a job from {job.Status} to {report.Status}.", "invalid_transition");
        }

        var previousStatus = job.Status;
        job.LastSequence = report.Sequence;
        job.Status = report.Status;
        job.Phase = ScanIngestionService.Truncate(report.Phase, 64);
        job.ProgressPercent = report.Percent is { } p ? Math.Clamp(p, 0, 100) : job.ProgressPercent;
        job.ProgressMessage = ScanIngestionService.Truncate(report.Message, 1000);
        job.LastProgressAt = now;
        job.UpdatedAt = now;
        job.LeaseExpiresAt = null;
        if (report.Status is JobStatus.Running && job.StartedAt is null)
        {
            job.StartedAt = now;
            EndpointEvents.Add(db, endpoint, "JobStarted", $"{Describe(job.Type)} started.", now, EventSeverity.Info, job.Id);
        }

        job.RebootRequired |= report.RebootRequired;
        var updatesChanged = await ApplyItemsAsync(endpoint, job, report.Items, now, ct);

        if (report.Status.IsTerminal())
        {
            job.StartedAt ??= now;
            job.ErrorCode = ScanIngestionService.Truncate(report.ErrorCode, 64);
            job.ErrorMessage = ScanIngestionService.Truncate(report.ErrorMessage, 4000);
            job.Result = report.Result is null ? job.Result : JsonSerializer.Serialize(report.Result, ContractJson.Options);
            job.CompletedAt = now;
            job.ProgressPercent = report.Status == JobStatus.Completed ? 100 : job.ProgressPercent;
            if (previousStatus == JobStatus.Failed && job.TimedOut)
            {
                job.TimedOut = false; // genuine late result replaces the timeout outcome
            }

            foreach (var item in job.Updates.Where(i => i.Status is JobItemStatus.Pending or JobItemStatus.Downloading or JobItemStatus.Downloaded or JobItemStatus.Installing))
            {
                item.Status = report.Status == JobStatus.Cancelled ? JobItemStatus.Cancelled : JobItemStatus.Skipped;
                item.SkipReason ??= report.Status == JobStatus.Cancelled ? SkipReasons.Cancelled : SkipReasons.NotApplicable;
                item.UpdatedAt = now;
            }

            var summary = report.Result;
            var text = report.Status switch
            {
                JobStatus.Completed when summary is { Failed: > 0 } => $"{Describe(job.Type)} completed with errors: {summary.Succeeded} succeeded, {summary.Failed} failed.",
                JobStatus.Completed when summary is not null && job.Type == JobType.InstallUpdates => $"{Describe(job.Type)} completed: {summary.Succeeded} installed, {summary.Skipped} skipped.",
                JobStatus.Completed => $"{Describe(job.Type)} completed.",
                JobStatus.Cancelled => $"{Describe(job.Type)} cancelled.",
                JobStatus.Expired => $"{Describe(job.Type)} expired before it could start: {report.ErrorMessage ?? report.ErrorCode ?? "deadline passed"}",
                _ => $"{Describe(job.Type)} failed: {report.ErrorMessage ?? WindowsUpdateErrors.DescribeCode(report.ErrorCode)}",
            };
            var severity = report.Status == JobStatus.Failed || summary is { Failed: > 0 } ? EventSeverity.Error
                : report.Status == JobStatus.Expired ? EventSeverity.Warning : EventSeverity.Info;
            EndpointEvents.Add(db, endpoint, "JobFinished", text, now, severity, job.Id,
                new { status = report.Status, report.ErrorCode, report.RebootRequired });
        }
        else if (report.Status == JobStatus.WaitingForReboot && previousStatus != JobStatus.WaitingForReboot)
        {
            EndpointEvents.Add(db, endpoint, "JobWaitingForReboot",
                "Windows requires a restart before more updates can be installed. The job continues automatically after the endpoint restarts.",
                now, EventSeverity.Warning, job.Id);
        }

        if (report.RebootRequired)
        {
            ScanIngestionService.ApplyRebootState(db, endpoint,
                true, endpoint.RebootReasons.Union(["Updates installed by EasyPatch require a restart"]).ToList(), now);
        }

        if (updatesChanged || report.RebootRequired)
        {
            var policy = await policies.ResolveAsync(endpoint, ct);
            var rows = await db.EndpointUpdates.Where(u => u.EndpointId == endpointId).ToListAsync(ct);
            ComplianceService.ApplyCounts(endpoint, rows, policy, now);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task<bool> ApplyItemsAsync(Endpoint endpoint, Job job, IReadOnlyList<JobItemReport> items, DateTimeOffset now, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return false;
        }

        var changedInventory = false;
        var byGuid = job.Updates.ToDictionary(u => u.UpdateGuid);
        var finalGuids = new List<(JobItemReport Report, bool WasFinal)>();
        foreach (var report in items.GroupBy(i => i.UpdateGuid).Select(g => g.Last()))
        {
            if (!byGuid.TryGetValue(report.UpdateGuid, out var item))
            {
                item = new JobUpdate
                {
                    Id = Guid.CreateVersion7(now),
                    JobId = job.Id,
                    UpdateGuid = report.UpdateGuid,
                    Title = "",
                };
                db.JobUpdates.Add(item);
                byGuid[report.UpdateGuid] = item;
            }

            var wasFinal = item.Status is JobItemStatus.Succeeded or JobItemStatus.Failed;
            item.RevisionNumber = report.RevisionNumber;
            item.Title = ScanIngestionService.Truncate(report.Title, 1000) ?? item.Title;
            item.KbNumber = KbNumber.Normalize(report.KbNumber) ?? item.KbNumber;
            item.Category = report.Category;
            item.Status = report.Status;
            item.SkipReason = ScanIngestionService.Truncate(report.SkipReason, 64);
            item.ResultCode = report.ResultCode;
            item.ErrorCode = ScanIngestionService.Truncate(report.ErrorCode, 32);
            item.ErrorMessage = ScanIngestionService.Truncate(
                report.ErrorMessage ?? (report.ErrorCode is not null ? WindowsUpdateErrors.DescribeCode(report.ErrorCode) : null), 2000);
            item.RebootRequired = report.RebootRequired;
            item.DownloadPercent = report.DownloadPercent;
            item.StartedAt = report.StartedAt ?? item.StartedAt;
            item.CompletedAt = report.CompletedAt ?? (report.Status is JobItemStatus.Succeeded or JobItemStatus.Failed or JobItemStatus.Skipped ? now : item.CompletedAt);
            item.UpdatedAt = now;
            if (report.Status is JobItemStatus.Succeeded or JobItemStatus.Failed && !wasFinal)
            {
                finalGuids.Add((report, wasFinal));
            }
        }

        if (finalGuids.Count > 0)
        {
            var guids = finalGuids.Select(f => f.Report.UpdateGuid).ToList();
            var rows = await db.EndpointUpdates.Where(u => u.EndpointId == endpoint.Id && guids.Contains(u.UpdateGuid))
                .ToDictionaryAsync(u => u.UpdateGuid, ct);
            foreach (var (report, _) in finalGuids)
            {
                if (!rows.TryGetValue(report.UpdateGuid, out var row))
                {
                    var catalogRow = await db.Updates.AsNoTracking()
                        .Where(u => u.UpdateGuid == report.UpdateGuid)
                        .OrderByDescending(u => u.RevisionNumber).FirstOrDefaultAsync(ct);
                    if (catalogRow is null)
                    {
                        continue; // never seen in a scan; the post-install scan will create it
                    }

                    row = new EndpointUpdate
                    {
                        Id = Guid.CreateVersion7(now),
                        EndpointId = endpoint.Id,
                        UpdateGuid = report.UpdateGuid,
                        UpdateId = catalogRow.Id,
                        State = EndpointUpdateState.Missing,
                        FirstDetectedAt = now,
                        LastDetectedAt = now,
                        StateChangedAt = now,
                        IsRequired = true,
                    };
                    db.EndpointUpdates.Add(row);
                }

                var errorMessage = report.ErrorMessage ?? (report.ErrorCode is not null ? WindowsUpdateErrors.DescribeCode(report.ErrorCode) : null);
                UpdateReconciler.ApplyInstallOutcome(row, report.Status == JobItemStatus.Succeeded, report.RebootRequired,
                    ScanIngestionService.Truncate(report.ErrorCode, 32), ScanIngestionService.Truncate(errorMessage, 2000), job.Id, now);
                changedInventory = true;
            }
        }

        return changedInventory;
    }

    internal static void Finish(Job job, JobStatus status, DateTimeOffset now, string? message, bool timedOut = false)
    {
        job.Status = status;
        job.CompletedAt = now;
        job.UpdatedAt = now;
        job.LeaseExpiresAt = null;
        job.TimedOut = timedOut;
        if (message is not null && status is JobStatus.Failed or JobStatus.Expired or JobStatus.Cancelled)
        {
            job.ErrorMessage ??= message;
        }

        foreach (var item in job.Updates.Where(i => i.Status is JobItemStatus.Pending or JobItemStatus.Downloading or JobItemStatus.Downloaded or JobItemStatus.Installing))
        {
            item.Status = status == JobStatus.Cancelled ? JobItemStatus.Cancelled : JobItemStatus.Skipped;
            item.UpdatedAt = now;
        }
    }

    public static string Describe(JobType type) => type switch
    {
        JobType.InstallUpdates => "Update installation",
        JobType.UpdateScan => "Update scan",
        JobType.InventoryRefresh => "Inventory refresh",
        JobType.Restart => "Restart",
        JobType.NotifyUser => "Message to users",
        _ => type.ToString(),
    };
}
