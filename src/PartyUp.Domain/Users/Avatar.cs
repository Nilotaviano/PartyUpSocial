using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Users;

/// <summary>
/// An avatar is either a chosen emoji or exactly one linked platform's picture (D-050); arbitrary
/// image uploads are forbidden (D-032). Which glyphs count as emoji is application-layer charset
/// policy — the domain only guarantees the two shapes are mutually exclusive.
/// </summary>
public readonly record struct Avatar
{
    /// <summary>Long enough for ZWJ sequences (flags, family and profession emoji).</summary>
    public const int MaxEmojiLength = 16;

    /// <summary>The D-050 fallback when the avatar-source platform link is removed.</summary>
    public const string FallbackEmoji = "\U0001F3AE";

    private readonly string? _emoji;

    private Avatar(AvatarKind kind, string? emoji, PlatformLinkId? sourcePlatformLinkId)
    {
        Kind = kind;
        _emoji = emoji;
        SourcePlatformLinkId = sourcePlatformLinkId;
    }

    public AvatarKind Kind { get; }

    /// <summary>The chosen glyph; empty when <see cref="Kind"/> is <see cref="AvatarKind.Platform"/>.</summary>
    public string Emoji => _emoji ?? string.Empty;

    /// <summary>The single avatar-source platform link; null unless <see cref="Kind"/> is Platform.</summary>
    public PlatformLinkId? SourcePlatformLinkId { get; }

    public bool IsEmoji => Kind == AvatarKind.Emoji;

    public bool IsPlatform => Kind == AvatarKind.Platform;

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

        return new Avatar(AvatarKind.Emoji, trimmed, null);
    }

    public static Avatar FromPlatformLink(PlatformLinkId sourcePlatformLinkId) =>
        new(AvatarKind.Platform, null, sourcePlatformLinkId);

    public bool DrawsFrom(PlatformLinkId platformLinkId) =>
        IsPlatform && SourcePlatformLinkId == platformLinkId;

    public override string ToString() =>
        IsPlatform
            ? FormattableString.Invariant($"platform:{SourcePlatformLinkId}")
            : FormattableString.Invariant($"emoji:{Emoji}");
}
