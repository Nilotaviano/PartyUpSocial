using System.Buffers.Text;
using System.Security.Cryptography;
using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Users;

/// <summary>
/// One linked account's captured picture (AVATARS_DESIGN.md §3.1, D-190). An aggregate-internal
/// collection on <see cref="User"/>, one row per <see cref="AvatarSourceKind"/> per user. Bytes are
/// already re-encoded WebP by the time they reach this constructor — capture-time validation (host
/// allowlist, magic-byte sniff, size cap) is an application-layer concern (AVATARS_DESIGN.md §3.2).
/// </summary>
public sealed class AvatarSource
{
    /// <summary>A-084: ≥256 bits of entropy, carved out of A-015's hash-only rule.</summary>
    public const int TokenByteLength = 32;

    private AvatarSource()
    {
        Token = string.Empty;
        ContentType = string.Empty;
        Bytes = [];
    }

    private AvatarSource(
        AvatarSourceId id,
        UserId userId,
        AvatarSourceKind kind,
        string token,
        byte[] bytes,
        string contentType,
        Instant fetchedAt)
    {
        Id = id;
        UserId = userId;
        Kind = kind;
        Token = token;
        Bytes = bytes;
        ContentType = contentType;
        FetchedAt = fetchedAt;
    }

    public AvatarSourceId Id { get; private set; }

    public UserId UserId { get; private set; }

    public AvatarSourceKind Kind { get; private set; }

    /// <summary>
    /// A-084: a plaintext capability slug, minted once and never rotated. Possession of the token is
    /// the proxy's whole authorization; deleting this row is the only revocation.
    /// </summary>
    public string Token { get; private set; }

    /// <summary>Re-encoded WebP, captured server-side (D-032). Never the upstream bytes verbatim.</summary>
    public byte[] Bytes { get; private set; }

    /// <summary>Server-chosen, from a closed allowlist -- never the upstream's declared type.</summary>
    public string ContentType { get; private set; }

    /// <summary>Staleness marker for the daily Steam refresh job (AVATARS_DESIGN.md §3.4).</summary>
    public Instant FetchedAt { get; private set; }

    internal static AvatarSource Capture(
        UserId userId,
        AvatarSourceKind kind,
        byte[] bytes,
        string contentType,
        Instant fetchedAt) =>
        new(AvatarSourceId.New(), userId, kind, CreateToken(), Validated(bytes), ValidatedContentType(contentType), fetchedAt);

    /// <summary>Refreshes the captured picture in place; the token never changes (A-084).</summary>
    internal void Refresh(byte[] bytes, string contentType, Instant fetchedAt)
    {
        Bytes = Validated(bytes);
        ContentType = ValidatedContentType(contentType);
        FetchedAt = fetchedAt;
    }

    /// <summary>A-084: <see cref="TokenByteLength"/> CSPRNG bytes, URL-safe base64, stored in plaintext.</summary>
    public static string CreateToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenByteLength));

    private static byte[] Validated(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            throw new DomainException(DomainError.ValidationFailed, "An avatar source requires captured bytes.");
        }

        return bytes;
    }

    private static string ValidatedContentType(string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            throw new DomainException(DomainError.ValidationFailed, "An avatar source requires a content type.");
        }

        return contentType;
    }
}
