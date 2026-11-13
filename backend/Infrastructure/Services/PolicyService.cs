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

    public async Task<EffectivePolicy> ResolveAsync(Guid organizationId, Guid? siteId, CancellationToken ct = default)
    {
        var global = await GetGlobalAsync(ct);
        var org = await GetOrganizationPolicyAsync(organizationId, ct);
        var site = siteId is { } s ? await GetSitePolicyAsync(s, ct) : null;
        return PolicyResolver.Resolve(global, org, site);
    }

    /// <summary>Resolves policies for many endpoints with three queries.</summary>
    public async Task<Func<Guid, Guid?, EffectivePolicy>> CreateResolverAsync(CancellationToken ct = default)
    {
        var all = await db.Policies.AsNoTracking().ToListAsync(ct);
        var global = all.FirstOrDefault(p => p.Scope == PolicyScope.Global) ?? PolicyDefaults.CreateGlobal(clock.GetUtcNow());
        var orgs = all.Where(p => p.Scope == PolicyScope.Organization && p.OrganizationId != null).ToDictionary(p => p.OrganizationId!.Value);
        var sites = all.Where(p => p.Scope == PolicyScope.Site && p.SiteId != null).ToDictionary(p => p.SiteId!.Value);
        var cache = new Dictionary<(Guid, Guid?), EffectivePolicy>();
        return (orgId, siteId) =>
        {
            if (!cache.TryGetValue((orgId, siteId), out var policy))
            {
                policy = PolicyResolver.Resolve(global, orgs.GetValueOrDefault(orgId), siteId is { } s ? sites.GetValueOrDefault(s) : null);
                cache[(orgId, siteId)] = policy;
            }

            return policy;
        };
    }

    /// <summary>Creates or replaces the policy row for a scope. Returns the stored row.</summary>
    public async Task<Policy> UpsertAsync(PolicyScope scope, Guid? organizationId, Guid? siteId, Policy values, Guid? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var errors = PolicyResolver.ValidatePolicy(values);
        if (errors.Count > 0)
        {
            throw AppException.BadRequest(string.Join(" ", errors), "validation_failed");
        }

        var existing = scope switch
        {
            PolicyScope.Global => await db.Policies.FirstOrDefaultAsync(p => p.Scope == PolicyScope.Global, ct),
            PolicyScope.Organization => await db.Policies.FirstOrDefaultAsync(p => p.Scope == scope && p.OrganizationId == organizationId, ct),
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
        }

        if (existing is null)
        {
            existing = new Policy { Id = Guid.CreateVersion7(), Scope = scope, OrganizationId = organizationId, SiteId = siteId };
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
        existing.UpdatedAt = clock.GetUtcNow();
        existing.UpdatedByUserId = userId;
        return existing;
    }

    public async Task<bool> DeleteAsync(PolicyScope scope, Guid id, CancellationToken ct = default)
    {
        var row = scope == PolicyScope.Organization
            ? await db.Policies.FirstOrDefaultAsync(p => p.Scope == scope && p.OrganizationId == id, ct)
            : await db.Policies.FirstOrDefaultAsync(p => p.Scope == scope && p.SiteId == id, ct);
        if (row is null)
        {
            return false;
        }

        db.Policies.Remove(row);
        return true;
    }
}
