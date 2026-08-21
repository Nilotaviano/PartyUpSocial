using NodaTime;
using PartyUp.Application.Polls;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Tests.Polls;

/// <summary>
/// D-225's poll arm at the application seam: the poll parent is locked, the votes follow the seat,
/// and a blocked pair with the organizer is the same generic refusal the session arm gives.
/// </summary>
public sealed class PollGuestUpgradeCommandTests
{
    [Fact]
    public async Task Upgrade_MovesTheVoterAndItsVotesUnderThePollLock_D225()
    {
        Harness harness = new();
        GuestIdentity guest = harness.ClaimGuest("Casey");
        harness.Poll.Vote(guest.Participant, harness.Poll.Options[0].Id, VoteChoice.Available, harness.Clock);
        _ = harness.Poll.DequeueEvents();

        UserId newcomer = UserId.New();
        GuestPollCommandResult result = await harness.UseCase.UpgradeAsync(
            harness.Poll.Id, harness.LinkId, newcomer, default);

        ParticipantRef userRef = ParticipantRef.ForUser(newcomer);
        Assert.True(result.Succeeded);
        Assert.NotNull(harness.Poll.FindParticipant(userRef));
        Assert.Null(harness.Poll.FindParticipant(guest.Participant));
        Assert.Equal(
            VoteChoice.Available,
            harness.Poll.FindVote(harness.Poll.Options[0].Id, userRef)!.Choice);
        Assert.Equal(GuestLinkState.Upgraded, harness.Link.State);

        IReadOnlyCollection<PollId> locked = Assert.Single(harness.Locks.LockedPollBatches);
        Assert.Contains(harness.Poll.Id, locked);
        Assert.Empty(harness.Locks.LockedUserBatches);
    }

    [Fact]
    public async Task Upgrade_ByABlockedPairOfTheOrganizer_IsGenericAndLeavesTheVoterUntouched_D225()
    {
        Harness harness = new();
        GuestIdentity guest = harness.ClaimGuest("Casey");
        _ = harness.Poll.DequeueEvents();

        UserId blocked = UserId.New();
        harness.Blocks.Add(Block.Create(
            harness.OrganizerId, blocked, Username.Create("blockeduser"), harness.Clock));

        GuestPollCommandResult result = await harness.UseCase.UpgradeAsync(
            harness.Poll.Id, harness.LinkId, blocked, default);

        Assert.False(result.Succeeded);
        Assert.Equal("upgrade_unavailable", result.Code);
        Assert.NotNull(harness.Poll.FindParticipant(guest.Participant));
        Assert.Null(harness.Poll.FindParticipant(ParticipantRef.ForUser(blocked)));
        Assert.Equal(GuestLinkState.Active, harness.Link.State);
    }

    private sealed class Harness
    {
        internal Harness()
        {
            Clock = new MutableClock(Instant.FromUtc(2026, 3, 2, 12, 0));
            Polls = new FakePollRepository();
            Locks = new FakePollLockRepository();
            Blocks = new FakeBlockRepository();
            OrganizerId = UserId.New();

            Poll = Poll.Create(
                OrganizerId,
                new PollDetails(
                    SessionTitle.Create("Raid poll"),
                    Platform.NotApplicable,
                    null,
                    SessionNotes.Empty,
                    minAttendance: 1,
                    maxCapacity: null,
                    visibility: ContentVisibility.Private),
                PollDeadlineChoice.Hours24,
                PollSpecs.Of(
                    Clock.GetCurrentInstant() + Duration.FromHours(5),
                    Clock.GetCurrentInstant() + Duration.FromHours(6)),
                [],
                Clock);
            _ = Poll.DequeueEvents();
            Polls.Add(Poll);

            (GuestLink link, _) = Poll.CreateGuestLink(Clock);
            Link = link;

            UseCase = new UpgradeGuestPollUseCase(
                Polls,
                Locks,
                Blocks,
                new RecordingPollNotificationMaterializer(),
                new RollbackAwarePollUnitOfWork(Polls),
                Clock);
        }

        internal MutableClock Clock { get; }

        internal FakePollRepository Polls { get; }

        internal FakePollLockRepository Locks { get; }

        internal FakeBlockRepository Blocks { get; }

        internal UserId OrganizerId { get; }

        internal Poll Poll { get; }

        internal GuestLink Link { get; }

        internal GuestLinkId LinkId => Link.Id;

        internal UpgradeGuestPollUseCase UseCase { get; }

        internal GuestIdentity ClaimGuest(string displayName) =>
            Poll.ClaimGuestLink(Link.Id, DisplayName.Create(displayName), Clock);
    }
}
