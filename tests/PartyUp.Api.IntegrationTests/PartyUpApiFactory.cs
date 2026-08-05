using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NodaTime;
using PartyUp.Api.Auth;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Identity;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;
using Testcontainers.PostgreSql;
using System.Threading.Channels;

namespace PartyUp.Api.IntegrationTests;

internal sealed class PartyUpApiFactory : WebApplicationFactory<Program>
{
    private readonly bool _googleEnabled;
    private readonly bool? _steamEnabled;
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly CapturingEmailSender _emailSender = new();
    private readonly CountingPasswordHasher _passwordHasher = new();
    private readonly PostAuthenticationGateProbe _postAuthenticationGateProbe = new();

    // PHASE4_DESIGN.md §9.2: an ephemeral in-memory Data Protection key ring re-keys whenever the
    // host restarts inside this factory, which would make guest-cookie assertions flaky. Each factory
    // instance gets its own on-disk key directory instead, cleaned up on disposal.
    private readonly string _dataProtectionKeyPath =
        Path.Combine(Path.GetTempPath(), "partyup-dp-" + Guid.NewGuid().ToString("N"));

    internal MutableClock Clock { get; } =
        new(Instant.FromUtc(2026, 7, 31, 12, 0));

    internal CapturingEmailSender EmailSender => _emailSender;

    internal CountingPasswordHasher PasswordHasher => _passwordHasher;

    internal PostAuthenticationGateProbe PostAuthenticationGateProbe =>
        _postAuthenticationGateProbe;

    internal PartyUpApiFactory(bool googleEnabled = false, bool? steamEnabled = null)
    {
        _googleEnabled = googleEnabled;
        _steamEnabled = steamEnabled;
    }

    internal Task StartAsync() => _postgres.StartAsync();

    internal Task StopAsync() => _postgres.DisposeAsync().AsTask();

    internal async Task<UserId> SeedUserAsync(
        bool completeProfile = true,
        bool pendingDeletion = false,
        bool banned = false,
        bool admin = false,
        string? email = "player@example.test",
        bool emailConfirmed = true,
        string? password = null)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AuthRole>>();

        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create($"PU-{Guid.NewGuid():N}"[..9]),
            AgeGateSource.SelfAttested,
            Clock);

        if (completeProfile)
        {
            // A v7 UserId's leading hex chars are its millisecond timestamp: users seeded within the
            // same test run collide on it, silently, because nothing here enforced uniqueness. Draw
            // the suffix from fresh entropy instead so concurrently seeded users never collide.
            //
            // This deliberately stops short of mirroring ChangeUsernameUseCase's
            // UsernameRegistryEntry.ClaimNew insert: several existing Sessions test fixtures
            // (SessionAttachmentRaceTests/SessionBlockScopeTests/SessionJoinRequestInheritedAccessTests'
            // own EnsureDistinctUsernamesAsync helpers) already claim a registry row for a seeded user
            // via that exact pattern without first reserving this one, so an unconditional insert here
            // collides with theirs on ux_username_registry_owner_id_active. Those helpers are outside
            // this ticket's write set; entropy alone resolves the actual collision this fixes.
            string suffix = Guid.NewGuid().ToString("N")[..8];
            user.CompleteOnboarding(
                Username.Create($"user{suffix}"),
                DisplayName.Create("Test Player"),
                Avatar.FromEmoji("🙂"),
                DateTimeZoneProviders.Tzdb["America/Fortaleza"],
                Locale.Create("en"),
                Clock);
        }

        if (pendingDeletion)
        {
            user.RequestDeletion(Clock);
        }

        if (banned)
        {
            user.Ban(Clock);
        }

        context.DomainUsers.Add(user);
        await context.SaveChangesAsync();

        string identityName = user.Id.Value.ToString("D");
        var authUser = new AuthUser
        {
            Id = user.Id.Value,
            UserName = identityName,
            NormalizedUserName = identityName,
            Email = email,
            EmailConfirmed = email is not null && emailConfirmed,
            LockoutEnabled = true,
        };
        IdentityResult createResult = password is null
            ? await userManager.CreateAsync(authUser)
            : await userManager.CreateAsync(authUser, password);
        Assert.True(createResult.Succeeded);

        if (admin)
        {
            if (!await roleManager.RoleExistsAsync("admin"))
            {
                Assert.True((await roleManager.CreateAsync(new AuthRole("admin"))).Succeeded);
            }

            Assert.True((await userManager.AddToRoleAsync(authUser, "admin")).Succeeded);
        }

        return user.Id;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Db"] = _postgres.GetConnectionString(),
                ["Database:MigrateOnStartup"] = "true",
                ["Deletion:SweepEnabled"] = "false",
                ["Email:Provider"] = "log",
            };
            if (_googleEnabled)
            {
                settings["Google:ClientId"] = "test-google-client";
                settings["Google:ClientSecret"] = "test-google-secret";
            }

            if (_steamEnabled is { } steamEnabled)
            {
                settings["Steam:Enabled"] = steamEnabled.ToString();
            }

            configuration.AddInMemoryCollection(settings);
        });
        builder.ConfigureTestServices(services =>
        {
            if (_googleEnabled &&
                !services.Any(descriptor =>
                    descriptor.ServiceType == typeof(IConfigureOptions<GoogleOptions>)))
            {
                services.AddAuthentication()
                    .AddGoogle(
                        GoogleDefaults.AuthenticationScheme,
                        options => GoogleAuthSetup.ConfigureOptions(
                            options,
                            "test-google-client",
                            "test-google-secret",
                            isDevelopment: false));
            }

            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(_emailSender);
            services.RemoveAll<IPasswordHasher<AuthUser>>();
            services.AddSingleton<IPasswordHasher<AuthUser>>(_passwordHasher);
            services.RemoveAll<IUserRepository>();
            services.AddScoped<UserRepository>();
            services.AddScoped<IUserRepository>(provider =>
                new ProbingUserRepository(
                    provider.GetRequiredService<UserRepository>(),
                    _postAuthenticationGateProbe));

            Directory.CreateDirectory(_dataProtectionKeyPath);
            services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(_dataProtectionKeyPath));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try
            {
                if (Directory.Exists(_dataProtectionKeyPath))
                {
                    Directory.Delete(_dataProtectionKeyPath, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup: a locked handle on a temp directory is not worth failing a test run over.
            }
        }
    }
}

