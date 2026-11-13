using EasyPatch.Contracts;
using EasyPatch.Contracts.Agent;
using EasyPatch.Contracts.Policy;
using EasyPatch.Contracts.Updates;
using EasyPatch.Core.Entities;

namespace EasyPatch.Core.Domain;

public sealed record MaintenanceWindowSettings(IReadOnlyList<DayOfWeek> Days, TimeOnly Start, int DurationMinutes)
{
    public bool IsConfigured => Days.Count > 0 && DurationMinutes > 0;
}

/// <summary>Fully resolved policy for one endpoint, with the scope each value came from.</summary>
public sealed record EffectivePolicy
{
    public int CheckInIntervalSeconds { get; init; }
    public int ScanIntervalMinutes { get; init; }
    public int InventoryIntervalMinutes { get; init; }
    public int JitterPercent { get; init; }
    public IReadOnlyList<UpdateCategory> AllowedCategories { get; init; } = [];
    public bool IncludeOptional { get; init; }
    public IReadOnlyList<string> ExcludedKbs { get; init; } = [];
    public bool AcceptEulas { get; init; }
    public bool AutoInstallEnabled { get; init; }
    public MaintenanceWindowSettings? MaintenanceWindow { get; init; }
    public int ScanStaleAfterHours { get; init; }
    public int MaxConcurrentInstallsPerSite { get; init; }
    public bool TrayIconEnabled { get; init; } = true;
    public bool InstallNotificationsEnabled { get; init; } = true;

    /// <summary>Enabled recurring restart, or null.</summary>
    public RestartScheduleSettings? RestartSchedule { get; init; }

    /// <summary>Windows Update settings to enforce (null groups are not managed).</summary>
    public WindowsUpdatePolicySettings WindowsUpdate { get; init; } = new();
    public IReadOnlyDictionary<string, PolicyScope> Sources { get; init; } = new Dictionary<string, PolicyScope>();

    public AgentConfig ToAgentConfig() => new()
    {
        CheckInIntervalSeconds = CheckInIntervalSeconds,
        ActiveJobCheckInIntervalSeconds = Math.Min(15, CheckInIntervalSeconds),
        ScanIntervalMinutes = ScanIntervalMinutes,
        InventoryIntervalMinutes = InventoryIntervalMinutes,
        JitterPercent = JitterPercent,
        UserExperience = new UserExperienceConfig { TrayIcon = TrayIconEnabled, InstallNotifications = InstallNotificationsEnabled },
        WindowsUpdate = Enum.GetValues<WindowsUpdatePolicyGroup>().Any(WindowsUpdate.IsManaged) ? WindowsUpdate : null,
    };

    public InstallFilterOptions ToFilter(IEnumerable<Guid>? excludedUpdateGuids = null) => new()
    {
        Categories = AllowedCategories,
        IncludeOptional = IncludeOptional,
        ExcludedKbs = ExcludedKbs,
        ExcludedUpdateGuids = excludedUpdateGuids?.ToArray() ?? [],
        AcceptEulas = AcceptEulas,
    };
}

public static class PolicyDefaults
{
    public static Policy CreateGlobal(DateTimeOffset now) => new()
    {
        Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
        Scope = PolicyScope.Global,
        CheckInIntervalSeconds = 60,
        ScanIntervalMinutes = 360,
        InventoryIntervalMinutes = 1440,
        JitterPercent = 10,
        AllowedCategories = UpdateClassifier.DefaultInstallCategories.Select(c => c.ToString()).ToList(),
        IncludeOptional = false,
        ExcludedKbs = [],
        AcceptEulas = true,
        AutoInstallEnabled = false,
        MaintenanceDays = [],
        MaintenanceStart = new TimeOnly(2, 0),
        MaintenanceDurationMinutes = 180,
        ScanStaleAfterHours = 48,
        MaxConcurrentInstallsPerSite = 10,
        TrayIconEnabled = true,
        InstallNotificationsEnabled = true,
        UpdatedAt = now,
    };
}

