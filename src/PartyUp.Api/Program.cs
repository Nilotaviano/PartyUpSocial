using System.Globalization;
using System.Net;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Api;
using PartyUp.Api.Auth;
using PartyUp.Api.Endpoints;
using PartyUp.Api.ErrorHandling;
using PartyUp.Api.Hosting;
using PartyUp.Api.Logging;
using PartyUp.Api.Security;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Application.Admin;
using PartyUp.Application.Deletion;
using PartyUp.Application.Friends;
using PartyUp.Application.Onboarding;
using PartyUp.Application.Profile;
using PartyUp.Application.Sessions;
using PartyUp.Infrastructure;
using PartyUp.Infrastructure.Email;
using PartyUp.Infrastructure.Jobs;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Push;
using PartyUp.Infrastructure.Steam;
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

builder.Services.AddSingleton(new LoggingEmailSenderEnvironment(
    builder.Environment.IsDevelopment(),
    builder.Environment.ContentRootPath));
builder.Services.AddSingleton(new LoggingPushSenderEnvironment(
    builder.Environment.IsDevelopment(),
    builder.Environment.ContentRootPath));
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSingleton<ISteamOpenIdVerifier>(services =>
    services.GetRequiredService<SteamOpenIdVerifier>());
builder.Services.AddHostedService<EmailDispatchHostedService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IClock>(NodaTime.SystemClock.Instance);
builder.Services.AddScoped<CurrentUserAccessor>();
builder.Services.AddScoped<ICurrentUserAccessor>(services =>
    services.GetRequiredService<CurrentUserAccessor>());
builder.Services.AddScoped<PostAuthenticationGateUseCase>();
builder.Services.AddScoped<RegisterWithPasswordUseCase>();
builder.Services.AddScoped<VerifyEmailUseCase>();
builder.Services.AddScoped<SignInWithPasswordUseCase>();
builder.Services.AddScoped<RequestMagicLinkUseCase>();
builder.Services.AddScoped<ConsumeMagicLinkUseCase>();
builder.Services.AddScoped<RequestPasswordResetUseCase>();
builder.Services.AddScoped<ResetPasswordUseCase>();
builder.Services.AddScoped<ChangePasswordUseCase>();
builder.Services.AddScoped<SetPasswordUseCase>();
builder.Services.AddScoped<ChangeEmailUseCase>();
builder.Services.AddScoped<ReauthenticateUseCase>();
builder.Services.AddScoped<IssueExternalIntentUseCase>();
builder.Services.AddScoped<CompleteExternalSignInUseCase>();
builder.Services.AddScoped<UnlinkSteamUseCase>();
builder.Services.AddScoped<ListAuthMethodsUseCase>();
builder.Services.AddScoped<UnlinkAuthMethodUseCase>();
builder.Services.AddScoped<LinkPlatformUseCase>();
builder.Services.AddScoped<ChangePlatformHandleUseCase>();
builder.Services.AddScoped<UnlinkPlatformUseCase>();
builder.Services.AddScoped<GetMyProfileUseCase>();
builder.Services.AddScoped<ChangeUsernameUseCase>();
builder.Services.AddScoped<ChangeDisplayNameUseCase>();
builder.Services.AddScoped<ChangeAvatarUseCase>();
builder.Services.AddScoped<ChangeTimeZoneUseCase>();
builder.Services.AddScoped<ChangeLocaleUseCase>();
builder.Services.AddScoped<SetReminderDefaultUseCase>();
builder.Services.AddScoped<SetEmailNotificationsUseCase>();
builder.Services.AddScoped<UnsubscribeEmailNotificationsUseCase>();
builder.Services.AddScoped<CompleteTourUseCase>();
builder.Services.AddScoped<RequestAccountDeletionUseCase>();
builder.Services.AddScoped<RequestPublicDeletionUseCase>();
builder.Services.AddScoped<IssueSteamDeletionConfirmationUseCase>();
builder.Services.AddScoped<ConfirmPublicDeletionUseCase>();
builder.Services.AddScoped<ExecuteDueDeletionsUseCase>();
builder.Services.AddScoped<PurgeExpiredAuthTokensUseCase>();
builder.Services.AddScoped<ListUsersUseCase>();
builder.Services.AddScoped<GetUserUseCase>();
builder.Services.AddScoped<GetUsageDashboardUseCase>();
builder.Services.AddScoped<BanUserUseCase>();
builder.Services.AddScoped<BanAndRemoveContentUseCase>();
builder.Services.AddScoped<UnbanUserUseCase>();
builder.Services.AddScoped<ReadDeletionJournalUseCase>();
builder.Services.AddScoped<GetOnboardingStateUseCase>();
builder.Services.AddScoped<CheckUsernameAvailabilityUseCase>();
builder.Services.AddScoped<ValidateProfileDraftUseCase>();
builder.Services.AddScoped<CompleteOnboardingUseCase>();
builder.Services.AddScoped<GetFriendShareUseCase>();
builder.Services.AddScoped<GetProfileUseCase>();
builder.Services.AddScoped<ResolveFriendCodeUseCase>();
builder.Services.AddScoped<ListFriendsUseCase>();
builder.Services.AddScoped<ListFriendRequestsUseCase>();
builder.Services.AddScoped<ListBlockedUsersUseCase>();
builder.Services.AddSingleton<ExternalAuthOriginOptions>();
builder.Services.AddSingleton(new AccountEmailOptions(
    builder.Configuration["PublicBaseUrl"] ?? "http://localhost:5173"));
