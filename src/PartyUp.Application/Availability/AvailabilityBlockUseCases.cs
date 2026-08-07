using PartyUp.Application.Abstractions;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Availability;

/// <summary>
/// One-time block commands (PHASE5_DESIGN.md §5.2, §6.1). Every write is a thin transaction wrapper
/// over Phase 1 Domain (A-076: no Domain change). A plain create touches only <c>users</c> — it locks
/// no series or block row, so P5-LK-01's per-table statement is simply omitted (§6.1 step 4).
/// </summary>
public sealed class CreateAvailabilityBlockUseCase(IAvailabilityRepository availability, IUnitOfWork unitOfWork)
{
    public Task<AvailabilityBlock> ExecuteAsync(UserId ownerId, TimeRange range, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                AvailabilityVisibility.RequireWrite(ownerId, Viewer.Create(ownerId));
                await availability.LockOwnerAsync(ownerId, ct);

                AvailabilityBlock block = AvailabilityBlock.Create(ownerId, range);
                availability.AddBlock(block);
                return block;
            },
            cancellationToken);
}

/// <summary>
/// Edits a one-time block's window (§6.1 step 4a, id form). A non-owner target — a <paramref
/// name="blockId"/> that resolves to someone else's row — is <see cref="DomainError.NotFound"/>,
/// never <see cref="DomainError.Forbidden"/> (§6.1 step 1).
/// </summary>
public sealed class EditAvailabilityBlockUseCase(IAvailabilityRepository availability, IUnitOfWork unitOfWork)
{
    public Task<AvailabilityBlock> ExecuteAsync(
        UserId ownerId, AvailabilityBlockId blockId, TimeRange range, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                AvailabilityVisibility.RequireWrite(ownerId, Viewer.Create(ownerId));
                await availability.LockOwnerAsync(ownerId, ct);

                IReadOnlyList<AvailabilityBlock> locked = await availability.LockBlocksAsync([blockId], ct);
                AvailabilityBlock block = RequireOwned(locked, blockId, ownerId);

                block.Reschedule(range);
                return block;
            },
            cancellationToken);

    internal static AvailabilityBlock RequireOwned(
        IReadOnlyList<AvailabilityBlock> locked, AvailabilityBlockId blockId, UserId ownerId)
    {
        AvailabilityBlock? block = locked.FirstOrDefault(b => b.Id == blockId);
        if (block is null || block.OwnerId != ownerId)
        {
            throw new DomainException(DomainError.NotFound, "No such availability block.");
        }

        return block;
    }
}

/// <summary>Idempotent delete (§8): a missing or non-owner target is a no-op, never an error.</summary>
public sealed class DeleteAvailabilityBlockUseCase(IAvailabilityRepository availability, IUnitOfWork unitOfWork)
{
    public Task<bool> ExecuteAsync(
        UserId ownerId, AvailabilityBlockId blockId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                AvailabilityVisibility.RequireWrite(ownerId, Viewer.Create(ownerId));
                await availability.LockOwnerAsync(ownerId, ct);

                IReadOnlyList<AvailabilityBlock> locked = await availability.LockBlocksAsync([blockId], ct);
                AvailabilityBlock? block = locked.FirstOrDefault(b => b.Id == blockId && b.OwnerId == ownerId);
                if (block is null)
                {
                    return false;
                }

                availability.RemoveBlock(block);
                return true;
            },
            cancellationToken);
}
