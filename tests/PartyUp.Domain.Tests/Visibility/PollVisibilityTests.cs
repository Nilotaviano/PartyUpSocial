using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Tests.Polls;
using PartyUp.Domain.Visibility;

namespace PartyUp.Domain.Tests.Visibility;

public class PollVisibilityTests
{
    private static PollUnderTest PrivatePoll(FakeClock clock, int inviteeCount = 2)
    {
        UserId organizer = UserId.New();
        UserId[] invitees = [.. Enumerable.Range(0, inviteeCount).Select(_ => UserId.New())];

        Poll poll = Poll.Create(
            organizer,
            TestPolls.Details(visibility: ContentVisibility.Private),
            PollDeadlineChoice.Hours24,
            TestPolls.Specs([TestPolls.Hours(72), TestPolls.Hours(96)]),
            invitees,
            clock);

        return new PollUnderTest(poll, organizer, invitees);
    }

    [Fact]
    public void Resolve_GivesTheOrganizerDirectAccess_D019()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest p = TestPolls.New(clock);

        Assert.Equal(VisibilityGrant.Direct, VisibilityEngine.ResolvePoll(p.Poll, TestViewers.Of(p.Organizer)).Grant);
    }

    [Fact]
    public void Resolve_GivesAnActivePollParticipantDirectAccess_D018()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest p = TestPolls.New(clock);

        Assert.Equal(VisibilityGrant.Direct, VisibilityEngine.ResolvePoll(p.Poll, TestViewers.Of(p.Invitee(0))).Grant);
    }

    [Fact]
    public void Resolve_DropsARemovedPollParticipant_D077()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest p = TestPolls.New(clock);
        p.Poll.RemoveParticipant(p.InviteeRef(0), clock);

        Assert.False(VisibilityEngine.ResolvePoll(p.Poll, TestViewers.Of(p.Invitee(0))).IsVisible);
        Assert.False(VisibilityEngine.ResolvePoll(p.Poll, TestViewers.FriendOf(p.Invitee(0))).IsVisible);
    }

    [Fact]
    public void Resolve_GivesAPendingPollJoinRequesterDirectAccess_D043()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest p = TestPolls.New(clock);
        UserId requester = UserId.New();
        p.Poll.OpenJoinRequest(requester, clock);

        Assert.Equal(VisibilityGrant.Direct, VisibilityEngine.ResolvePoll(p.Poll, TestViewers.Of(requester)).Grant);
    }

    [Fact]
    public void Resolve_GrantsARejectedPollRequesterNothing_D043()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest p = TestPolls.New(clock);
        UserId requester = UserId.New();
        JoinRequest request = p.Poll.OpenJoinRequest(requester, clock);
        p.Poll.RejectJoinRequest(request.Id, clock);

        Assert.False(VisibilityEngine.ResolvePoll(p.Poll, TestViewers.Of(requester)).IsVisible);
    }

    [Fact]
    public void Resolve_InheritsThroughAVoterWhoHasNotVotedYet_D018()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest p = TestPolls.New(clock);

        VisibilityResult result = VisibilityEngine.ResolvePoll(p.Poll, TestViewers.FriendOf(p.Invitee(0)));

        Assert.Equal(VisibilityGrant.Inherited, result.Grant);
        Assert.Equal(p.Invitee(0), result.ExtenderId);
    }

    [Fact]
    public void Resolve_StopsInheritingThroughAVoterWhoseEveryVoteIsUnavailable_D051()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest p = TestPolls.New(clock);
        p.Poll.Vote(p.InviteeRef(0), p.Option(0).Id, VoteChoice.Unavailable, clock);
        p.Poll.Vote(p.InviteeRef(0), p.Option(1).Id, VoteChoice.Unavailable, clock);

        Assert.True(p.Poll.VotedUnavailableThroughout(p.InviteeRef(0)));
        Assert.False(VisibilityEngine.ResolvePoll(p.Poll, TestViewers.FriendOf(p.Invitee(0))).IsVisible);
    }

    [Fact]
    public void Resolve_KeepsDirectAccessForAVoterWhoseEveryVoteIsUnavailable_D051()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest p = TestPolls.New(clock);
        p.Poll.Vote(p.InviteeRef(0), p.Option(0).Id, VoteChoice.Unavailable, clock);
        p.Poll.Vote(p.InviteeRef(0), p.Option(1).Id, VoteChoice.Unavailable, clock);

        Assert.Equal(VisibilityGrant.Direct, VisibilityEngine.ResolvePoll(p.Poll, TestViewers.Of(p.Invitee(0))).Grant);
    }

    [Theory]
    [InlineData(VoteChoice.Available)]
    [InlineData(VoteChoice.Maybe)]
    public void Resolve_InheritsThroughAVoterWithOneNonUnavailableVote_D051(VoteChoice choice)
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest p = TestPolls.New(clock);
        p.Poll.Vote(p.InviteeRef(0), p.Option(0).Id, VoteChoice.Unavailable, clock);
        p.Poll.Vote(p.InviteeRef(0), p.Option(1).Id, choice, clock);

        Assert.Equal(VisibilityGrant.Inherited, VisibilityEngine.ResolvePoll(p.Poll, TestViewers.FriendOf(p.Invitee(0))).Grant);
    }

    [Fact]
    public void Resolve_ResumesInheritingWhenAnAllUnavailableVoterRevisesAVote_D051()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest p = TestPolls.New(clock);
        Viewer friendOfVoter = TestViewers.FriendOf(p.Invitee(0));
        p.Poll.Vote(p.InviteeRef(0), p.Option(0).Id, VoteChoice.Unavailable, clock);
        p.Poll.Vote(p.InviteeRef(0), p.Option(1).Id, VoteChoice.Unavailable, clock);
        Assert.False(VisibilityEngine.ResolvePoll(p.Poll, friendOfVoter).IsVisible);

        p.Poll.Vote(p.InviteeRef(0), p.Option(1).Id, VoteChoice.Available, clock);

        Assert.Equal(VisibilityGrant.Inherited, VisibilityEngine.ResolvePoll(p.Poll, friendOfVoter).Grant);
    }

    [Fact]
    public void Resolve_KeepsTheOrganizerAsAnExtenderEvenWhenTheyVoteUnavailableThroughout_D085()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest p = TestPolls.New(clock);
        p.Poll.Vote(p.OrganizerRef, p.Option(0).Id, VoteChoice.Unavailable, clock);
        p.Poll.Vote(p.OrganizerRef, p.Option(1).Id, VoteChoice.Unavailable, clock);

        VisibilityResult result = VisibilityEngine.ResolvePoll(p.Poll, TestViewers.FriendOf(p.Organizer));

        Assert.Equal(VisibilityGrant.Inherited, result.Grant);
        Assert.Equal(p.Organizer, result.ExtenderId);
    }

    [Fact]
    public void Resolve_HidesAPrivatePollFromAFriendOfAnExtender_D019()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest p = PrivatePoll(clock);

        Assert.False(VisibilityEngine.ResolvePoll(p.Poll, TestViewers.FriendOf(p.Organizer)).IsVisible);
        Assert.Equal(VisibilityGrant.Direct, VisibilityEngine.ResolvePoll(p.Poll, TestViewers.Of(p.Invitee(0))).Grant);
    }

    [Fact]
    public void Resolve_HidesAPollFromAUserBlockedWithItsOrganizer_D065()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest p = TestPolls.New(clock);
        Viewer blocked = TestViewers.Of(p.Invitee(0), blocked: [p.Organizer]);

        Assert.False(VisibilityEngine.ResolvePoll(p.Poll, blocked).IsVisible);
    }

    [Fact]
    public void Describe_LeavesGuestVotersOutOfTheExtenderSet_D018()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest p = TestPolls.New(clock, inviteeCount: 1);
        PollGuests.Claim(p.Poll, clock, "Casey");

        VisibilityTarget target = PollVisibility.Describe(p.Poll);

        Assert.Equal(3, p.Poll.Participants.Count);
        Assert.Equal(2, target.Members.Count);
    }

    [Fact]
    public void Describe_MarksTheOrganizerRowLikeAnyOtherActiveVoter_D018()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest p = TestPolls.New(clock, inviteeCount: 1);

        VisibilityTarget target = PollVisibility.Describe(p.Poll);

        Assert.True(target.TryFindMember(p.Organizer, out VisibilityMember organizer));
        Assert.True(organizer.KeepsDirectAccess);
        Assert.True(organizer.ExtendsAccess);
    }

    [Fact]
    public void Require_ReportsAnInvisiblePollAsNotFound_D019()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest p = PrivatePoll(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => VisibilityEngine.RequirePoll(p.Poll, TestViewers.Stranger()));

        Assert.Equal(DomainError.NotFound, error.Error);
    }

    [Fact]
    public void Describe_RejectsNullArguments_D019()
    {
        Assert.Throws<ArgumentNullException>(() => PollVisibility.Describe(null!));
        Assert.Throws<ArgumentNullException>(() => PollVisibility.KeepsDirectAccess(null!));
        Assert.Throws<ArgumentNullException>(() => PollVisibility.ExtendsAccess(null!, votedUnavailableThroughout: false));
    }
}
