using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Users;

// Domain events carry ids and instants only — never names or rendered text (DOMAIN_MODEL §9).

public sealed record UserOnboardingCompleted(UserId UserId) : IDomainEvent;

public sealed record UserUsernameChanged(UserId UserId) : IDomainEvent;

public sealed record UserDeletionRequested(UserId UserId, Instant GraceEndsAt) : IDomainEvent;

public sealed record UserDeletionCanceled(UserId UserId) : IDomainEvent;

public sealed record UserTombstoned(UserId UserId, Instant DeletedAt) : IDomainEvent;

public sealed record UserBanned(UserId UserId, Instant BannedAt) : IDomainEvent;

public sealed record UserUnbanned(UserId UserId) : IDomainEvent;
