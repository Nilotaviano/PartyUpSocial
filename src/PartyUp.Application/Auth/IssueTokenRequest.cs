using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Auth;

public sealed record IssueTokenRequest(
    AuthTokenPurpose Purpose,
    UserId? UserId,
    string? EmailNormalized,
    Duration Ttl,
    PendingRegistration? Pending = null,
    ExternalIntentPayload? Intent = null,
    byte[]? RequestIpHash = null);