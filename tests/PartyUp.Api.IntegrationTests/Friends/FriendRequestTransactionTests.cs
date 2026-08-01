using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Friends;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class FriendRequestTransactionTests : IAsyncLifetime
{
    private readonly FriendGraphDatabase _database = new();
    private readonly MutableClock _clock = new(Instant.FromUtc(2026, 8, 1, 21, 0));
    private User[] _users = [];

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        _users = await _database.SeedUsersAsync(2, _clock);
        await FriendCommandIntegration.EnsureUsernameRegistryAsync(_database, _users, _clock);
    }

    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task NewSendWritesOneRowAndCrossedAcceptanceWritesNone_D015_D105()
    {
        await using (AppDbContext first = _database.CreateContext())
        {
            FriendGraphCommandResult created = await FriendCommandIntegration.SendAsync(
                first,
                new FriendPairLocker(first),
                _users[0].Id,
                _users[1].Username!.Value.Value,
                _clock);
            Assert.True(created.Created);
        }

        await using (AppDbContext crossed = _database.CreateContext())
        {
            FriendGraphCommandResult accepted = await FriendCommandIntegration.SendAsync(
                crossed,
                new FriendPairLocker(crossed),
                _users[1].Id,
                _users[0].Username!.Value.Value,
                _clock);
            Assert.Equal("accepted", accepted.Friendship!.Status);
        }

        await using AppDbContext verification = _database.CreateContext();
        Assert.Equal(1, await verification.Friendships.CountAsync());
        Assert.Equal(1, await verification.Notifications.CountAsync());
        Notification notification = await verification.Notifications.AsNoTracking().SingleAsync();
        Assert.Equal(_users[1].Id, notification.RecipientUserId);
        Assert.Equal(_users[0].Id, notification.ActorUserId);
        Assert.Equal(NotificationType.FriendRequestReceived, notification.Type);
    }

    [Fact]
    public async Task NotificationInsertConstraintFailure_RollsBackTheRequest_D105()
    {
        await using (AppDbContext context = _database.CreateContext())
        {
            var useCase = new SendFriendRequestUseCase(
                new UserGraphResolver(context),
                new FriendPairLocker(context),
                new FriendshipRepository(context),
                new BlockRepository(context),
                new ConstraintViolatingNotificationRepository(context),
                new UnitOfWork(context),
                _clock);

            DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
                useCase.ExecuteAsync(
                    _users[0].Id,
                    _users[1].Username!.Value.Value,
                    default));
            PostgresException postgresException =
                Assert.IsType<PostgresException>(exception.InnerException);
            Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
            Assert.Equal("ck_notifications_type", postgresException.ConstraintName);
        }

        await using AppDbContext verification = _database.CreateContext();
        Assert.Equal(0, await verification.Friendships.CountAsync());
        Assert.Equal(0, await verification.Notifications.CountAsync());
    }
}

internal sealed class ConstraintViolatingNotificationRepository(
    AppDbContext context) : INotificationRepository
{
    private readonly NotificationRepository _inner = new(context);

    public void Add(Notification notification)
    {
        _inner.Add(notification);
        context.Entry(notification).Property(candidate => candidate.Type).CurrentValue =
            (NotificationType)25;
    }

    public Task AddSteamMatchIfAbsentAsync(
        Notification notification,
        CancellationToken cancellationToken) =>
        _inner.AddSteamMatchIfAbsentAsync(notification, cancellationToken);

    public Task DeleteForRecipientAsync(
        UserId recipientUserId,
        CancellationToken cancellationToken) =>
        _inner.DeleteForRecipientAsync(recipientUserId, cancellationToken);
}
