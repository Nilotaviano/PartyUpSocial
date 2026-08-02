using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Friends;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Friends;

public sealed class BlockEffectsExecutorTests
{
    [Fact]
    public async Task Execute_LoadsScopeAppliesDomainDeletesRowsAndForwardsReminders_D077()
    {
        IClock clock = FriendTestData.Clock();
        User blocker = FriendTestData.Active("effect-blocker", "Effect Blocker");
        User blocked = FriendTestData.Active("effect-blocked", "Effect Blocked");
        Friendship friendship = BlockExecutorTestData.Friendship(blocker, blocked, clock);
        Session session = BlockExecutorTestData.Session(blocker.Id, blocked.Id, clock, makeGoing: true);
        Block block = Block.Create(blocker.Id, blocked.Id, blocked.Username!.Value, clock);
        var calls = new List<string>();
        var repository = new BlockTestFriendshipRepository(calls) { Existing = friendship };
        var scope = new RecordingBlockEffectScopeProvider(
            new BlockEffectScope([session], []));
        var reminders = new RecordingReminderScheduleWriter();
        var executor = new BlockEffectsExecutor(scope, repository, reminders, clock);

        await executor.ExecuteAsync(block, [friendship], default);

        Assert.Equal(1, scope.Calls);
        Assert.Equal((blocker.Id, blocked.Id), scope.LastPair);
        Assert.Same(friendship, Assert.Single(repository.Removed));
        Assert.Equal(
            ParticipationStatus.Removed,
            session.Find(ParticipantRef.ForUser(blocked.Id))!.Status);
        SessionReminderCanceled canceled = Assert.Single(reminders.Applied);
        Assert.Equal(session.Id, canceled.SessionId);
        Assert.Equal(blocked.Id, canceled.UserId);
    }

    [Fact]
    public async Task Execute_DrainsBlockDerivedSessionEventsWithoutNotificationMaterialization_D105()
    {
        IClock clock = FriendTestData.Clock();
        User blocker = FriendTestData.Active("drain-blocker", "Drain Blocker");
        User blocked = FriendTestData.Active("drain-blocked", "Drain Blocked");
        Session control = BlockExecutorTestData.Session(blocker.Id, blocked.Id, clock, makeGoing: true);
        Block controlBlock = Block.Create(
            blocker.Id,
            blocked.Id,
            blocked.Username!.Value,
            clock);
        _ = BlockEffects.Apply(controlBlock, [], [control], [], clock);
        SessionUnconfirmed derived = Assert.Single(
            control.DequeueEvents().OfType<SessionUnconfirmed>());
        Assert.NotEmpty(
            RecipientComputer.ForSessionEvent(control, derived, RecipientFilter.None));

        Session actual = BlockExecutorTestData.Session(blocker.Id, blocked.Id, clock, makeGoing: true);
        Block block = Block.Create(blocker.Id, blocked.Id, blocked.Username!.Value, clock);
        var executor = new BlockEffectsExecutor(
            new RecordingBlockEffectScopeProvider(new BlockEffectScope([actual], [])),
            new BlockTestFriendshipRepository([]),
            new RecordingReminderScheduleWriter(),
            clock);

        await executor.ExecuteAsync(block, [], default);

        Assert.Empty(actual.DequeueEvents());
    }

}

internal sealed class RecordingBlockEffectScopeProvider(BlockEffectScope scope)
    : IBlockEffectScopeProvider
{
    internal int Calls { get; private set; }

    internal (UserId First, UserId Second)? LastPair { get; private set; }

    public Task<BlockEffectScope> LoadForUpdateAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken)
    {
        Calls++;
        LastPair = (first, second);
        return Task.FromResult(scope);
    }
}

internal sealed class RecordingReminderScheduleWriter : IReminderScheduleWriter
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

internal static class BlockExecutorTestData
{
    internal static Friendship Friendship(User first, User second, IClock clock)
    {
        Friendship friendship = PartyUp.Domain.Friendships.Friendship.Request(first, second, clock);
        friendship.Accept(second.Id, clock);
        _ = friendship.DequeueEvents();
        return friendship;
    }

    internal static Session Session(
        UserId organizer,
        UserId invitee,
        IClock clock,
        bool makeGoing)
    {
        var details = new SessionDetails(
            SessionTitle.Create("Block effect session"),
            clock.GetCurrentInstant() + Duration.FromHours(24),
            Duration15.FromMinutes(120),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 2,
            maxCapacity: null,
            isPrivate: false);
        Session session = PartyUp.Domain.Sessions.Session.Create(
            organizer,
            details,
            [invitee],
            clock);
        if (makeGoing)
        {
            session.Rsvp(ParticipantRef.ForUser(invitee), ParticipationStatus.Going, clock);
        }

        _ = session.DequeueEvents();
        return session;
    }

    internal static Poll Poll(UserId organizer, UserId invitee, IClock clock)
    {
        var details = new PollDetails(
            SessionTitle.Create("Block effect poll"),
            Duration15.FromMinutes(120),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            isPrivate: false);
        Poll poll = PartyUp.Domain.Polls.Poll.Create(
            organizer,
            details,
            PollDeadlineChoice.Hours24,
            [
                clock.GetCurrentInstant() + Duration.FromHours(72),
                clock.GetCurrentInstant() + Duration.FromHours(96),
            ],
            [invitee],
            clock);
        _ = poll.DequeueEvents();
        return poll;
    }
}
