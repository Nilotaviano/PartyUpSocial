namespace PartyUp.Domain.Kernel;

/// <summary>
/// The audience level of a session or a poll (D-198, DOMAIN_MODEL §7): <see cref="Public"/> admits
/// rule-2 friend-of-extender inheritance, <see cref="Private"/> is rule 1 only, and
/// <see cref="Groups"/> admits rule 2g — owners and Accepted members of the targeted friend groups.
/// Persisted; append only, never renumber.
/// </summary>
public enum ContentVisibility
{
    Public = 0,

    Private = 1,

    Groups = 2,
}
