using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Application.Admin;

public sealed class ReadDeletionJournalUseCase(IDeletionJournal journal)
{
    public Task<IReadOnlyList<DeletionJournalEntry>> ExecuteAsync(
        Instant since,
        int limit,
        CancellationToken cancellationToken) =>
        journal.ReadSinceAsync(since, limit, cancellationToken);
}
