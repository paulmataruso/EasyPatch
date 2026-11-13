namespace EasyPatch.Core.Domain;

public readonly record struct WindowOccurrence(DateTimeOffset Start, DateTimeOffset End);

public static class MaintenanceWindow
{
    /// <summary>
    /// Returns the window occurrence containing <paramref name="now"/>, if any. Windows may cross midnight
    /// (e.g. Saturday 22:00 for 6 hours). Days refer to the day the window starts, in <paramref name="timeZoneId"/>.
    /// </summary>
    public static WindowOccurrence? FindOpenWindow(MaintenanceWindowSettings settings, string? timeZoneId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsConfigured)
        {
            return null;
        }

        var tz = ResolveTimeZone(timeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(now, tz);
        var duration = TimeSpan.FromMinutes(settings.DurationMinutes);

        // A window that started up to (duration) ago may still be open: check today and the previous days it spans.
        var daysBack = (int)Math.Ceiling(duration.TotalDays);
        for (var offset = 0; offset <= daysBack; offset++)
        {
            var day = DateOnly.FromDateTime(localNow.DateTime).AddDays(-offset);
            if (!settings.Days.Contains(day.DayOfWeek))
            {
                continue;
            }

            var localStart = day.ToDateTime(settings.Start, DateTimeKind.Unspecified);
            if (tz.IsInvalidTime(localStart))
            {
                localStart = localStart.AddHours(1); // skipped by DST: start at the next valid hour
            }

            var start = new DateTimeOffset(localStart, tz.GetUtcOffset(localStart));
            var end = start + duration;
            if (now >= start && now < end)
            {
                return new WindowOccurrence(start, end);
            }
        }

        return null;
    }

    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    public static bool IsValidTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return false;
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }
}
