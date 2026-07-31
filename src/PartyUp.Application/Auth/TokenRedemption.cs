using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Auth;

public sealed record TokenRedemption(
    bool Succeeded,
    TokenRedemptionFailure? Failure,
    AuthTokenPurpose Purpose,
    UserId? UserId,
    string? EmailNormalized,
    PendingRegistration? Pending,
    ExternalIntentPayload? Intent,
    Instant ConsumedAt);