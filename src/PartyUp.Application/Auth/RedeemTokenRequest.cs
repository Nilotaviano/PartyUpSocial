namespace PartyUp.Application.Auth;

public sealed record RedeemTokenRequest(
    AuthTokenPurpose Purpose,
    string RawToken,
    string? BinderHash = null,
    bool? AgeGateAttested = null);