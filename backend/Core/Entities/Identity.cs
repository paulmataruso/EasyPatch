namespace EasyPatch.Core.Entities;

public sealed class User
{
    public Guid Id { get; set; }
    public required string Username { get; set; }
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public UserRole Role { get; set; }
    public bool IsActive { get; set; } = true;
    public bool MfaEnabled { get; set; }

    /// <summary>TOTP secret protected with ASP.NET Data Protection.</summary>
    public string? MfaSecretProtected { get; set; }

    /// <summary>Pending (not yet confirmed) TOTP secret during MFA setup.</summary>
    public string? MfaPendingSecretProtected { get; set; }
    public long? MfaLastUsedStep { get; set; }
    public List<string> RecoveryCodeHashes { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public int FailedLoginCount { get; set; }
    public int LockoutCount { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }
    public DateTimeOffset PasswordChangedAt { get; set; }
    public bool MustChangePassword { get; set; }
    public List<UserOrganizationAccess> OrganizationAccess { get; set; } = [];
}

public sealed class UserSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public required string TokenHash { get; set; }
    public bool MfaCompleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}

/// <summary>Restricts a non-SuperAdmin user to specific organizations (customer-scoped admins).</summary>
public sealed class UserOrganizationAccess
{
    public Guid UserId { get; set; }
    public Guid OrganizationId { get; set; }
}

public sealed class PasswordResetToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
}

public sealed class AuditLog
{
    public long Id { get; set; }
    public Guid? UserId { get; set; }
    public string? Username { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? EndpointId { get; set; }
    public required string Action { get; set; }
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }

    /// <summary>JSON document with action-specific details.</summary>
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
