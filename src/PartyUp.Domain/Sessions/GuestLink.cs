using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Sessions;

/// <summary>
/// An organizer-minted, initially <em>unclaimed</em> bearer link (DOMAIN_MODEL §5.4). It creates no
/// participation and reserves no capacity: a guest claiming at a full session waitlists like anyone
/// (D-084, §12-6). The token is a ≥256-bit CSPRNG secret stored only as a SHA-256 digest (A-015) and
/// is never re-issued — D-048 forbids renewal, so finalization carries poll links onto the session
/// with their tokens preserved.
/// </summary>
public sealed class GuestLink
{
    /// <summary>A-015: ≥256 bits of entropy.</summary>
    public const int TokenByteLength = 32;

    private GuestLink() => TokenHash = string.Empty;

    private GuestLink(GuestLinkId id, SessionId? sessionId, PollId? pollId, string tokenHash, Instant createdAt)
    {
        Id = id;
        SessionId = sessionId;
        PollId = pollId;
        TokenHash = tokenHash;
        State = GuestLinkState.Active;
        CreatedAt = createdAt;
    }

    public GuestLinkId Id { get; private set; }

    public SessionId? SessionId { get; private set; }

    public PollId? PollId { get; private set; }

    /// <summary>The digest, never the token: a database leak must not leak live bearer credentials.</summary>
    public string TokenHash { get; private set; }

    public GuestLinkState State { get; private set; }

    /// <summary>Null until the first visit claims the link (D-017).</summary>
    public GuestIdentityId? GuestIdentityId { get; private set; }

    public Instant CreatedAt { get; private set; }

    public Instant? ClaimedAt { get; private set; }

    public bool IsActive => State == GuestLinkState.Active;

    public bool IsClaimed => GuestIdentityId.HasValue;

    public static (GuestLink Link, string Token) CreateForSession(SessionId sessionId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        string token = CreateToken();
        GuestLink link = new(GuestLinkId.New(), sessionId, null, HashToken(token), clock.GetCurrentInstant());
        return (link, token);
    }

    public static (GuestLink Link, string Token) CreateForPoll(PollId pollId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        string token = CreateToken();
        GuestLink link = new(GuestLinkId.New(), null, pollId, HashToken(token), clock.GetCurrentInstant());
        return (link, token);
    }

    /// <summary>A-015: <see cref="TokenByteLength"/> CSPRNG bytes, URL-safe base64, never logged.</summary>
    public static string CreateToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenByteLength));

    public static string HashToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public bool MatchesToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(HashToken(token)),
            Encoding.UTF8.GetBytes(TokenHash));
    }

    internal void Claim(GuestIdentityId guestIdentityId, Instant now)
    {
        if (IsClaimed)
        {
            throw new DomainException(DomainError.Conflict, "This guest link has already been claimed.");
        }

        GuestIdentityId = guestIdentityId;
        ClaimedAt = now;
    }

    /// <summary>First revocation wins: a canceled session's link never later reads as removal-revoked.</summary>
    internal void Revoke(GuestLinkState state)
    {
        if (state == GuestLinkState.Active)
        {
            throw new DomainException(DomainError.ValidationFailed, "Revocation needs a non-Active state.");
        }

        if (IsActive)
        {
            State = state;
        }
    }

    /// <summary>§5.4/§6.4-6: finalization re-parents the link with its token preserved (D-048 forbids re-issue).</summary>
    internal void AttachToSession(SessionId sessionId)
    {
        if (PollId is null)
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                "Only a poll-owned guest link can be re-parented onto a session.");
        }

        SessionId = sessionId;
        PollId = null;
    }
}
