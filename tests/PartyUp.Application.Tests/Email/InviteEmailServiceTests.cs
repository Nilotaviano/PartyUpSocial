using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Application.Email;
using PartyUp.Application.Tests.Friends;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Email;

/// <summary>
/// D-189 §5: <see cref="InviteEmailService"/> exercised directly against the committed aggregate +
/// dequeued events the five call sites hand it — never a post-commit re-read.
/// </summary>
public sealed class InviteEmailServiceTests
{
    private static readonly Instant Origin = Instant.FromUtc(2026, 3, 2, 12, 0);

    [Fact]
    public async Task NotifySessionEventsAsync_DispatchesSessionInvitation_D189()
    {
        Fixture fixture = new();
        (Session session, UserId inviteeId) = fixture.CreateSession();
        fixture.Reader.Recipients[session.OrganizerId] = Fixture.OrganizerRecipient(session.OrganizerId);
        fixture.Reader.Recipients[inviteeId] = Fixture.EnabledRecipient(inviteeId);

        var events = new IDomainEvent[] { new SessionInvitationSent(session.Id, inviteeId) };

        await fixture.Service.NotifySessionEventsAsync(session, events, default);

        EmailMessage message = Assert.Single(fixture.Dispatcher.Messages);
        Assert.Equal(EmailTemplate.SessionInvitation, message.Template);
        Assert.Equal("invitee@example.test", message.ToAddress);
        Assert.Equal(session.Title.Value, message.Model["Title"]);
        Assert.Equal("Organizer Name", message.Model["OrganizerName"]);
        Assert.True(message.Model.ContainsKey("StartTime"));
        Assert.Equal(1, fixture.UnitOfWork.Executions);
        Assert.Single(fixture.Tokens.Issues);
        Assert.Equal(AuthTokenPurpose.EmailUnsubscribe, fixture.Tokens.Issues[0].Purpose);
    }

    [Fact]
    public async Task NotifySessionEventsAsync_DispatchesApprovalTemplate_StatusNeutral_D189()
    {
        Fixture fixture = new();
        (Session session, UserId requesterId) = fixture.CreateSession();
        fixture.Reader.Recipients[session.OrganizerId] = Fixture.OrganizerRecipient(session.OrganizerId);
        fixture.Reader.Recipients[requesterId] = Fixture.EnabledRecipient(requesterId);

        var events = new IDomainEvent[]
        {
            new SessionJoinRequestAccepted(session.Id, JoinRequestId.New(), requesterId),
        };

        await fixture.Service.NotifySessionEventsAsync(session, events, default);

        EmailMessage message = Assert.Single(fixture.Dispatcher.Messages);
        Assert.Equal(EmailTemplate.SessionJoinRequestApproved, message.Template);
        Assert.False(message.Model.ContainsKey("StartTime"));
    }

    [Fact]
    public async Task NotifyPollEventsAsync_DispatchesPollInvitation_D189()
    {
        Fixture fixture = new();
        (Poll poll, UserId inviteeId) = fixture.CreatePoll();
        fixture.Reader.Recipients[poll.OrganizerId] = Fixture.OrganizerRecipient(poll.OrganizerId);
        fixture.Reader.Recipients[inviteeId] = Fixture.EnabledRecipient(inviteeId);

        var events = new IDomainEvent[] { new PollInvitationSent(poll.Id, inviteeId) };

        await fixture.Service.NotifyPollEventsAsync(poll, events, default);

        EmailMessage message = Assert.Single(fixture.Dispatcher.Messages);
        Assert.Equal(EmailTemplate.PollInvitation, message.Template);
        Assert.False(message.Model.ContainsKey("StartTime"));
    }

    [Fact]
    public async Task NotifyPollEventsAsync_DispatchesApprovalTemplate_D189()
    {
        Fixture fixture = new();
        (Poll poll, UserId requesterId) = fixture.CreatePoll();
        fixture.Reader.Recipients[poll.OrganizerId] = Fixture.OrganizerRecipient(poll.OrganizerId);
        fixture.Reader.Recipients[requesterId] = Fixture.EnabledRecipient(requesterId);

        var events = new IDomainEvent[]
        {
            new PollJoinRequestAccepted(poll.Id, JoinRequestId.New(), requesterId),
        };

        await fixture.Service.NotifyPollEventsAsync(poll, events, default);

        EmailMessage message = Assert.Single(fixture.Dispatcher.Messages);
        Assert.Equal(EmailTemplate.PollJoinRequestApproved, message.Template);
    }

    [Fact]
    public async Task NotifySessionEventsAsync_SettingOff_NoDispatch_D189()
    {
        Fixture fixture = new();
        (Session session, UserId inviteeId) = fixture.CreateSession();
        fixture.Reader.Recipients[session.OrganizerId] = Fixture.OrganizerRecipient(session.OrganizerId);
        fixture.Reader.Recipients[inviteeId] = Fixture.EnabledRecipient(inviteeId) with
        {
            EmailNotificationsEnabled = false,
        };

        var events = new IDomainEvent[] { new SessionInvitationSent(session.Id, inviteeId) };

        await fixture.Service.NotifySessionEventsAsync(session, events, default);

        Assert.Empty(fixture.Dispatcher.Messages);
        Assert.Equal(0, fixture.UnitOfWork.Executions);
    }

    [Fact]
    public async Task NotifySessionEventsAsync_NoConfirmedEmail_NoDispatch_D189()
    {
        Fixture fixture = new();
        (Session session, UserId inviteeId) = fixture.CreateSession();
        fixture.Reader.Recipients[session.OrganizerId] = Fixture.OrganizerRecipient(session.OrganizerId);
        // Invitee intentionally absent from the reader -- the reader contract is "confirmed only",
        // so an unconfirmed address never shows up in the result set at all.

        var events = new IDomainEvent[] { new SessionInvitationSent(session.Id, inviteeId) };

        await fixture.Service.NotifySessionEventsAsync(session, events, default);

        Assert.Empty(fixture.Dispatcher.Messages);
    }

