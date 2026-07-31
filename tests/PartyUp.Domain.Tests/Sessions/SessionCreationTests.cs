using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Tests.Sessions;

public class SessionCreationTests
{
    [Fact]
    public void Create_StartsTheOrganizerGoingAndPermanent_D075()
    {
        FakeClock clock = TestSessions.Clock();

        SessionUnderTest fixture = TestSessions.New(clock);

        Participation organizer = fixture.Row(fixture.OrganizerRef);
        Assert.Equal(ParticipationStatus.Going, organizer.Status);
        Assert.False(organizer.IsWaitlisted);
        Assert.Equal(fixture.Organizer, fixture.Session.OrganizerId);
        Assert.Null(fixture.Session.OriginPollId);
    }

    [Fact]
    public void Create_DoesNotApplyCreationDefaults_D129()
    {
        FakeClock clock = TestSessions.Clock();

        Session session = Session.Create(
            UserId.New(),
            TestSessions.Details(
                minAttendance: 2,
                maxCapacity: 6,
                durationMinutes: 45,
                isPrivate: true,
                platform: Platform.Steam),
            [],
            clock);

        // Every D-129 default (min 4, max 4, 120 minutes, platform N/A, non-private) is application-layer;
        // the aggregate must carry exactly what it was handed.
        Assert.Equal(2, session.MinAttendance);
        Assert.Equal(6, session.MaxCapacity);
        Assert.Equal(45, session.Duration.TotalMinutes);
        Assert.Equal(Platform.Steam, session.Platform);
        Assert.True(session.IsPrivate);
    }

