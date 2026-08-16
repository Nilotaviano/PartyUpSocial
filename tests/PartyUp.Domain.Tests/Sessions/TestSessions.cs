using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Tests.Sessions;

internal sealed record SessionUnderTest(Session Session, UserId Organizer, IReadOnlyList<UserId> Invitees)
{
    internal ParticipantRef OrganizerRef => ParticipantRef.ForUser(Organizer);

    internal UserId Invitee(int index) => Invitees[index];

    internal ParticipantRef InviteeRef(int index) => ParticipantRef.ForUser(Invitees[index]);

    internal Participation Row(ParticipantRef participant) =>
        Session.Find(participant) ?? throw new InvalidOperationException("No such roster row.");

    internal Participation Row(int inviteeIndex) => Row(InviteeRef(inviteeIndex));

    internal IReadOnlyList<IDomainEvent> Drain() => Session.DequeueEvents();
}

internal static class TestSessions
{
    internal static readonly Instant Origin = Instant.FromUtc(2026, 3, 2, 12, 0);

    internal static Instant DefaultStart => Origin + Duration.FromHours(24);

    internal const int DefaultDurationMinutes = 120;

    internal static FakeClock Clock() => new(Origin);

    internal static SessionDetails Details(
        Instant? startTime = null,
        int minAttendance = 2,
        int? maxCapacity = null,
        int durationMinutes = DefaultDurationMinutes,
        ContentVisibility visibility = ContentVisibility.Public,
        Platform platform = Platform.NotApplicable,
        string title = "Raid night",
        string? game = null,
        string? notes = null) =>
        new(
            SessionTitle.Create(title),
            startTime ?? DefaultStart,
            Duration15.FromMinutes(durationMinutes),
            platform,
            game is null ? null : SessionGame.FreeText(GameName.Create(game)),
            notes is null ? SessionNotes.Empty : SessionNotes.Create(notes),
            minAttendance,
            maxCapacity,
            visibility);

    /// <summary>A public session with the organizer Going plus <paramref name="inviteeCount"/> Pending invitees.</summary>
    internal static SessionUnderTest New(
        IClock clock,
        int inviteeCount = 2,
        int minAttendance = 2,
        int? maxCapacity = null,
        int durationMinutes = DefaultDurationMinutes,
        ContentVisibility visibility = ContentVisibility.Public,
        Instant? startTime = null,
        IReadOnlyCollection<FriendGroupId>? audienceGroups = null)
    {
        UserId organizer = UserId.New();
        UserId[] invitees = [.. Enumerable.Range(0, inviteeCount).Select(_ => UserId.New())];

        Session session = Session.Create(
            organizer,
            Details(startTime, minAttendance, maxCapacity, durationMinutes, visibility),
            invitees,
            clock,
            audienceGroups);

        return new SessionUnderTest(session, organizer, invitees);
    }

    /// <summary>A private, guest-only session (D-125) with no registered invitees.</summary>
    internal static SessionUnderTest GuestOnly(
        IClock clock,
        int minAttendance = 1,
        int? maxCapacity = null,
        Instant? startTime = null)
    {
        UserId organizer = UserId.New();
        Session session = Session.Create(
            organizer,
            Details(startTime, minAttendance, maxCapacity, visibility: ContentVisibility.Private),
            [],
            clock);

        return new SessionUnderTest(session, organizer, []);
    }

    internal static FakeClock At(Instant instant) => new(instant);
}
