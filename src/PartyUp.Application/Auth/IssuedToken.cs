using NodaTime;

namespace PartyUp.Application.Auth;

public sealed record IssuedToken(Guid TokenId, string RawToken, Instant ExpiresAt);