using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Polls;
using PartyUp.Application.Tests.Friends;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Polls;

/// <summary>
/// D-186 at the application seam: leaving is silent (D-088). A-016 makes notification records
/// transactional, so a stray one would commit alongside the leave — this asserts the materializer
/// is handed nothing to write, which is the only way a notification could be created.
/// </summary>
public sealed class PollLeaveUseCaseTests
{
    [Fact]
    public async Task LeavePoll_MaterializesNoNotificationForAnyone_D186_D088()
    {
        FakePollRepository polls = new();
        RecordingPollNotificationMaterializer materializer = new();
        RollbackAwarePollUnitOfWork unitOfWork = new(polls);
        FakeFriendshipRepository friendships = new();
        FakeBlockRepository blocks = new();
        MutableClock clock = new(Instant.FromUtc(2026, 3, 2, 12, 0));
        (UserId organizerId, UserId inviteeId) = friendships.MakeAccepted(clock);

        Poll poll = NewPoll(organizerId, inviteeId, clock);
        poll.Vote(ParticipantRef.ForUser(inviteeId), poll.Options[0].Id, VoteChoice.Available, clock);
        _ = poll.DequeueEvents();
        polls.Add(poll);

        var useCase = new PollVoteUseCases(
            polls, new FakePollLockRepository(), friendships, blocks, new FakeFriendGroupRepository(), materializer, unitOfWork, clock);

        PollCommandResult result = await useCase.LeavePollAsync(poll.Id, inviteeId, default);

        Assert.True(result.Succeeded);
        Assert.Equal(1, materializer.Calls);
        Assert.Empty(materializer.Events);
        Assert.Null(poll.FindParticipant(ParticipantRef.ForUser(inviteeId)));
        Assert.Empty(poll.VotesOf(ParticipantRef.ForUser(inviteeId)));
    }

    /// <summary>
    /// Fail-closed on the branch that matters: this caller CAN see the poll (an accepted friend of
    /// the organizer, D-125's non-private visibility) but holds no participant row, and is answered
    /// with exactly the <c>poll_not_found</c> a missing poll gives — "you are not on it" and "it
    /// does not exist" must stay indistinguishable.
    /// </summary>
    [Fact]
    public async Task LeavePoll_ByAVisibleNonParticipant_IsPollNotFound_D186()
    {
        FakePollRepository polls = new();
        RecordingPollNotificationMaterializer materializer = new();
        RollbackAwarePollUnitOfWork unitOfWork = new(polls);
        FakeFriendshipRepository friendships = new();
        FakeBlockRepository blocks = new();
        MutableClock clock = new(Instant.FromUtc(2026, 3, 2, 12, 0));

        User organizer = FriendTestData.Active($"o{Guid.NewGuid():N}"[..12], "Organizer");
        User invitee = FriendTestData.Active($"i{Guid.NewGuid():N}"[..12], "Invitee");
        User outsider = FriendTestData.Active($"x{Guid.NewGuid():N}"[..12], "Outsider");
        Befriend(friendships, organizer, invitee, clock);
        Befriend(friendships, organizer, outsider, clock);

        Poll poll = NewPoll(organizer.Id, invitee.Id, clock);
        _ = poll.DequeueEvents();
        polls.Add(poll);

        var useCase = new PollVoteUseCases(
            polls, new FakePollLockRepository(), friendships, blocks, new FakeFriendGroupRepository(), materializer, unitOfWork, clock);

        PollCommandResult result = await useCase.LeavePollAsync(poll.Id, outsider.Id, default);

        Assert.False(result.Succeeded);
        Assert.Equal("poll_not_found", result.Code);
        Assert.Equal(0, materializer.Calls);
        Assert.Equal(2, poll.ActiveParticipantCount);
    }

    private static void Befriend(FakeFriendshipRepository friendships, User first, User second, IClock clock)
    {
        Friendship friendship = Friendship.Request(first, second, clock);
        friendship.Accept(second.Id, clock);
        _ = friendship.DequeueEvents();
        friendships.Items.Add(friendship);
    }

    private static Poll NewPoll(UserId organizerId, UserId inviteeId, MutableClock clock) =>
        Poll.Create(
            organizerId,
            new PollDetails(
                SessionTitle.Create("Raid poll"),
                Platform.NotApplicable,
                null,
                SessionNotes.Empty,
                minAttendance: 1,
                maxCapacity: null,
                visibility: ContentVisibility.Public),
            PollDeadlineChoice.Hours24,
            PollSpecs.Of(clock.GetCurrentInstant() + Duration.FromHours(5), clock.GetCurrentInstant() + Duration.FromHours(6)),
            [inviteeId],
            clock);
}

internal sealed class RecordingPollNotificationMaterializer : IPollNotificationMaterializer
{
    internal int Calls { get; private set; }

    internal List<IDomainEvent> Events { get; } = [];

    public Task MaterializeAsync(Poll poll, IReadOnlyList<IDomainEvent> events, CancellationToken cancellationToken)
    {
        Calls++;
        Events.AddRange(events);
        return Task.CompletedTask;
    }
}
