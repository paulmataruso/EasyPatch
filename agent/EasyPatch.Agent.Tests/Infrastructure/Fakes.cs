using EasyPatch.Agent.Core.Abstractions;
using EasyPatch.Contracts;
using EasyPatch.Contracts.Agent;
using EasyPatch.Contracts.Updates;

namespace EasyPatch.Agent.Tests.Infrastructure;

/// <summary>Deterministic, manually advanced clock.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _lock = new();
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_lock)
        {
            _now += by;
        }
    }
}

public sealed class FakeBootTime(DateTimeOffset boot) : IBootTimeProvider
{
    public DateTimeOffset Boot { get; set; } = boot;

    public DateTimeOffset GetLastBootTime() => Boot;
}

/// <summary>Reversible transform standing in for DPAPI, with a marker proving that stored keys went through it.</summary>
public sealed class TestSecretProtector : ISecretProtector
{
    public static readonly byte[] Marker = "TESTPROT"u8.ToArray();

    public int ProtectCalls { get; private set; }

    public byte[] Protect(byte[] plaintext)
    {
        ProtectCalls++;
        return [.. Marker, .. plaintext.Select(b => (byte)(b ^ 0x5A))];
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        if (protectedData.Length < Marker.Length || !protectedData.AsSpan(0, Marker.Length).SequenceEqual(Marker))
        {
            throw new System.Security.Cryptography.CryptographicException("Not protected by TestSecretProtector.");
        }

        return protectedData.Skip(Marker.Length).Select(b => (byte)(b ^ 0x5A)).ToArray();
    }
}

public sealed class InMemoryBootstrap : IBootstrapConfigSource
{
    public string? ServerUrl { get; set; }
    public string? EnrollmentToken { get; set; }
    public string? DeploymentCode { get; set; }
    public int ClearCalls { get; private set; }

    public BootstrapConfig Read() => new(ServerUrl, EnrollmentToken, DeploymentCode, null);

    public void ClearEnrollmentSecrets()
    {
        ClearCalls++;
        EnrollmentToken = null;
        DeploymentCode = null;
    }
}

public sealed class FakeInventory : IInventoryCollector
{
    public int Calls { get; private set; }

    public Task<InventoryReport> CollectAsync(InventoryContext context, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new InventoryReport
        {
            Hostname = "TEST-PC",
            DnsDomain = "corp.test",
            OsCaption = "Microsoft Windows 11 Pro",
            OsBuildNumber = 26100,
            OsUbr = 2605,
            SmbiosUuid = "4C4C4544-0000-0000-0000-000000000001",
            AgentVersion = context.AgentVersion,
        });
    }
}

public sealed class FakeRebootDetector : IRebootDetector
{
    public RebootStatus Status { get; set; } = RebootStatus.None;

    public Task<RebootStatus> DetectAsync(CancellationToken cancellationToken) => Task.FromResult(Status);
}

public sealed class FakeUpdateSource : IUpdateSourceDetector
{
    public UpdateSourceInfo Detect() => new() { Type = UpdateSourceType.WindowsUpdate, AutomaticUpdatesMode = "NotConfigured" };
}

/// <summary>One update known to <see cref="FakeWindowsUpdate"/>.</summary>
public sealed class FakeUpdate
{
    public required ReportedUpdate Metadata { get; init; }
    public bool Installed { get; set; }
    public bool PendingReboot { get; set; }
    public bool Downloaded { get; set; }
    public bool RequiresReboot { get; init; }
    public int? DownloadHResult { get; set; }
    public int? InstallHResult { get; set; }
    public Guid Guid => Metadata.UpdateGuid;
}

/// <summary>
/// Test double for the Windows Update Agent with WUA's observable semantics: an installed update that needs a
/// restart keeps appearing in "IsInstalled=0" with RebootRequired=true until <see cref="SimulateReboot"/>.
/// </summary>
public sealed class FakeWindowsUpdate : IWindowsUpdateService
{
    private readonly Lock _lock = new();

    public List<FakeUpdate> Updates { get; } = [];

    public List<string> Operations { get; } = [];

    public List<HistoryEntryReport> History { get; } = [];

    public bool RebootRequiredBeforeInstallation { get; set; }

    public Exception? ScanException { get; set; }

    public int Scans { get; private set; }

    public int OpenHandles { get; private set; }

    /// <summary>Invoked before each install; may throw, cancel or change state.</summary>
    public Func<FakeUpdate, CancellationToken, Task>? BeforeInstall { get; set; }

