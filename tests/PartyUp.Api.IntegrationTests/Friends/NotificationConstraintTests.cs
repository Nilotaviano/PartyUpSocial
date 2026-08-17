using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class NotificationConstraintTests : IAsyncLifetime
{
    private readonly FriendGraphDatabase _database = new();

    public Task InitializeAsync() => _database.StartAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task Insert_EnforcesFriendShapesAndSteamUniqueness_D105()
    {
        var clock = new MutableClock(Instant.FromUtc(2026, 8, 1, 16, 0));
        User[] users = await _database.SeedUsersAsync(2, clock);
        Notification steamMatch = Notification.Create(
            users[0].Id,
            NotificationType.SteamImportMatch,
            NotificationSubject.ForActor(users[1].Id),
            clock);
        clock.Advance(Duration.FromMinutes(2));
        steamMatch.MarkRead(clock);
        Instant optionStart = Instant.FromUtc(2026, 8, 8, 20, 0);
        SessionId sessionId = SessionId.New();
        PollId pollId = PollId.New();
        Notification selectedOption = Notification.Create(
            users[0].Id,
            NotificationType.PollOptionSelected,
            NotificationSubject.ForSelectedOption(pollId, sessionId, optionStart),
            clock);

        // notifications.poll_id is FK-enforced (fk_notifications_polls_poll_id), so the subject's
        // poll must exist as a row before the notification can be inserted.
        await using (AppDbContext pollSeed = _database.CreateContext())
        {
            await pollSeed.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO polls
                    (id, organizer_id, title, duration_minutes, platform, notes, min_attendance,
                     max_capacity, visibility, deadline_choice, deadline, status, created_at, selection_window_end)
                VALUES
                    ({pollId.Value}, {users[0].Id.Value}, 'Selected-option subject', 60, 3, '', 1, NULL, 1, 3,
                     {optionStart}, 0, {optionStart}, {optionStart})
                """);
        }

        await using (AppDbContext context = _database.CreateContext())
        {
            context.Notifications.AddRange(steamMatch, selectedOption);
            await context.SaveChangesAsync();
        }

        await using (AppDbContext verification = _database.CreateContext())
        {
            Notification reloaded = await verification.Notifications
                .AsNoTracking()
                .SingleAsync(notification => notification.Id == steamMatch.Id);
            Assert.Equal(steamMatch.Id, reloaded.Id);
            Assert.Equal(users[0].Id, reloaded.RecipientUserId);
            Assert.Equal(NotificationType.SteamImportMatch, reloaded.Type);
            Assert.Equal(users[1].Id, reloaded.ActorUserId);
            Assert.Equal(steamMatch.CreatedAt, reloaded.CreatedAt);
            Assert.Equal(steamMatch.ReadAt, reloaded.ReadAt);

            Notification option = await verification.Notifications
                .AsNoTracking()
                .SingleAsync(notification => notification.Id == selectedOption.Id);
            Assert.Equal(NotificationType.PollOptionSelected, option.Type);
            Assert.Equal(sessionId, option.SessionId);
            Assert.Equal(pollId, option.PollId);
            Assert.Equal(optionStart, option.OptionStartTime);
        }

        await using (AppDbContext duplicateContext = _database.CreateContext())
        {
            duplicateContext.Notifications.Add(Notification.Create(
                users[0].Id,
                NotificationType.SteamImportMatch,
                NotificationSubject.ForActor(users[1].Id),
                clock));
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicateContext.SaveChangesAsync());
        }

        await using (AppDbContext friendRequestContext = _database.CreateContext())
        {
            friendRequestContext.Notifications.AddRange(
                Notification.Create(
                    users[0].Id,
                    NotificationType.FriendRequestReceived,
                    NotificationSubject.ForActor(users[1].Id),
                    clock),
                Notification.Create(
                    users[0].Id,
                    NotificationType.FriendRequestReceived,
                    NotificationSubject.ForActor(users[1].Id),
                    clock));
            await friendRequestContext.SaveChangesAsync();
        }

        await AssertInvalidTypeAsync(users, clock);
        await AssertMissingActorAsync(users, clock);
        await AssertFriendNotificationWithSessionAsync(users, clock);
    }

    private async Task AssertInvalidTypeAsync(User[] users, MutableClock clock)
    {
        await using AppDbContext context = _database.CreateContext();
        await Assert.ThrowsAnyAsync<DbException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO notifications
                    (id, recipient_user_id, type, created_at)
                VALUES
                    ({Guid.CreateVersion7()}, {users[0].Id.Value}, {(short)25},
                     {clock.GetCurrentInstant()})
                """));
    }

    private async Task AssertMissingActorAsync(User[] users, MutableClock clock)
    {
        await using AppDbContext context = _database.CreateContext();
        await Assert.ThrowsAnyAsync<DbException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO notifications
                    (id, recipient_user_id, type, created_at)
                VALUES
                    ({Guid.CreateVersion7()}, {users[0].Id.Value}, {(short)24},
                     {clock.GetCurrentInstant()})
                """));
    }

    private async Task AssertFriendNotificationWithSessionAsync(User[] users, MutableClock clock)
    {
        await using AppDbContext context = _database.CreateContext();
        await Assert.ThrowsAnyAsync<DbException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO notifications
                    (id, recipient_user_id, type, session_id, actor_user_id, created_at)
                VALUES
                    ({Guid.CreateVersion7()}, {users[0].Id.Value}, {(short)23},
                     {Guid.CreateVersion7()}, {users[1].Id.Value}, {clock.GetCurrentInstant()})
                """));
    }
}
