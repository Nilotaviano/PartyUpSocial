using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Users;

/// <summary>
/// The pure domain service over the one username constraint domain (DOMAIN_MODEL §3). It decides;
/// loading the candidate rows and enforcing the DB-level unique index on
/// <see cref="UsernameRegistryEntry.NameCanonical"/> belongs to the layers above.
/// </summary>
public static class UsernameRegistry
{
    /// <summary>D-145: a former username is reserved for thirty days.</summary>
    public static Duration ReservationPeriod => Duration.FromDays(30);

    /// <summary>
    /// The canonical form is the trimmed name lower-cased with the invariant culture — usernames
    /// are case-insensitive (D-128), and the canonical form is what the unique index covers.
    /// </summary>
    public static string Canonicalize(Username name) => Canonicalize(name.Value);

    public static string Canonicalize(string? name)
    {
        string trimmed = name?.Trim() ?? string.Empty;
#pragma warning disable CA1308 // The canonical username form is lower case by design (DOMAIN_MODEL §3); it never drives a security decision.
        return trimmed.ToLowerInvariant();
#pragma warning restore CA1308
    }

    public static bool SameName(Username left, Username right) =>
        string.Equals(Canonicalize(left), Canonicalize(right), StringComparison.Ordinal);

    /// <summary>
    /// Claims <paramref name="name"/> for <paramref name="ownerId"/>. <paramref name="existing"/>
    /// is the row already holding the canonical name, or null when nothing holds it.
    /// </summary>
    public static UsernameRegistryEntry Claim(
        UsernameRegistryEntry? existing,
        Username name,
        UserId ownerId,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (existing is null)
        {
            return UsernameRegistryEntry.ClaimNew(name, ownerId);
        }

        RequireSameName(existing, name);

        if (existing.IsActive)
        {
            if (existing.OwnerId == ownerId)
            {
                return existing;
            }

            throw new DomainException(
                DomainError.Duplicate,
                FormattableString.Invariant($"Username '{Canonicalize(name)}' is already taken."));
        }

        existing.ClaimFrom(ownerId, clock);
        return existing;
    }

    /// <summary>
    /// D-128/D-145: the old row flips to <c>Reserved(+30 d)</c> and the new name is claimed in the
    /// same constraint domain. Re-styling the capitalisation of the same canonical name touches no
    /// row at all. The new name is claimed first, so a rejected claim leaves the old row untouched.
    /// </summary>
    public static UsernameChangeResult Change(
        UsernameRegistryEntry current,
        UsernameRegistryEntry? target,
        Username newName,
        UserId ownerId,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(clock);

        RequireOwnedBy(current, ownerId);

        if (string.Equals(current.NameCanonical, Canonicalize(newName), StringComparison.Ordinal))
        {
            if (target is not null && !ReferenceEquals(target, current))
            {
                throw new DomainException(
                    DomainError.ValidationFailed,
                    "The canonical name is unchanged, so no other registry row can hold it.");
            }

            return new UsernameChangeResult(null, current);
        }

        UsernameRegistryEntry claimed = Claim(target, newName, ownerId, clock);
        current.Reserve(clock);

        return new UsernameChangeResult(current, claimed);
    }

    /// <summary>
    /// §11 step 6 / §12-36: the tombstoned user's name enters the same thirty-day reservation, with
    /// no owner reference left behind.
    /// </summary>
    public static UsernameRegistryEntry Release(UsernameRegistryEntry entry, UserId ownerId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(clock);

        RequireOwnedBy(entry, ownerId);
        entry.Reserve(clock);

        return entry;
    }

    private static void RequireSameName(UsernameRegistryEntry entry, Username name)
    {
        if (!entry.Matches(name))
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"Registry row '{entry.NameCanonical}' does not hold '{Canonicalize(name)}'."));
        }
    }

    private static void RequireOwnedBy(UsernameRegistryEntry entry, UserId ownerId)
    {
        if (!entry.IsActive || entry.OwnerId != ownerId)
        {
            throw new DomainException(
                DomainError.Forbidden,
                FormattableString.Invariant($"Username '{entry.NameCanonical}' is not actively owned by {ownerId}."));
        }
    }
}
