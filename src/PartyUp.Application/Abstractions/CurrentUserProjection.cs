using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Abstractions;

public sealed record CurrentUserProjection(
    UserId Id,
    UserLifecycle Lifecycle,
    bool IsBanned,
    Instant? GraceEndsAt,
    bool IsAdmin,
    bool IsProfileComplete,
    Locale? Locale,
    string SecurityStamp,
    Instant? LastSeenAt = null);
