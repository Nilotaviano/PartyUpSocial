using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Visibility;

/// <summary>
/// The availability arm of §7: the owner reads and writes their own blocks — an owner is not their
/// own friend, so this has to be an explicit grant — and Accepted friends read them, nobody else
/// and nobody writes (D-009/D-024). Blocking deletes the friendship (D-065), so a blocked
/// counterpart falls out; the check is explicit anyway, because the engine fails closed.
/// </summary>
public static class AvailabilityVisibility
{
    public static bool CanRead(UserId ownerId, Viewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        if (viewer.Id == ownerId)
        {
            return true;
        }

        return viewer.IsFriendOf(ownerId) && !viewer.IsBlockedWith(ownerId);
    }

    /// <summary>D-009: availability is edited by its owner only; friends' access is read-only.</summary>
    public static bool CanWrite(UserId ownerId, Viewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        return viewer.Id == ownerId;
    }

    /// <summary>The overlap engine's input filter (D-100): selected friends the viewer may actually read.</summary>
    public static IReadOnlyList<UserId> ReadableOwners(IEnumerable<UserId> ownerIds, Viewer viewer)
    {
        ArgumentNullException.ThrowIfNull(ownerIds);

        List<UserId> readable = [];
        foreach (UserId ownerId in ownerIds)
        {
            if (CanRead(ownerId, viewer) && !readable.Contains(ownerId))
            {
                readable.Add(ownerId);
            }
        }

        return readable;
    }

    public static void RequireRead(UserId ownerId, Viewer viewer)
    {
        if (!CanRead(ownerId, viewer))
        {
            throw new DomainException(DomainError.NotFound, "No such availability.");
        }
    }

    public static void RequireWrite(UserId ownerId, Viewer viewer)
    {
        if (!CanWrite(ownerId, viewer))
        {
            throw new DomainException(DomainError.NotFound, "No such availability.");
        }
    }
}
