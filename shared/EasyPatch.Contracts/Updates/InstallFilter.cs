using EasyPatch.Contracts.Agent;

namespace EasyPatch.Contracts.Updates;

public readonly record struct FilterDecision(bool Install, string? SkipReason, UpdateCategory Category)
{
    public static FilterDecision Accept(UpdateCategory category) => new(true, null, category);

    public static FilterDecision Skip(string reason, UpdateCategory category) => new(false, reason, category);
}

/// <summary>
/// The single source of truth for "which updates does this job install". The agent applies it to a fresh scan;
/// the server applies it to the last reported scan to build the install preview and compliance flags.
/// </summary>
public static class InstallFilter
{
    public static FilterDecision Evaluate(ReportedUpdate update, InstallUpdatesPayload payload)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(payload);
        var decision = Evaluate(update.UpdateGuid, update.KbNumbers, UpdateClassifier.Classify(update), update.BrowseOnly,
            update.IsInstalled, update.RebootRequired, payload);
        if (!decision.Install)
        {
            return decision;
        }

        if (update.CanRequestUserInput)
        {
            return FilterDecision.Skip(SkipReasons.RequiresUserInput, decision.Category);
        }

        if (!update.EulaAccepted && !payload.Filter.AcceptEulas)
        {
            return FilterDecision.Skip(SkipReasons.EulaNotAccepted, decision.Category);
        }

        return decision;
    }

    /// <summary>Core decision from already-classified metadata (used by the server with stored categories).</summary>
    public static FilterDecision Evaluate(Guid updateGuid, IEnumerable<string> kbNumbers, UpdateCategory category, bool browseOnly,
        bool isInstalled, bool rebootRequired, InstallUpdatesPayload payload)
    {
        ArgumentNullException.ThrowIfNull(kbNumbers);
        ArgumentNullException.ThrowIfNull(payload);
        if (isInstalled)
        {
            return FilterDecision.Skip(SkipReasons.AlreadyInstalled, category);
        }

        if (rebootRequired)
        {
            return FilterDecision.Skip(SkipReasons.PendingReboot, category);
        }

        if (IsExcluded(updateGuid, kbNumbers, payload.Filter))
        {
            return FilterDecision.Skip(SkipReasons.Excluded, category);
        }

        if (payload.Mode == InstallMode.Selected)
        {
            return payload.SelectedUpdateGuids.Contains(updateGuid)
                ? FilterDecision.Accept(category)
                : FilterDecision.Skip(SkipReasons.NotSelected, category);
        }

        var policyDecision = EvaluatePolicy(category, browseOnly, payload.Filter);
        return policyDecision is null ? FilterDecision.Accept(category) : FilterDecision.Skip(policyDecision, category);
    }

    /// <summary>Returns null when the category/optional rules allow the update, otherwise the skip reason.</summary>
    public static string? EvaluatePolicy(UpdateCategory category, bool browseOnly, InstallFilterOptions filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!filter.Categories.Contains(category))
        {
            return SkipReasons.CategoryNotAllowed;
        }

        if (browseOnly && !filter.IncludeOptional)
        {
            return SkipReasons.OptionalNotAllowed;
        }

        return null;
    }

    /// <summary>True when policy would install this update automatically (used for compliance "required" flag).</summary>
    public static bool IsRequired(UpdateCategory category, bool browseOnly, Guid updateGuid, IEnumerable<string> kbNumbers, InstallFilterOptions filter) =>
        !IsExcluded(updateGuid, kbNumbers, filter) && EvaluatePolicy(category, browseOnly, filter) is null;

    public static bool IsExcluded(Guid updateGuid, IEnumerable<string> kbNumbers, InstallFilterOptions filter)
    {
        ArgumentNullException.ThrowIfNull(kbNumbers);
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.ExcludedUpdateGuids.Contains(updateGuid))
        {
            return true;
        }

        if (filter.ExcludedKbs.Count == 0)
        {
            return false;
        }

        foreach (var kb in kbNumbers)
        {
            var normalized = KbNumber.Normalize(kb);
            if (normalized is not null && filter.ExcludedKbs.Any(x => string.Equals(KbNumber.Normalize(x), normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}

public static class KbNumber
{
    /// <summary>Normalizes "5044284", "kb5044284" or "KB5044284" to "KB5044284". Returns null if not a KB number.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("KB", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        return trimmed.Length is > 0 and <= 10 && trimmed.All(char.IsAsciiDigit) ? "KB" + trimmed : null;
    }
}
