using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Users;

/// <summary>
/// One row in the single username constraint domain (DOMAIN_MODEL §3). <c>Active</c> rows carry
/// their owner; <c>Reserved</c> rows carry the canonical name and a free-after instant only — never
/// a <see cref="UserId"/>, so no PII outlives a tombstone (§11 step 6, §12-36).
/// </summary>
public sealed class UsernameRegistryEntry
{
    private UsernameRegistryEntry()
    {
    }

    private UsernameRegistryEntry(string nameCanonical, UserId ownerId)
    {
        NameCanonical = nameCanonical;
        State = UsernameRegistryState.Active;
        OwnerId = ownerId;
    }

    /// <summary>Trimmed and lower-cased; case-insensitive uniqueness is DB-enforced over it (D-128).</summary>
    public string NameCanonical { get; private set; } = string.Empty;

    public UsernameRegistryState State { get; private set; }

    public UserId? OwnerId { get; private set; }

    /// <summary>The instant the name becomes claimable again; null while <c>Active</c> (D-145).</summary>
    public Instant? ReservedUntil { get; private set; }

    public bool IsActive => State == UsernameRegistryState.Active;

    public bool IsReserved => State == UsernameRegistryState.Reserved;

    public static UsernameRegistryEntry ClaimNew(Username name, UserId ownerId)
    {
        string canonical = UsernameRegistry.Canonicalize(name);

        if (canonical.Length == 0)
        {
            throw new DomainException(DomainError.ValidationFailed, "A registry entry requires a username.");
        }

        return new UsernameRegistryEntry(canonical, ownerId);
    }

    public bool Matches(Username name) =>
        string.Equals(NameCanonical, UsernameRegistry.Canonicalize(name), StringComparison.Ordinal);

    /// <summary>D-145: a reserved name stays un-claimable until its free-after instant.</summary>
    public bool IsClaimable(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return IsReserved && ReservedUntil is { } freeAfter && clock.GetCurrentInstant() >= freeAfter;
    }

    /// <summary>Claims a lapsed reservation. Use <see cref="UsernameRegistry"/> rather than calling directly.</summary>
    public void ClaimFrom(UserId ownerId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (IsActive)
        {
            throw new DomainException(
                DomainError.Duplicate,
                FormattableString.Invariant($"Username '{NameCanonical}' is already taken."));
        }

        if (!IsClaimable(clock))
        {
            throw new DomainException(
                DomainError.NameReserved,
                FormattableString.Invariant($"Username '{NameCanonical}' is reserved until {ReservedUntil!.Value}."));
        }

        State = UsernameRegistryState.Active;
        OwnerId = ownerId;
        ReservedUntil = null;
    }

    /// <summary>
    /// Flips an owned name into its thirty-day, owner-less reservation — the one mechanism behind
    /// both a username change (D-145) and a tombstone (§11 step 6, §12-36).
    /// </summary>
    public void Reserve(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (!IsActive)
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                FormattableString.Invariant($"Username '{NameCanonical}' is already reserved."));
        }

        State = UsernameRegistryState.Reserved;
        OwnerId = null;
        ReservedUntil = clock.GetCurrentInstant() + UsernameRegistry.ReservationPeriod;
    }
}
