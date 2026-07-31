namespace PartyUp.Application.Abstractions;

public interface IDeletionJournalShipper
{
    Task<int> ShipPendingAsync(CancellationToken cancellationToken);
}
