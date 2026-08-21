namespace PartyUp.Api.Unfurl;

/// <summary>Which of D-224's three cards a resolved link unfurls as.</summary>
public enum UnfurlCardKind
{
    /// <summary>A live session link.</summary>
    Session = 0,

    /// <summary>A live poll link.</summary>
    Poll = 1,

    /// <summary>Every other state, including an unknown or malformed token.</summary>
    Unavailable = 2,
}

/// <summary>
/// The already-localized, already-clamped lines both the Open Graph document and the PNG render, so
/// the two can never disagree. A <see cref="UnfurlCardKind.Unavailable"/> card carries no session or
/// poll detail at all: its title is the generic "Invite link" and its primary line is the notice.
/// </summary>
/// <param name="Kind">Which card this is.</param>
/// <param name="Title">The card's heading — a session or poll title, or the generic fallback.</param>
/// <param name="GameName">The game line, or <see langword="null"/> when the subject names no game.</param>
/// <param name="PrimaryLine">Session: the start date and time. Poll: the call to vote. Otherwise: the notice.</param>
/// <param name="SecondaryLine">Session: "N going". Poll: "closes {deadline}". Otherwise: absent.</param>
/// <param name="Locale">The locale every string above was rendered in.</param>
public sealed record UnfurlCard(
    UnfurlCardKind Kind,
    string Title,
    string? GameName,
    string? PrimaryLine,
    string? SecondaryLine,
    UnfurlLocale Locale)
{
    /// <summary>The one-line description the metadata carries; the card renders the same parts stacked.</summary>
    public string Description => UnfurlText.JoinParts(GameName, PrimaryLine, SecondaryLine);

    /// <summary>The generic card, in <paramref name="locale"/> when an organizer is known and English otherwise.</summary>
    public static UnfurlCard Unavailable(UnfurlLocale locale) => new(
        UnfurlCardKind.Unavailable,
        UnfurlStrings.InviteLink(locale),
        GameName: null,
        UnfurlStrings.Unavailable(locale),
        SecondaryLine: null,
        locale);
}
