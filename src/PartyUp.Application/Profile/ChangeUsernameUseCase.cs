using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Text;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Profile;

public sealed class ChangeUsernameUseCase(
    IUserRepository users,
    IUsernameRegistryRepository usernames,
    ITextPolicy textPolicy,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<ProfileMutationResult> ExecuteAsync(
        UserId userId,
        string candidate,
        CancellationToken cancellationToken)
    {
        TextPolicyResult policy = textPolicy.Check(candidate, TextField.Username, locale: null);
        if (!policy.IsAllowed)
        {
            return ProfileMutationResult.Failed(policy.ErrorKey!);
        }

        Username next = Username.Create(policy.NormalizedValue);
        return await unitOfWork.ExecuteAsync(
            async ct =>
            {
                User? user = await users.FindForUpdateAsync(userId, ct);
                if (user is null)
                {
                    return ProfileMutationResult.Failed("not_found");
                }

                if (!user.CanChangeUsername(clock) &&
                    user.Username is { } currentName &&
                    !UsernameRegistry.SameName(currentName, next))
                {
                    return ProfileMutationResult.Failed(
                        "username_cooldown",
                        user.UsernameChangedAt + User.UsernameChangeCooldown);
                }

                UsernameRegistryEntry? current = await usernames.FindActiveForOwnerAsync(userId, ct);
                if (current is null)
                {
                    return ProfileMutationResult.Failed("not_found");
                }

                string canonical = UsernameRegistry.Canonicalize(next);
                UsernameRegistryEntry? target = string.Equals(
                    current.NameCanonical,
                    canonical,
                    StringComparison.Ordinal)
                    ? current
                    : await usernames.FindAsync(canonical, ct);
                UsernameChangeResult change = UsernameRegistry.Change(
                    current,
                    target,
                    next,
                    userId,
                    clock);
                if (target is null)
                {
                    usernames.Add(change.ClaimedEntry);
                }

                user.ChangeUsername(next, clock);
                return ProfileMutationResult.Completed;
            },
            cancellationToken);
    }
}

public sealed record ProfileMutationResult(bool Succeeded, string? Code, Instant? RetryAt)
{
    public static ProfileMutationResult Completed { get; } = new(true, null, null);

    public static ProfileMutationResult Failed(string code, Instant? retryAt = null) =>
        new(false, code, retryAt);
}
