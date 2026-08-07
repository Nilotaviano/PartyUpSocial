using PartyUp.Application.Availability;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// The availability read port (PHASE5_DESIGN.md §5.1). One method, returning raw rows only — it
/// never expands, merges, or ranks (A-076); that is Application's and Domain's job. Its statement
/// count is fixed at four regardless of owner count, which is the number the A-079 budget is stated
/// in (<c>AvailabilityReadRepositoryStatementCountTests</c>, S2).
/// </summary>
public interface IAvailabilityReadRepository
{
    Task<IReadOnlyList<AvailabilityOwnerData>> ReadOwnersWindowAsync(
        IReadOnlyCollection<UserId> ownerIds,
        TimeRange window,
        CancellationToken cancellationToken);
}
