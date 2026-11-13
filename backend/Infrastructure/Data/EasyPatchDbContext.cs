using System.Text;
using System.Text.Json;
using EasyPatch.Contracts;
using EasyPatch.Contracts.Json;
using EasyPatch.Core.Domain;
using EasyPatch.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EasyPatch.Infrastructure.Data;

public sealed class EasyPatchDbContext(DbContextOptions<EasyPatchDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<UserOrganizationAccess> UserOrganizationAccess => Set<UserOrganizationAccess>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<ServerSettingsRow> ServerSettings => Set<ServerSettingsRow>();
    public DbSet<OsLifecycleEntry> OsLifecycleEntries => Set<OsLifecycleEntry>();
    public DbSet<ComplianceSnapshot> ComplianceSnapshots => Set<ComplianceSnapshot>();
    public DbSet<Endpoint> Endpoints => Set<Endpoint>();
    public DbSet<AgentCredential> AgentCredentials => Set<AgentCredential>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<DeploymentCode> DeploymentCodes => Set<DeploymentCode>();
    public DbSet<EnrollmentRequest> EnrollmentRequests => Set<EnrollmentRequest>();
    public DbSet<EndpointEvent> EndpointEvents => Set<EndpointEvent>();
    public DbSet<Update> Updates => Set<Update>();
    public DbSet<EndpointUpdate> EndpointUpdates => Set<EndpointUpdate>();
    public DbSet<UpdateExclusion> UpdateExclusions => Set<UpdateExclusion>();
    public DbSet<UpdateScan> UpdateScans => Set<UpdateScan>();
    public DbSet<UpdateHistoryEntry> UpdateHistoryEntries => Set<UpdateHistoryEntry>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobUpdate> JobUpdates => Set<JobUpdate>();

    /// <summary>SQL list of statuses covered by the duplicate-job guard (used in a partial unique index).</summary>
    internal static readonly string DedupeStatusSql =
        string.Join(", ", JobTransitions.DedupeStatuses.Select(s => $"'{s}'"));

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Store enums as readable strings.
        configurationBuilder.Properties<UserRole>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<EndpointStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<EndpointUpdateState>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<ComplianceStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<OsSupportStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<EsuOverride>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<PolicyScope>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<EnrollmentRequestStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<ScanStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<EventSeverity>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<UpdateCategory>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<JobType>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<JobStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<JobTrigger>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<JobItemStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<ScanTrigger>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureIdentity(modelBuilder);
        ConfigureTenancy(modelBuilder);
        ConfigureEndpoints(modelBuilder);
        ConfigureUpdates(modelBuilder);
        ConfigureJobs(modelBuilder);
        Seed(modelBuilder);
        ApplySnakeCaseNames(modelBuilder);
    }

    private static void ConfigureIdentity(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.Property(x => x.Username).HasMaxLength(64);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.PasswordHash).HasMaxLength(256);
            e.HasIndex(x => x.Username).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
            e.HasMany(x => x.OrganizationAccess).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<UserSession>(e =>
        {
            e.ToTable("user_sessions");
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(512);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<UserOrganizationAccess>(e =>
        {
            e.ToTable("user_organization_access");
            e.HasKey(x => new { x.UserId, x.OrganizationId });
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<PasswordResetToken>(e =>
        {
            e.ToTable("password_reset_tokens");
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.Property(x => x.Action).HasMaxLength(128);
            e.Property(x => x.Username).HasMaxLength(64);
            e.Property(x => x.TargetType).HasMaxLength(64);
            e.Property(x => x.TargetId).HasMaxLength(128);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.Details).HasColumnType("jsonb");
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            e.HasIndex(x => x.UserId);
        });
    }

    private static void ConfigureTenancy(ModelBuilder b)
    {
        b.Entity<Organization>(e =>
        {
            e.ToTable("organizations");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.ContactName).HasMaxLength(200);
            e.Property(x => x.ContactEmail).HasMaxLength(256);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasMany(x => x.Sites).WithOne(x => x.Organization).HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Site>(e =>
        {
            e.ToTable("sites");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Address).HasMaxLength(500);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
        });
        b.Entity<Policy>(e =>
        {
            e.ToTable("policies");
            e.HasIndex(x => x.Scope).IsUnique().HasFilter("scope = 'Global'");
            e.HasIndex(x => x.OrganizationId).IsUnique().HasFilter("scope = 'Organization'");
            e.HasIndex(x => x.SiteId).IsUnique().HasFilter("scope = 'Site'");
            e.HasIndex(x => x.EndpointId).IsUnique().HasFilter("scope = 'Endpoint'");
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Endpoint>().WithMany().HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Cascade);
            Json(e.Property(x => x.RestartSchedule));
            Json(e.Property(x => x.WindowsUpdate));
        });
        b.Entity<ServerSettingsRow>(e =>
        {
            e.ToTable("server_settings");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Data).HasColumnType("jsonb");
        });
        b.Entity<OsLifecycleEntry>(e =>
        {
            e.ToTable("os_lifecycle_entries");
            e.Property(x => x.Product).HasMaxLength(64);
            e.Property(x => x.EditionGroup).HasMaxLength(64);
            e.Property(x => x.VersionLabel).HasMaxLength(64);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasIndex(x => new { x.Product, x.EditionGroup, x.BuildNumber }).IsUnique();
        });
        b.Entity<ComplianceSnapshot>(e =>
        {
            e.ToTable("compliance_snapshots");
            e.HasIndex(x => new { x.OrganizationId, x.Date }).IsUnique();
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureEndpoints(ModelBuilder b)
    {
        b.Entity<Endpoint>(e =>
        {
            e.ToTable("endpoints");
            e.Property(x => x.Hostname).HasMaxLength(255);
            foreach (var name in new[]
                     {
                         nameof(Endpoint.DnsDomain), nameof(Endpoint.OperatingSystem), nameof(Endpoint.OsEdition),
                         nameof(Endpoint.OsVersion), nameof(Endpoint.OsBuild), nameof(Endpoint.OsProductType),
                         nameof(Endpoint.Architecture), nameof(Endpoint.SerialNumber), nameof(Endpoint.Manufacturer),
                         nameof(Endpoint.Model), nameof(Endpoint.SmbiosUuid), nameof(Endpoint.ProcessorName),
                         nameof(Endpoint.TimeZoneId), nameof(Endpoint.AgentVersion), nameof(Endpoint.LastSeenIp),
                         nameof(Endpoint.CurrentActivity), nameof(Endpoint.UpdateSourceType), nameof(Endpoint.UpdateSourceServer),
                         nameof(Endpoint.TargetReleaseVersion), nameof(Endpoint.AutomaticUpdatesMode),
                         nameof(Endpoint.WindowsUpdateServiceStartMode), nameof(Endpoint.WindowsUpdateServiceState),
                     })
            {
                e.Property(name).HasMaxLength(255);
            }

            e.Property(x => x.LastScanError).HasMaxLength(2000);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.Property(x => x.WindowsUpdatePolicyStatus).HasColumnType("jsonb");
            e.Property(x => x.AgentCapabilities).HasDefaultValueSql("'{}'::text[]");
            e.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.OrganizationId);
            e.HasIndex(x => x.SiteId);
            e.HasIndex(x => x.Hostname);
            e.HasIndex(x => x.LastCheckInAt);
            e.HasIndex(x => x.ComplianceStatus);
            e.HasIndex(x => new { x.OrganizationId, x.SmbiosUuid });
        });
        b.Entity<AgentCredential>(e =>
        {
            e.ToTable("agent_credentials");
            e.Property(x => x.CredentialId).HasMaxLength(64);
            e.Property(x => x.Algorithm).HasMaxLength(32);
            e.Property(x => x.KeyFingerprint).HasMaxLength(32);
            e.Property(x => x.RevokedReason).HasMaxLength(500);
            e.HasIndex(x => x.CredentialId).IsUnique();
            e.HasIndex(x => x.EndpointId);
            e.HasIndex(x => x.KeyFingerprint);
            e.HasOne(x => x.Endpoint).WithMany().HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<EnrollmentToken>(e =>
        {
            e.ToTable("enrollment_tokens");
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.TokenPrefix).HasMaxLength(16);
            e.Property(x => x.Description).HasMaxLength(500);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.OrganizationId);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<DeploymentCode>(e =>
        {
            e.ToTable("deployment_codes");
            e.Property(x => x.Code).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => x.OrganizationId);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<EnrollmentRequest>(e =>
        {
            e.ToTable("enrollment_requests");
            e.Property(x => x.RequestId).HasMaxLength(64);
            e.Property(x => x.KeyFingerprint).HasMaxLength(32);
            e.Property(x => x.Hostname).HasMaxLength(255);
            e.Property(x => x.SerialNumber).HasMaxLength(255);
            e.Property(x => x.OsCaption).HasMaxLength(255);
            e.Property(x => x.AgentVersion).HasMaxLength(64);
            e.Property(x => x.SourceIp).HasMaxLength(64);
            e.Property(x => x.RejectReason).HasMaxLength(500);
            e.Property(x => x.DeviceJson).HasColumnType("jsonb");
            e.HasIndex(x => x.RequestId).IsUnique();
            e.HasIndex(x => new { x.DeploymentCodeId, x.Status });
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.KeyFingerprint);
            e.HasOne<DeploymentCode>().WithMany().HasForeignKey(x => x.DeploymentCodeId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<EndpointEvent>(e =>
        {
            e.ToTable("endpoint_events");
            e.Property(x => x.Type).HasMaxLength(64);
            e.Property(x => x.Message).HasMaxLength(2000);
            e.Property(x => x.Details).HasColumnType("jsonb");
            e.HasIndex(x => new { x.EndpointId, x.CreatedAt });
            e.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            e.HasIndex(x => x.CreatedAt);
            e.HasOne<Endpoint>().WithMany().HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureUpdates(ModelBuilder b)
    {
        b.Entity<Update>(e =>
        {
            e.ToTable("updates");
            e.Property(x => x.Title).HasMaxLength(1000);
            e.Property(x => x.Description).HasMaxLength(8000);
            e.Property(x => x.MsClassification).HasMaxLength(128);
            e.Property(x => x.MsrcSeverity).HasMaxLength(32);
            e.Property(x => x.SupportUrl).HasMaxLength(1000);
            e.HasIndex(x => new { x.UpdateGuid, x.RevisionNumber }).IsUnique();
            e.HasIndex(x => x.Category);
            e.Ignore(x => x.PrimaryKb);
        });
        b.Entity<EndpointUpdate>(e =>
        {
            e.ToTable("endpoint_updates");
            e.Property(x => x.ErrorCode).HasMaxLength(32);
            e.Property(x => x.ErrorMessage).HasMaxLength(2000);
            e.HasIndex(x => new { x.EndpointId, x.UpdateGuid }).IsUnique();
            e.HasIndex(x => new { x.EndpointId, x.State });
            e.HasIndex(x => new { x.UpdateGuid, x.State });
            e.HasOne(x => x.Update).WithMany().HasForeignKey(x => x.UpdateId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Endpoint>().WithMany().HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<UpdateExclusion>(e =>
        {
            e.ToTable("update_exclusions");
            e.Property(x => x.Reason).HasMaxLength(500);
            e.HasIndex(x => new { x.EndpointId, x.UpdateGuid }).IsUnique();
            e.HasOne<Endpoint>().WithMany().HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<UpdateScan>(e =>
        {
            e.ToTable("update_scans");
            e.Property(x => x.ErrorCode).HasMaxLength(32);
            e.Property(x => x.ErrorMessage).HasMaxLength(2000);
            e.Property(x => x.UpdateSource).HasMaxLength(500);
            e.HasIndex(x => new { x.EndpointId, x.AgentScanId }).IsUnique();
            e.HasIndex(x => new { x.EndpointId, x.ReceivedAt });
            e.HasIndex(x => x.ReceivedAt);
            e.HasOne<Endpoint>().WithMany().HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<UpdateHistoryEntry>(e =>
        {
            e.ToTable("update_history_entries");
            e.Property(x => x.Title).HasMaxLength(1000);
            e.Property(x => x.KbNumber).HasMaxLength(16);
            e.Property(x => x.Operation).HasMaxLength(32);
            e.Property(x => x.HResult).HasMaxLength(16);
            e.Property(x => x.ClientApplicationId).HasMaxLength(200);
            e.Property(x => x.Fingerprint).HasMaxLength(64);
            e.HasIndex(x => new { x.EndpointId, x.Fingerprint }).IsUnique();
            e.HasIndex(x => new { x.EndpointId, x.Date });
            e.HasOne<Endpoint>().WithMany().HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureJobs(ModelBuilder b)
    {
        b.Entity<Job>(e =>
        {
            e.ToTable("jobs");
            e.Property(x => x.RequestedBy).HasMaxLength(128);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.Result).HasColumnType("jsonb");
            e.Property(x => x.Phase).HasMaxLength(64);
            e.Property(x => x.ProgressMessage).HasMaxLength(1000);
            e.Property(x => x.ErrorCode).HasMaxLength(64);
            e.Property(x => x.ErrorMessage).HasMaxLength(4000);
            e.Property(x => x.DedupeKey).HasMaxLength(128);
            e.Property(x => x.Version).IsRowVersion();
            e.HasOne(x => x.Endpoint).WithMany().HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Updates).WithOne().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.EndpointId, x.Status });
            e.HasIndex(x => new { x.Status, x.ExpiresAt });
            e.HasIndex(x => new { x.Status, x.LeaseExpiresAt });
            e.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.EndpointId, x.DedupeKey })
                .IsUnique()
                .HasDatabaseName("ux_jobs_active_dedupe")
                .HasFilter($"dedupe_key IS NOT NULL AND status IN ({DedupeStatusSql})");
        });
        b.Entity<JobUpdate>(e =>
        {
            e.ToTable("job_updates");
            e.Property(x => x.Title).HasMaxLength(1000);
            e.Property(x => x.KbNumber).HasMaxLength(16);
            e.Property(x => x.SkipReason).HasMaxLength(64);
            e.Property(x => x.ErrorCode).HasMaxLength(32);
            e.Property(x => x.ErrorMessage).HasMaxLength(2000);
            e.HasIndex(x => new { x.JobId, x.UpdateGuid }).IsUnique();
            e.HasIndex(x => x.UpdateGuid);
            e.HasIndex(x => x.CompletedAt);
        });
    }

    /// <summary>Maps a settings record to a jsonb column (serialized with the agent protocol's JSON options).</summary>
    private static void Json<T>(PropertyBuilder<T?> property)
        where T : class
    {
        property.HasColumnType("jsonb").HasConversion(
            v => JsonSerializer.Serialize(v, ContractJson.Options),
            v => JsonSerializer.Deserialize<T>(v, ContractJson.Options),
            new ValueComparer<T?>(
                (a, b) => JsonSerializer.Serialize(a, ContractJson.Options) == JsonSerializer.Serialize(b, ContractJson.Options),
                v => v == null ? 0 : JsonSerializer.Serialize(v, ContractJson.Options).GetHashCode(StringComparison.Ordinal),
                v => v == null ? null : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, ContractJson.Options), ContractJson.Options)));
    }

    private static void Seed(ModelBuilder b)
    {
        var seedTime = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        b.Entity<ServerSettingsRow>().HasData(new ServerSettingsRow { Id = 1, Data = "{}", UpdatedAt = seedTime });
        b.Entity<OsLifecycleEntry>().HasData(LifecycleEvaluator.SeedEntries());
    }

    private static void ApplySnakeCaseNames(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.Name == nameof(Job.Version) && entity.ClrType == typeof(Job))
                {
                    continue; // mapped to the xmin system column by Npgsql
                }

                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    internal static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var prevLower = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                var nextLower = i + 1 < name.Length && char.IsLower(name[i + 1]) && i > 0 && char.IsUpper(name[i - 1]);
                if (i > 0 && (prevLower || nextLower))
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
