namespace PartyUp.Domain.Polls;

/// <summary>
/// What D-225's duplicate branch severed from the aggregate: the guest's participant row and its
/// votes, dropped because the account already holds its own. Both are non-cascading, required FKs,
/// so the caller has to hand them to the persistence layer as explicit deletions.
/// </summary>
public sealed record PollGuestUpgradeResult(
    PollParticipant? RemovedParticipant,
    IReadOnlyList<PollVote> RemovedVotes)
{
    /// <summary>The re-point branch: the guest rows became the account's, nothing was dropped.</summary>
    public static PollGuestUpgradeResult NothingRemoved { get; } = new(null, []);
}
