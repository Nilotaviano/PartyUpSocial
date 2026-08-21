using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Tests.Polls;

/// <summary>
/// D-225's poll mirror: the guest voter row and its votes follow the guest onto the account, unless
/// the account already votes here — then the account's votes stand and the guest's are dropped.
/// </summary>
public class PollGuestUpgradeTests
{
    [Fact]
    public void UpgradeGuest_WithNoParticipantRowForTheAccount_RePointsTheVoterAndItsVotes_D225()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.GuestOnly(clock);
        (GuestLink link, _) = fixture.Poll.CreateGuestLink(clock);
        GuestIdentity guest = fixture.Poll.ClaimGuestLink(link.Id, DisplayName.Create("Robin"), clock);
        ParticipantRef guestRef = guest.Participant;

        fixture.Poll.Vote(guestRef, fixture.Option(0).Id, VoteChoice.Available, clock);
        fixture.Poll.Vote(guestRef, fixture.Option(1).Id, VoteChoice.Unavailable, clock);
        PollParticipant voter = fixture.Row(guestRef);
        NodaTime.Instant? firstResponseAt = voter.FirstResponseAt;

        UserId user = UserId.New();
        fixture.Poll.UpgradeGuest(guest.Id, user, clock);

        ParticipantRef userRef = ParticipantRef.ForUser(user);
        PollParticipant upgraded = fixture.Row(userRef);
        Assert.Same(voter, upgraded);
        Assert.True(upgraded.IsRegistered);
        Assert.Equal(firstResponseAt, upgraded.FirstResponseAt);
        Assert.Null(fixture.Poll.FindParticipant(guestRef));

        Assert.Empty(fixture.Poll.VotesOf(guestRef));
        Assert.Equal(2, fixture.Poll.VotesOf(userRef).Count);
        Assert.Equal(VoteChoice.Available, fixture.Poll.FindVote(fixture.Option(0).Id, userRef)!.Choice);
        Assert.Equal(VoteChoice.Unavailable, fixture.Poll.FindVote(fixture.Option(1).Id, userRef)!.Choice);

        Assert.Equal(GuestLinkState.Upgraded, link.State);
        Assert.Equal(GuestLinkView.RevokedNotice, fixture.Poll.ResolveGuestLink(link.Id, clock));
        Assert.Equal(user, guest.UpgradedToUserId);
    }

    [Fact]
    public void UpgradeGuest_WhenTheAccountAlreadyVotes_KeepsTheAccountRowAndDropsTheGuests_D225()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, inviteeCount: 1, minAttendance: 1);
        UserId invitee = fixture.Invitee(0);
        ParticipantRef inviteeRef = fixture.InviteeRef(0);
        fixture.Poll.Vote(inviteeRef, fixture.Option(0).Id, VoteChoice.Available, clock);

        (GuestLink link, _) = fixture.Poll.CreateGuestLink(clock);
        GuestIdentity guest = fixture.Poll.ClaimGuestLink(link.Id, DisplayName.Create("Robin"), clock);
        ParticipantRef guestRef = guest.Participant;
        fixture.Poll.Vote(guestRef, fixture.Option(0).Id, VoteChoice.Unavailable, clock);
        fixture.Poll.Vote(guestRef, fixture.Option(1).Id, VoteChoice.Maybe, clock);
        PollParticipant registered = fixture.Row(inviteeRef);

        fixture.Poll.UpgradeGuest(guest.Id, invitee, clock);

        // The account's own vote is authoritative — the guest's contradicting one is dropped, not merged.
        Assert.Same(registered, fixture.Row(inviteeRef));
        Assert.Equal(VoteChoice.Available, fixture.Poll.FindVote(fixture.Option(0).Id, inviteeRef)!.Choice);
        Assert.Null(fixture.Poll.FindVote(fixture.Option(1).Id, inviteeRef));
        Assert.Null(fixture.Poll.FindParticipant(guestRef));
        Assert.Empty(fixture.Poll.VotesOf(guestRef));

        Assert.Equal(GuestLinkState.Upgraded, link.State);
        Assert.Equal(invitee, guest.UpgradedToUserId);

        // Terminal: the spent token can never take the seat again.
        DomainException again = Assert.Throws<DomainException>(
            () => fixture.Poll.UpgradeGuest(guest.Id, invitee, clock));
        Assert.Equal(DomainError.Conflict, again.Error);
    }
}
