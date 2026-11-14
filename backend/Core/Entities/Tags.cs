namespace EasyPatch.Core.Entities;

/// <summary>MSP-wide label for endpoints (e.g. "pilot", "servers"). A tag can carry a policy layer that applies between the
/// site and endpoint layers; when an endpoint has several tags, the one with the higher priority wins.</summary>
public sealed class Tag
{
    public const int MaxNameLength = 40;

    public Guid Id { get; set; }
    public required string Name { get; set; }

    /// <summary>One of <see cref="TagColors.All"/>.</summary>
    public required string Color { get; set; }
    public string? Description { get; set; }

    /// <summary>Higher priority tags override lower ones when an endpoint has several tags with policies.</summary>
    public int Priority { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class EndpointTag
{
    public Guid EndpointId { get; set; }
    public Guid TagId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class TagColors
{
    public static IReadOnlyList<string> All { get; } = ["gray", "blue", "green", "amber", "red", "purple", "teal", "pink"];

    public static bool IsValid(string? color) => color is not null && All.Contains(color);
}
