using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using PartyUp.Api;
using PartyUp.Infrastructure;
using PartyUp.Infrastructure.Persistence;
using Serilog;
using Serilog.Formatting.Compact;

const string DataProtectionKeyPath = "/data/dataprotection";

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((services, logger) =>
{
    logger
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext();

    if (builder.Environment.IsProduction())
    {
        logger.WriteTo.Console(new CompactJsonFormatter());
    }
    else
    {
        logger.WriteTo.Console(formatProvider: CultureInfo.InvariantCulture);
    }
});

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddOpenApi();

// The connection string is read from the service provider instead of being captured here, because
// WebApplicationFactory layers its configuration in only after service registration has run.
builder.Services.AddHealthChecks()
    .AddNpgSql(services => services.GetRequiredService<IConfiguration>().GetConnectionString("Db")!);

if (Directory.Exists(DataProtectionKeyPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(DataProtectionKeyPath));
}

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

app.UseSerilogRequestLogging();

// /health/live is process-only (no registered checks run) so the proxy/container layer can tell
// a dead app from a reachable app with an unhealthy dependency; /health includes Postgres.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health");
app.MapOpenApi();

var api = app.MapGroup(ApiRoutes.V1);

// The SPA fallback below is a catch-all, so without an API-scoped fallback an unmatched
// /api/v1 route would answer with index.html and 200 instead of 404.
api.MapFallback(() => Results.NotFound());

if (Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot")))
{
    app.UseStaticFiles();
    app.MapFallbackToFile("index.html");
}

app.Run();

public partial class Program;