TimeSpan tokenRetention = ReadTimeSpan(
    builder.Configuration,
    "Auth:TokenRetention",
    "30.00:00:00");
builder.Services.AddSingleton(new AuthTokenRetentionOptions(
    Duration.FromTimeSpan(tokenRetention)));
builder.Services.AddSingleton(new DeletionSweepOptions(
    builder.Configuration.GetValue("Deletion:SweepEnabled", true),
    ReadTimeSpan(builder.Configuration, "Deletion:SweepInterval", "00:15:00"),
    Math.Max(1, builder.Configuration.GetValue("Deletion:SweepBatchSize", 50))));
builder.Services.AddSingleton(new AvatarRefreshOptions(
    Math.Max(1, builder.Configuration.GetValue("Avatars:RefreshBatchSize", 200))));
builder.Services.AddSingleton<ScopedDeletionUserExecutor>();
builder.Services.AddSingleton<IDeletionUserExecutor>(services =>
    services.GetRequiredService<ScopedDeletionUserExecutor>());
builder.Services.AddSingleton<DeletionSweepHostedService>();
builder.Services.AddSingleton<IDeletionExecutionScheduler>(services =>
    services.GetRequiredService<DeletionSweepHostedService>());
builder.Services.AddHostedService(services =>
    services.GetRequiredService<DeletionSweepHostedService>());
builder.Services.AddSingleton<AdminSeeder>();
builder.Services.AddHostedService(services =>
    services.GetRequiredService<AdminSeeder>());

// PHASE4_DESIGN.md §7.2: Hangfire.PostgreSql owns its own `hangfire` schema, created at storage
// init, outside EF and outside the §4.3 round-trip gate — no EF migration ever references it. The
// connection string is resolved from the service provider, not from `builder.Configuration`
// directly, for the same reason the health check below does: WebApplicationFactory layers its
// configuration in only after service registration has run. The dashboard is never mapped, so it
// stays unreachable regardless of environment.
builder.Services.AddHangfire((services, config) => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(
        options => options.UseNpgsqlConnection(
            services.GetRequiredService<IConfiguration>().GetConnectionString("Db")),
        new PostgreSqlStorageOptions { SchemaName = "hangfire" }));

// The "Testing" environment (WebApplicationFactory) never runs the actual worker, and never
// registers the recurring rows either: S3's tests exercise SessionDueSweepJob/SessionReminderDueJob
// directly through DI — the same pattern every other Phase 4 use-case test already uses — instead of
// racing a real one-minute cadence against the test's own transactions. Recurring-row registration
// happens in RecurringJobRegistrationService, a BackgroundService that retries with backoff instead
// of dialing Hangfire storage synchronously on the startup path (that would make every non-"Testing"
// host build, including WebApplicationFactory-based integration tests, fail whenever storage isn't
// reachable yet).
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHangfireServer();
    builder.Services.AddHostedService<RecurringJobRegistrationService>();
}