    [Fact]
    public async Task NotifySessionEventsAsync_ReInviteOfDeclinedInvitee_ReMails_D189()
    {
        Fixture fixture = new();
        (Session session, UserId inviteeId) = fixture.CreateSession();
        fixture.Reader.Recipients[session.OrganizerId] = Fixture.OrganizerRecipient(session.OrganizerId);
        fixture.Reader.Recipients[inviteeId] = Fixture.EnabledRecipient(inviteeId);

        var firstInvite = new IDomainEvent[] { new SessionInvitationSent(session.Id, inviteeId) };
        await fixture.Service.NotifySessionEventsAsync(session, firstInvite, default);

        var reInvite = new IDomainEvent[] { new SessionInvitationSent(session.Id, inviteeId) };
        await fixture.Service.NotifySessionEventsAsync(session, reInvite, default);

        Assert.Equal(2, fixture.Dispatcher.Messages.Count);
        Assert.All(fixture.Dispatcher.Messages, m => Assert.Equal(EmailTemplate.SessionInvitation, m.Template));
    }

    [Fact]
    public async Task NotifySessionEventsAsync_ModelBuiltFromPassedAggregate_D047()
    {
        Fixture fixture = new();
        (Session session, UserId inviteeId) = fixture.CreateSession(title: "In-memory title");
        fixture.Reader.Recipients[session.OrganizerId] = Fixture.OrganizerRecipient(session.OrganizerId);
        fixture.Reader.Recipients[inviteeId] = Fixture.EnabledRecipient(inviteeId);

        var events = new IDomainEvent[] { new SessionInvitationSent(session.Id, inviteeId) };

        await fixture.Service.NotifySessionEventsAsync(session, events, default);

        EmailMessage message = Assert.Single(fixture.Dispatcher.Messages);
        Assert.Equal("In-memory title", message.Model["Title"]);
    }

    [Fact]
    public async Task NotifySessionEventsAsync_NoRelevantEvents_NoDispatch_D189()
    {
        Fixture fixture = new();
        (Session session, _) = fixture.CreateSession();
        fixture.Reader.Recipients[session.OrganizerId] = Fixture.OrganizerRecipient(session.OrganizerId);

        var events = new IDomainEvent[] { new SessionConfirmed(session.Id) };

        await fixture.Service.NotifySessionEventsAsync(session, events, default);

        Assert.Empty(fixture.Dispatcher.Messages);
        Assert.Empty(fixture.Reader.Calls);
    }

    private sealed class Fixture
    {
        internal Fixture()
        {
            Reader = new FakeEmailRecipientReader();
            Tokens = new FakeTokenService();
            Dispatcher = new FakeEmailDispatcher();
            UnitOfWork = new FakeUnitOfWork();
            Service = TestInviteEmailService.Create(Reader, Tokens, Dispatcher, UnitOfWork);
            Clock = new TestClock(Origin);
        }

        internal FakeEmailRecipientReader Reader { get; }

        internal FakeTokenService Tokens { get; }

        internal FakeEmailDispatcher Dispatcher { get; }

        internal FakeUnitOfWork UnitOfWork { get; }

        internal InviteEmailService Service { get; }

        internal TestClock Clock { get; }

        internal (Session Session, UserId InviteeId) CreateSession(string title = "Raid night")
        {
            User organizer = FriendTestData.Active($"org{Guid.NewGuid():N}"[..12], "Organizer");
            User invitee = FriendTestData.Active($"inv{Guid.NewGuid():N}"[..12], "Invitee");

            SessionDetails details = new(
                SessionTitle.Create(title),
                Clock.GetCurrentInstant() + Duration.FromHours(24),
                Duration15.FromMinutes(120),
                Platform.NotApplicable,
                null,
                SessionNotes.Empty,
                minAttendance: 1,
                maxCapacity: 4,
                isPrivate: false);

            Session session = Session.Create(organizer.Id, details, [invitee.Id], Clock);
            _ = session.DequeueEvents();
            return (session, invitee.Id);
        }

        internal (Poll Poll, UserId InviteeId) CreatePoll()
        {
            User organizer = FriendTestData.Active($"porg{Guid.NewGuid():N}"[..12], "Poll Organizer");
            User invitee = FriendTestData.Active($"pinv{Guid.NewGuid():N}"[..12], "Poll Invitee");

            PollDetails details = new(
                SessionTitle.Create("Raid poll"),
                Duration15.FromMinutes(60),
                Platform.NotApplicable,
                null,
                SessionNotes.Empty,
                minAttendance: 1,
                maxCapacity: null,
                isPrivate: false);

            Poll poll = Poll.Create(
                organizer.Id,
                details,
                PollDeadlineChoice.Hours24,
                [Clock.GetCurrentInstant() + Duration.FromHours(5), Clock.GetCurrentInstant() + Duration.FromHours(6)],
                [invitee.Id],
                Clock);
            _ = poll.DequeueEvents();
            return (poll, invitee.Id);
        }

        internal static EmailRecipient OrganizerRecipient(UserId organizerId) => new(
            organizerId,
            "organizer@example.test",
            "Organizer Name",
            Locale.Create("en"),
            DateTimeZoneProviders.Tzdb.GetSystemDefault(),
            true);

        internal static EmailRecipient EnabledRecipient(UserId userId) => new(
            userId,
            "invitee@example.test",
            "Invitee Name",
            Locale.Create("en"),
            DateTimeZoneProviders.Tzdb.GetSystemDefault(),
            true);
    }
}
