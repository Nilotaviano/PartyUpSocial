using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// The read side of D-224's link unfurl: the handful of fields a crawler's Open Graph document and
/// the server-rendered preview card need for an already-resolved guest link. Link <em>state</em>
/// never comes from here — it comes from <see cref="IGuestLinkResolver"/> plus the aggregate's own
/// <c>ResolveGuestLink</c>, so nothing is re-derived from timestamps.
/// </summary>
public interface IGuestLinkUnfurlReader
{
    /// <summary>The session card's fields, or <see langword="null"/> when no such session exists.</summary>
    Task<UnfurlSessionDetails?> GetSessionDetailsAsync(SessionId sessionId, CancellationToken cancellationToken);

    /// <summary>The poll card's fields, or <see langword="null"/> when no such poll exists.</summary>
    Task<UnfurlPollDetails?> GetPollDetailsAsync(PollId pollId, CancellationToken cancellationToken);
}

/// <summary>
/// A live session link's card fields. <paramref name="GoingCount"/> is the active Going count
/// (organizer and guests included, waitlisted rows excluded); <paramref name="TimeZone"/> and
/// <paramref name="Locale"/> are the organizer's, already defaulted to UTC and <c>en</c> when the
/// organizer has set neither (D-060/D-146: the viewer of an unfurl is unknown).
/// </summary>
public sealed record UnfurlSessionDetails(
    string Title,
    string? GameName,
    Instant StartTime,
    int GoingCount,
    DateTimeZone TimeZone,
    string Locale);

/// <summary>
/// A live poll link's card fields. <paramref name="Deadline"/> is the voting deadline, not the
/// organizer's selection-window close; zone and locale carry the same organizer defaults as
/// <see cref="UnfurlSessionDetails"/>.
/// </summary>
public sealed record UnfurlPollDetails(
    string Title,
    string? GameName,
    Instant Deadline,
    DateTimeZone TimeZone,
    string Locale);
