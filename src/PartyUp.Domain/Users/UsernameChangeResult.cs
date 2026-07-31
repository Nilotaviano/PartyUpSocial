namespace PartyUp.Domain.Users;

/// <summary>
/// The two rows a username change touches: the old name flipped to <c>Reserved</c> and the row now
/// owning the new name. <see cref="ReleasedEntry"/> is null when only the capitalisation changed,
/// which claims no new row.
/// </summary>
public sealed record UsernameChangeResult(
    UsernameRegistryEntry? ReleasedEntry,
    UsernameRegistryEntry ClaimedEntry)
{
    public bool IsCaseOnlyRestyle => ReleasedEntry is null;
}