    public FakeUpdate Add(string title, Guid? classification = null, string? kb = null, bool browseOnly = false, bool isDriver = false,
        bool requiresReboot = false, bool installed = false)
    {
        var update = new FakeUpdate
        {
            Metadata = new ReportedUpdate
            {
                UpdateGuid = Guid.NewGuid(),
                RevisionNumber = 200,
                Title = title,
                KbNumbers = kb is null ? [] : [kb],
                Categories = classification is { } id ? [new UpdateCategoryRef { Id = id, Name = MicrosoftClassifications.NameOf(id) ?? "x", Type = "UpdateClassification" }] : [],
                BrowseOnly = browseOnly,
                IsDriver = isDriver,
                RebootBehavior = requiresReboot ? 1 : 0,
            },
            RequiresReboot = requiresReboot,
            Installed = installed,
        };
        Updates.Add(update);
        return update;
    }

    public void SimulateReboot()
    {
        lock (_lock)
        {
            foreach (var update in Updates.Where(u => u.PendingReboot))
            {
                update.PendingReboot = false;
                update.Installed = true;
            }

            RebootRequiredBeforeInstallation = false;
        }
    }

    public int InstallCount(Guid guid) => Operations.Count(o => o == $"install:{guid}");

    public Task<WindowsUpdateScanResult> ScanAsync(bool includeInstalled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            Scans++;
            if (ScanException is not null)
            {
                throw ScanException;
            }

            var missing = Updates.Where(u => !u.Installed)
                .Select(u => new UpdateCandidate(u.Metadata with { IsDownloaded = u.Downloaded, RebootRequired = u.PendingReboot }, new Handle(this, u)))
                .ToList();
            var installed = includeInstalled
                ? Updates.Where(u => u.Installed).Select(u => u.Metadata with { IsInstalled = true }).ToList()
                : [];
            return Task.FromResult(new WindowsUpdateScanResult(missing, installed, microsoftUpdateRegistered: false));
        }
    }

    public Task<bool> IsRebootRequiredBeforeInstallationAsync(CancellationToken cancellationToken) =>
        Task.FromResult(RebootRequiredBeforeInstallation);

    public Task<UpdateOperationResult> DownloadAsync(IUpdateHandle update, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = ((Handle)update).Update;
        lock (_lock)
        {
            Operations.Add($"download:{target.Guid}");
            if (target.DownloadHResult is { } hr)
            {
                return Task.FromResult(new UpdateOperationResult(UpdateOperationResult.Failed, hr, false));
            }

            target.Downloaded = true;
            return Task.FromResult(new UpdateOperationResult(UpdateOperationResult.Succeeded, 0, false));
        }
    }

    public async Task<UpdateOperationResult> InstallAsync(IUpdateHandle update, bool acceptEula, CancellationToken cancellationToken)
    {
        var target = ((Handle)update).Update;
        if (BeforeInstall is not null)
        {
            await BeforeInstall(target, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            Operations.Add($"install:{target.Guid}");
            if (target.InstallHResult is { } hr)
            {
                History.Add(Entry(target, OperationResultCode: 4, hr));
                return new UpdateOperationResult(UpdateOperationResult.Failed, hr, false);
            }

            if (target.RequiresReboot)
            {
                target.PendingReboot = true;
            }
            else
            {
                target.Installed = true;
            }

            History.Add(Entry(target, OperationResultCode: 2, 0));
            return new UpdateOperationResult(UpdateOperationResult.Succeeded, 0, target.RequiresReboot);
        }
    }

    public Task<IReadOnlyList<HistoryEntryReport>> GetHistoryAsync(DateTimeOffset? since, int maxEntries, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<HistoryEntryReport> entries = History.Where(h => since is null || h.Date > since).Take(maxEntries).ToList();
            return Task.FromResult(entries);
        }
    }

    private static HistoryEntryReport Entry(FakeUpdate update, int OperationResultCode, int hresult) => new()
    {
        UpdateGuid = update.Guid,
        RevisionNumber = update.Metadata.RevisionNumber,
        Title = update.Metadata.Title,
        KbNumber = update.Metadata.KbNumbers.Count > 0 ? update.Metadata.KbNumbers[0] : null,
        Operation = "Installation",
        ResultCode = OperationResultCode,
        HResult = hresult == 0 ? null : WindowsUpdateErrors.Format(hresult),
        Date = DateTimeOffset.UtcNow,
        ClientApplicationId = "EasyPatch Agent",
    };

    private sealed class Handle : IUpdateHandle
    {
        private readonly FakeWindowsUpdate _owner;
        private bool _disposed;

        public Handle(FakeWindowsUpdate owner, FakeUpdate update)
        {
            _owner = owner;
            Update = update;
            _owner.OpenHandles++;
        }

        public FakeUpdate Update { get; }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _owner.OpenHandles--;
            }
        }
    }
}
