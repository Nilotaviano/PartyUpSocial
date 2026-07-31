using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodaTime;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Identity;
using PartyUp.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using System.Threading.Channels;

namespace PartyUp.Api.IntegrationTests;

internal sealed class PartyUpApiFactory : WebApplicationFactory<Program>
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly CapturingEmailSender _emailSender = new();

    internal MutableClock Clock { get; } =
        new(Instant.FromUtc(2026, 7, 31, 12, 0));

    internal CapturingEmailSender EmailSender => _emailSender;

    internal Task StartAsync() => _postgres.StartAsync();

    internal Task StopAsync() => _postgres.DisposeAsync().AsTask();

    internal async Task<UserId> SeedUserAsync(
        bool completeProfile = true,
        bool pendingDeletion = false,
        bool admin = false,
        string? email = "player@example.test",
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
            string suffix = user.Id.Value.ToString("N")[..8];
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

        context.DomainUsers.Add(user);
        await context.SaveChangesAsync();

        string identityName = user.Id.Value.ToString("D");
        var authUser = new AuthUser
        {
            Id = user.Id.Value,
            UserName = identityName,
            NormalizedUserName = identityName,
            Email = email,
            EmailConfirmed = email is not null,
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
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Db"] = _postgres.GetConnectionString(),
                ["Database:MigrateOnStartup"] = "true",
                ["Deletion:SweepEnabled"] = "false",
                ["Email:Provider"] = "log",
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(_emailSender);
        });
    }
}

internal sealed class MutableClock(Instant current) : IClock
{
    public Instant GetCurrentInstant() => current;

    public void Advance(Duration duration) => current += duration;
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
}
