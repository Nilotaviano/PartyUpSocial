using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Application.Auth;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Deletion;

public sealed class IssueSteamDeletionConfirmationUseCase(
    IUserAccountGateway accounts,
    ISecurityTokenService tokens,
    IUnitOfWork unitOfWork)
{
    private static readonly Duration ConfirmationTtl = Duration.FromMinutes(10);

    public async Task<SteamDeletionConfirmationResult> ExecuteAsync(
        string steamId64,
        CancellationToken cancellationToken)
    {
        UserId? userId = await accounts.FindUserIdByLoginAsync(
            ExternalProviderPolicy.Steam,
            steamId64,
            cancellationToken);
        if (userId is null)
        {
            return SteamDeletionConfirmationResult.Failed("not_found");
        }

        IssuedToken issued = await unitOfWork.ExecuteAsync(
            ct => tokens.IssueAsync(
                new IssueTokenRequest(
                    AuthTokenPurpose.DeletionRequestConfirmation,
                    userId,
                    null,
                    ConfirmationTtl),
                ct),
            cancellationToken);
        return SteamDeletionConfirmationResult.Completed(issued.RawToken);
    }
}

public sealed record SteamDeletionConfirmationResult(bool Succeeded, string? Code, string? Token)
{
    public static SteamDeletionConfirmationResult Completed(string token) =>
        new(true, null, token);

    public static SteamDeletionConfirmationResult Failed(string code) =>
        new(false, code, null);
}
