using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Tests.Sessions;

/// <summary>
/// D-225: a claimed guest seat becomes a registered account's seat. Two branches only — re-point when
/// the account holds no row, D-044 removal of the guest row when it does — and in both the link ends
/// terminal so the token can never re-claim the seat.
/// </summary>
public class SessionGuestUpgradeTests
{
    [Fact]
    public void UpgradeGuest_WithNoRegisteredRow_RePointsTheSeatKeepingStatusAndOrder_D225()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock, minAttendance: 1, maxCapacity: 1);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);

        // The organizer holds the only slot, so the guest's Going waitlists (D-084) — the case where
        // a lost position would be visible.
        GuestIdentity guest = fixture.Session.ClaimGuestLink(
            link.Id, DisplayName.Create("Robin"), ParticipationStatus.Going, clock);
        Participation seat = fixture.Row(guest.Participant);
        Instant claimedAt = seat.CreatedAt;
        int occupancyBefore = fixture.Session.ActiveOccupancy;
        Assert.True(seat.IsWaitlisted);

        clock.Advance(Duration.FromMinutes(5));
        UserId user = UserId.New();
        fixture.Session.UpgradeGuest(guest.Id, user, clock);

        Participation upgraded = fixture.Row(ParticipantRef.ForUser(user));
        Assert.Same(seat, upgraded);
        Assert.True(upgraded.IsRegistered);
        Assert.False(upgraded.IsGuest);
        Assert.Equal(ParticipationStatus.Going, upgraded.Status);
        Assert.True(upgraded.IsWaitlisted);
        Assert.Equal(claimedAt, upgraded.CreatedAt);
        Assert.Equal(ParticipationOrigin.GuestLink, upgraded.Origin);
        Assert.Null(fixture.Session.Find(guest.Participant));
        Assert.Equal(occupancyBefore, fixture.Session.ActiveOccupancy);

        // Provenance survives on the identity row; the display name is left alone.
        Assert.Equal(user, guest.UpgradedToUserId);
        Assert.Equal(clock.GetCurrentInstant(), guest.UpgradedAt);
        Assert.Equal("Robin", guest.DisplayName.Value);
    }

    [Fact]
    public void UpgradeGuest_WhenTheAccountAlreadyHoldsARow_RemovesTheGuestSeatAndFreesCapacity_D225()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 1, maxCapacity: 3);
        UserId invitee = fixture.Invitee(0);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);

        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);
        (GuestLink otherLink, _) = fixture.Session.CreateGuestLink(clock);
        GuestIdentity guest = fixture.Session.ClaimGuestLink(
            link.Id, DisplayName.Create("Robin"), ParticipationStatus.Going, clock);
        GuestIdentity waiting = fixture.Session.ClaimGuestLink(
            otherLink.Id, DisplayName.Create("Alex"), ParticipationStatus.Going, clock);
        Assert.True(fixture.Row(waiting.Participant).IsWaitlisted);

        Participation registered = fixture.Row(ParticipantRef.ForUser(invitee));
        Instant registeredCreatedAt = registered.CreatedAt;
        fixture.Drain();

        fixture.Session.UpgradeGuest(guest.Id, invitee, clock);

        // The registered row is kept untouched; the guest row goes through the D-044 removal path.
        Assert.Same(registered, fixture.Row(ParticipantRef.ForUser(invitee)));
        Assert.Equal(ParticipationStatus.Going, registered.Status);
        Assert.False(registered.IsWaitlisted);
        Assert.Equal(registeredCreatedAt, registered.CreatedAt);
        Assert.Equal(ParticipationStatus.Removed, fixture.Row(guest.Participant).Status);

        // Capacity freed, so the waitlist vacancy the organizer acts on is raised (D-084).
        Assert.Equal(2, fixture.Session.ActiveOccupancy);
        Assert.True(fixture.Session.HasFreeSlot);
        Assert.Single(fixture.Drain().OfType<SessionWaitlistVacancy>());
        Assert.Equal(GuestLinkState.Upgraded, link.State);
        Assert.Equal(invitee, guest.UpgradedToUserId);
    }

    [Fact]
    public void UpgradeGuest_LeavesEveryLinkOfThatGuestTerminal_D225()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock, minAttendance: 1);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);
        (GuestLink untouched, _) = fixture.Session.CreateGuestLink(clock);
        GuestIdentity guest = fixture.Session.ClaimGuestLink(
            link.Id, DisplayName.Create("Robin"), ParticipationStatus.Going, clock);

        fixture.Session.UpgradeGuest(guest.Id, UserId.New(), clock);

        Assert.Equal(GuestLinkState.Upgraded, link.State);
        Assert.Equal(GuestLinkView.RevokedNotice, fixture.Session.ResolveGuestLink(link.Id, clock));

        // Another guest's still-unclaimed link is untouched.
        Assert.Equal(GuestLinkState.Active, untouched.State);

        // The bearer token is spent: no further guest write and no second upgrade land on that seat.
        DomainException write = Assert.Throws<DomainException>(
            () => fixture.Session.RespondAsGuest(link.Id, ParticipationStatus.Maybe, clock));
        Assert.Equal(DomainError.Forbidden, write.Error);

        DomainException again = Assert.Throws<DomainException>(
            () => fixture.Session.UpgradeGuest(guest.Id, UserId.New(), clock));
        Assert.Equal(DomainError.Conflict, again.Error);
    }
}
