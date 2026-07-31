namespace PartyUp.Application.Auth;

public sealed record ExternalIntentPayload(
    string Provider,
    ExternalStartMode Mode,
    string ReturnTo,
    bool AgeGateAttested,
    string BinderHash);