public static class PolicyResolver
{
    /// <summary>Resolves global → organization → site → endpoint. Scalars: most specific non-null wins.
    /// Excluded KB lists are merged. The maintenance window, the restart schedule and each Windows Update group
    /// override as a unit.</summary>
    public static EffectivePolicy Resolve(Policy global, Policy? organization, Policy? site, Policy? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(global);
        var sources = new Dictionary<string, PolicyScope>();
        var layers = new List<(Policy Policy, PolicyScope Scope)> { (global, PolicyScope.Global) };
        if (organization is not null)
        {
            layers.Add((organization, PolicyScope.Organization));
        }

        if (site is not null)
        {
            layers.Add((site, PolicyScope.Site));
        }

        if (endpoint is not null)
        {
            layers.Add((endpoint, PolicyScope.Endpoint));
        }

        T Pick<T>(string name, Func<Policy, T?> selector, T fallback)
            where T : struct
        {
            var value = fallback;
            var scope = PolicyScope.Global;
            foreach (var (policy, layerScope) in layers)
            {
                if (selector(policy) is { } v)
                {
                    value = v;
                    scope = layerScope;
                }
            }

            sources[name] = scope;
            return value;
        }

        IReadOnlyList<UpdateCategory> categories = UpdateClassifier.DefaultInstallCategories;
        sources[nameof(EffectivePolicy.AllowedCategories)] = PolicyScope.Global;
        foreach (var (policy, layerScope) in layers)
        {
            if (policy.AllowedCategories is { } names)
            {
                categories = ParseCategories(names);
                sources[nameof(EffectivePolicy.AllowedCategories)] = layerScope;
            }
        }

        MaintenanceWindowSettings? window = null;
        sources[nameof(EffectivePolicy.MaintenanceWindow)] = PolicyScope.Global;
        foreach (var (policy, layerScope) in layers)
        {
            if (policy.MaintenanceDays is { } days)
            {
                window = new MaintenanceWindowSettings(
                    days.Where(d => d is >= 0 and <= 6).Distinct().Order().Select(d => (DayOfWeek)d).ToArray(),
                    policy.MaintenanceStart ?? new TimeOnly(2, 0),
                    policy.MaintenanceDurationMinutes ?? 180);
                sources[nameof(EffectivePolicy.MaintenanceWindow)] = layerScope;
            }
        }

        RestartScheduleSettings? restart = null;
        sources[nameof(EffectivePolicy.RestartSchedule)] = PolicyScope.Global;
        foreach (var (policy, layerScope) in layers)
        {
            if (policy.RestartSchedule is { } schedule)
            {
                restart = schedule;
                sources[nameof(EffectivePolicy.RestartSchedule)] = layerScope;
            }
        }

        T? Group<T>(string name, Func<PolicyWindowsUpdate, PolicyGroupSetting<T>?> selector)
            where T : class
        {
            T? value = null;
            sources["WindowsUpdate." + name] = PolicyScope.Global;
            foreach (var (policy, layerScope) in layers)
            {
                if (policy.WindowsUpdate is { } wu && selector(wu) is { } group)
                {
                    value = group.Managed ? group.Value : null;
                    sources["WindowsUpdate." + name] = layerScope;
                }
            }

            return value;
        }

        var windowsUpdate = new WindowsUpdatePolicySettings
        {
            AutomaticUpdates = Group(nameof(WindowsUpdatePolicyGroup.AutomaticUpdates), w => w.AutomaticUpdates),
            FeatureUpdates = Group(nameof(WindowsUpdatePolicyGroup.FeatureUpdates), w => w.FeatureUpdates),
            QualityUpdates = Group(nameof(WindowsUpdatePolicyGroup.QualityUpdates), w => w.QualityUpdates),
            RestartBehavior = Group(nameof(WindowsUpdatePolicyGroup.RestartBehavior), w => w.RestartBehavior),
        };

        var excluded = layers
            .SelectMany(l => l.Policy.ExcludedKbs)
            .Select(KbNumber.Normalize)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new EffectivePolicy
        {
            CheckInIntervalSeconds = Pick(nameof(EffectivePolicy.CheckInIntervalSeconds), p => p.CheckInIntervalSeconds, 60),
            ScanIntervalMinutes = Pick(nameof(EffectivePolicy.ScanIntervalMinutes), p => p.ScanIntervalMinutes, 360),
            InventoryIntervalMinutes = Pick(nameof(EffectivePolicy.InventoryIntervalMinutes), p => p.InventoryIntervalMinutes, 1440),
            JitterPercent = Pick(nameof(EffectivePolicy.JitterPercent), p => p.JitterPercent, 10),
            AllowedCategories = categories,
            IncludeOptional = Pick(nameof(EffectivePolicy.IncludeOptional), p => p.IncludeOptional, false),
            ExcludedKbs = excluded,
            AcceptEulas = Pick(nameof(EffectivePolicy.AcceptEulas), p => p.AcceptEulas, true),
            AutoInstallEnabled = Pick(nameof(EffectivePolicy.AutoInstallEnabled), p => p.AutoInstallEnabled, false),
            MaintenanceWindow = window is { IsConfigured: true } ? window : null,
            ScanStaleAfterHours = Pick(nameof(EffectivePolicy.ScanStaleAfterHours), p => p.ScanStaleAfterHours, 48),
            MaxConcurrentInstallsPerSite = Pick(nameof(EffectivePolicy.MaxConcurrentInstallsPerSite), p => p.MaxConcurrentInstallsPerSite, 10),
            TrayIconEnabled = Pick(nameof(EffectivePolicy.TrayIconEnabled), p => p.TrayIconEnabled, true),
            InstallNotificationsEnabled = Pick(nameof(EffectivePolicy.InstallNotificationsEnabled), p => p.InstallNotificationsEnabled, true),
            RestartSchedule = restart is { Enabled: true } ? restart : null,
            WindowsUpdate = windowsUpdate,
            Sources = sources,
        };
    }

