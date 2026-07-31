using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Accounts;

public sealed class PostAuthenticationGateUseCaseTests
{
    private static readonly Instant Origin = Instant.FromUtc(2026, 7, 31, 12, 0);

    [Fact]
    public async Task Execute_CancelsAPendingDeletionOnASuccessfulCredentialCheck_D158()
    {
        var clock = new TestClock(Origin);
        User user = ActiveUser(clock);
        user.RequestDeletion(clock);
        var useCase = Create(user, clock, out FakeUnitOfWork unitOfWork);

        PostAuthenticationResult result = await useCase.ExecuteAsync(user.Id, default);

        var admitted = Assert.IsType<PostAuthenticationResult.Admitted>(result);
        Assert.True(admitted.DeletionWasCanceled);
        Assert.True(user.IsActive);
        Assert.Null(user.GraceEndsAt);
        Assert.Equal(1, unitOfWork.ExecutionCount);
    }

    [Fact]
    public async Task Execute_RefusesLoginAfterTheGraceElapsed_D148()
    {
        var clock = new TestClock(Origin);
        User user = ActiveUser(clock);
        user.RequestDeletion(clock);
        clock.Advance(User.DeletionGracePeriod);
        var useCase = Create(user, clock, out _, out FakeDeletionExecutionScheduler scheduler);

        PostAuthenticationResult result = await useCase.ExecuteAsync(user.Id, default);

        var refused = Assert.IsType<PostAuthenticationResult.Refused>(result);
        Assert.Equal("account_pending_removal", refused.Code);
        Assert.True(user.IsPendingDeletion);
        Assert.Equal([user.Id], scheduler.Scheduled);
    }

    [Fact]
    public async Task Execute_RefusesATombstonedAccount_D148()
    {
        var clock = new TestClock(Origin);
        User user = ActiveUser(clock);
        user.RequestDeletion(clock);
        clock.Advance(User.DeletionGracePeriod);
        user.MarkDeleted(clock);
        var useCase = Create(user, clock, out _);

        PostAuthenticationResult result = await useCase.ExecuteAsync(user.Id, default);

        var refused = Assert.IsType<PostAuthenticationResult.Refused>(result);
        Assert.Equal("invalid_credentials", refused.Code);
    }

    [Fact]
    public async Task Execute_RefusesABannedAccountBeforeAnyCookie_D180()
    {
        var clock = new TestClock(Origin);
        User user = ActiveUser(clock);
        user.Ban(clock);
        var useCase = Create(user, clock, out _);

        PostAuthenticationResult result = await useCase.ExecuteAsync(user.Id, default);

        var refused = Assert.IsType<PostAuthenticationResult.Refused>(result);
        Assert.Equal("account_banned", refused.Code);
    }

    [Fact]
    public async Task Execute_ReturnsAnOnboardingUserToOnboardingOnCancel_D158()
    {
        var clock = new TestClock(Origin);
        User user = OnboardingUser(clock);
        user.RequestDeletion(clock);
        var useCase = Create(user, clock, out _);

        PostAuthenticationResult result = await useCase.ExecuteAsync(user.Id, default);

        Assert.IsType<PostAuthenticationResult.Admitted>(result);
        Assert.True(user.IsOnboarding);
        Assert.False(user.IsProfileComplete);
    }

    private static PostAuthenticationGateUseCase Create(
        User user,
        IClock clock,
        out FakeUnitOfWork unitOfWork)
    {
        return Create(user, clock, out unitOfWork, out _);
    }

    private static PostAuthenticationGateUseCase Create(
        User user,
        IClock clock,
        out FakeUnitOfWork unitOfWork,
        out FakeDeletionExecutionScheduler scheduler)
    {
        unitOfWork = new FakeUnitOfWork();
        scheduler = new FakeDeletionExecutionScheduler();
        return new PostAuthenticationGateUseCase(
            new FakeUserRepository(user),
            unitOfWork,
            scheduler,
            clock);
    }

    private static User OnboardingUser(IClock clock) =>
        User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("PU-ABC123"),
            AgeGateSource.SelfAttested,
            clock);

    private static User ActiveUser(IClock clock)
    {
        User user = OnboardingUser(clock);
        user.CompleteOnboarding(
            Username.Create("gamer"),
            DisplayName.Create("Gamer"),
            Avatar.FromEmoji("🙂"),
            DateTimeZoneProviders.Tzdb["America/Fortaleza"],
            Locale.Create("en"),
            clock);
        return user;
    }

    private sealed class TestClock(Instant current) : IClock
    {
        public Instant GetCurrentInstant() => current;

        public void Advance(Duration duration) => current += duration;
    }

    private sealed class FakeUserRepository(User user) : IUserRepository
    {
        public Task<User?> FindAsync(UserId id, CancellationToken cancellationToken) =>
            Task.FromResult<User?>(id == user.Id ? user : null);

        public Task<User?> FindForUpdateAsync(UserId id, CancellationToken cancellationToken) =>
            FindAsync(id, cancellationToken);

        public Task<IReadOnlyList<UserId>> FindDueForDeletionAsync(
            Instant now,
            int max,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UserId>>([]);

        public void Add(User addedUser) => throw new NotSupportedException();
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int ExecutionCount { get; private set; }

        public Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> work,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return work(cancellationToken);
        }
    }

    private sealed class FakeDeletionExecutionScheduler : IDeletionExecutionScheduler
    {
        public List<UserId> Scheduled { get; } = [];

        public ValueTask ScheduleAsync(UserId id, CancellationToken cancellationToken)
        {
            Scheduled.Add(id);
            return ValueTask.CompletedTask;
        }
    }
}
