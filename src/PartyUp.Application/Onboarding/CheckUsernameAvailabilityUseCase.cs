using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Text;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Onboarding;

public sealed class CheckUsernameAvailabilityUseCase(
    ITextPolicy textPolicy,
    IUsernameRegistryRepository usernames,
    IClock clock)
{
    public async Task<UsernameAvailability> ExecuteAsync(
        string username,
        CancellationToken cancellationToken)
    {
        TextPolicyResult policy = textPolicy.Check(
            username,
            TextField.Username,
            locale: null);
        if (!policy.IsAllowed)
        {
            return new UsernameAvailability(
                false,
                policy.ErrorKey == "errors.text.profanity" ? "profanity" : "invalid");
        }

        Username value;
        try
        {
            value = Username.Create(policy.NormalizedValue);
        }
        catch (DomainException)
        {
            return new UsernameAvailability(false, "invalid");
        }

        UsernameRegistryEntry? existing = await usernames.FindAsync(
            UsernameRegistry.Canonicalize(value),
            cancellationToken);
        if (existing is null || existing.IsClaimable(clock))
        {
            return new UsernameAvailability(true, null);
        }

        return new UsernameAvailability(false, existing.IsReserved ? "reserved" : "taken");
    }
}

public sealed record UsernameAvailability(bool Available, string? Reason);
