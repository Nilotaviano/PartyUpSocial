using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users.DeletionExecution;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Infrastructure.Deletion;

public sealed class DeletionJournal(AppDbContext context) : IDeletionJournal
{
    public void Append(DeletionJournalEntry entry) =>
        context.DeletionJournal.Add(
            new DeletionJournalRecord(entry.UserId, entry.ExecutedAt));

    public async Task<IReadOnlyList<DeletionJournalEntry>> ReadSinceAsync(
        Instant since,
        int limit,
        CancellationToken cancellationToken) =>
        await context.DeletionJournal
            .AsNoTracking()
            .Where(entry => entry.ExecutedAt >= since)
            .OrderBy(entry => entry.ExecutedAt)
            .Take(limit)
            .Select(entry => new DeletionJournalEntry(entry.UserId, entry.ExecutedAt))
            .ToListAsync(cancellationToken);
}

public sealed class DeletionJournalRecord
{
    private DeletionJournalRecord()
    {
    }

    public DeletionJournalRecord(UserId userId, Instant executedAt)
    {
        UserId = userId;
        ExecutedAt = executedAt;
    }

    public UserId UserId { get; private set; }

    public Instant ExecutedAt { get; private set; }

    public Instant? ShippedAt { get; set; }
}
