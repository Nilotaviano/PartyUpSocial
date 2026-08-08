using PartyUp.Application.Polls;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// The Poll aggregate's own persistence port, mirroring <see cref="ISessionRepository"/>
/// (PHASE6_DESIGN.md §2). Every invariant-touching command loads through
/// <see cref="LoadForUpdateAsync"/>, which locks the poll parent row before its options,
/// participants, votes, join requests, guest identities and guest links are read — no
/// occupancy/option-count query ever runs earlier (P3-LK-01, the poll parent's own analogue of
/// A-068/A-069).
/// </summary>
public interface IPollRepository
{
    void Add(Poll poll);

    /// <summary>
    /// <see cref="Poll.DeleteOption"/> removes the option (and cascades its votes) from the
    /// aggregate's own in-memory collections only — the option/vote rows carry a required FK with no
    /// cascade delete configured (D-080's "no floor" option semantics is a Restrict relationship, not
    /// an owned/cascade one), so a collection-severance alone would surface an EF orphan exception
    /// rather than a delete. The application layer calls this explicitly, once, right after
    /// <see cref="Poll.DeleteOption"/> mutates the aggregate, to tell the change tracker these rows are
    /// deleted rather than merely detached.
    /// </summary>
    void RemoveOptionAndCascadedVotes(PollOption deletedOption, IReadOnlyList<PollVote> cascadedVotes);

    /// <summary>
    /// A non-locking read for callers that only need the aggregate shape; invariant-touching
    /// commands must use <see cref="LoadForUpdateAsync"/> instead.
    /// </summary>
    Task<Poll?> FindAsync(PollId id, CancellationToken cancellationToken);

    /// <summary>
    /// A non-tracking read for a pre-lock authorization probe (mirrors
    /// <see cref="ISessionRepository.FindNoTrackingAsync"/>): the returned aggregate is never
    /// entered into the change tracker, so a later <see cref="LoadForUpdateAsync"/> call for the
    /// same id in the same unit of work is guaranteed to see a fresh row under its own lock rather
    /// than EF's identity-map reuse of this earlier, untracked probe.
    /// </summary>
    Task<Poll?> FindNoTrackingAsync(PollId id, CancellationToken cancellationToken);

    /// <summary>
    /// Locks the poll parent with <c>FOR NO KEY UPDATE</c> before loading every child collection the
    /// aggregate owns. This is the sole entry point invariant-touching poll commands use.
    /// </summary>
    Task<PollForUpdate?> LoadForUpdateAsync(PollId id, CancellationToken cancellationToken);
}
