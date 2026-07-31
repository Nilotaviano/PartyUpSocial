using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Accounts;

public abstract record PostAuthenticationResult
{
    private PostAuthenticationResult()
    {
    }

    public sealed record Admitted(UserId UserId, bool DeletionWasCanceled)
        : PostAuthenticationResult;

    public sealed record Refused(string Code)
        : PostAuthenticationResult;
}
