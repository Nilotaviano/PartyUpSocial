using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Tests.Sessions;

/// <summary>
/// The headline session decisions the traceability table needs a domain assertion for: the core
/// create/invite/respond journey (D-005/D-006), the detail payload (D-010/D-011/D-041/D-176), the
/// link invitation (D-012), capacity plus waitlist (D-021), and the reset boundary (D-027).
/// </summary>
public class SessionCoreDecisionTests
{
    [Fact]
    public void Create_SchedulesAFixedTimeAndInvitesFriends_D005()
    {
        FakeClock clock = TestSessions.Clock();

        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2);

        Assert.Equal(TestSessions.DefaultStart, fixture.Session.StartTime);
        Assert.Equal(ParticipationStatus.Going, fixture.Row(fixture.OrganizerRef).Status);
        Assert.All(
            fixture.Invitees,
            invitee =>
            {
                Participation row = fixture.Row(ParticipantRef.ForUser(invitee));
                Assert.Equal(ParticipationStatus.Pending, row.Status);
                Assert.Equal(ParticipationOrigin.Invited, row.Origin);
            });
        Assert.Equal(2, fixture.Drain().OfType<SessionInvitationSent>().Count());
    }

    [Fact]
    public void Respond_AcceptsGoingMaybeAndDeclined_D006()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 3, minAttendance: 1);

        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Maybe, clock);
        fixture.Session.DeclineInvitation(fixture.InviteeRef(2), clock);

        Assert.Equal(ParticipationStatus.Going, fixture.Row(0).Status);
        Assert.Equal(ParticipationStatus.Maybe, fixture.Row(1).Status);
        Assert.Equal(ParticipationStatus.Declined, fixture.Row(2).Status);
    }

    [Fact]
    public void Game_IsOptionalAndClearable_D010()
    {
        FakeClock clock = TestSessions.Clock();
        Session session = Session.Create(
            UserId.New(),
            TestSessions.Details(game: null),
            [UserId.New()],
            clock);

        Assert.Null(session.Game);

        session.ChangeGame(Platform.Steam, SessionGame.FreeText(GameName.Create("Deep Rock Galactic")), clock);
        Assert.Equal("Deep Rock Galactic", session.Game!.Name.Value);

        session.ChangeGame(Platform.NotApplicable, null, clock);
        Assert.Null(session.Game);
    }

    [Fact]
    public void Create_CarriesPlatformDurationAndNotes_D011()
    {
        FakeClock clock = TestSessions.Clock();

        Session session = Session.Create(
            UserId.New(),
            TestSessions.Details(
                durationMinutes: 90,
                platform: Platform.PlayStation,
                game: "Helldivers 2",
                notes: "Bring the shield relay."),
            [UserId.New()],
            clock);

        Assert.Equal(Platform.PlayStation, session.Platform);
        Assert.Equal(90, session.Duration.TotalMinutes);
        Assert.Equal("Bring the shield relay.", session.Notes.Value);
        Assert.Equal("Helldivers 2", session.Game!.Name.Value);
    }

    [Fact]
    public void Game_IsFreeTextOnEveryPlatform_D176()
    {
        FakeClock clock = TestSessions.Clock();
        Session session = Session.Create(UserId.New(), TestSessions.Details(), [UserId.New()], clock);

        foreach (Platform platform in Enum.GetValues<Platform>())
        {
            session.ChangeGame(platform, SessionGame.FreeText(GameName.Create("A game typed by hand")), clock);

            Assert.Equal(platform, session.Platform);
            Assert.Equal("A game typed by hand", session.Game!.Name.Value);
        }
    }

    /// <summary>
    /// §5.1: the game is a value object holding a Steam catalog reference xor free text. Only Steam
    /// offers catalog selection — every other platform is free text alone.
    /// </summary>
    [Fact]
    public void Game_TakesACatalogReferenceOnSteamAlone_D176()
    {
        FakeClock clock = TestSessions.Clock();
        Session session = Session.Create(UserId.New(), TestSessions.Details(), [UserId.New()], clock);
        SessionGame catalog = SessionGame.FromSteamCatalog(548430, GameName.Create("Deep Rock Galactic"));

        session.ChangeGame(Platform.Steam, catalog, clock);

        Assert.True(session.Game!.IsCatalogReference);
        Assert.Equal(548430, session.Game.SteamAppId);
        Assert.False(SessionGame.FreeText(GameName.Create("Deep Rock Galactic")).IsCatalogReference);

        foreach (Platform platform in new[] { Platform.PlayStation, Platform.Xbox, Platform.NotApplicable })
        {
            Assert.Equal(
                DomainError.ValidationFailed,
                Assert.Throws<DomainException>(() => session.ChangeGame(platform, catalog, clock)).Error);
        }

        Assert.Equal(
            DomainError.ValidationFailed,
            Assert.Throws<DomainException>(() => Details(Platform.Xbox, catalog)).Error);
    }

    [Fact]
    public void Game_UsesTheSameBoundsWhicheverSourceSuppliedIt_D041()
    {
        string longest = new('x', GameName.MaxLength);

        Assert.Equal(longest, SessionGame.FreeText(GameName.Create(longest)).Name.Value);
        Assert.Equal(longest, SessionGame.FromSteamCatalog(548430, GameName.Create(longest)).Name.Value);
        Assert.Equal(
            DomainError.ValidationFailed,
            Assert.Throws<DomainException>(() => GameName.Create(new string('x', GameName.MaxLength + 1))).Error);
        Assert.Equal(
            DomainError.ValidationFailed,
            Assert.Throws<DomainException>(() => SessionGame.FreeText(default)).Error);
        Assert.Equal(
            DomainError.ValidationFailed,
            Assert.Throws<DomainException>(() => SessionGame.FromSteamCatalog(548430, default)).Error);
        Assert.Equal(
            DomainError.ValidationFailed,
            Assert.Throws<DomainException>(
                () => SessionGame.FromSteamCatalog(0, GameName.Create("Deep Rock Galactic"))).Error);
        Assert.Equal(
            DomainError.ValidationFailed,
            Assert.Throws<DomainException>(() => Details(Platform.Steam, SessionGame.FreeText(default))).Error);
    }

    private static SessionDetails Details(Platform platform, SessionGame game) =>
        new(
            SessionTitle.Create("Raid night"),
            TestSessions.DefaultStart,
            Duration15.FromMinutes(60),
            platform,
            game,
            SessionNotes.Empty,
            2,
            null,
            false);

    [Fact]
    public void CreateGuestLink_SharesTheInvitationByLink_D012()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 1);

        (GuestLink link, string token) = fixture.Session.CreateGuestLink(clock);
        GuestIdentity guest = fixture.Session.ClaimGuestLink(
            link.Id, DisplayName.Create("Robin"), ParticipationStatus.Going, clock);

        Assert.False(fixture.Session.IsPrivate);
        Assert.Same(link, fixture.Session.FindLinkByToken(token));
        Assert.Equal(ParticipationOrigin.GuestLink, fixture.Row(guest.Participant).Origin);
        Assert.Equal(ParticipationStatus.Going, fixture.Row(guest.Participant).Status);
    }

    [Fact]
    public void Rsvp_BeyondTheMaximumPlayerCountFormsTheWaitlist_D021()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 1, maxCapacity: 2);

        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        fixture.Session.Rsvp(fixture.InviteeRef(1), ParticipationStatus.Going, clock);

        Assert.Equal(2, fixture.Session.MaxCapacity);
        Assert.Equal(2, fixture.Session.ActiveOccupancy);
        Assert.False(fixture.Session.HasFreeSlot);
        Assert.Equal(1, fixture.Session.WaitlistCount);
        Assert.True(fixture.Row(1).IsWaitlisted);
    }

    [Fact]
    public void Edit_ResetsResponsesOnlyForTheStartTime_D027()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 1, maxCapacity: 4);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);

        fixture.Session.ChangeTitle(SessionTitle.Create("Later raid"), clock);
        fixture.Session.ChangeGame(Platform.Xbox, SessionGame.FreeText(GameName.Create("Sea of Thieves")), clock);
        fixture.Session.ChangeNotes(SessionNotes.Create("Voice chat on."), clock);
        fixture.Session.ChangeMaxCapacity(6, clock);
        fixture.Session.ChangeDuration(Duration15.FromMinutes(180), clock);
        fixture.Session.ChangePrivacy(isPrivate: true, clock);

        Assert.Equal(ParticipationStatus.Going, fixture.Row(0).Status);

        fixture.Session.ChangeStartTime(TestSessions.Origin + Duration.FromHours(48), clock);

        Assert.Equal(ParticipationStatus.Pending, fixture.Row(0).Status);
        Assert.Equal(ParticipationStatus.Pending, fixture.Row(fixture.OrganizerRef).Status);
    }

    [Fact]
    public void Create_MintsAFixedTimeSessionWithNoOriginPoll_D064()
    {
        FakeClock clock = TestSessions.Clock();

        SessionUnderTest fixture = TestSessions.New(clock);

        Assert.Null(fixture.Session.OriginPollId);
        Assert.Equal(SessionStatus.Unconfirmed, fixture.Session.Status);
    }
}
