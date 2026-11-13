using EasyPatch.Infrastructure.Data;
using EasyPatch.Infrastructure.Maintenance;
using EasyPatch.Infrastructure.Security;
using EasyPatch.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EasyPatch.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers the database, domain services and shared singletons used by both the API and the worker.</summary>
    public static IServiceCollection AddEasyPatchCore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = DatabaseConnection.Build(configuration);
        services.AddDbContext<EasyPatchDbContext>(o => o.UseNpgsql(connectionString, n =>
        {
            n.EnableRetryOnFailure(0); // explicit transactions are used; retries are handled by callers
            n.CommandTimeout(60);
        }));

        services.AddOptions<EasyPatchOptions>().Bind(configuration.GetSection(EasyPatchOptions.Section));
        services.AddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();

        services.AddScoped<SettingsService>();
        services.AddScoped<PolicyService>();
        services.AddScoped<AuditService>();
        services.AddScoped<ComplianceService>();
        services.AddScoped<ScanIngestionService>();
        services.AddScoped<InstallPreviewService>();
        services.AddScoped<JobService>();
        services.AddScoped<EnrollmentService>();
        services.AddScoped<CheckInService>();

        services.AddSingleton<CredentialCache>();
        services.AddSingleton<NonceStore>();
        services.AddSingleton<PresenceTracker>();
        services.AddSingleton<AgentPackageService>();
        return services;
    }

    /// <summary>Data protection keys encrypt TOTP secrets at rest. They are stored on a volume separate from the
    /// database so that a database dump alone does not expose MFA secrets.</summary>
    public static IServiceCollection AddEasyPatchDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var keysPath = configuration["EasyPatch:DataProtectionKeysPath"] ?? "/app/keys";
        services.AddDataProtection()
            .SetApplicationName("EasyPatch")
            .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        return services;
    }

    /// <summary>Registers the worker's periodic maintenance tasks and their hosted loops.</summary>
    public static IServiceCollection AddEasyPatchMaintenance(this IServiceCollection services)
    {
        services.AddScoped<IActorContext, SystemActorContext>();
        services.AddScoped<ITenantContext, SystemTenantContext>();
        services.AddScoped<JobMaintenanceTask>();
        services.AddScoped<PresenceMonitorTask>();
        services.AddScoped<ScheduledInstallTask>();
        services.AddScoped<ComplianceSnapshotTask>();
        services.AddScoped<RetentionTask>();
        services.AddHostedService<MaintenanceLoop<JobMaintenanceTask>>();
        services.AddHostedService<MaintenanceLoop<PresenceMonitorTask>>();
        services.AddHostedService<MaintenanceLoop<ScheduledInstallTask>>();
        services.AddHostedService<MaintenanceLoop<ComplianceSnapshotTask>>();
        services.AddHostedService<MaintenanceLoop<RetentionTask>>();
        return services;
    }
}

/// <summary>Builds the PostgreSQL connection string. The password is read from a Docker secret file when
/// <c>Database:PasswordFile</c> is set, so it never appears in environment variables or image layers.</summary>
public static class DatabaseConnection
{
    public static string Build(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Database");
        var explicitConnection = configuration.GetConnectionString("EasyPatch");
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(explicitConnection ?? string.Empty);
        if (explicitConnection is null)
        {
            builder.Host = section["Host"] ?? "postgres";
            builder.Port = int.TryParse(section["Port"], out var port) ? port : 5432;
            builder.Database = section["Name"] ?? "easypatch";
            builder.Username = section["User"] ?? "easypatch";
        }

        var passwordFile = section["PasswordFile"];
        if (!string.IsNullOrEmpty(passwordFile))
        {
            builder.Password = File.ReadAllText(passwordFile).Trim();
        }
        else if (section["Password"] is { Length: > 0 } password)
        {
            builder.Password = password;
        }

        builder.MaxPoolSize = int.TryParse(section["MaxPoolSize"], out var pool) ? pool : 50;
        builder.ApplicationName = configuration["EasyPatch:ApplicationName"] ?? "easypatch";
        builder.IncludeErrorDetail = false;
        return builder.ConnectionString;
    }
}
