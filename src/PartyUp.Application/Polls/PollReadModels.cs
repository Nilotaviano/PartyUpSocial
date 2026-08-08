using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;

namespace PartyUp.Application.Polls;

/// <summary>
/// What <c>IPollRepository.LoadForUpdateAsync</c> returns once the poll parent is locked, mirroring
/// <c>SessionForUpdate</c>: the aggregate plus the sibling <c>notification_mutes</c> rows for this
/// poll, read under the same parent lock — not part of the aggregate's own child collections, but
/// needed by <c>FinalizePollUseCase</c> to carry mutes onto the created session (§12-15). A poll has
/// no reminder handoffs of its own (those attach to the session finalization creates).
/// </summary>
public sealed record PollForUpdate(Poll Poll, IReadOnlyList<NotificationMute> Mutes);
