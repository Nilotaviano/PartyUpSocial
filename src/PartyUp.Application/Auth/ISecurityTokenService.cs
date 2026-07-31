namespace PartyUp.Application.Auth;

public interface ISecurityTokenService
{
    Task<IssuedToken> IssueAsync(IssueTokenRequest request, CancellationToken cancellationToken);

    Task<TokenRedemption> RedeemAsync(
        RedeemTokenRequest request,
        CancellationToken cancellationToken);
}