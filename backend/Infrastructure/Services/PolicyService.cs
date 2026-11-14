using EasyPatch.Core.Domain;
using EasyPatch.Core.Entities;
using EasyPatch.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EasyPatch.Infrastructure.Services;

public sealed class PolicyService(EasyPatchDbContext db, TimeProvider clock)
{
    public async Task<Policy> GetGlobalAsync(CancellationToken ct = default) =>
        await db.Policies.AsNoTracking().FirstOrDefaultAsync(p => p.Scope == PolicyScope.Global, ct)
        ?? PolicyDefaults.CreateGlobal(clock.GetUtcNow());

    public Task<Policy?> GetOrganizationPolicyAsync(Guid organizationId, CancellationToken ct = default) =>
        db.Policies.AsNoTracking().FirstOrDefaultAsync(p => p.Scope == PolicyScope.Organization && p.OrganizationId == organizationId, ct);

    public Task<Policy?> GetSitePolicyAsync(Guid siteId, CancellationToken ct = default) =>
        db.Policies.AsNoTracking().FirstOrDefaultAsync(p => p.Scope == PolicyScope.Site && p.SiteId == siteId, ct);

    public Task<Policy?> GetEndpointPolicyAsync(Guid endpointId, CancellationToken ct = default) =>
        db.Policies.AsNoTracking().FirstOrDefaultAsync(p => p.Scope == PolicyScope.Endpoint && p.EndpointId == endpointId, ct);

    /// <summary>Effective policy for an organization or site (no endpoint overrides).</summary>
    public async Task<EffectivePolicy> ResolveAsync(Guid organizationId, Guid? siteId, CancellationToken ct = default)
    {
        var global = await GetGlobalAsync(ct);
        var org = await GetOrganizationPolicyAsync(organizationId, ct);
        var site = siteId is { } s ? await GetSitePolicyAsync(s, ct) : null;
        return PolicyResolver.Resolve(global, org, site);
    }

    /// <summary>Effective policy for one endpoint, including its own overrides.</summary>
    public async Task<EffectivePolicy> ResolveAsync(Endpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var global = await GetGlobalAsync(ct);
        var org = await GetOrganizationPolicyAsync(endpoint.OrganizationId, ct);
        var site = endpoint.SiteId is { } s ? await GetSitePolicyAsync(s, ct) : null;
        var own = await GetEndpointPolicyAsync(endpoint.Id, ct);
        return PolicyResolver.Resolve(global, org, site, own);
    }

    /// <summary>Resolves policies for many endpoints with one query: (organization, site, endpoint) → policy.</summary>
    public async Task<Func<Guid, Guid?, Guid, EffectivePolicy>> CreateResolverAsync(CancellationToken ct = default)
    {
        var all = await db.Policies.AsNoTracking().ToListAsync(ct);
        var global = all.FirstOrDefault(p => p.Scope == PolicyScope.Global) ?? PolicyDefaults.CreateGlobal(clock.GetUtcNow());
        var orgs = all.Where(p => p.Scope == PolicyScope.Organization && p.OrganizationId != null).ToDictionary(p => p.OrganizationId!.Value);
        var sites = all.Where(p => p.Scope == PolicyScope.Site && p.SiteId != null).ToDictionary(p => p.SiteId!.Value);
        var endpoints = all.Where(p => p.Scope == PolicyScope.Endpoint && p.EndpointId != null).ToDictionary(p => p.EndpointId!.Value);
        var cache = new Dictionary<(Guid, Guid?), EffectivePolicy>();
        return (orgId, siteId, endpointId) =>
        {
            var org = orgs.GetValueOrDefault(orgId);
            var site = siteId is { } s ? sites.GetValueOrDefault(s) : null;
            if (endpoints.TryGetValue(endpointId, out var own))
            {
                return PolicyResolver.Resolve(global, org, site, own);
            }

            if (!cache.TryGetValue((orgId, siteId), out var policy))
            {
                policy = PolicyResolver.Resolve(global, org, site);
                cache[(orgId, siteId)] = policy;
            }

            return policy;
        };
    }

    /// <summary>Creates or replaces the policy row for a scope. Returns the stored row.</summary>
    public async Task<Policy> UpsertAsync(PolicyScope scope, Guid? organizationId, Guid? siteId, Policy values, Guid? userId, CancellationToken ct = default) =>
        await UpsertAsync(scope, organizationId, siteId, null, values, userId, ct);

