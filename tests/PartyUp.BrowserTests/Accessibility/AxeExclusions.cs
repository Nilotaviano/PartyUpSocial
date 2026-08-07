namespace PartyUp.BrowserTests.Accessibility;

/// <summary>
/// One checked-in, dated axe finding on a Phase 4 route (A-082, P5-AXE-02). <paramref name="RouteName"/>
/// must match an <see cref="AuditedRoutes"/> entry's <c>Name</c>; <paramref name="RuleId"/> is the axe
/// rule id (e.g. <c>color-contrast</c>); <paramref name="DiscoveredOn"/> is the ISO date the S1 seed
/// run recorded it; <paramref name="TraceabilityRow"/> cites the `TRACEABILITY.md` line carrying this
/// exact entry.
/// </summary>
public sealed record AxeExclusion(
    string RouteName,
    string RuleId,
    string DiscoveredOn,
    string TraceabilityRow);

/// <summary>
/// The S1 seed list (A-082, PHASE5_DESIGN.md §9): every axe finding recorded by actually running the
/// axe lane over every Phase 4 route in <see cref="AuditedRoutes"/> at the shipped WCAG 2.2 A/AA tag
/// set, at S1 close. This is a discovery artifact, not a remediation backlog — default disposition is
/// a named carry to the Phase 8 audit/remediation pass (`IMPLEMENTATION_PLAN.md:126`,
/// `TRACEABILITY.md:166`); P5-AXE-02 (S8) is the gate that disposes every entry. The list may only
/// shrink from here (<see cref="AxeExclusionListTests"/>): no slice between S1 and S8 may add to it,
/// and no Phase 5 route may ever appear here — a new route ships axe-clean from the slice that adds it.
/// </summary>
public static class AxeExclusions
{
    public static IReadOnlyList<AxeExclusion> Entries { get; } =
    [
    ];
}
