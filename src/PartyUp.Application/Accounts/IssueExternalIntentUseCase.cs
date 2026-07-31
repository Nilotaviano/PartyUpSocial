using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Accounts;

public sealed class IssueExternalIntentUseCase(
    ReauthenticateUseCase reauthenticate,
    ISecurityTokenService tokens,
    IUnitOfWork unitOfWork)
{
    private static readonly Duration IntentTtl = Duration.FromMinutes(10);

    public async Task<ExternalIntentIssueResult> ExecuteAsync(
        IssueExternalIntentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!ExternalProviderPolicy.IsSupported(command.Provider))
        {
            return ExternalIntentIssueResult.Failed("provider_unavailable");
        }

        UserId? subject = command.Mode == ExternalStartMode.Login
            ? null
            : command.CallerId;
        if (command.Mode != ExternalStartMode.Login && subject is null)
        {
            return ExternalIntentIssueResult.Failed("unauthorized");
        }

        return await unitOfWork.ExecuteAsync(
            async ct =>
            {
                if (command.Mode == ExternalStartMode.Link &&
                    !await reauthenticate.ValidateProofAsync(
                        subject!.Value,
                        command.CurrentPassword,
                        command.ReauthenticationToken,
                        ct))
                {
                    return ExternalIntentIssueResult.Failed("reauth_required");
                }

                IssuedToken issued = await tokens.IssueAsync(
                    new IssueTokenRequest(
                        AuthTokenPurpose.ExternalStartIntent,
                        subject,
                        null,
                        IntentTtl,
                        Intent: new ExternalIntentPayload(
                            ExternalProviderPolicy.Canonicalize(command.Provider),
                            command.Mode,
                            command.ReturnTo,
                            command.AgeGateAttested,
                            command.BinderHash)),
                    ct);
                return ExternalIntentIssueResult.Issued(issued.RawToken);
            },
            cancellationToken);
    }
}

public sealed record IssueExternalIntentCommand(
    string Provider,
    ExternalStartMode Mode,
    string ReturnTo,
    bool AgeGateAttested,
    string BinderHash,
    UserId? CallerId,
    string? CurrentPassword,
    string? ReauthenticationToken);

public sealed record ExternalIntentIssueResult(bool Succeeded, string? Intent, string? Code)
{
    public static ExternalIntentIssueResult Issued(string intent) => new(true, intent, null);

    public static ExternalIntentIssueResult Failed(string code) => new(false, null, code);
}

public static class ExternalProviderPolicy
{
    public const string Google = "google";
    public const string Steam = "steam";

    public static bool IsSupported(string provider) =>
        string.Equals(provider, Google, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(provider, Steam, StringComparison.OrdinalIgnoreCase);

    public static string Canonicalize(string provider) =>
        string.Equals(provider, Google, StringComparison.OrdinalIgnoreCase) ? Google : Steam;

    public static bool CanCreateWithoutAddress(string provider) =>
        string.Equals(provider, Steam, StringComparison.OrdinalIgnoreCase);
}
