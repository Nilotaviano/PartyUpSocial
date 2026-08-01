using System.Security.Cryptography;
using System.Text;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.Hosting;

public sealed partial class AdminSeeder(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    IClock clock,
    ILogger<AdminSeeder> logger) : IHostedService
{
    private const string AdminRole = "admin";

    public Task StartAsync(CancellationToken cancellationToken) =>
        SeedAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;
        var accounts = services.GetRequiredService<IUserAccountGateway>();
        var refusals = services.GetRequiredService<IAdminSeedRefusalStore>();
        var sessions = services.GetRequiredService<IAccountSessionService>();
        var users = services.GetRequiredService<IUserRepository>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();

        await unitOfWork.ExecuteAsync(
            async ct =>
            {
                await accounts.EnsureRoleExistsAsync(AdminRole, ct);
                return true;
            },
            cancellationToken);

        string[] seedUserIds = ReadSeedValues("Admin:SeedUserIds");
        foreach (string configuredId in seedUserIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(configuredId, out Guid value))
            {
                LogInvalidSeedUserId(logger, configuredId);
                continue;
            }

            await GrantAsync(
                UserId.From(value),
                accounts,
                sessions,
                users,
                unitOfWork,
                cancellationToken);
        }

        foreach (string email in ReadSeedValues("Admin:SeedEmails")
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string emailHash = HashEmail(email);
            SeedEmailOutcome outcome = await ProcessSeedEmailAsync(
                email,
                accounts,
                refusals,
                sessions,
                users,
                unitOfWork,
                cancellationToken);
            switch (outcome)
            {
                case SeedEmailOutcome.NoAccount:
                    LogSeedEmailWithoutAccount(logger, emailHash);
                    break;
                case SeedEmailOutcome.Unconfirmed:
                    LogUnconfirmedSeedEmail(logger, emailHash);
                    break;
                case SeedEmailOutcome.PreviouslyRefused:
                    LogPreviouslyRefusedSeedEmail(logger, emailHash);
                    break;
                case SeedEmailOutcome.Granted:
                    break;
            }
        }
    }

    private async Task<SeedEmailOutcome> ProcessSeedEmailAsync(
        string email,
        IUserAccountGateway accounts,
        IAdminSeedRefusalStore refusals,
        IAccountSessionService sessions,
        IUserRepository users,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteAsync(
            async ct =>
            {
                UserId? userId = await accounts.FindUserIdByEmailAsync(email, ct);
                if (userId is null)
                {
                    return SeedEmailOutcome.NoAccount;
                }

                // Refusals bind only SeedEmails; SeedUserIds deliberately remains the recovery path.
                if (await refusals.ExistsAsync(userId.Value, ct))
                {
                    return SeedEmailOutcome.PreviouslyRefused;
                }

                AccountEmailInfo? emailInfo = await accounts.GetEmailAsync(userId.Value, ct);
                if (emailInfo is not { IsConfirmed: true })
                {
                    await refusals.RecordAsync(userId.Value, clock.GetCurrentInstant(), ct);
                    return SeedEmailOutcome.Unconfirmed;
                }

                if (await users.FindAsync(userId.Value, ct) is null)
                {
                    return SeedEmailOutcome.NoAccount;
                }

                bool changed = await accounts.AddToRoleAsync(userId.Value, AdminRole, ct);
                if (changed)
                {
                    await sessions.SignOutEverywhereAsync(userId.Value, ct);
                }

                return SeedEmailOutcome.Granted;
            },
            cancellationToken);

    private async Task GrantAsync(
        UserId userId,
        IUserAccountGateway accounts,
        IAccountSessionService sessions,
        IUserRepository users,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        bool accountExists = await unitOfWork.ExecuteAsync(
            async ct =>
            {
                if (await users.FindAsync(userId, ct) is null)
                {
                    return false;
                }

                bool changed = await accounts.AddToRoleAsync(userId, AdminRole, ct);
                if (changed)
                {
                    await sessions.SignOutEverywhereAsync(userId, ct);
                }

                return true;
            },
            cancellationToken);
        if (!accountExists)
        {
            LogSeedUserIdWithoutAccount(logger, userId.Value);
        }
    }

    private string[] ReadSeedValues(string key) =>
        configuration.GetSection(key).Get<string[]>()?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray() ?? [];

    private static string HashEmail(string email)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToUpperInvariant()));
        return Convert.ToHexString(bytes);
    }

    private enum SeedEmailOutcome
    {
        NoAccount,
        Unconfirmed,
        PreviouslyRefused,
        Granted,
    }

    [LoggerMessage(
        EventId = 9001,
        Level = LogLevel.Error,
        Message = "Admin seed user id {SeedUserId} is not a valid GUID; refusing to grant the admin role")]
    private static partial void LogInvalidSeedUserId(ILogger logger, string seedUserId);

    [LoggerMessage(
        EventId = 9002,
        Level = LogLevel.Warning,
        Message = "Admin seed email {EmailHash} has no account; it will be checked again at the next startup")]
    private static partial void LogSeedEmailWithoutAccount(ILogger logger, string emailHash);

    [LoggerMessage(
        EventId = 9003,
        Level = LogLevel.Error,
        Message = "Admin seed email {EmailHash} matched an unconfirmed account; refusing to grant the admin role")]
    private static partial void LogUnconfirmedSeedEmail(ILogger logger, string emailHash);

    [LoggerMessage(
        EventId = 9004,
        Level = LogLevel.Error,
        Message = "Admin seed email {EmailHash} previously matched an unconfirmed account; refusing to grant the admin role")]
    private static partial void LogPreviouslyRefusedSeedEmail(ILogger logger, string emailHash);

    [LoggerMessage(
        EventId = 9005,
        Level = LogLevel.Warning,
        Message = "Admin seed user id {SeedUserId} has no account; it will be checked again at the next startup")]
    private static partial void LogSeedUserIdWithoutAccount(ILogger logger, Guid seedUserId);
}
