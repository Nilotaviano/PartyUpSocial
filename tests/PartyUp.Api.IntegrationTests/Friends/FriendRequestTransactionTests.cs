using Microsoft.EntityFrameworkCore;
using NodaTime;
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
    public async Task NotificationInsertFailure_RollsBackTheRequest_D105()
    {
        await using (AppDbContext context = _database.CreateContext())
        {
            var useCase = new SendFriendRequestUseCase(
                new UserGraphResolver(context),
                new FriendPairLocker(context),
                new FriendshipRepository(context),
                new BlockRepository(context),
                new ThrowingNotificationRepository(),
                new UnitOfWork(context),
                _clock);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                useCase.ExecuteAsync(
                    _users[0].Id,
                    _users[1].Username!.Value.Value,
                    default));
        }

        await using AppDbContext verification = _database.CreateContext();
        Assert.Equal(0, await verification.Friendships.CountAsync());
        Assert.Equal(0, await verification.Notifications.CountAsync());
    }
}

internal sealed class ThrowingNotificationRepository : INotificationRepository
{
    public void Add(Notification notification) =>
        throw new InvalidOperationException("forced notification failure");

    public Task AddSteamMatchIfAbsentAsync(
        Notification notification,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("forced notification failure");

    public Task DeleteForRecipientAsync(
        UserId recipientUserId,
        CancellationToken cancellationToken) => Task.CompletedTask;
}
