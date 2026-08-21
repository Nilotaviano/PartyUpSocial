using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Api.Unfurl;

/// <summary>
/// Turns a raw guest-link token into the card D-224 says it unfurls as, for both the crawler's Open
/// Graph document and the PNG. Link state is the domain's answer (<c>ResolveGuestLink</c>) on top of
/// <see cref="IGuestLinkResolver"/> — never re-derived from timestamps here — and only a live link
/// ever reaches <see cref="IGuestLinkUnfurlReader"/> for detail. Every failure is the same generic
/// card, so an unknown token is indistinguishable from a revoked one.
/// </summary>
public sealed class GuestUnfurlResolver(
    IGuestLinkResolver links,
    ISessionRepository sessions,
    IPollRepository polls,
    IGuestLinkUnfurlReader reader,
    IClock clock)
{
    private const int TitleMaxLength = 90;
    private const int GameMaxLength = 60;

    /// <summary>Resolves <paramref name="token"/>; never throws for a bad or unknown token.</summary>
    public async Task<UnfurlCard> ResolveAsync(string token, CancellationToken cancellationToken)
    {
        ResolvedGuestLink? resolved = await links.ResolveAsync(token, cancellationToken);
        if (resolved is not null)
        {
            return await ResolveSessionAsync(resolved, cancellationToken);
        }

        ResolvedPollGuestLink? resolvedPoll = await links.ResolvePollAsync(token, cancellationToken);
        return resolvedPoll is null
            ? UnfurlCard.Unavailable(UnfurlLocale.English)
            : await ResolvePollAsync(resolvedPoll, cancellationToken);
    }

    private async Task<UnfurlCard> ResolveSessionAsync(
        ResolvedGuestLink resolved,
        CancellationToken cancellationToken)
    {
        Session? session = await sessions.FindNoTrackingAsync(resolved.SessionId, cancellationToken);
        if (session is null)
        {
            return UnfurlCard.Unavailable(UnfurlLocale.English);
        }

        GuestLinkView view;
        try
        {
            view = session.ResolveGuestLink(resolved.GuestLinkId, clock);
        }
        catch (DomainException)
        {
            return UnfurlCard.Unavailable(UnfurlLocale.English);
        }

        UnfurlSessionDetails? details = await reader.GetSessionDetailsAsync(resolved.SessionId, cancellationToken);
        if (details is null)
        {
            return UnfurlCard.Unavailable(UnfurlLocale.English);
        }

        UnfurlLocale locale = UnfurlStrings.Resolve(details.Locale);
        return view == GuestLinkView.LiveView
            ? new UnfurlCard(
                UnfurlCardKind.Session,
                UnfurlText.Clamp(details.Title, TitleMaxLength),
                UnfurlText.Clamp(details.GameName, GameMaxLength) is { Length: > 0 } game ? game : null,
                UnfurlStrings.Moment(locale, details.StartTime, details.TimeZone),
                UnfurlStrings.Going(locale, details.GoingCount),
                locale)
            : UnfurlCard.Unavailable(locale);
    }

    private async Task<UnfurlCard> ResolvePollAsync(
        ResolvedPollGuestLink resolved,
        CancellationToken cancellationToken)
    {
        Poll? poll = await polls.FindNoTrackingAsync(resolved.PollId, cancellationToken);
        if (poll is null)
        {
            return UnfurlCard.Unavailable(UnfurlLocale.English);
        }

        GuestLinkView view;
        try
        {
            view = poll.ResolveGuestLink(resolved.GuestLinkId, clock);
        }
        catch (DomainException)
        {
            return UnfurlCard.Unavailable(UnfurlLocale.English);
        }

        UnfurlPollDetails? details = await reader.GetPollDetailsAsync(resolved.PollId, cancellationToken);
        if (details is null)
        {
            return UnfurlCard.Unavailable(UnfurlLocale.English);
        }

        UnfurlLocale locale = UnfurlStrings.Resolve(details.Locale);
        return view == GuestLinkView.LiveView
            ? new UnfurlCard(
                UnfurlCardKind.Poll,
                UnfurlText.Clamp(details.Title, TitleMaxLength),
                UnfurlText.Clamp(details.GameName, GameMaxLength) is { Length: > 0 } game ? game : null,
                UnfurlStrings.VoteOnWhenToPlay(locale),
                UnfurlStrings.Closes(locale, UnfurlStrings.Moment(locale, details.Deadline, details.TimeZone)),
                locale)
            : UnfurlCard.Unavailable(locale);
    }
}
