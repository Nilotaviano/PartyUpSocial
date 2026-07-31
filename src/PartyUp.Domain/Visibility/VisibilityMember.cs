using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Visibility;

/// <summary>
/// One registered roster or voter row, reduced to the two facts §7 asks of it: whether it keeps its
/// own holder's direct access (rule 1) and whether it extends access to that holder's friends
/// (rule 2). Guests hold neither — they are never extenders (D-018) and reach content through their
/// link, not the engine (§5.4).
/// </summary>
public readonly record struct VisibilityMember
{
    private VisibilityMember(UserId userId, bool keepsDirectAccess, bool extendsAccess)
    {
        UserId = userId;
        KeepsDirectAccess = keepsDirectAccess;
        ExtendsAccess = extendsAccess;
    }

    public UserId UserId { get; }

    public bool KeepsDirectAccess { get; }

    public bool ExtendsAccess { get; }

    public static VisibilityMember Create(UserId userId, bool keepsDirectAccess, bool extendsAccess) =>
        new(userId, keepsDirectAccess, extendsAccess);
}