    [Fact]
    public void Create_RejectsAStartInsideFifteenMinutes_D130()
    {
        FakeClock clock = TestSessions.Clock();
        SessionDetails details = TestSessions.Details(TestSessions.Origin + Duration.FromMinutes(14));

        DomainException error = Assert.Throws<DomainException>(
            () => Session.Create(UserId.New(), details, [UserId.New()], clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Create_RejectsAStartBeyondOneYear_D130()
    {
        FakeClock clock = TestSessions.Clock();
        SessionDetails details = TestSessions.Details(TestSessions.Origin + Duration.FromDays(366));

        DomainException error = Assert.Throws<DomainException>(
            () => Session.Create(UserId.New(), details, [UserId.New()], clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Create_AcceptsBothD130Boundaries_D130()
    {
        FakeClock clock = TestSessions.Clock();

        Session earliest = Session.Create(
            UserId.New(),
            TestSessions.Details(TestSessions.Origin + SessionStartWindow.MinimumLead),
            [UserId.New()],
            clock);
        Session latest = Session.Create(
            UserId.New(),
            TestSessions.Details(TestSessions.Origin + SessionStartWindow.MaximumLead),
            [UserId.New()],
            clock);

        Assert.Equal(TestSessions.Origin + Duration.FromMinutes(15), earliest.StartTime);
        Assert.Equal(TestSessions.Origin + Duration.FromDays(365), latest.StartTime);
    }

    [Fact]
    public void Create_RejectsAMinimumBelowOne_D141()
    {
        DomainException error = Assert.Throws<DomainException>(() => TestSessions.Details(minAttendance: 0));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Create_RejectsAMaximumBelowTheMinimum_D141()
    {
        DomainException error = Assert.Throws<DomainException>(
            () => TestSessions.Details(minAttendance: 4, maxCapacity: 3));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Create_AllowsAMaximumEqualToTheMinimum_D141()
    {
        SessionDetails details = TestSessions.Details(minAttendance: 4, maxCapacity: 4);

        Assert.Equal(4, details.MaxCapacity);
    }

    [Fact]
    public void Create_AllowsAnUnlimitedMaximum_D091()
    {
        SessionDetails details = TestSessions.Details(maxCapacity: null);

        Assert.Null(details.MaxCapacity);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(1441)]
    public void Create_RejectsADurationOffTheD142Grid_D142(int minutes)
    {
        DomainException error = Assert.Throws<DomainException>(() => Duration15.FromMinutes(minutes));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Create_AcceptsTheD142Bounds_D142()
    {
        SessionDetails shortest = TestSessions.Details(durationMinutes: 15);
        SessionDetails longest = TestSessions.Details(durationMinutes: 24 * 60);

        Assert.Equal(15, shortest.Duration.TotalMinutes);
        Assert.Equal(1440, longest.Duration.TotalMinutes);
    }

    [Fact]
    public void Create_RejectsANonPrivateSessionWithoutAnInvitedFriend_D099()
    {
        FakeClock clock = TestSessions.Clock();
        SessionDetails details = TestSessions.Details(isPrivate: false);

        DomainException error = Assert.Throws<DomainException>(
            () => Session.Create(UserId.New(), details, [], clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Create_AllowsAGuestOnlyPrivateSession_D125()
    {
        FakeClock clock = TestSessions.Clock();

        SessionUnderTest fixture = TestSessions.GuestOnly(clock);

        Assert.True(fixture.Session.IsPrivate);
        Assert.Single(fixture.Session.Participations);
    }

    [Fact]
    public void Create_RejectsTheOrganizerAmongTheInvitees_D075()
    {
        FakeClock clock = TestSessions.Clock();
        UserId organizer = UserId.New();

        DomainException error = Assert.Throws<DomainException>(
            () => Session.Create(organizer, TestSessions.Details(), [organizer], clock));

        Assert.Equal(DomainError.Conflict, error.Error);
    }

    [Fact]
    public void Create_RejectsADuplicateInvitee_D020()
    {
        FakeClock clock = TestSessions.Clock();
        UserId invitee = UserId.New();

        DomainException error = Assert.Throws<DomainException>(
            () => Session.Create(UserId.New(), TestSessions.Details(), [invitee, invitee], clock));

        Assert.Equal(DomainError.Duplicate, error.Error);
    }

    [Fact]
    public void Create_RaisesOneInvitationPerInvitee_D020()
    {
        FakeClock clock = TestSessions.Clock();

        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 3);

        List<SessionInvitationSent> invitations = [.. fixture.Drain().OfType<SessionInvitationSent>()];
        Assert.Equal(3, invitations.Count);
        Assert.All(invitations, invitation => Assert.Equal(fixture.Session.Id, invitation.SessionId));
    }

    [Fact]
    public void Create_ConfirmsImmediatelyWhenTheMinimumIsOne_D053()
    {
        FakeClock clock = TestSessions.Clock();

        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 1);

        Assert.Equal(SessionStatus.Confirmed, fixture.Session.Status);
        Assert.Single(fixture.Drain().OfType<SessionConfirmed>());
    }

    [Fact]
    public void Create_StaysUnconfirmedBelowTheMinimum_D053()
    {
        FakeClock clock = TestSessions.Clock();

        SessionUnderTest fixture = TestSessions.New(clock, minAttendance: 2);

        Assert.Equal(SessionStatus.Unconfirmed, fixture.Session.Status);
        Assert.Empty(fixture.Drain().OfType<SessionConfirmed>());
    }

    [Fact]
    public void Create_DerivesEndTimeFromStartAndDuration_D142()
    {
        FakeClock clock = TestSessions.Clock();

        SessionUnderTest fixture = TestSessions.New(clock, durationMinutes: 90);

        Assert.Equal(fixture.Session.StartTime + Duration.FromMinutes(90), fixture.Session.EndTime);
        Assert.Equal(fixture.Session.EndTime, fixture.Session.Window.End);
    }

    [Fact]
    public void Create_RejectsATitlelessSession_D057()
    {
        DomainException error = Assert.Throws<DomainException>(
            () => new SessionDetails(
                default,
                TestSessions.DefaultStart,
                Duration15.FromMinutes(60),
                Platform.NotApplicable,
                null,
                SessionNotes.Empty,
                2,
                null,
                false));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Create_RejectsANullClockOrDetails_D130()
    {
        FakeClock clock = TestSessions.Clock();

        Assert.Throws<ArgumentNullException>(() => Session.Create(UserId.New(), null!, [], clock));
        Assert.Throws<ArgumentNullException>(
            () => Session.Create(UserId.New(), TestSessions.Details(), [UserId.New()], null!));
    }

    [Fact]
    public void IsInProgress_IsAClockEvaluationNotAStatus_D089()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock);

        Assert.False(fixture.Session.IsInProgress(clock));

        clock.Reset(fixture.Session.StartTime);
        Assert.True(fixture.Session.IsInProgress(clock));

        clock.Reset(fixture.Session.EndTime);
        Assert.False(fixture.Session.IsInProgress(clock));
    }
}