internal sealed class MutableClock(Instant current) : IClock
{
    public Instant GetCurrentInstant() => current;

    public void Advance(Duration duration) => current += duration;
}

internal sealed class CountingPasswordHasher : IPasswordHasher<AuthUser>
{
    private readonly PasswordHasher<AuthUser> _inner = new();
    private int _verifications;

    internal int Verifications => Volatile.Read(ref _verifications);

    public string HashPassword(AuthUser user, string password) =>
        _inner.HashPassword(user, password);

    public PasswordVerificationResult VerifyHashedPassword(
        AuthUser user,
        string hashedPassword,
        string providedPassword)
    {
        Interlocked.Increment(ref _verifications);
        return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}

internal sealed class PostAuthenticationGateProbe
{
    private int _entries;

    internal int Entries => Volatile.Read(ref _entries);

    internal void Reset() => Volatile.Write(ref _entries, 0);

    internal void RecordEntry() => Interlocked.Increment(ref _entries);
}

internal sealed class ProbingUserRepository(
    UserRepository inner,
    PostAuthenticationGateProbe probe) : IUserRepository
{
    public Task<User?> FindAsync(UserId id, CancellationToken cancellationToken) =>
        inner.FindAsync(id, cancellationToken);

    public Task<User?> FindForUpdateAsync(UserId id, CancellationToken cancellationToken)
    {
        probe.RecordEntry();
        return inner.FindForUpdateAsync(id, cancellationToken);
    }

    public Task<IReadOnlyList<UserId>> FindDueForDeletionAsync(
        Instant now,
        int max,
        CancellationToken cancellationToken) =>
        inner.FindDueForDeletionAsync(now, max, cancellationToken);

    public void Add(User user) => inner.Add(user);
}

internal sealed class CapturingEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _messages = [];
    private readonly Channel<bool> _signal = Channel.CreateUnbounded<bool>();
    private readonly object _gate = new();

    internal IReadOnlyList<EmailMessage> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.ToArray();
            }
        }
    }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _messages.Add(message);
        }

        _signal.Writer.TryWrite(true);
        return Task.CompletedTask;
    }

    internal async Task<EmailMessage> WaitForAsync(
        Func<EmailMessage, bool> predicate,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            lock (_gate)
            {
                EmailMessage? found = _messages.LastOrDefault(predicate);
                if (found is not null)
                {
                    return found;
                }
            }

            await _signal.Reader.ReadAsync(timeout.Token);
        }
    }

    internal async Task<IReadOnlyList<EmailMessage>> WaitForCountAsync(
        Func<EmailMessage, bool> predicate,
        int count,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            lock (_gate)
            {
                EmailMessage[] found = _messages.Where(predicate).ToArray();
                if (found.Length >= count)
                {
                    return found;
                }
            }

            await _signal.Reader.ReadAsync(timeout.Token);
        }
    }
}
