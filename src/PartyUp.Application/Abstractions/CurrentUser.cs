using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Abstractions;

public sealed record CurrentUser(
    UserId Id,
    UserLifecycle Lifecycle,
    bool IsBanned,
    Instant? GraceEndsAt,
    bool IsAdmin,
    bool IsProfileComplete,
    Locale? Locale);
