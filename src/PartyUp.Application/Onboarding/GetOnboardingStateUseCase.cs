using PartyUp.Application.Abstractions;

namespace PartyUp.Application.Onboarding;

public sealed class GetOnboardingStateUseCase(ICurrentUserAccessor currentUser)
{
    public async Task<OnboardingState?> ExecuteAsync(CancellationToken cancellationToken)
    {
        CurrentUser? user = await currentUser.GetAsync(cancellationToken);
        return user is null
            ? null
            : new OnboardingState(["ageGate", "auth"], "username", user.IsProfileComplete);
    }
}

public sealed record OnboardingState(
    IReadOnlyList<string> CompletedSteps,
    string NextStep,
    bool IsComplete);
