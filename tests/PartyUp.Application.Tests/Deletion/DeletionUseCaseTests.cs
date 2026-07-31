using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Application.Deletion;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Application.Tests.Deletion;

public sealed class RequestAccountDeletionUseCaseTests
{
    [Fact]
    public async Task Execute_StartsASevenDayGrace_D148()
    {
        TestContext context = Create(active: true);

        AccountDeletionRequestResult result = await context.UseCase.ExecuteAsync(
            new RequestAccountDeletionCommand(context.User.Id, true),
            default);

        Assert.True(result.Succeeded);
        Assert.Equal(context.Clock.GetCurrentInstant() + Duration.FromDays(7), result.GraceEndsAt);
        Assert.True(context.User.IsPendingDeletion);
    }

    [Fact]
    public async Task Execute_SignsOutEveryDevice_D165()
    {
        TestContext context = Create(active: true);

        await context.UseCase.ExecuteAsync(
            new RequestAccountDeletionCommand(context.User.Id, true),
            default);

        Assert.Equal(1, context.Sessions.Rotations);
    }

    [Fact]
    public async Task Execute_MailsTheCancellationDisclosure_D158()
    {
        TestContext context = Create(active: true);

        await context.UseCase.ExecuteAsync(
            new RequestAccountDeletionCommand(context.User.Id, true),
            default);

        EmailMessage message = Assert.Single(context.Email.Messages);
        Assert.Equal(EmailTemplate.DeletionRequested, message.Template);
        Assert.True(message.Model.ContainsKey("GraceEndsAt"));
        Assert.False(message.Model.ContainsKey("Token"));
        Assert.Equal("AsTyped@Example.test", message.ToAddress);
    }

    [Fact]
    public async Task Execute_RequiresReauthentication_D165()
    {
        TestContext context = Create(active: true);

        AccountDeletionRequestResult result = await context.UseCase.ExecuteAsync(
            new RequestAccountDeletionCommand(context.User.Id, false),
            default);

        Assert.Equal("reauth_required", result.Code);
        Assert.True(context.User.IsActive);
        Assert.Equal(0, context.UnitOfWork.Executions);
    }

    [Fact]
    public async Task Execute_DeletesAnAccountAbandonedDuringOnboarding_D072()
    {
        TestContext context = Create(active: false);

        AccountDeletionRequestResult result = await context.UseCase.ExecuteAsync(
            new RequestAccountDeletionCommand(context.User.Id, true),
            default);

        Assert.True(result.Succeeded);
        Assert.True(context.User.IsPendingDeletion);
        Assert.False(context.User.IsProfileComplete);
    }

    private static TestContext Create(bool active)
    {
        var clock = new MutableClock(Instant.FromUtc(2026, 7, 31, 12, 0));
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("ABCDEFGHJK"),
            AgeGateSource.SelfAttested,
            clock);
        if (active)
        {
            Complete(user, clock);
        }

        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var accounts = new FakeAccountGateway();
        accounts.Emails[user.Id] = new AccountEmailInfo("AsTyped@Example.test", true);
        var sessions = new FakeAccountSessionService();
        var unitOfWork = new FakeUnitOfWork();
        var email = new FakeEmailDispatcher();
        var useCase = new RequestAccountDeletionUseCase(
            users,
            accounts,
            sessions,
            unitOfWork,
            email,
            clock);
        return new TestContext(useCase, user, sessions, unitOfWork, email, clock);
    }

    private sealed record TestContext(
        RequestAccountDeletionUseCase UseCase,
        User User,
        FakeAccountSessionService Sessions,
        FakeUnitOfWork UnitOfWork,
        FakeEmailDispatcher Email,
        MutableClock Clock);

    internal static void Complete(User user, IClock clock) =>
        user.CompleteOnboarding(
            Username.Create("player"),
            DisplayName.Create("Player"),
            Avatar.FromEmoji("🎮"),
            DateTimeZoneProviders.Tzdb["America/Fortaleza"],
            Locale.Create("en"),
            clock);
}

public sealed class ConfirmPublicDeletionUseCaseTests
{
    [Fact]
    public async Task Execute_VerifiesIdentityBeforeStartingTheGrace_D072()
    {
        var clock = new MutableClock(Instant.FromUtc(2026, 7, 31, 12, 0));
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("KLMNPQRSTU"),
            AgeGateSource.SelfAttested,
            clock);
        RequestAccountDeletionUseCaseTests.Complete(user, clock);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var accounts = new FakeAccountGateway();
        var tokens = new FakeTokenService();
        var request = new RequestAccountDeletionUseCase(
            users,
            accounts,
            new FakeAccountSessionService(),
            new FakeUnitOfWork(),
            new FakeEmailDispatcher(),
            clock);
        var useCase = new ConfirmPublicDeletionUseCase(tokens, request, new FakeUnitOfWork());

