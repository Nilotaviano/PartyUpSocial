using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Tests.Polls;
using PartyUp.Domain.Tests.Sessions;

namespace PartyUp.Domain.Tests.Lifecycle;

/// <summary>
/// Shared world-building for the block (§4) and deletion (§11) lifecycle services: both walk over
/// whole sessions and polls, so the fixtures hand out aggregates with a chosen organizer and a
/// chosen position on the clock.
/// </summary>
internal static class LifecycleFixtures
{
    internal static readonly Instant Origin = Instant.FromUtc(2026, 6, 1, 12, 0);

    internal static FakeClock Clock() => new(Origin);

    internal static FakeClock At(Instant instant) => new(instant);

    internal static Instant Hours(int hoursFromOrigin) => Origin + Duration.FromHours(hoursFromOrigin);

    internal static Instant Days(int daysFromOrigin) => Origin + Duration.FromDays(daysFromOrigin);

    /// <summary>The instant a deletion requested at <see cref="Origin"/> becomes executable (D-148).</summary>
    internal static Instant GraceEnd => Origin + PartyUp.Domain.Users.User.DeletionGracePeriod;

    internal static Session NewSession(
        UserId organizer,
        IReadOnlyCollection<UserId> invitees,
        Instant startTime,
        IClock clock,
        int minAttendance = 2,
        int? maxCapacity = null,
        int durationMinutes = 120,
        bool isPrivate = false) =>
        Session.Create(
            organizer,
            TestSessions.Details(startTime, minAttendance, maxCapacity, durationMinutes, isPrivate),
            invitees,
            clock);

    internal static Poll NewPoll(
        UserId organizer,
        IReadOnlyCollection<UserId> invitees,
        IReadOnlyCollection<Instant> optionStartTimes,
        IClock clock,
        int minAttendance = 2,
        int? maxCapacity = null) =>
        Poll.Create(
            organizer,
            TestPolls.Details(minAttendance, maxCapacity),
            PollDeadlineChoice.Hours24,
            optionStartTimes,
            invitees,
            clock);

    internal static Participation RowOf(Session session, UserId user)
    {
        ArgumentNullException.ThrowIfNull(session);

        return session.Find(ParticipantRef.ForUser(user))
            ?? throw new InvalidOperationException("No such roster row.");
    }

    internal static PollParticipant RowOf(Poll poll, UserId user)
    {
        ArgumentNullException.ThrowIfNull(poll);

        return poll.FindParticipant(ParticipantRef.ForUser(user))
            ?? throw new InvalidOperationException("No such poll participant.");
    }
}
