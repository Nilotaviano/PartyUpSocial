using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Application.Tests.Deletion;

// The Infrastructure.Friends.DeletionResultApplier adapter (which now also forwards
// DeletionExecutionResult.CanceledReminders to IReminderScheduleWriter, PHASE4_DESIGN.md §5.3) cannot
// be constructed here: PartyUp.Application.Tests does not and must not reference PartyUp.Infrastructure
// (A-dependency-rule). That forwarding is covered by
// tests/PartyUp.Api.IntegrationTests/Friends/FriendBlockConcurrencyTests.cs instead. This class keeps
// exercising the shared DeletionResultApplication.ApplyAsync static helper, which is the part that is
// reachable from the Application layer.
public sealed class DeletionResultApplierTests
{

    [Fact]
    public async Task Apply_DeletesFriendshipsBlocksAndRecipientNotificationsButPreservesActorOnlyRows_D092()
    {
        var clock = new MutableClock(Instant.FromUtc(2026, 8, 1, 12, 0));
        User subject = CreateUser(clock, "ABCDEFGHJK");
        User counterpart = CreateUser(clock, "KLMNPQRSTU");
        User control = CreateUser(clock, "UVWXYZ2345");
        Friendship friendship = Friendship.Request(subject, counterpart, clock);
        Block block = Block.Create(
            counterpart.Id,
            subject.Id,
            subject.Username!.Value,
            clock);
        Notification recipient = Notification.Create(
            subject.Id,
            NotificationType.FriendRequestReceived,
            NotificationSubject.ForActor(counterpart.Id),
            clock);
        Notification actorOnly = Notification.Create(
            counterpart.Id,
            NotificationType.FriendRequestReceived,
            NotificationSubject.ForActor(subject.Id),
            clock);
        Notification unaffected = Notification.Create(
            control.Id,
            NotificationType.FriendRequestReceived,
            NotificationSubject.ForActor(counterpart.Id),
            clock);
        var friendships = new RecordingFriendshipRepository(friendship);
        var blocks = new RecordingBlockRepository(block);
        var notifications = new RecordingNotificationRepository(
            recipient,
            actorOnly,
            unaffected);
        var result = new DeletionExecutionResult(
            CanceledSessions: [],
            ExpiredPolls: [],
            RemovedFromSessions: [],
            RemovedFromPolls: [],
            LapsedJoinRequests: [],
            CanceledReminders: [],
            DeletedFriendships: [friendship],
            DeletedBlocks: [block],
            PurgedData: [UserDataScope.Notifications],
            ReservedUsername: null,
            Journal: new DeletionJournalEntry(subject.Id, clock.GetCurrentInstant()),
            DeletedGroups: []);
        using var cancellation = new CancellationTokenSource();

        await DeletionResultApplication.ApplyAsync(
            result,
            subject.Id,
            friendships,
            blocks,
            notifications,
            cancellation.Token);

        Assert.Empty(friendships.Rows);
        Assert.Empty(blocks.Rows);
        Assert.DoesNotContain(recipient, notifications.Rows);
        Assert.Contains(actorOnly, notifications.Rows);
        Assert.Contains(unaffected, notifications.Rows);
        Assert.Equal(subject.Id, Assert.Single(notifications.DeletedRecipients));
        Assert.Equal(cancellation.Token, notifications.CancellationToken);
    }

    private static User CreateUser(IClock clock, string friendCode)
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create(friendCode),
            AgeGateSource.SelfAttested,
            clock);
        user.CompleteOnboarding(
            Username.Create($"delete{suffix}"),
            DisplayName.Create($"Delete {suffix}"),
            Avatar.Fallback,
            DateTimeZone.Utc,
            Locale.Create("en"),
            clock);
        return user;
    }

    private sealed class RecordingFriendshipRepository(params Friendship[] rows)
        : IFriendshipRepository
    {
        internal List<Friendship> Rows { get; } = [.. rows];

        public Task<Friendship?> FindPairAsync(
            UserId first,
            UserId second,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Friendship?> FindPairForUpdateAsync(
            UserId first,
            UserId second,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FriendshipPair?> FindOwnedPairAsync(
            FriendshipId friendshipId,
            UserId actorId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Friendship?> FindByIdForUpdateAsync(
            FriendshipId friendshipId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<AcceptedFriendProjection>> ListAcceptedAsync(
            UserId userId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PendingFriendProjection>> ListPendingAsync(
            UserId userId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Add(Friendship friendship) => Rows.Add(friendship);

        public void Remove(Friendship friendship) => Rows.Remove(friendship);
    }

    private sealed class RecordingBlockRepository(params Block[] rows) : IBlockRepository
    {
        internal List<Block> Rows { get; } = [.. rows];

        public Task<IReadOnlyList<Block>> FindPairForUpdateAsync(
            UserId first,
            UserId second,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BlockPair?> FindOutboundOwnedPairAsync(
            BlockId blockId,
            UserId blockerId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Block?> FindByIdForUpdateAsync(
            BlockId blockId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<BlockedUserProjection>> ListOutboundAsync(
            UserId blockerId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Add(Block block) => Rows.Add(block);

        public void Remove(Block block) => Rows.Remove(block);
    }

    private sealed class RecordingNotificationRepository(params Notification[] rows)
        : INotificationRepository
    {
        internal List<Notification> Rows { get; } = [.. rows];

        internal List<UserId> DeletedRecipients { get; } = [];

        internal CancellationToken CancellationToken { get; private set; }

        public void Add(Notification notification) => Rows.Add(notification);

        public Task<Notification?> FindAsync(NotificationId id, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.Find(row => row.Id == id));

        public Task<Notification?> FindOwnedAsync(
            NotificationId id,
            UserId recipientId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Rows.Find(row => row.Id == id && row.RecipientUserId == recipientId));

        public Task<IReadOnlyList<Notification>> ListUnreadForUpdateAsync(
            UserId recipientId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Notification>>(
                [.. Rows.Where(row => row.RecipientUserId == recipientId && row.ReadAt is null)]);

        public Task AddSteamMatchIfAbsentAsync(
            Notification notification,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteForRecipientAsync(
            UserId recipientUserId,
            CancellationToken cancellationToken)
        {
            DeletedRecipients.Add(recipientUserId);
            CancellationToken = cancellationToken;
            Rows.RemoveAll(notification => notification.RecipientUserId == recipientUserId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingReminderScheduleWriter : IReminderScheduleWriter
    {
        internal List<SessionReminderCanceled> Applied { get; } = [];

        public Task ApplyAsync(
            SessionReminderCanceled cancellation,
            CancellationToken cancellationToken)
        {
            Applied.Add(cancellation);
            return Task.CompletedTask;
        }
    }
}
