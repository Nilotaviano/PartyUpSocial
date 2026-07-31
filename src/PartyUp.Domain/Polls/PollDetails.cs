using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Polls;

/// <summary>
/// Every eventual session detail a poll collects up front (D-154, DOMAIN_MODEL §6.1). Candidate
/// options vary only by start time, so the start is the single session field this shape does not
/// carry; <see cref="ForStartTime"/> supplies it when finalization mints the session.
/// </summary>
public sealed record PollDetails
{
    public PollDetails(
        SessionTitle title,
        Duration15 duration,
        Platform platform,
        SessionGame? game,
        SessionNotes notes,
        int minAttendance,
        int? maxCapacity,
        bool isPrivate)
    {
        // The field rules live in SessionDetails — a poll carries exactly the session's details
        // (§6.1) — and the start time belongs to the option (D-154), so validation runs against a
        // placeholder instant that ForStartTime replaces. SessionDetails never constrains the start.
        SessionDetails validated = new(
            title,
            NodaConstants.UnixEpoch,
            duration,
            platform,
            game,
            notes,
            minAttendance,
            maxCapacity,
            isPrivate);

        Title = validated.Title;
        Duration = validated.Duration;
        Platform = validated.Platform;
        Game = validated.Game;
        Notes = validated.Notes;
        MinAttendance = validated.MinAttendance;
        MaxCapacity = validated.MaxCapacity;
        IsPrivate = validated.IsPrivate;
    }

    public SessionTitle Title { get; }

    public Duration15 Duration { get; }

    public Platform Platform { get; }

    public SessionGame? Game { get; }

    public SessionNotes Notes { get; }

    /// <summary>D-141: an integer of at least 1; only active Going rows count toward it.</summary>
    public int MinAttendance { get; }

    /// <summary>D-091: null means unlimited attendance, so finalization waitlists nobody.</summary>
    public int? MaxCapacity { get; }

    public bool IsPrivate { get; }

    /// <summary>§6.4-2: the selected option's instant becomes the session's fixed start time.</summary>
    public SessionDetails ForStartTime(Instant startTime) =>
        new(Title, startTime, Duration, Platform, Game, Notes, MinAttendance, MaxCapacity, IsPrivate);
}
