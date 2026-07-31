using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Visibility;

/// <summary>
/// The single answer the visibility engine returns (DOMAIN_MODEL §7): found, and by which rule, or
/// not found. Callers branch on <see cref="IsVisible"/> only — a viewer must never be able to tell
/// "you may not see this" from "this does not exist".
/// </summary>
public readonly record struct VisibilityResult
{
    private VisibilityResult(VisibilityGrant grant, UserId? extenderId)
    {
        Grant = grant;
        ExtenderId = extenderId;
    }

    public VisibilityGrant Grant { get; }

    /// <summary>The friend whose participation carried rule 2; null for direct and not-found results.</summary>
    public UserId? ExtenderId { get; }

    public bool IsVisible => Grant != VisibilityGrant.None;

    /// <summary>The fail-closed answer, indistinguishable from a nonexistent target.</summary>
    public static VisibilityResult NotFound => default;

    public static VisibilityResult Direct => new(VisibilityGrant.Direct, null);

    public static VisibilityResult Inherited(UserId extenderId) =>
        new(VisibilityGrant.Inherited, extenderId);
}