    public async Task<Policy> UpsertAsync(PolicyScope scope, Guid? organizationId, Guid? siteId, Guid? endpointId, Policy values, Guid? userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var errors = PolicyResolver.ValidatePolicy(values).ToList();
        if (scope == PolicyScope.Endpoint && values.MaxConcurrentInstallsPerSite is not null)
        {
            errors.Add("The per-site install limit applies to a whole site and cannot be set for a single endpoint.");
        }

        if (errors.Count > 0)
        {
            throw AppException.BadRequest(string.Join(" ", errors), "validation_failed");
        }

        var existing = scope switch
        {
            PolicyScope.Global => await db.Policies.FirstOrDefaultAsync(p => p.Scope == PolicyScope.Global, ct),
            PolicyScope.Organization => await db.Policies.FirstOrDefaultAsync(p => p.Scope == scope && p.OrganizationId == organizationId, ct),
            PolicyScope.Endpoint => await db.Policies.FirstOrDefaultAsync(p => p.Scope == scope && p.EndpointId == endpointId, ct),
            _ => await db.Policies.FirstOrDefaultAsync(p => p.Scope == scope && p.SiteId == siteId, ct),
        };

        if (scope == PolicyScope.Global)
        {
            // The global policy must define every value.
            var defaults = PolicyDefaults.CreateGlobal(clock.GetUtcNow());
            values.CheckInIntervalSeconds ??= defaults.CheckInIntervalSeconds;
            values.ScanIntervalMinutes ??= defaults.ScanIntervalMinutes;
            values.InventoryIntervalMinutes ??= defaults.InventoryIntervalMinutes;
            values.JitterPercent ??= defaults.JitterPercent;
            values.AllowedCategories ??= defaults.AllowedCategories;
            values.IncludeOptional ??= defaults.IncludeOptional;
            values.AcceptEulas ??= defaults.AcceptEulas;
            values.AutoInstallEnabled ??= defaults.AutoInstallEnabled;
            values.MaintenanceDays ??= [];
            values.MaintenanceStart ??= defaults.MaintenanceStart;
            values.MaintenanceDurationMinutes ??= defaults.MaintenanceDurationMinutes;
            values.ScanStaleAfterHours ??= defaults.ScanStaleAfterHours;
            values.MaxConcurrentInstallsPerSite ??= defaults.MaxConcurrentInstallsPerSite;
            values.TrayIconEnabled ??= defaults.TrayIconEnabled;
            values.InstallNotificationsEnabled ??= defaults.InstallNotificationsEnabled;
            values.AgentAutoUpdateEnabled ??= defaults.AgentAutoUpdateEnabled;
        }

        if (existing is null)
        {
            existing = new Policy
            {
                Id = Guid.CreateVersion7(), Scope = scope, OrganizationId = organizationId, SiteId = siteId, EndpointId = endpointId,
            };
            db.Policies.Add(existing);
        }

        existing.CheckInIntervalSeconds = values.CheckInIntervalSeconds;
        existing.ScanIntervalMinutes = values.ScanIntervalMinutes;
        existing.InventoryIntervalMinutes = values.InventoryIntervalMinutes;
        existing.JitterPercent = values.JitterPercent;
        existing.AllowedCategories = values.AllowedCategories;
        existing.IncludeOptional = values.IncludeOptional;
        existing.ExcludedKbs = values.ExcludedKbs
            .Select(Contracts.Updates.KbNumber.Normalize).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        existing.AcceptEulas = values.AcceptEulas;
        existing.AutoInstallEnabled = values.AutoInstallEnabled;
        existing.MaintenanceDays = values.MaintenanceDays;
        existing.MaintenanceStart = values.MaintenanceStart;
        existing.MaintenanceDurationMinutes = values.MaintenanceDurationMinutes;
        existing.ScanStaleAfterHours = values.ScanStaleAfterHours;
        existing.MaxConcurrentInstallsPerSite = values.MaxConcurrentInstallsPerSite;
        existing.TrayIconEnabled = values.TrayIconEnabled;
        existing.InstallNotificationsEnabled = values.InstallNotificationsEnabled;
        existing.AgentAutoUpdateEnabled = values.AgentAutoUpdateEnabled;
        existing.RestartSchedule = values.RestartSchedule;
        existing.WindowsUpdate = values.WindowsUpdate;
        existing.UpdatedAt = clock.GetUtcNow();
        existing.UpdatedByUserId = userId;
        return existing;
    }

    public async Task<bool> DeleteAsync(PolicyScope scope, Guid id, CancellationToken ct = default)
    {
        var row = scope switch
        {
            PolicyScope.Organization => await db.Policies.FirstOrDefaultAsync(p => p.Scope == scope && p.OrganizationId == id, ct),
            PolicyScope.Endpoint => await db.Policies.FirstOrDefaultAsync(p => p.Scope == scope && p.EndpointId == id, ct),
            _ => await db.Policies.FirstOrDefaultAsync(p => p.Scope == scope && p.SiteId == id, ct),
        };
        if (row is null)
        {
            return false;
        }

        db.Policies.Remove(row);
        return true;
    }
}