        AccountDeletionRequestResult rejected = await useCase.ExecuteAsync("unknown", default);
        Assert.Equal("invalid_token", rejected.Code);
        Assert.True(user.IsActive);

        tokens.RedemptionResults.Enqueue(new TokenRedemption(
            true,
            null,
            AuthTokenPurpose.DeletionRequestConfirmation,
            user.Id,
            null,
            null,
            null,
            clock.GetCurrentInstant()));
        AccountDeletionRequestResult accepted = await useCase.ExecuteAsync("proven", default);

        Assert.True(accepted.Succeeded);
        Assert.True(user.IsPendingDeletion);
    }
}

public sealed class ExecuteDeletionForUserUseCaseTests
{
    [Fact]
    public async Task Execute_TombstonesOnlyAnElapsedGrace_D148()
    {
        ExecutionContext context = Create();

        Assert.False(await context.UseCase.ExecuteAsync(context.User.Id, default));
        Assert.True(context.User.IsPendingDeletion);

        context.Clock.Advance(User.DeletionGracePeriod);
        Assert.True(await context.UseCase.ExecuteAsync(context.User.Id, default));
        Assert.True(context.User.IsTombstoned);
        Assert.Contains(context.User.Id, context.Accounts.PurgedCredentials);
    }

    [Fact]
    public async Task Execute_AppendsTheJournalEntry_D092()
    {
        ExecutionContext context = Create();
        context.Clock.Advance(User.DeletionGracePeriod);

        await context.UseCase.ExecuteAsync(context.User.Id, default);

        DeletionJournalEntry entry = Assert.Single(context.Journal.Entries);
        Assert.Equal(context.User.Id, entry.UserId);
        Assert.True(context.Username.IsReserved);
        Assert.Equal(1, context.Shipper.Calls);
    }

    [Fact]
    public async Task Execute_IsIdempotent_D148()
    {
        ExecutionContext context = Create();
        context.Clock.Advance(User.DeletionGracePeriod);

        Assert.True(await context.UseCase.ExecuteAsync(context.User.Id, default));
        Assert.False(await context.UseCase.ExecuteAsync(context.User.Id, default));

        Assert.Single(context.Journal.Entries);
        Assert.Single(context.Accounts.PurgedCredentials);
    }

    private static ExecutionContext Create()
    {
        var clock = new MutableClock(Instant.FromUtc(2026, 7, 31, 12, 0));
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("UVWXYZ2345"),
            AgeGateSource.SelfAttested,
            clock);
        RequestAccountDeletionUseCaseTests.Complete(user, clock);
        UsernameRegistryEntry username = UsernameRegistryEntry.ClaimNew(
            user.Username!.Value,
            user.Id);
        user.RequestDeletion(clock);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var accounts = new FakeAccountGateway();
        var journal = new FakeDeletionJournal();
        var shipper = new FakeDeletionJournalShipper();
        var useCase = new ExecuteDeletionForUserUseCase(
            users,
            new FakeDeletionScopeProvider(username),
            accounts,
            journal,
            shipper,
            new FakeUnitOfWork(),
            clock);
        return new ExecutionContext(
            useCase,
            user,
            username,
            accounts,
            journal,
            shipper,
            clock);
    }

    private sealed record ExecutionContext(
        ExecuteDeletionForUserUseCase UseCase,
        User User,
        UsernameRegistryEntry Username,
        FakeAccountGateway Accounts,
        FakeDeletionJournal Journal,
        FakeDeletionJournalShipper Shipper,
        MutableClock Clock);
}

internal sealed class MutableClock(Instant current) : IClock
{
    public Instant GetCurrentInstant() => current;

    public void Advance(Duration duration) => current += duration;
}

internal sealed class FakeDeletionScopeProvider(UsernameRegistryEntry username)
    : IDeletionScopeProvider
{
    public Task<DeletionScope> LoadAsync(UserId id, CancellationToken cancellationToken) =>
        Task.FromResult(new DeletionScope([], [], [], [], username));
}

internal sealed class FakeDeletionJournal : IDeletionJournal
{
    public List<DeletionJournalEntry> Entries { get; } = [];

    public void Append(DeletionJournalEntry entry) => Entries.Add(entry);

    public Task<IReadOnlyList<DeletionJournalEntry>> ReadSinceAsync(
        Instant since,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeletionJournalEntry>>(
            Entries.Where(entry => entry.ExecutedAt >= since).Take(limit).ToArray());
}

internal sealed class FakeDeletionJournalShipper : IDeletionJournalShipper
{
    public int Calls { get; private set; }

    public Task<int> ShipPendingAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(1);
    }
}
