namespace PartyUp.Domain.Visibility;

// Values may reach read models: append only, never renumber.

/// <summary>
/// Which of the DOMAIN_MODEL §7 rules granted access. <see cref="None"/> is the fail-closed
/// default: no rule matched, so the target does not exist for this viewer.
/// </summary>
public enum VisibilityGrant
{
    None = 0,

    /// <summary>§7 rule 1 — organizer, a retained participation, or a Pending join request.</summary>
    Direct = 1,

    /// <summary>§7 rule 2 — an Accepted friendship with an extender on a non-private target.</summary>
    Inherited = 2,
}
