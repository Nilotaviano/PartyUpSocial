using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// The one poll read-side lookup the voter-grid view needs beyond the aggregate itself
/// (PHASE6_DESIGN.md §4 "GET /{id}"): a poll's registered participants carry no display name of
/// their own (unlike a guest's <c>GuestIdentity.DisplayName</c>, already inside the aggregate), so
/// this bulk-resolves them the same way <c>SessionReadRepository.FetchDisplayNamesAsync</c> does for
/// sessions — a distinct port because it is the poll surface's only read-repository dependency; no
/// paginated poll list exists yet (S4c).
/// </summary>
public interface IPollReadRepository
{
    Task<IReadOnlyDictionary<Guid, DisplayName?>> FindDisplayNamesAsync(
        IReadOnlyCollection<UserId> userIds,
        CancellationToken cancellationToken);
}
