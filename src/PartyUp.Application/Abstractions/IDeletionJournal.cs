using NodaTime;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Application.Abstractions;

public interface IDeletionJournal
{
    void Append(DeletionJournalEntry entry);

    Task<IReadOnlyList<DeletionJournalEntry>> ReadSinceAsync(
        Instant since,
        int limit,
        CancellationToken cancellationToken);
}
