using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Api;
using PartyUp.Api.Auth;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
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
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IClock>(NodaTime.SystemClock.Instance);
builder.Services.AddScoped<CurrentUserAccessor>();
builder.Services.AddScoped<ICurrentUserAccessor>(services =>
    services.GetRequiredService<CurrentUserAccessor>());
builder.Services.AddScoped<PostAuthenticationGateUseCase>();
builder.Services.AddScoped<AuthenticateAndSignIn>();
builder.Services.AddPartyUpCookieAuthentication(builder.Environment);
builder.Services.AddPartyUpAuthorization();
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

app.UseSerilogRequestLogging(options =>
    options.IncludeQueryInRequestPath = false);

app.UseAuthentication();
app.UseAuthorization();

// /health/live is process-only (no registered checks run) so the proxy/container layer can tell
// a dead app from a reachable app with an unhealthy dependency; /health includes Postgres.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health");
app.MapOpenApi();

var api = app.MapGroup(ApiRoutes.V1);

if (app.Environment.IsEnvironment("Testing"))
{
    api.MapPost("/_slice2/sign-in/{userId:guid}", async (
        Guid userId,
        AuthenticateAndSignIn signIn,
        CancellationToken cancellationToken) =>
    {
        PostAuthenticationResult result = await signIn.ExecuteAsync(
            PartyUp.Domain.Kernel.UserId.From(userId),
            cancellationToken);
        return result is PostAuthenticationResult.Admitted
            ? Results.NoContent()
            : Results.Unauthorized();
    });
    api.MapGet("/_slice2/authenticated", () => Results.NoContent())
        .RequireAuthorization(AuthorizationPolicies.RequireAuthenticated);
    api.MapGet("/_slice2/onboarded", () => Results.NoContent())
        .RequireAuthorization(AuthorizationPolicies.RequireOnboarded);
    api.MapPost("/_slice2/sign-out", async (HttpContext context) =>
    {
        await context.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Results.NoContent();
    });
}

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
