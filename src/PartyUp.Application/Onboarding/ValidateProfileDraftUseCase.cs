using NodaTime;
using PartyUp.Application.Accounts;
using PartyUp.Application.Text;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Onboarding;

public sealed class ValidateProfileDraftUseCase(ITextPolicy textPolicy)
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
            DateTimeZoneProviders.Tzdb.GetZoneOrNull(draft.TimeZone) is null)
        {
            errors["timeZone"] = ["errors.timeZone.invalid"];
        }

        if (draft.Locale is not null && !IsSupportedLocale(draft.Locale))
        {
            errors["locale"] = ["errors.locale.invalid"];
        }

        return new ProfileDraftValidation(errors);
    }

    internal static Avatar ToAvatar(AvatarDraft draft) =>
        draft.Kind.Trim().ToLowerInvariant() switch
        {
            "emoji" => Avatar.FromEmoji(draft.Emoji),
            "platform" when draft.PlatformLinkId is { } id =>
                Avatar.FromPlatformLink(PlatformLinkId.From(id)),
            _ => throw new DomainException(
                DomainError.ValidationFailed,
                "The avatar shape is invalid."),
        };

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

public sealed record AvatarDraft(string Kind, string? Emoji, Guid? PlatformLinkId);

public sealed record ProfileDraftValidation(
    IReadOnlyDictionary<string, IReadOnlyList<string>> Errors)
{
    public bool IsValid => Errors.Count == 0;
}
