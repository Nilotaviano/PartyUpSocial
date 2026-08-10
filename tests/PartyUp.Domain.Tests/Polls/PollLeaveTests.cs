using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Tests.Polls;

/// <summary>
/// D-186: a registered participant removes themselves. The participation is undone — the votes are
/// withdrawn and the participant row is erased — rather than frozen the way the block and deletion
/// executors' <see cref="Poll.RemoveParticipant"/> freezes it, which is why the two transitions are
/// separate and why <see cref="RemoveParticipant_StillOnlyMarksTheRowRemovedAndKeepsItsVotes_D186"/>
/// guards the older one.
/// </summary>
public class PollLeaveTests
{
    [Fact]
    public void Leave_WithdrawsTheVotesAndErasesTheParticipantRow_D186()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Available, clock);
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(1).Id, VoteChoice.Maybe, clock);
        Assert.NotNull(fixture.Row(fixture.InviteeRef(0)).FirstResponseAt);
        int rosterBefore = fixture.Poll.ActiveParticipantCount;

        fixture.Poll.Leave(fixture.Invitee(0), clock);

        Assert.Empty(fixture.Poll.VotesOf(fixture.InviteeRef(0)));
        Assert.Null(fixture.Poll.FindParticipant(fixture.InviteeRef(0)));
        Assert.Equal(rosterBefore - 1, fixture.Poll.ActiveParticipantCount);
    }

    [Fact]
    public void Leave_IsSilentAndRaisesNothing_D186_D088()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Available, clock);
        fixture.Drain();

        fixture.Poll.Leave(fixture.Invitee(0), clock);

        Assert.Empty(fixture.Drain());
    }

    /// <summary>
    /// The realistic failure mode of a delete-based design is an over-broad delete, and it would be
    /// silent: nothing else on the poll may move.
    /// </summary>
    [Fact]
    public void Leave_DisturbsNoOtherParticipantsVotesRequestsOrGuests_D186()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2);
        (_, ParticipantRef guest) = PollGuests.Claim(fixture.Poll, clock, "Robin");

        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Available, clock);
        fixture.Poll.Vote(fixture.InviteeRef(1), fixture.Option(0).Id, VoteChoice.Maybe, clock);
        fixture.Poll.Vote(fixture.InviteeRef(1), fixture.Option(1).Id, VoteChoice.Unavailable, clock);
        fixture.Poll.Vote(fixture.OrganizerRef, fixture.Option(0).Id, VoteChoice.Available, clock);
        fixture.Poll.Vote(guest, fixture.Option(1).Id, VoteChoice.Available, clock);

        UserId outsider = UserId.New();
        JoinRequest request = fixture.Poll.OpenJoinRequest(outsider, clock);
        Instant stayerFirstResponse = fixture.Row(fixture.InviteeRef(1)).FirstResponseAt!.Value;

        fixture.Poll.Leave(fixture.Invitee(0), clock);

        Assert.Equal(2, fixture.Poll.VotesOf(fixture.InviteeRef(1)).Count);
        Assert.Single(fixture.Poll.VotesOf(fixture.OrganizerRef));
        Assert.Single(fixture.Poll.VotesOf(guest));
        Assert.Equal(stayerFirstResponse, fixture.Row(fixture.InviteeRef(1)).FirstResponseAt);
        Assert.NotNull(fixture.Poll.FindParticipant(guest));
        Assert.True(fixture.Poll.FindParticipant(guest)!.IsActive);
        Assert.Single(fixture.Poll.GuestIdentities);
        Assert.True(fixture.Poll.JoinRequests.Single(r => r.Id == request.Id).IsPending);
        Assert.Equal(2, fixture.Poll.Options.Count);
    }

    /// <summary>
    /// The row is erased, so a second leave is indistinguishable from never having been on the poll
    /// and errors as <see cref="DomainError.NotFound"/> rather than succeeding idempotently. That is
    /// the deliberate choice: the application layer maps it to the same fail-closed
    /// <c>poll_not_found</c> a stranger gets, so the second call leaks nothing the first did not.
    /// </summary>
    [Fact]
    public void Leave_ASecondTime_IsNotFound_D186()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.Leave(fixture.Invitee(0), clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Leave(fixture.Invitee(0), clock));

        Assert.Equal(DomainError.NotFound, error.Error);
    }

    [Fact]
    public void Leave_ByTheOrganizer_IsRefused_D186()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Leave(fixture.Organizer, clock));

        Assert.Equal(DomainError.Conflict, error.Error);
        Assert.NotNull(fixture.Poll.FindParticipant(fixture.OrganizerRef));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Leave_OnATerminalPoll_MutatesNothing_D186(bool canceled)
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Available, clock);

        if (canceled)
        {
            fixture.Poll.Cancel(clock);
        }
        else
        {
            clock.Reset(TestPolls.DefaultDeadline);
            fixture.Poll.Finalize(fixture.Option(0).Id, clock);
        }

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.Leave(fixture.Invitee(0), clock));

        Assert.Equal(DomainError.TerminalState, error.Error);
        Assert.NotNull(fixture.Poll.FindParticipant(fixture.InviteeRef(0)));
        Assert.Single(fixture.Poll.VotesOf(fixture.InviteeRef(0)));
    }

    /// <summary>
    /// D-186's explicit carve-out from D-172: write-once binds within a participation, not across a
    /// leave. Rejoining is by the route they first joined — here the D-118 join request — and is not
    /// blocked the way <see cref="Poll.RemoveParticipant"/>'s <c>Removed</c> row blocks it.
    /// </summary>
    [Fact]
    public void Leave_ThenRejoinAndVote_StampsAFreshFirstResponseAt_D186_D172()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Available, clock);
        Instant original = fixture.Row(fixture.InviteeRef(0)).FirstResponseAt!.Value;

        fixture.Poll.Leave(fixture.Invitee(0), clock);

        clock.Advance(Duration.FromHours(3));
        JoinRequest request = fixture.Poll.OpenJoinRequest(fixture.Invitee(0), clock);
        fixture.Poll.ApproveJoinRequest(request.Id, clock);
        Assert.Null(fixture.Row(fixture.InviteeRef(0)).FirstResponseAt);

        clock.Advance(Duration.FromHours(1));
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(1).Id, VoteChoice.Maybe, clock);

        Instant rejoined = fixture.Row(fixture.InviteeRef(0)).FirstResponseAt!.Value;
        Assert.Equal(TestPolls.Hours(4), rejoined);
        Assert.True(rejoined > original);
    }

    [Fact]
    public void Leave_BeforeFinalization_DropsThemFromTheAllocatedRoster_D186()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 2);
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Available, clock);
        fixture.Poll.Vote(fixture.InviteeRef(1), fixture.Option(0).Id, VoteChoice.Available, clock);

        fixture.Poll.Leave(fixture.Invitee(0), clock);

        clock.Reset(TestPolls.DefaultDeadline);
        PollFinalizationResult result = fixture.Poll.Finalize(fixture.Option(0).Id, clock);

        Assert.DoesNotContain(
            result.Session.Participations,
            participation => participation.Matches(fixture.InviteeRef(0)));
        Assert.Contains(
            result.Session.Participations,
            participation => participation.Matches(fixture.InviteeRef(1)));
    }

    /// <summary>
    /// The regression guard the D-186 work is fenced by: the block executor (§4, D-076 masking) and
    /// the deletion executor (§11 step 3, D-092) call <see cref="Poll.RemoveParticipant"/>, and both
    /// depend on it doing exactly what it did before — freeze the row as <c>Removed</c>, keep its
    /// votes for §6.3's exclusion rules, leave <c>FirstResponseAt</c> alone, and stay a permanent
    /// bar to rejoining. Nothing here may drift toward D-186's semantics.
    /// </summary>
    [Fact]
    public void RemoveParticipant_StillOnlyMarksTheRowRemovedAndKeepsItsVotes_D186()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.Vote(fixture.InviteeRef(0), fixture.Option(0).Id, VoteChoice.Available, clock);
        Instant firstResponse = fixture.Row(fixture.InviteeRef(0)).FirstResponseAt!.Value;

        fixture.Poll.RemoveParticipant(fixture.InviteeRef(0), clock);

        PollParticipant row = fixture.Row(fixture.InviteeRef(0));
        Assert.Equal(PollParticipantStatus.Removed, row.Status);
        Assert.False(row.IsActive);
        Assert.Equal(firstResponse, row.FirstResponseAt);
        Assert.Single(fixture.Poll.VotesOf(fixture.InviteeRef(0)));

        // Removal stays permanent (§5.3, §12-24) — the leave path must not have softened it.
        Assert.Equal(
            DomainError.RemovalIsPermanent,
            Assert.Throws<DomainException>(() => fixture.Poll.OpenJoinRequest(fixture.Invitee(0), clock)).Error);
        Assert.Equal(
            DomainError.RemovalIsPermanent,
            Assert.Throws<DomainException>(() => fixture.Poll.Leave(fixture.Invitee(0), clock)).Error);
    }

    /// <summary>D-186: guests are unchanged — their removal stays organizer-driven (D-044).</summary>
    [Fact]
    public void Leave_HasNoGuestSurface_D186()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.GuestOnly(clock);
        (_, ParticipantRef guest) = PollGuests.Claim(fixture.Poll, clock, "Robin");
        fixture.Poll.Vote(guest, fixture.Option(0).Id, VoteChoice.Available, clock);

        // A guest holds no UserId, so Poll.Leave cannot name one: the only removal path they have is
        // the organizer's RemoveGuest, which freezes rather than erases.
        fixture.Poll.RemoveGuest(guest.RequireGuestIdentityId(), clock);

        Assert.Equal(PollParticipantStatus.Removed, fixture.Poll.FindParticipant(guest)!.Status);
        Assert.Single(fixture.Poll.VotesOf(guest));
    }
}
