using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Tests.Polls;

public class PollGuestTests
{
    [Fact]
    public void CreateGuestLink_MintsAnUnclaimedLinkThatCreatesNoParticipant_D125()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.GuestOnly(clock);

        (GuestLink link, string token) = fixture.Poll.CreateGuestLink(clock);

        Assert.False(link.IsClaimed);
        Assert.Single(fixture.Poll.Participants);
        Assert.Equal(link.Id, fixture.Poll.FindLinkByToken(token)!.Id);
    }

    [Fact]
    public void ClaimGuestLink_TurnsTheGuestIntoAnActiveVoter_D017()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.GuestOnly(clock);
        (GuestLink link, _) = fixture.Poll.CreateGuestLink(clock);

        GuestIdentity identity = fixture.Poll.ClaimGuestLink(link.Id, DisplayName.Create("Robin"), clock);

        Assert.Equal("Robin", identity.DisplayName.Value);
        Assert.True(fixture.Row(identity.Participant).IsActive);
        Assert.Null(fixture.Row(identity.Participant).FirstResponseAt);
    }

    [Fact]
    public void ClaimGuestLink_RejectsASecondClaim_D017()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.GuestOnly(clock);
        (GuestLink link, _) = fixture.Poll.CreateGuestLink(clock);
        fixture.Poll.ClaimGuestLink(link.Id, DisplayName.Create("Robin"), clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.ClaimGuestLink(link.Id, DisplayName.Create("Alex"), clock));

        Assert.Equal(DomainError.Conflict, error.Error);
    }

    [Fact]
    public void ClaimGuestLink_ClosesWithVoting_D079()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.GuestOnly(clock);
        (GuestLink link, _) = fixture.Poll.CreateGuestLink(clock);
        FakeClock atDeadline = TestPolls.At(TestPolls.DefaultDeadline);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.ClaimGuestLink(link.Id, DisplayName.Create("Robin"), atDeadline));

        Assert.Equal(DomainError.WindowClosed, error.Error);
        Assert.Equal(GuestLinkView.LiveView, fixture.Poll.ResolveGuestLink(link.Id, atDeadline));
    }

    [Fact]
    public void RenameGuest_IsLastWriteWins_D126()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.GuestOnly(clock);
        (GuestLinkId linkId, ParticipantRef guest) = PollGuests.Claim(fixture.Poll, clock, "Robin");

        fixture.Poll.RenameGuest(linkId, DisplayName.Create("Robin H."), clock);
        fixture.Poll.RenameGuest(linkId, DisplayName.Create("Robin Hood"), clock);

        GuestIdentity identity = fixture.Poll.GuestIdentities.Single(g => g.Participant == guest);
        Assert.Equal("Robin Hood", identity.DisplayName.Value);
    }

    [Fact]
    public void RemoveGuest_RemovesTheVoterAndRevokesTheirLink_D044()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.GuestOnly(clock);
        (GuestLinkId linkId, ParticipantRef guest) = PollGuests.Claim(fixture.Poll, clock, "Robin");

        fixture.Poll.RemoveGuest(guest.RequireGuestIdentityId(), clock);

        Assert.True(fixture.Row(guest).IsRemoved);
        Assert.Equal(GuestLinkView.RevokedNotice, fixture.Poll.ResolveGuestLink(linkId, clock));
    }

    [Fact]
    public void RevokeGuestLink_RevokesAnUnclaimedLinkWithNoRosterEffect_D150()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.GuestOnly(clock);
        (GuestLink link, _) = fixture.Poll.CreateGuestLink(clock);

        fixture.Poll.RevokeGuestLink(link.Id, clock);

        Assert.Equal(GuestLinkState.RevokedByRemoval, link.State);
        Assert.Single(fixture.Poll.Participants);
    }

    [Fact]
    public void RevokeGuestLink_RejectsAClaimedLink_D044()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.GuestOnly(clock);
        (GuestLinkId linkId, _) = PollGuests.Claim(fixture.Poll, clock, "Robin");

        DomainException error = Assert.Throws<DomainException>(() => fixture.Poll.RevokeGuestLink(linkId, clock));

        Assert.Equal(DomainError.Conflict, error.Error);
    }

    [Fact]
    public void ResolveGuestLink_ShowsTheLiveViewWhileClosed_D048()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.GuestOnly(clock);
        (GuestLink link, _) = fixture.Poll.CreateGuestLink(clock);
        FakeClock closed = TestPolls.At(TestPolls.DefaultDeadline + Duration.FromHours(1));

        Assert.Equal(GuestLinkView.LiveView, fixture.Poll.ResolveGuestLink(link.Id, closed));
        Assert.Equal(PollStatus.Closed, fixture.Poll.Status);
    }

    [Fact]
    public void CreateGuestLink_IsRejectedOnceVotingCloses_D079()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.GuestOnly(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Poll.CreateGuestLink(TestPolls.At(TestPolls.DefaultDeadline)));

        Assert.Equal(DomainError.WindowClosed, error.Error);
    }
}
