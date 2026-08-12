using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Users;

/// <summary>
/// An avatar is either a chosen emoji or the picture drawn from exactly one of the user's linked
/// accounts (D-050/D-190); arbitrary image uploads are forbidden (D-032). Which glyphs count as emoji
/// is application-layer charset policy -- the domain only guarantees the two shapes are mutually
/// exclusive.
/// </summary>
public readonly record struct Avatar
{
    /// <summary>Long enough for ZWJ sequences (flags, family and profession emoji).</summary>
    public const int MaxEmojiLength = 16;

    /// <summary>The D-050 fallback when the avatar-source account is unlinked or removed.</summary>
    public const string FallbackEmoji = "\U0001F3AE";

    private readonly string? _emoji;
    private readonly string? _token;

    private Avatar(AvatarKind kind, string? emoji, AvatarSourceId? sourceId, string? token)
    {
        Kind = kind;
        _emoji = emoji;
        SourceId = sourceId;
        _token = token;
    }

    public AvatarKind Kind { get; }

    /// <summary>The chosen glyph; empty when <see cref="Kind"/> is <see cref="AvatarKind.External"/>.</summary>
    public string Emoji => _emoji ?? string.Empty;

    /// <summary>The single avatar source; null unless <see cref="Kind"/> is <see cref="AvatarKind.External"/>.</summary>
    public AvatarSourceId? SourceId { get; }

    /// <summary>
    /// AVATARS_DESIGN.md §3.1/§3.3: the proxy token denormalized from the referenced
    /// <see cref="AvatarSource"/> onto the <c>users</c> row so <c>AvatarResponse.From</c> stays a
    /// pure, join-free mapper. Empty unless <see cref="Kind"/> is <see cref="AvatarKind.External"/>.
    /// Only <see cref="User"/> may set it authoritatively (it re-derives the token from its own
    /// <see cref="AvatarSource"/> rows on every mutation) -- callers building a draft
    /// <see cref="Avatar"/> need not know it.
    /// </summary>
    public string Token => _token ?? string.Empty;

    public bool IsEmoji => Kind == AvatarKind.Emoji;

    public bool IsExternal => Kind == AvatarKind.External;

    /// <summary>True only for <c>default(Avatar)</c>, which carries neither shape.</summary>
    public bool IsUnset => Kind == AvatarKind.Emoji && Emoji.Length == 0;

    public static Avatar Fallback => FromEmoji(FallbackEmoji);

    public static Avatar FromEmoji(string? emoji)
    {
        string trimmed = emoji?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            throw new DomainException(DomainError.ValidationFailed, "An emoji avatar requires a glyph.");
        }

        if (trimmed.Length > MaxEmojiLength)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"An emoji avatar must be at most {MaxEmojiLength} character(s) long, but was {trimmed.Length}."));
        }

        return new Avatar(AvatarKind.Emoji, trimmed, null, null);
    }

    /// <summary>
    /// <paramref name="token"/> defaults empty: application-layer drafts only ever name a
    /// <see cref="AvatarSourceId"/> (AVATARS_DESIGN.md §3.5's draft shape). <see cref="User.ChangeAvatar"/>
    /// and <see cref="User.CompleteOnboarding"/> overwrite it with the owning <see cref="AvatarSource"/>'s
    /// real token before it is ever persisted, so a caller-supplied token is never trusted.
    /// </summary>
    public static Avatar FromSource(AvatarSourceId sourceId, string? token = null) =>
        new(AvatarKind.External, null, sourceId, token ?? string.Empty);

    public bool DrawsFrom(AvatarSourceId sourceId) =>
        IsExternal && SourceId == sourceId;

    public override string ToString() =>
        IsExternal
            ? FormattableString.Invariant($"external:{SourceId}")
            : FormattableString.Invariant($"emoji:{Emoji}");
}
