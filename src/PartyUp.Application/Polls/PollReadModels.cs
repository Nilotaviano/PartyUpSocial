using PartyUp.Domain.Polls;

namespace PartyUp.Application.Polls;

/// <summary>
/// What <c>IPollRepository.LoadForUpdateAsync</c> returns once the poll parent is locked. Unlike
/// <c>SessionForUpdate</c>, a poll has no sibling tables written under its parent lock outside its
/// own aggregate child collections (no poll-side reminder handoffs in this phase; mute rows are read
/// by the notification materializer, not by command use cases) — this wrapper exists for the same
/// "the lock happened, here is what it protects" shape, not because more than the aggregate is
/// needed today.
/// </summary>
public sealed record PollForUpdate(Poll Poll);
