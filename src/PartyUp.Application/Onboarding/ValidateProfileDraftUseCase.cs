using NodaTime;
using PartyUp.Application.Accounts;
using PartyUp.Application.Profile;
using PartyUp.Application.Text;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Onboarding;

public sealed class ValidateProfileDraftUseCase(
    ITextPolicy textPolicy,
    IDateTimeZoneProvider timeZones)
{
    public ProfileDraftValidation Execute(ProfileDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        ValidateText(
            draft.Username,
            "username",
            TextField.Username,
            value => Username.Create(value),
            errors);
        ValidateText(
            draft.DisplayName,
            "displayName",
            TextField.DisplayName,
            value => DisplayName.Create(value),
            errors);

        if (draft.Avatar is not null)
        {
            try
            {
                _ = ToAvatar(draft.Avatar);
            }
            catch (DomainException)
            {
                errors["avatar"] = ["errors.avatar.invalid"];
            }
        }

        if (draft.TimeZone is not null &&
            timeZones.GetZoneOrNull(draft.TimeZone) is null)
        {
            errors["timeZone"] = ["errors.timeZone.invalid"];
        }

        if (draft.Locale is not null && !IsSupportedLocale(draft.Locale))
        {
            errors["locale"] = ["errors.locale.invalid"];
        }

        return new ProfileDraftValidation(errors);
    }

    /// <summary>
    /// A client picks an emoji glyph or names one of its own server-captured avatar sources
    /// (AVATARS_DESIGN.md §3.5); it never uploads or supplies picture bytes (D-032). A source
    /// draft only carries the <see cref="AvatarSourceId"/> here -- ownership and the token are
    /// resolved by <see cref="User.ChangeAvatar"/>/<see cref="User.CompleteOnboarding"/>, which
    /// have the aggregate's own avatar sources to check against.
    /// </summary>
    internal static Avatar ToAvatar(AvatarDraft draft) =>
        draft.Kind.Trim().ToLowerInvariant() switch
        {
            "emoji" => FromCatalogEmoji(draft.Emoji),
            "source" => FromSourceId(draft.AvatarSourceId),
            _ => throw new DomainException(
                DomainError.ValidationFailed,
                "The avatar shape is invalid."),
        };

    /// <summary>
    /// <see cref="Avatar.FromEmoji"/> trims, so the catalog check runs on the stored glyph.
    /// </summary>
    private static Avatar FromCatalogEmoji(string? emoji)
    {
        Avatar avatar = Avatar.FromEmoji(emoji);
        return AvatarCatalog.Contains(avatar.Emoji)
            ? avatar
            : throw new DomainException(
                DomainError.ValidationFailed,
                "The avatar glyph is not in the catalog.");
    }

    private static Avatar FromSourceId(Guid? avatarSourceId) =>
        avatarSourceId is { } id
            ? Avatar.FromSource(AvatarSourceId.From(id))
            : throw new DomainException(
                DomainError.ValidationFailed,
                "A source avatar requires an avatarSourceId.");

    internal static Locale ToLocale(string locale) => AccountInput.Locale(locale);

    private void ValidateText<T>(
        string? candidate,
        string key,
        TextField field,
        Func<string?, T> create,
        Dictionary<string, IReadOnlyList<string>> errors)
    {
        if (candidate is null)
        {
            return;
        }

        TextPolicyResult policy = textPolicy.Check(candidate, field, locale: null);
        if (!policy.IsAllowed)
        {
            errors[key] = [policy.ErrorKey!];
            return;
        }

        try
        {
            _ = create(policy.NormalizedValue);
        }
        catch (DomainException)
        {
            errors[key] = [$"errors.{key}.invalid"];
        }
    }

    private static bool IsSupportedLocale(string locale)
    {
        string value = locale.Trim();
        return value.Equals("en", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("en-", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("es", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("es-", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("pt", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("pt-", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record ProfileDraft(
    string? Username,
    string? DisplayName,
    AvatarDraft? Avatar,
    string? TimeZone,
    string? Locale);

public sealed record AvatarDraft(string Kind, string? Emoji, Guid? PlatformLinkId, Guid? AvatarSourceId = null);

public sealed record ProfileDraftValidation(
    IReadOnlyDictionary<string, IReadOnlyList<string>> Errors)
{
    public bool IsValid => Errors.Count == 0;
}