    public static IReadOnlyList<UpdateCategory> ParseCategories(IEnumerable<string> names) =>
        names.Select(n => Enum.TryParse<UpdateCategory>(n, ignoreCase: true, out var c) ? (UpdateCategory?)c : null)
            .OfType<UpdateCategory>()
            .Distinct()
            .ToArray();

    public static IReadOnlyList<string> ValidatePolicy(Policy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var errors = new List<string>();
        void Range(string name, int? value, int min, int max)
        {
            if (value is { } v && (v < min || v > max))
            {
                errors.Add($"{name} must be between {min} and {max}.");
            }
        }

        Range(nameof(policy.CheckInIntervalSeconds), policy.CheckInIntervalSeconds, 15, 3600);
        Range(nameof(policy.ScanIntervalMinutes), policy.ScanIntervalMinutes, 30, 10080);
        Range(nameof(policy.InventoryIntervalMinutes), policy.InventoryIntervalMinutes, 60, 10080);
        Range(nameof(policy.JitterPercent), policy.JitterPercent, 0, 50);
        Range(nameof(policy.MaintenanceDurationMinutes), policy.MaintenanceDurationMinutes, 15, 1440);
        Range(nameof(policy.ScanStaleAfterHours), policy.ScanStaleAfterHours, 1, 720);
        Range(nameof(policy.MaxConcurrentInstallsPerSite), policy.MaxConcurrentInstallsPerSite, 1, 1000);
        if (policy.AllowedCategories is { } cats &&
            cats.Any(c => !Enum.TryParse<UpdateCategory>(c, ignoreCase: true, out _)))
        {
            errors.Add("AllowedCategories contains an unknown category.");
        }

        if (policy.MaintenanceDays is { } days && days.Any(d => d is < 0 or > 6))
        {
            errors.Add("MaintenanceDays must be 0 (Sunday) to 6 (Saturday).");
        }

        if (policy.ExcludedKbs.Any(kb => KbNumber.Normalize(kb) is null))
        {
            errors.Add("ExcludedKbs must contain KB numbers such as KB5044284.");
        }

        if (policy.RestartSchedule is { } restart)
        {
            errors.AddRange(restart.Validate());
        }

        if (policy.WindowsUpdate is { } windowsUpdate)
        {
            errors.AddRange(windowsUpdate.Validate());
        }

        return errors;
    }
}
