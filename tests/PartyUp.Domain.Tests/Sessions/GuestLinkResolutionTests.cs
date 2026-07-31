using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Tests.Sessions;

public class GuestLinkResolutionTests
{
    private static readonly Instant Start = Instant.FromUtc(2026, 4, 1, 20, 0);
    private static readonly Instant End = Start + Duration.FromHours(2);

    [Fact]
    public void ActiveLink_BeforeTheEndTime_ResolvesToTheLiveView_D048()
    {
        GuestLinkView view = GuestLinkResolution.ForSession(
            GuestLinkState.Active, SessionStatus.Confirmed, End, Start + Duration.FromMinutes(30));

        Assert.Equal(GuestLinkView.LiveView, view);
    }

    [Fact]
    public void ActiveLink_AtTheEndTime_ResolvesToTheEndedNotice_D048()
    {
        Assert.Equal(
            GuestLinkView.EndedNotice,
            GuestLinkResolution.ForSession(GuestLinkState.Active, SessionStatus.Confirmed, End, End));
    }

    [Fact]
    public void CanceledLink_ResolvesToTheCancellationNotice_D174()
    {
        // D-167: this notice is the guest's only cancellation channel.
        Assert.Equal(
            GuestLinkView.CanceledNotice,
            GuestLinkResolution.ForSession(GuestLinkState.RevokedByCancel, SessionStatus.Canceled, End, Start));
    }

    [Fact]
    public void CanceledSession_ResolvesToTheCancellationNoticeEvenPastTheEndTime_D167()
    {
        Assert.Equal(
            GuestLinkView.CanceledNotice,
            GuestLinkResolution.ForSession(
                GuestLinkState.RevokedByCancel, SessionStatus.Canceled, End, End + Duration.FromDays(1)));
    }

    [Fact]
    public void RemovedGuestLink_ResolvesToTheRevocationNotice_D044()
    {
        Assert.Equal(
            GuestLinkView.RevokedNotice,
            GuestLinkResolution.ForSession(GuestLinkState.RevokedByRemoval, SessionStatus.Confirmed, End, Start));
    }

    [Fact]
    public void ExpiredPollLink_ResolvesToThePollExpiryNotice_D048()
    {
        Assert.Equal(
            GuestLinkView.PollExpiredNotice,
            GuestLinkResolution.ForPoll(GuestLinkState.ExpiredPoll, PollStatus.Expired));
    }

    [Theory]
    [InlineData(SessionStatus.Completed)]
    [InlineData(SessionStatus.Expired)]
    public void EndedSession_ResolvesToTheEndedNotice_D048(SessionStatus status)
    {
        Assert.Equal(
            GuestLinkView.EndedNotice,
            GuestLinkResolution.ForSession(GuestLinkState.Active, status, End, End + Duration.FromHours(1)));
    }

    [Fact]
    public void CanceledSessionStatus_OutranksTheEndedNotice_D167()
    {
        Assert.Equal(
            GuestLinkView.CanceledNotice,
            GuestLinkResolution.ForSession(
                GuestLinkState.Active, SessionStatus.Canceled, End, End + Duration.FromHours(1)));
    }

    [Theory]
    [InlineData(PollStatus.Polling)]
    [InlineData(PollStatus.Closed)]
    public void ActivePollLink_ResolvesToTheLiveView_D048(PollStatus status)
    {
        Assert.Equal(GuestLinkView.LiveView, GuestLinkResolution.ForPoll(GuestLinkState.Active, status));
    }

    [Fact]
    public void ActivePollLink_OnACanceledPoll_ResolvesToTheCancellationNotice_D167()
    {
        Assert.Equal(
            GuestLinkView.CanceledNotice,
            GuestLinkResolution.ForPoll(GuestLinkState.Active, PollStatus.Canceled));
    }

    [Fact]
    public void FromRevocation_IsNullForAnActiveLink_D048()
    {
        Assert.Null(GuestLinkResolution.FromRevocation(GuestLinkState.Active));
        Assert.Equal(GuestLinkView.CanceledNotice, GuestLinkResolution.FromRevocation(GuestLinkState.RevokedByCancel));
        Assert.Equal(GuestLinkView.RevokedNotice, GuestLinkResolution.FromRevocation(GuestLinkState.RevokedByRemoval));
        Assert.Equal(GuestLinkView.PollExpiredNotice, GuestLinkResolution.FromRevocation(GuestLinkState.ExpiredPoll));
    }

    [Fact]
    public void ResolveGuestLink_OnTheAggregate_TracksTheSessionsOwnClock_D048()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock, minAttendance: 1);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);

        Assert.Equal(GuestLinkView.LiveView, fixture.Session.ResolveGuestLink(link.Id, clock));

        clock.Reset(fixture.Session.EndTime);
        Assert.Equal(GuestLinkView.EndedNotice, fixture.Session.ResolveGuestLink(link.Id, clock));

        fixture.Session.ApplyDueTransitions(clock);
        Assert.Equal(GuestLinkView.EndedNotice, fixture.Session.ResolveGuestLink(link.Id, clock));
    }

    [Fact]
    public void ResolveGuestLink_AfterCancellation_ShowsTheCancellationNotice_D174()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock, minAttendance: 1);
        (GuestLink link, _) = fixture.Session.CreateGuestLink(clock);

        fixture.Session.Cancel(clock);

        Assert.Equal(GuestLinkView.CanceledNotice, fixture.Session.ResolveGuestLink(link.Id, clock));
    }

    [Fact]
    public void ResolveGuestLink_ForAnUnknownLink_IsNotFound_D048()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.GuestOnly(clock);

        DomainException error = Assert.Throws<DomainException>(
            () => fixture.Session.ResolveGuestLink(GuestLinkId.New(), clock));

        Assert.Equal(DomainError.NotFound, error.Error);
    }

    [Fact]
    public void ForPoll_OnAFinalizedPoll_IsRejected_D048()
    {
        DomainException error = Assert.Throws<DomainException>(
            () => GuestLinkResolution.ForPoll(GuestLinkState.Active, PollStatus.Finalized));

        Assert.Equal(DomainError.InvalidTransition, error.Error);
    }
}
