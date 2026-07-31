using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Tests.Polls;

public class PollStateMachineTests
{
    [Fact]
    public void ApplyDueTransitions_ClosesAtTheDeadline_D079()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        fixture.Poll.ApplyDueTransitions(TestPolls.At(TestPolls.DefaultDeadline));

        Assert.Equal(PollStatus.Closed, fixture.Poll.Status);
    }

    [Fact]
    public void ApplyDueTransitions_LeavesAPollPollingOneMinuteBeforeTheDeadline_D079()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        fixture.Poll.ApplyDueTransitions(TestPolls.At(TestPolls.DefaultDeadline - Duration.FromMinutes(1)));

        Assert.Equal(PollStatus.Polling, fixture.Poll.Status);
    }

    [Fact]
    public void Close_NotifiesEveryoneThatSelectionIsAwaited_D120()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Drain();

        fixture.Poll.Close(TestPolls.At(TestPolls.DefaultDeadline));

        Assert.Single(fixture.Drain().OfType<PollClosed>());
    }

    [Fact]
    public void Close_IsIdempotent_D079()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        FakeClock deadline = TestPolls.At(TestPolls.DefaultDeadline);
        fixture.Poll.Close(deadline);
        fixture.Drain();

        fixture.Poll.Close(deadline);

        Assert.Equal(PollStatus.Closed, fixture.Poll.Status);
        Assert.Empty(fixture.Drain());
    }

    [Fact]
    public void Close_IsRejectedBeforeTheDeadline_D079()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        DomainException error = Assert.Throws<DomainException>(() => fixture.Poll.Close(clock));

        Assert.Equal(DomainError.WindowClosed, error.Error);
    }

    [Fact]
    public void Close_LapsesPendingJoinRequests_D118()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        JoinRequest request = fixture.Poll.OpenJoinRequest(UserId.New(), clock);

        fixture.Poll.Close(TestPolls.At(TestPolls.DefaultDeadline));

        Assert.Equal(JoinRequestStatus.Lapsed, request.Status);
    }

    [Fact]
    public void Close_LeavesGuestLinksActive_D048()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.GuestOnly(clock);
        (GuestLink link, _) = fixture.Poll.CreateGuestLink(clock);

        fixture.Poll.Close(TestPolls.At(TestPolls.DefaultDeadline));

        Assert.Equal(GuestLinkState.Active, link.State);
    }

    [Fact]
    public void Expire_HappensExactlyFortyEightHoursAfterTheDeadline_D088()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        Instant windowEnd = fixture.Poll.SelectionWindowEndsAt;

        fixture.Poll.ApplyDueTransitions(TestPolls.At(windowEnd - Duration.FromMinutes(1)));
        Assert.Equal(PollStatus.Closed, fixture.Poll.Status);

        fixture.Poll.ApplyDueTransitions(TestPolls.At(windowEnd));
        Assert.Equal(PollStatus.Expired, fixture.Poll.Status);
    }

    [Fact]
    public void Expire_ClosesAnUnsweptPollOnTheWayThrough_D088()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Drain();

        fixture.Poll.ApplyDueTransitions(TestPolls.At(fixture.Poll.SelectionWindowEndsAt));

        IReadOnlyList<IDomainEvent> events = fixture.Drain();
        Assert.Single(events.OfType<PollClosed>());
        Assert.Single(events.OfType<PollExpired>());
        Assert.Equal(PollStatus.Expired, fixture.Poll.Status);
    }

    [Fact]
    public void Expire_IsRejectedBeforeTheSelectionWindowEnds_D140()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(
            clock, optionStartTimes: [TestPolls.Hours(1), TestPolls.Hours(2)]);
        FakeClock afterEveryOption = TestPolls.At(TestPolls.Hours(30));
        fixture.Poll.ApplyDueTransitions(afterEveryOption);

        DomainException error = Assert.Throws<DomainException>(() => fixture.Poll.Expire(afterEveryOption));

        Assert.Equal(DomainError.WindowClosed, error.Error);
        Assert.Equal(PollStatus.Closed, fixture.Poll.Status);
    }

    [Fact]
    public void Expire_TurnsGuestLinksIntoExpiredPollNotices_D170()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.GuestOnly(clock);
        (GuestLink link, _) = fixture.Poll.CreateGuestLink(clock);

        fixture.Poll.ApplyDueTransitions(TestPolls.At(fixture.Poll.SelectionWindowEndsAt));

        Assert.Equal(GuestLinkState.ExpiredPoll, link.State);
        Assert.Equal(GuestLinkView.PollExpiredNotice, fixture.Poll.ResolveGuestLink(link.Id, clock));
    }

    [Fact]
    public void Expire_NotifiesEveryRegisteredParticipant_D170()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Drain();

        fixture.Poll.ApplyDueTransitions(TestPolls.At(fixture.Poll.SelectionWindowEndsAt));

        Assert.Single(fixture.Drain().OfType<PollExpired>());
    }

    [Fact]
    public void Expire_IsIdempotent_D088()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        FakeClock late = TestPolls.At(fixture.Poll.SelectionWindowEndsAt);
        fixture.Poll.ApplyDueTransitions(late);
        fixture.Drain();

        fixture.Poll.Expire(late);

        Assert.Empty(fixture.Drain());
    }

    [Fact]
    public void ExpireForOrganizerDeletion_ExpiresAPollingPollAtAnyTime_D092()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        JoinRequest request = fixture.Poll.OpenJoinRequest(UserId.New(), clock);
        fixture.Drain();

        fixture.Poll.ExpireForOrganizerDeletion(clock);

        Assert.Equal(PollStatus.Expired, fixture.Poll.Status);
        Assert.Equal(JoinRequestStatus.Lapsed, request.Status);
        Assert.Single(fixture.Drain().OfType<PollExpired>());
    }

    [Fact]
    public void ExpireForOrganizerDeletion_ExpiresAClosedPoll_D092()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        FakeClock closed = TestPolls.At(TestPolls.DefaultDeadline + Duration.FromHours(1));
        fixture.Poll.ApplyDueTransitions(closed);

        fixture.Poll.ExpireForOrganizerDeletion(closed);

        Assert.Equal(PollStatus.Expired, fixture.Poll.Status);
    }

    [Fact]
    public void ExpireForOrganizerDeletion_IsRejectedOnACanceledPoll_D092()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.Cancel(clock);

        DomainException error = Assert.Throws<DomainException>(() => fixture.Poll.ExpireForOrganizerDeletion(clock));

        Assert.Equal(DomainError.InvalidTransition, error.Error);
    }

    [Fact]
    public void Cancel_RevokesGuestLinksAndLapsesRequests_D166()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        (GuestLink link, _) = fixture.Poll.CreateGuestLink(clock);
        JoinRequest request = fixture.Poll.OpenJoinRequest(UserId.New(), clock);
        fixture.Drain();

        fixture.Poll.Cancel(clock);

        Assert.Equal(PollStatus.Canceled, fixture.Poll.Status);
        Assert.Equal(GuestLinkState.RevokedByCancel, link.State);
        Assert.Equal(JoinRequestStatus.Lapsed, request.Status);
        Assert.Single(fixture.Drain().OfType<PollCanceled>());
        Assert.Equal(GuestLinkView.CanceledNotice, fixture.Poll.ResolveGuestLink(link.Id, clock));
    }

    [Fact]
    public void Cancel_IsAllowedWhileClosed_D156()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        FakeClock closed = TestPolls.At(TestPolls.DefaultDeadline + Duration.FromHours(2));

        fixture.Poll.Cancel(closed);

        Assert.Equal(PollStatus.Canceled, fixture.Poll.Status);
    }

    [Fact]
    public void Cancel_IsImpossibleOnceThePollHasLogicallyExpired_D156()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        FakeClock afterWindow = TestPolls.At(fixture.Poll.SelectionWindowEndsAt + Duration.FromMinutes(1));

        DomainException error = Assert.Throws<DomainException>(() => fixture.Poll.Cancel(afterWindow));

        Assert.Equal(DomainError.InvalidTransition, error.Error);
        Assert.Equal(PollStatus.Expired, fixture.Poll.Status);
    }

    [Fact]
    public void Cancel_IsIdempotent_D156()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);
        fixture.Poll.Cancel(clock);
        fixture.Drain();

        fixture.Poll.Cancel(clock);

        Assert.Empty(fixture.Drain());
    }

    [Fact]
    public void Cancel_IsRejectedAfterFinalization_D156()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock, minAttendance: 1);
        FakeClock afterDeadline = TestPolls.At(TestPolls.DefaultDeadline + Duration.FromMinutes(2));
        fixture.Poll.Finalize(fixture.Option(0).Id, afterDeadline);

        DomainException error = Assert.Throws<DomainException>(() => fixture.Poll.Cancel(afterDeadline));

        Assert.Equal(DomainError.InvalidTransition, error.Error);
    }

    [Fact]
    public void Poll_HasNoEditSurface_D154()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest fixture = TestPolls.New(clock);

        Assert.DoesNotContain(
            typeof(Poll).GetMethods(),
            method => method.Name.StartsWith("Change", StringComparison.Ordinal));
        Assert.Equal(TestPolls.DefaultDeadline, fixture.Poll.Deadline);
    }
}
