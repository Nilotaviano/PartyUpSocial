using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Onboarding;

public sealed class CompleteOnboardingUseCase(
    ICurrentUserAccessor currentUser,
    IUserRepository users,
    IUsernameRegistryRepository usernames,
    ValidateProfileDraftUseCase validator,
    IUnitOfWork unitOfWork,
    IDateTimeZoneProvider timeZones,
    IClock clock)
{
    public Task<OnboardingCompletionResult> ExecuteAsync(
        ProfileDraft draft,
        CancellationToken cancellationToken)
    {
        ProfileDraftValidation validation = validator.Execute(draft);
        if (!validation.IsValid ||
            draft.Username is null ||
            draft.DisplayName is null ||
            draft.Avatar is null ||
            draft.TimeZone is null ||
            draft.Locale is null)
        {
            var errors = new Dictionary<string, IReadOnlyList<string>>(
                validation.Errors,
                StringComparer.Ordinal);
            AddRequired(errors, "username", draft.Username);
            AddRequired(errors, "displayName", draft.DisplayName);
            AddRequired(errors, "avatar", draft.Avatar);
            AddRequired(errors, "timeZone", draft.TimeZone);
            AddRequired(errors, "locale", draft.Locale);
            return Task.FromResult(
                OnboardingCompletionResult.Invalid(errors));
        }

        Username username = Username.Create(draft.Username);
        DisplayName displayName = DisplayName.Create(draft.DisplayName);
        Avatar avatar = ValidateProfileDraftUseCase.ToAvatar(draft.Avatar);
        DateTimeZone timeZone = timeZones.GetZoneOrNull(draft.TimeZone)
            ?? throw new DomainException(DomainError.ValidationFailed, "The time zone is invalid.");
        Locale locale = ValidateProfileDraftUseCase.ToLocale(draft.Locale);

        return CompleteAsync(
            username,
            displayName,
            avatar,
            timeZone,
            locale,
            cancellationToken);
    }

    private static void AddRequired(
        Dictionary<string, IReadOnlyList<string>> errors,
        string field,
        object? value)
    {
        if (value is null && !errors.ContainsKey(field))
        {
            errors[field] = [$"errors.{field}.required"];
        }
    }

    private async Task<OnboardingCompletionResult> CompleteAsync(
        Username username,
        DisplayName displayName,
        Avatar avatar,
        DateTimeZone timeZone,
        Locale locale,
        CancellationToken cancellationToken)
    {
        CurrentUser? caller = await currentUser.GetAsync(cancellationToken);
        if (caller is null)
        {
            return OnboardingCompletionResult.Failed("unauthorized");
        }

        return await unitOfWork.ExecuteAsync(
            async ct =>
            {
                User? user = await users.FindForUpdateAsync(caller.Id, ct);
                if (user is null)
                {
                    return OnboardingCompletionResult.Failed("not_found");
                }

                string canonical = UsernameRegistry.Canonicalize(username);
                UsernameRegistryEntry? existing = await usernames.FindAsync(canonical, ct);
                UsernameRegistryEntry claimed = UsernameRegistry.Claim(
                    existing,
                    username,
                    user.Id,
                    clock);
                if (existing is null)
                {
                    usernames.Add(claimed);
                }

                user.CompleteOnboarding(
                    username,
                    displayName,
                    avatar,
                    timeZone,
                    locale,
                    clock);
                return OnboardingCompletionResult.Success(user.Id);
            },
            cancellationToken);
    }
}

public sealed record OnboardingCompletionResult(
    bool Succeeded,
    UserId? UserId,
    string? Code,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Errors)
{
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> NoErrors { get; } =
        new Dictionary<string, IReadOnlyList<string>>();

    public static OnboardingCompletionResult Success(UserId userId) =>
        new(true, userId, null, NoErrors);

    public static OnboardingCompletionResult Failed(string code) =>
        new(false, null, code, NoErrors);

    public static OnboardingCompletionResult Invalid(
        IReadOnlyDictionary<string, IReadOnlyList<string>> errors) =>
        new(false, null, "validation_failed", errors);
}
