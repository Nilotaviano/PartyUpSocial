using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Tests.Sessions;

public class SessionGuestTests
{
    [Fact]
    public void CreateGuestLink_IsUnclaimedAndReservesNoCapacity_D115()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock, minAttendance: 1, maxCapacity: 2);

        (GuestLink link, string token) = fixture.Session.CreateGuestLink(clock);

        Assert.False(link.IsClaimed);
        Assert.True(link.IsActive);
        Assert.Equal(fixture.Session.Id, link.SessionId);
        Assert.Null(link.PollId);
        Assert.Equal(1, fixture.Session.ActiveOccupancy);
        Assert.NotEmpty(token);
    }

    [Fact]
    public void CreateToken_IsCsprngAndStoredOnlyAsAHash_D115()
    {
        string first = GuestLink.CreateToken();
        string second = GuestLink.CreateToken();

        Assert.NotEqual(first, second);
        Assert.DoesNotContain("+", first, StringComparison.Ordinal);
        Assert.DoesNotContain("/", first, StringComparison.Ordinal);
        Assert.DoesNotContain("=", first, StringComparison.Ordinal);
        Assert.Equal(43, first.Length);
        Assert.Equal(GuestLink.HashToken(first), GuestLink.HashToken(first));
        Assert.NotEqual(GuestLink.HashToken(first), GuestLink.HashToken(second));
    }

    [Fact]
    public void GuestLink_MatchesOnlyItsOwnToken_D115()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock);
        (GuestLink link, string token) = fixture.Session.CreateGuestLink(clock);

        Assert.True(link.MatchesToken(token));
        Assert.False(link.MatchesToken(GuestLink.CreateToken()));
        Assert.False(link.MatchesToken(""));
        Assert.NotEqual(token, link.TokenHash);
        Assert.Same(link, fixture.Session.FindLinkByToken(token));
    }

    [Fact]
    public void ClaimGuestLink_CreatesTheIdentityAndTheParticipation_D017()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock, minAttendance: 2);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);

        GuestIdentity guest = fixture.Session.ClaimGuestLink(
            link.Id, DisplayName.Create("Robin"), ParticipationStatus.Going, clock);

        Participation row = fixture.Row(guest.Participant);
        Assert.Equal("Robin", guest.DisplayName.Value);
        Assert.Equal(ParticipationStatus.Going, row.Status);
        Assert.Equal(ParticipationOrigin.GuestLink, row.Origin);
        Assert.True(row.IsGuest);
        Assert.Equal(guest.Id, link.GuestIdentityId);
    }

    [Fact]
    public void ClaimGuestLink_GoingGuestsCountTowardTheMinimum_D062()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock, minAttendance: 2);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);
        Assert.Equal(SessionStatus.Unconfirmed, fixture.Session.Status);

        fixture.Session.ClaimGuestLink(link.Id, DisplayName.Create("Robin"), ParticipationStatus.Going, clock);

        Assert.Equal(2, fixture.Session.ActiveGoingCount);
        Assert.Equal(SessionStatus.Confirmed, fixture.Session.Status);
    }

    [Fact]
    public void ClaimGuestLink_AtCapacity_WaitlistsLikeAnyone_D084()
    {
        // §12-6: an unclaimed link is not a slot reservation.
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock, minAttendance: 1, maxCapacity: 1);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);
        fixture.Drain();

        GuestIdentity guest = fixture.Session.ClaimGuestLink(
            link.Id, DisplayName.Create("Robin"), ParticipationStatus.Going, clock);

        Assert.True(fixture.Row(guest.Participant).IsWaitlisted);
        Assert.Single(fixture.Drain().OfType<SessionWaitlistAdded>());
    }

    [Fact]
    public void ClaimGuestLink_ClosesAtTheSessionStart_D081()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);

        clock.Reset(fixture.Session.StartTime);

        DomainException error = Assert.Throws<DomainException>(
            () =>
            {
                _ = fixture.Session.ClaimGuestLink(
                    link.Id, DisplayName.Create("Robin"), ParticipationStatus.Going, clock);
            });

        Assert.Equal(DomainError.WindowClosed, error.Error);
    }

    [Fact]
    public void ClaimGuestLink_RejectsASecondClaimOfTheSameLink_D115()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);
        fixture.Session.ClaimGuestLink(link.Id, DisplayName.Create("Robin"), ParticipationStatus.Going, clock);

        DomainException error = Assert.Throws<DomainException>(
            () =>
            {
                _ = fixture.Session.ClaimGuestLink(
                    link.Id, DisplayName.Create("Alex"), ParticipationStatus.Going, clock);
            });

        Assert.Equal(DomainError.Conflict, error.Error);
    }

    [Fact]
    public void ClaimGuestLink_RejectsAnEmptyDisplayName_D017()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);

        DomainException error = Assert.Throws<DomainException>(
            () =>
            {
                _ = fixture.Session.ClaimGuestLink(link.Id, default, ParticipationStatus.Going, clock);
            });

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void RespondAsGuest_IsLastWriteWins_D126()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock, minAttendance: 1, maxCapacity: 4);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);
        GuestIdentity guest = fixture.Session.ClaimGuestLink(
            link.Id, DisplayName.Create("Robin"), ParticipationStatus.Going, clock);

        // Two devices, no inter-device conflict check: the last write stands.
        fixture.Session.RespondAsGuest(link.Id, ParticipationStatus.Maybe, clock);
        fixture.Session.RespondAsGuest(link.Id, ParticipationStatus.Going, clock);
        fixture.Session.RespondAsGuest(link.Id, ParticipationStatus.Declined, clock);

        Assert.Equal(ParticipationStatus.Declined, fixture.Row(guest.Participant).Status);
    }

    [Fact]
    public void RespondAsGuest_DecliningTwiceIsANoOp_D126()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);
        GuestIdentity guest = fixture.Session.ClaimGuestLink(
            link.Id, DisplayName.Create("Robin"), ParticipationStatus.Pending, clock);

        fixture.Session.RespondAsGuest(link.Id, ParticipationStatus.Declined, clock);
        fixture.Session.RespondAsGuest(link.Id, ParticipationStatus.Declined, clock);

        Assert.Equal(ParticipationStatus.Declined, fixture.Row(guest.Participant).Status);
    }

    [Fact]
    public void RespondAsGuest_ThroughARevokedLink_IsRejected_D044()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock, minAttendance: 1, maxCapacity: 4);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);
        GuestIdentity guest = fixture.Session.ClaimGuestLink(
            link.Id, DisplayName.Create("Robin"), ParticipationStatus.Going, clock);

        fixture.Session.RemoveGuest(guest.Id, clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Session.RespondAsGuest(link.Id, ParticipationStatus.Maybe, clock));

        Assert.Equal(DomainError.Forbidden, error.Error);
    }

    [Fact]
    public void RespondAsGuest_ThroughAnUnclaimedLink_IsRejected_D115()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Session.RespondAsGuest(link.Id, ParticipationStatus.Going, clock));

        Assert.Equal(DomainError.Conflict, error.Error);
    }

    [Fact]
    public void RemoveGuest_RemovesTheRowAndRevokesTheLink_D044()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock, minAttendance: 2, maxCapacity: 4);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);
        GuestIdentity guest = fixture.Session.ClaimGuestLink(
            link.Id, DisplayName.Create("Robin"), ParticipationStatus.Going, clock);
        Assert.Equal(SessionStatus.Confirmed, fixture.Session.Status);

        fixture.Session.RemoveGuest(guest.Id, clock);

        Assert.Equal(ParticipationStatus.Removed, fixture.Row(guest.Participant).Status);
        Assert.Equal(GuestLinkState.RevokedByRemoval, link.State);
        Assert.Equal(1, fixture.Session.ActiveOccupancy);
        Assert.Equal(SessionStatus.Unconfirmed, fixture.Session.Status);
    }

    [Fact]
    public void RemoveGuest_FreesTheSlotAndRaisesTheVacancy_D044()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock, minAttendance: 1, maxCapacity: 2);
        (GuestLink first, _) = fixture.Session.CreateGuestLink(clock);
        (GuestLink second, _) = fixture.Session.CreateGuestLink(clock);
        GuestIdentity guest = fixture.Session.ClaimGuestLink(
            first.Id, DisplayName.Create("Robin"), ParticipationStatus.Going, clock);
        fixture.Session.ClaimGuestLink(second.Id, DisplayName.Create("Alex"), ParticipationStatus.Going, clock);
        fixture.Drain();

        fixture.Session.RemoveGuest(guest.Id, clock);

        Assert.Single(fixture.Drain().OfType<SessionWaitlistVacancy>());
    }

    [Fact]
    public void RevokeGuestLink_RevokesAnUnclaimedLinkWithNoRosterEffect_D150()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock, minAttendance: 1, maxCapacity: 4);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);
        int rosterBefore = fixture.Session.Participations.Count;

        fixture.Session.RevokeGuestLink(link.Id, clock);

        Assert.Equal(GuestLinkState.RevokedByRemoval, link.State);
        Assert.Equal(rosterBefore, fixture.Session.Participations.Count);
        Assert.Equal(GuestLinkView.RevokedNotice, fixture.Session.ResolveGuestLink(link.Id, clock));
    }

    [Fact]
    public void RevokeGuestLink_RejectsAClaimedLink_D044()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);
        fixture.Session.ClaimGuestLink(link.Id, DisplayName.Create("Robin"), ParticipationStatus.Going, clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Session.RevokeGuestLink(link.Id, clock));

        Assert.Equal(DomainError.Conflict, error.Error);
    }

    [Fact]
    public void RenameGuest_IsLastWriteWins_D126()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);
        GuestIdentity guest = fixture.Session.ClaimGuestLink(
            link.Id, DisplayName.Create("Robin"), ParticipationStatus.Going, clock);

        fixture.Session.RenameGuest(link.Id, DisplayName.Create("Robin B."), clock);

        Assert.Equal("Robin B.", guest.DisplayName.Value);
    }

    [Fact]
    public void GuestParticipation_NeverCarriesAReminderChoice_D167()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);
        GuestIdentity guest = fixture.Session.ClaimGuestLink(
            link.Id, DisplayName.Create("Robin"), ParticipationStatus.Going, clock);

        Assert.Null(fixture.Row(guest.Participant).ReminderChoice);
        Assert.False(fixture.Row(guest.Participant).IsRegistered);
    }

    [Fact]
    public void GuestLink_ForAPollCarriesThePollTarget_D048()
    {
        FakeClock clock = TestSessions.Clock();
        PollId pollId = PollId.New();

        (GuestLink link, string token) = GuestLink.CreateForPoll(pollId, clock);

        Assert.Equal(pollId, link.PollId);
        Assert.Null(link.SessionId);
        Assert.True(link.MatchesToken(token));
    }

    [Fact]
    public void AdoptGuestLink_ReParentsWithTheTokenPreserved_D048()
    {
        // §6.4-6: finalization carries poll links across; D-048 forbids re-issuing one.
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock);
        (GuestLink link, string token) = GuestLink.CreateForPoll(PollId.New(), clock);
        GuestIdentity guest = GuestIdentity.ForPoll(PollId.New(), DisplayName.Create("Robin"), clock);

        fixture.Session.AdoptGuestLink(link);
        fixture.Session.AdoptGuestIdentity(guest);

        Assert.Equal(fixture.Session.Id, link.SessionId);
        Assert.Null(link.PollId);
        Assert.True(link.MatchesToken(token));
        Assert.Equal(fixture.Session.Id, guest.SessionId);
        Assert.Null(guest.PollId);
    }

    [Fact]
    public void AdoptGuestLink_RejectsASessionOwnedLink_D048()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock);
        (GuestLink link, _) = GuestLink.CreateForSession(SessionId.New(), clock);

        DomainException error = Assert.Throws<DomainException>(() => fixture.Session.AdoptGuestLink(link));

        Assert.Equal(DomainError.InvalidTransition, error.Error);
    }
}
