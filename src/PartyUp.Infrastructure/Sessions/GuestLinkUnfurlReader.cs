using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Infrastructure.Sessions;

/// <summary>
/// D-224's card projection: one untracked read per unfurl, never the aggregate — the endpoint has
/// already established link state through the domain, so this only supplies the display fields. The
/// organizer's unset time zone or locale collapses to UTC and <c>en</c> here rather than at the
/// render site, so both the metadata and the image agree on one default.
/// </summary>
public sealed class GuestLinkUnfurlReader(AppDbContext context) : IGuestLinkUnfurlReader
{
    private const string DefaultLocale = "en";

    public async Task<UnfurlSessionDetails?> GetSessionDetailsAsync(
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        var row = await context.Sessions
            .AsNoTracking()
            .Where(session => session.Id == sessionId)
            .Select(session => new
            {
                session.Title,
                GameName = session.Game == null ? (GameName?)null : session.Game.Name,
                session.StartTime,
                GoingCount = context.SessionParticipations.Count(participation =>
                    participation.SessionId == session.Id &&
                    !participation.IsWaitlisted &&
                    participation.Status == ParticipationStatus.Going),
                Organizer = context.DomainUsers
                    .Where(user => user.Id == session.OrganizerId)
                    .Select(user => new { user.TimeZone, user.Locale })
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new UnfurlSessionDetails(
                row.Title.Value,
                NullIfEmpty(row.GameName?.Value),
                row.StartTime,
                row.GoingCount,
                row.Organizer?.TimeZone ?? DateTimeZone.Utc,
                LocaleOf(row.Organizer?.Locale));
    }

    public async Task<UnfurlPollDetails?> GetPollDetailsAsync(
        PollId pollId,
        CancellationToken cancellationToken)
    {
        var row = await context.Polls
            .AsNoTracking()
            .Where(poll => poll.Id == pollId)
            .Select(poll => new
            {
                poll.Title,
                GameName = poll.Game == null ? (GameName?)null : poll.Game.Name,
                poll.Deadline,
                Organizer = context.DomainUsers
                    .Where(user => user.Id == poll.OrganizerId)
                    .Select(user => new { user.TimeZone, user.Locale })
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new UnfurlPollDetails(
                row.Title.Value,
                NullIfEmpty(row.GameName?.Value),
                row.Deadline,
                row.Organizer?.TimeZone ?? DateTimeZone.Utc,
                LocaleOf(row.Organizer?.Locale));
    }

    private static string LocaleOf(PartyUp.Domain.Users.Locale? locale) =>
        locale is { IsEmpty: false } present ? present.Value : DefaultLocale;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
