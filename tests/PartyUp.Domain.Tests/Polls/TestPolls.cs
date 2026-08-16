using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Tests.Polls;

internal static class PollGuests
{
    /// <summary>Mints a poll guest link and claims it, yielding the voter the guest becomes (D-017/D-033).</summary>
    internal static (GuestLinkId LinkId, ParticipantRef Guest) Claim(Poll poll, IClock clock, string displayName)
    {
        ArgumentNullException.ThrowIfNull(poll);

        (GuestLink link, _) = poll.CreateGuestLink(clock);
        GuestIdentity identity = poll.ClaimGuestLink(link.Id, DisplayName.Create(displayName), clock);

        return (link.Id, identity.Participant);
    }
}

internal sealed record PollUnderTest(Poll Poll, UserId Organizer, IReadOnlyList<UserId> Invitees)
{
    internal ParticipantRef OrganizerRef => ParticipantRef.ForUser(Organizer);

    internal UserId Invitee(int index) => Invitees[index];

    internal ParticipantRef InviteeRef(int index) => ParticipantRef.ForUser(Invitees[index]);

    internal PollOption Option(int index) => Poll.Options[index];

    internal PollParticipant Row(ParticipantRef participant) =>
        Poll.FindParticipant(participant) ?? throw new InvalidOperationException("No such poll participant.");

    internal IReadOnlyList<IDomainEvent> Drain() => Poll.DequeueEvents();
}

internal static class TestPolls
{
    internal static readonly Instant Origin = Instant.FromUtc(2026, 3, 2, 12, 0);

    internal const int DefaultDurationMinutes = 120;

    /// <summary>The default poll's deadline: 24 hours after <see cref="Origin"/> (D-070/D-131).</summary>
    internal static Instant DefaultDeadline => Origin + Duration.FromHours(24);

    internal static FakeClock Clock() => new(Origin);

    internal static FakeClock At(Instant instant) => new(instant);

    internal static Instant Hours(int hoursFromOrigin) => Origin + Duration.FromHours(hoursFromOrigin);

    internal static Instant Minutes(int minutesFromOrigin) => Origin + Duration.FromMinutes(minutesFromOrigin);

    internal static PollDetails Details(
        int minAttendance = 2,
        int? maxCapacity = null,
        int durationMinutes = DefaultDurationMinutes,
        ContentVisibility visibility = ContentVisibility.Public,
        string title = "Raid night",
        Platform platform = Platform.NotApplicable,
        string? game = null,
        string? notes = null) =>
        new(
            SessionTitle.Create(title),
            Duration15.FromMinutes(durationMinutes),
            platform,
            game is null ? null : SessionGame.FreeText(GameName.Create(game)),
            notes is null ? SessionNotes.Empty : SessionNotes.Create(notes),
            minAttendance,
            maxCapacity,
            visibility);

    /// <summary>A non-private poll: the organizer plus <paramref name="inviteeCount"/> Active invitees.</summary>
    internal static PollUnderTest New(
        IClock clock,
        int inviteeCount = 2,
        int minAttendance = 2,
        int? maxCapacity = null,
        int durationMinutes = DefaultDurationMinutes,
        PollDeadlineChoice deadlineChoice = PollDeadlineChoice.Hours24,
        IReadOnlyCollection<Instant>? optionStartTimes = null)
    {
        UserId organizer = UserId.New();
        UserId[] invitees = [.. Enumerable.Range(0, inviteeCount).Select(_ => UserId.New())];

        Poll poll = Poll.Create(
            organizer,
            Details(minAttendance, maxCapacity, durationMinutes),
            deadlineChoice,
            optionStartTimes ?? [Hours(72), Hours(96)],
            invitees,
            clock);

        return new PollUnderTest(poll, organizer, invitees);
    }

    /// <summary>A private, guest-only poll (D-125) with no registered invitees.</summary>
    internal static PollUnderTest GuestOnly(
        IClock clock,
        int minAttendance = 1,
        int? maxCapacity = null,
        IReadOnlyCollection<Instant>? optionStartTimes = null)
    {
        UserId organizer = UserId.New();

        Poll poll = Poll.Create(
            organizer,
            Details(minAttendance, maxCapacity, visibility: ContentVisibility.Private),
            PollDeadlineChoice.Hours24,
            optionStartTimes ?? [Hours(72), Hours(96)],
            [],
            clock);

        return new PollUnderTest(poll, organizer, []);
    }
}
