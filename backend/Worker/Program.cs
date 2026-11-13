using EasyPatch.Infrastructure;
using EasyPatch.Infrastructure.Data;

// Background worker: job lease recovery and expiry, presence (online/offline), maintenance-window scheduling,
// compliance snapshots and retention. Each task runs under a PostgreSQL advisory lock.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);
if (!builder.Environment.IsDevelopment() && builder.Configuration["Logging:Format"] != "text")
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);
}

builder.Services.AddEasyPatchCore(builder.Configuration);
builder.Services.AddEasyPatchMaintenance();
builder.Services.AddHealthChecks().AddDbContextCheck<EasyPatchDbContext>("database");

var app = builder.Build();
app.MapHealthChecks("/health");
await app.RunAsync();

/// <summary>Entry point marker for tests.</summary>
public partial class Program;
