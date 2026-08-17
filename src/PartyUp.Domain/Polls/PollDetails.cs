using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Polls;

/// <summary>
/// Every eventual session detail a poll collects up front (D-154, DOMAIN_MODEL §6.1). Candidate
/// options vary by start time and by length (D-211), so those two are the session fields this shape
/// does not carry; <see cref="ForOption"/> supplies them when finalization mints the session.
/// </summary>
public sealed record PollDetails
{
    public PollDetails(
        SessionTitle title,
        Platform platform,
        SessionGame? game,
        SessionNotes notes,
        int minAttendance,
        int? maxCapacity,
        ContentVisibility visibility)
    {
        // The field rules live in SessionDetails — a poll carries exactly the session's details
        // (§6.1) — while the start time and the length belong to the option (D-154/D-211), so
        // validation runs against placeholders that ForOption replaces. SessionDetails never
        // constrains the start, and each option validates its own length on the way in.
        SessionDetails validated = new(
            title,
            NodaConstants.UnixEpoch,
            Duration15.FromMinutes(Duration15.MinimumMinutes),
            platform,
            game,
            notes,
            minAttendance,
            maxCapacity,
            visibility);

        Title = validated.Title;
        Platform = validated.Platform;
        Game = validated.Game;
        Notes = validated.Notes;
        MinAttendance = validated.MinAttendance;
        MaxCapacity = validated.MaxCapacity;
        Visibility = validated.Visibility;
    }

    public SessionTitle Title { get; }

    public Platform Platform { get; }

    public SessionGame? Game { get; }

    public SessionNotes Notes { get; }

    /// <summary>D-141: an integer of at least 1; only active Going rows count toward it.</summary>
    public int MinAttendance { get; }

    /// <summary>D-091: null means unlimited attendance, so finalization waitlists nobody.</summary>
    public int? MaxCapacity { get; }

    public ContentVisibility Visibility { get; }

    /// <summary>§6.4-2: the selected option's instant and length become the session's (D-211).</summary>
    public SessionDetails ForOption(Instant startTime, Duration15 duration) =>
        new(Title, startTime, duration, Platform, Game, Notes, MinAttendance, MaxCapacity, Visibility);
}
