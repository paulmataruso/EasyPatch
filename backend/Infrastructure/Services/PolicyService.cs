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

    public Task<Policy?> GetTagPolicyAsync(Guid tagId, CancellationToken ct = default) =>
        db.Policies.AsNoTracking().FirstOrDefaultAsync(p => p.Scope == PolicyScope.Tag && p.TagId == tagId, ct);

    /// <summary>Policy layers of the tags an endpoint has (only tags that carry a policy).</summary>
    public async Task<IReadOnlyList<TagPolicyLayer>> GetTagLayersAsync(Guid endpointId, CancellationToken ct = default)
    {
        var rows = await (from et in db.EndpointTags.AsNoTracking()
                          join t in db.Tags.AsNoTracking() on et.TagId equals t.Id
                          join p in db.Policies.AsNoTracking() on t.Id equals p.TagId
                          where et.EndpointId == endpointId && p.Scope == PolicyScope.Tag
                          select new { t.Name, t.Priority, Policy = p }).ToListAsync(ct);
        return rows.Select(r => new TagPolicyLayer(r.Name, r.Priority, r.Policy)).ToList();
    }

    /// <summary>Effective policy for an organization or site (no endpoint overrides).</summary>
    public async Task<EffectivePolicy> ResolveAsync(Guid organizationId, Guid? siteId, CancellationToken ct = default)
    {
        var global = await GetGlobalAsync(ct);
        var org = await GetOrganizationPolicyAsync(organizationId, ct);
        var site = siteId is { } s ? await GetSitePolicyAsync(s, ct) : null;
        return PolicyResolver.Resolve(global, org, site);
    }

    /// <summary>Effective policy for one endpoint, including its tags and its own overrides.</summary>
    public async Task<EffectivePolicy> ResolveAsync(Endpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var global = await GetGlobalAsync(ct);
        var org = await GetOrganizationPolicyAsync(endpoint.OrganizationId, ct);
        var site = endpoint.SiteId is { } s ? await GetSitePolicyAsync(s, ct) : null;
        var tags = await GetTagLayersAsync(endpoint.Id, ct);
        var own = await GetEndpointPolicyAsync(endpoint.Id, ct);
        return PolicyResolver.Resolve(global, org, site, own, tags);
    }

    /// <summary>Resolves policies for many endpoints with a few queries: (organization, site, endpoint) → policy. The
    /// endpoint's tags are looked up internally.</summary>
    public async Task<Func<Guid, Guid?, Guid, EffectivePolicy>> CreateResolverAsync(CancellationToken ct = default)
    {
        var all = await db.Policies.AsNoTracking().ToListAsync(ct);
        var global = all.FirstOrDefault(p => p.Scope == PolicyScope.Global) ?? PolicyDefaults.CreateGlobal(clock.GetUtcNow());
        var orgs = all.Where(p => p.Scope == PolicyScope.Organization && p.OrganizationId != null).ToDictionary(p => p.OrganizationId!.Value);
        var sites = all.Where(p => p.Scope == PolicyScope.Site && p.SiteId != null).ToDictionary(p => p.SiteId!.Value);
        var endpoints = all.Where(p => p.Scope == PolicyScope.Endpoint && p.EndpointId != null).ToDictionary(p => p.EndpointId!.Value);
        var tagPolicies = all.Where(p => p.Scope == PolicyScope.Tag && p.TagId != null).ToDictionary(p => p.TagId!.Value);
        var endpointTags = new Dictionary<Guid, List<TagPolicyLayer>>();
        if (tagPolicies.Count > 0)
        {
            var taggedIds = tagPolicies.Keys.ToList();
            var tags = await db.Tags.AsNoTracking().Where(t => taggedIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
            var links = await db.EndpointTags.AsNoTracking().Where(et => taggedIds.Contains(et.TagId)).ToListAsync(ct);
            foreach (var link in links)
            {
                if (!tags.TryGetValue(link.TagId, out var tag))
                {
                    continue;
                }

                if (!endpointTags.TryGetValue(link.EndpointId, out var layers))
                {
                    endpointTags[link.EndpointId] = layers = [];
                }

                layers.Add(new TagPolicyLayer(tag.Name, tag.Priority, tagPolicies[link.TagId]));
            }
        }

        var cache = new Dictionary<(Guid, Guid?, string), EffectivePolicy>();
        return (orgId, siteId, endpointId) =>
        {
            var org = orgs.GetValueOrDefault(orgId);
            var site = siteId is { } s ? sites.GetValueOrDefault(s) : null;
            var tags = endpointTags.GetValueOrDefault(endpointId);
            if (endpoints.TryGetValue(endpointId, out var own))
            {
                return PolicyResolver.Resolve(global, org, site, own, tags);
            }

            var tagKey = tags is null ? "" : string.Join(',', tags.Select(t => t.Policy.TagId).Order());
            if (!cache.TryGetValue((orgId, siteId, tagKey), out var policy))
            {
                policy = PolicyResolver.Resolve(global, org, site, null, tags);
                cache[(orgId, siteId, tagKey)] = policy;
            }

            return policy;
        };
    }

    /// <summary>Creates or replaces the policy row for a scope. Returns the stored row.</summary>
    public async Task<Policy> UpsertAsync(PolicyScope scope, Guid? organizationId, Guid? siteId, Policy values, Guid? userId, CancellationToken ct = default) =>
        await UpsertAsync(scope, organizationId, siteId, null, values, userId, ct);

    public Task<Policy> UpsertAsync(PolicyScope scope, Guid? organizationId, Guid? siteId, Guid? endpointId, Policy values, Guid? userId,
        CancellationToken ct = default) =>
        UpsertCoreAsync(scope, organizationId, siteId, endpointId, null, values, userId, ct);

    public Task<Policy> UpsertTagPolicyAsync(Guid tagId, Policy values, Guid? userId, CancellationToken ct = default) =>
        UpsertCoreAsync(PolicyScope.Tag, null, null, null, tagId, values, userId, ct);

    private async Task<Policy> UpsertCoreAsync(PolicyScope scope, Guid? organizationId, Guid? siteId, Guid? endpointId, Guid? tagId, Policy values,
        Guid? userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(values);
        var errors = PolicyResolver.ValidatePolicy(values).ToList();
        if (scope is PolicyScope.Endpoint or PolicyScope.Tag && values.MaxConcurrentInstallsPerSite is not null)
        {
            errors.Add("The per-site install limit applies to a whole site and can only be set for an organization or a site.");
        }

        if (errors.Count > 0)
        {
            throw AppException.BadRequest(string.Join(" ", errors), "validation_failed");
        }

        var existing = scope switch
        {
            PolicyScope.Global => await db.Policies.FirstOrDefaultAsync(p => p.Scope == PolicyScope.Global, ct),
            PolicyScope.Organization => await db.Policies.FirstOrDefaultAsync(p => p.Scope == scope && p.OrganizationId == organizationId, ct),
            PolicyScope.Site => await db.Policies.FirstOrDefaultAsync(p => p.Scope == scope && p.SiteId == siteId, ct),
            PolicyScope.Endpoint => await db.Policies.FirstOrDefaultAsync(p => p.Scope == scope && p.EndpointId == endpointId, ct),
            PolicyScope.Tag => await db.Policies.FirstOrDefaultAsync(p => p.Scope == scope && p.TagId == tagId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
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
            values.InstallDelayDays ??= 0;
            values.SecurityInstallDelayDays ??= 0;
            values.KnownExploitedSkipsDelay ??= true;
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
                Id = Guid.CreateVersion7(), Scope = scope, OrganizationId = organizationId, SiteId = siteId, EndpointId = endpointId, TagId = tagId,
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
        existing.InstallDelayDays = values.InstallDelayDays;
        existing.SecurityInstallDelayDays = values.SecurityInstallDelayDays;
        existing.KnownExploitedSkipsDelay = values.KnownExploitedSkipsDelay;
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
        existing.ApplicationUpdates = values.ApplicationUpdates?.Normalized();
        existing.UpdatedAt = clock.GetUtcNow();
        existing.UpdatedByUserId = userId;
        return existing;
    }

    public async Task<bool> DeleteAsync(PolicyScope scope, Guid id, CancellationToken ct = default)
    {
        var row = scope switch
        {
            PolicyScope.Organization => await db.Policies.FirstOrDefaultAsync(p => p.Scope == scope && p.OrganizationId == id, ct),
            PolicyScope.Site => await db.Policies.FirstOrDefaultAsync(p => p.Scope == scope && p.SiteId == id, ct),
            PolicyScope.Endpoint => await db.Policies.FirstOrDefaultAsync(p => p.Scope == scope && p.EndpointId == id, ct),
            PolicyScope.Tag => await db.Policies.FirstOrDefaultAsync(p => p.Scope == scope && p.TagId == id, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
        if (row is null)
        {
            return false;
        }

        db.Policies.Remove(row);
        return true;
    }
}