// PHASE7_DESIGN.md §6: the browser lane's spawned-process trigger seam. Registered only when
// Push:DispatchIntervalSeconds is set (the Playwright fixture passes it at 2s); production never sets
// it and keeps the minutely Hangfire cadence above as the only one.
int? pushDispatchIntervalSeconds = builder.Configuration.GetValue<int?>("Push:DispatchIntervalSeconds");
if (pushDispatchIntervalSeconds is { } intervalSeconds && intervalSeconds > 0)
{
    builder.Services.AddHostedService(services => new PushDispatchTriggerService(
        services.GetRequiredService<IServiceScopeFactory>(),
        TimeSpan.FromSeconds(intervalSeconds),
        services.GetRequiredService<ILogger<PushDispatchTriggerService>>()));
}
builder.Services.AddScoped<AuthenticateAndSignIn>();
builder.Services.AddPartyUpCookieAuthentication(builder.Environment);
builder.Services.AddPartyUpGoogleAuthentication(
    builder.Configuration,
    builder.Environment);
builder.Services.AddPartyUpAuthorization();
builder.Services.AddSingleton<ProviderCapabilities>();
builder.Services.AddExceptionHandler<RequestBindingExceptionMapper>();
builder.Services.AddExceptionHandler<DomainExceptionMapper>();
builder.Services.AddExceptionHandler<TransientConflictExceptionMapper>();
builder.Services.AddExceptionHandler<InternalExceptionMapper>();
builder.Services.AddProblemDetails();
// Outside Development, binding failures short-circuit an empty 400 that never
// reaches the exception handlers; throwing keeps the JSON problem shape uniform.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "pu.csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});
builder.Services.AddSingleton<GuestLinkCredentialProtector>();
builder.Services.AddSingleton<PollGuestLinkCredentialProtector>();
builder.Services.AddScoped<ISessionVersionStamp, SessionVersionStamp>();
builder.Services.AddSingleton<RateLimitPartitionPepper>();
// register-ip's permit count, overridable for the browser test lane only
// (PlaywrightFixture.cs sets RateLimits__RegisterPerIpPerHour in the spawned API process's own
// environment); unset in every production deployment, so this stays exactly 5. The fallback is
// the literal 5, not the property's current value — RateLimitPolicies is a static class shared
// across every in-process WebApplicationFactory<Program> an integration-test run creates, so a
// self-referential fallback would let one factory's override leak into the next factory's default.
RateLimitPolicies.RegisterIpPermitLimit = builder.Configuration.GetValue("RateLimits:RegisterPerIpPerHour", 5);
builder.Services.AddRateLimiter(_ => { });
builder.Services.AddOptions<RateLimiterOptions>()
    .Configure<RateLimitPartitionPepper>((options, pepper) =>
    {
        options.GlobalLimiter = RateLimitPolicies.CreateGlobalLimiter(pepper.Value);
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = RateLimitPolicies.OnRejectedAsync;
    });
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();

    foreach (string trustedProxy in
             builder.Configuration.GetSection("Network:TrustedProxies").Get<string[]>() ?? [])
    {
        if (trustedProxy.Contains('/', StringComparison.Ordinal))
        {
            if (!System.Net.IPNetwork.TryParse(trustedProxy, out System.Net.IPNetwork network))
            {
                throw new InvalidOperationException(
                    $"Network:TrustedProxies contains invalid CIDR '{trustedProxy}'.");
            }

            options.KnownIPNetworks.Add(network);
        }
        else if (IPAddress.TryParse(trustedProxy, out IPAddress? address))
        {
            options.KnownProxies.Add(address);
        }
        else
        {
            throw new InvalidOperationException(
                $"Network:TrustedProxies contains invalid address '{trustedProxy}'.");
        }
    }
});
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<PartyUpOpenApiTransformer>();
    options.AddOperationTransformer<PartyUpOpenApiTransformer>();
});

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
_ = app.Services.GetRequiredService<ExternalAuthOriginOptions>();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

