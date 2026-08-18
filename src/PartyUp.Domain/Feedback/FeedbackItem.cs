using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Feedback;

/// <summary>
/// D-212/D-214: one in-app feedback submission. A record-keeping row rather than an aggregate with
/// invariants — the only rules it owns are the message's presence and length cap, the optional
/// context fields' caps, and the Handled toggle's <c>handled_at</c> stamping. Shape-only layering
/// mirrors <c>PushSubscription</c> (Domain entity, Application use cases, Infrastructure mapping).
/// </summary>
public sealed class FeedbackItem
{
    public const int MaximumMessageLength = 4000;

    /// <summary>Route paths and app versions — short client context (D-212).</summary>
    public const int MaximumShortContextLength = 512;

    /// <summary>User-agent strings, which are routinely far longer than a route (D-212).</summary>
    public const int MaximumUserAgentLength = 1024;

    private FeedbackItem()
    {
        Message = string.Empty;
    }

    private FeedbackItem(
        FeedbackItemId id,
        UserId? submitterUserId,
        FeedbackType type,
        string message,
        string? route,
        string? userAgent,
        string? locale,
        string? appVersion,
        Instant createdAt)
    {
        Id = id;
        SubmitterUserId = submitterUserId;
        Type = type;
        Message = message;
        Route = route;
        UserAgent = userAgent;
        Locale = locale;
        AppVersion = appVersion;
        CreatedAt = createdAt;
    }

    public FeedbackItemId Id { get; private set; }

    /// <summary>
    /// Null for an anonymous submission (D-213) and nulled again when the submitter's account is
    /// deleted (D-216) — the row and its message are retained either way.
    /// </summary>
    public UserId? SubmitterUserId { get; private set; }

    public FeedbackType Type { get; private set; }

    public string Message { get; private set; }

    public string? Route { get; private set; }

    public string? UserAgent { get; private set; }

    public string? Locale { get; private set; }

    public string? AppVersion { get; private set; }

    public Instant CreatedAt { get; private set; }

    public bool Handled { get; private set; }

    public Instant? HandledAt { get; private set; }

    public static FeedbackItem Create(
        UserId? submitterUserId,
        FeedbackType type,
        string message,
        string? route,
        string? userAgent,
        string? locale,
        string? appVersion,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (!Enum.IsDefined(type))
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"'{type}' is not a feedback type."));
        }

        return new FeedbackItem(
            FeedbackItemId.New(),
            submitterUserId,
            type,
            RequireMessage(message),
            OptionalText(route, MaximumShortContextLength),
            OptionalText(userAgent, MaximumUserAgentLength),
            OptionalText(locale, MaximumShortContextLength),
            OptionalText(appVersion, MaximumShortContextLength),
            clock.GetCurrentInstant());
    }

    /// <summary>
    /// D-214's Handled toggle. Idempotent in both directions: re-marking handled keeps the first
    /// instant, and clearing it removes the stamp so a later re-mark records the real second one.
    /// </summary>
    public void SetHandled(bool handled, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (handled)
        {
            HandledAt ??= clock.GetCurrentInstant();
            Handled = true;
            return;
        }

        Handled = false;
        HandledAt = null;
    }

    /// <summary>
    /// D-216: account deletion anonymizes rather than erases — the submitter reference goes, the row
    /// and every other column stay exactly as submitted. Idempotent.
    /// </summary>
    public void Anonymize() => SubmitterUserId = null;

    private static string RequireMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                "Feedback requires a message.");
        }

        string trimmed = message.Trim();

        if (trimmed.Length > MaximumMessageLength)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"A feedback message is at most {MaximumMessageLength} characters, but was {trimmed.Length}."));
        }

        return trimmed;
    }

    /// <summary>
    /// D-212: every attached context field is optional. Absent, empty and whitespace-only all land on
    /// null, so a submission carrying none of them still succeeds; an over-long one is truncated
    /// rather than rejected — client-supplied telemetry must never cost a user their message.
    /// </summary>
    private static string? OptionalText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= maximumLength ? trimmed : trimmed[..maximumLength];
    }
}