app.UseForwardedHeaders();
// PHASE4_DESIGN.md §9.2: a global response policy sets Referrer-Policy: no-referrer everywhere; the
// A-064 exchange route also sets it explicitly (belt-and-suspenders on the one route the token
// itself lives in the URL for).
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        return Task.CompletedTask;
    });
    await next(context);
});
app.UseExceptionHandler();
app.UseSerilogRequestLogging(options =>
{
    options.IncludeQueryInRequestPath = false;
    options.GetMessageTemplateProperties =
        FriendPathLogSanitizer.MessageTemplateProperties;
    options.EnrichDiagnosticContext = (diagnosticContext, context) =>
        diagnosticContext.Set(
            "RequestPath",
            FriendPathLogSanitizer.Sanitize(context.Request.Path));
});

app.UseRouting();
app.UseAuthentication();
app.UseRateLimitEmailPartitioning();
app.UseWhen(
    context => context.Request.Path.StartsWithSegments(ApiRoutes.V1) ||
        context.Request.Path.StartsWithSegments("/g") ||
        context.Request.Path.StartsWithSegments("/a"),
    branch => branch.UseRateLimiter());
app.UseAuthorization();
app.UseMiddleware<RequestContentTypeMiddleware>();
app.UseMiddleware<LastSeenTouchMiddleware>();

// /health/live is process-only (no registered checks run) so the proxy/container layer can tell
// a dead app from a reachable app with an unhealthy dependency; /health includes Postgres.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health");
app.MapOpenApi();

var api = app.MapGroup(ApiRoutes.V1);
api.AddEndpointFilter<ProblemAntiforgeryFilter>();
api.MapAuthEndpoints();
api.MapExternalAuthEndpoints(
    !string.IsNullOrWhiteSpace(app.Configuration["Google:ClientId"]),
    app.Configuration.GetValue("Steam:Enabled", true),
    app.Environment);
api.MapAccountDeletionEndpoints(
    app.Configuration.GetValue("Steam:Enabled", true),
    app.Environment);
api.MapOnboardingEndpoints();
api.MapMeEndpoints();
api.MapFriendEndpoints();
api.MapAdminMaintenanceEndpoints();
api.MapSessionEndpoints();
api.MapGuestSessionEndpoints();
api.MapAvailabilityEndpoints();
api.MapCalendarEndpoints();
api.MapPollEndpoints();
api.MapGuestPollEndpoints();
api.MapNotificationEndpoints();
api.MapEmailNotificationsEndpoints();
api.MapSteamCatalogEndpoints();

// PHASE4_DESIGN.md P4-HTTP-01: A-064/A-065 are ratified in ARCHITECTURE.md before this route is
// mapped. It sits outside the /api/v1 group by design (A-064: a transport exception, not a REST
// resource) and is therefore excluded from /openapi/v1.json.
app.MapGuestExchangeEndpoints();

// AVATARS_DESIGN.md §3.3: the picture proxy, the same A-064 exception as the guest exchange route
// above -- outside /api/v1 and outside /openapi/v1.json.
app.MapAvatarProxyEndpoints();

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
    }).WithName("Slice2SignIn");
    api.MapGet("/_slice2/authenticated", () => Results.NoContent())
        .WithName("Slice2Authenticated")
        .RequireAuthorization(AuthorizationPolicies.RequireAuthenticated);
    api.MapGet("/_slice2/onboarded", () => Results.NoContent())
        .WithName("Slice2Onboarded")
        .RequireAuthorization(AuthorizationPolicies.RequireOnboarded);
    api.MapPost("/_slice2/sign-out", async (HttpContext context) =>
    {
        await context.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Results.NoContent();
    }).WithName("Slice2SignOut");
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

static TimeSpan ReadTimeSpan(
    IConfiguration configuration,
    string key,
    string fallback)
{
    string configured = configuration[key] ?? fallback;
    if (!TimeSpan.TryParse(
            configured,
            CultureInfo.InvariantCulture,
            out TimeSpan value) ||
        value < TimeSpan.Zero)
    {
        throw new InvalidOperationException($"Configuration value '{key}' is not a non-negative duration.");
    }

    return value;
}

public partial class Program;
