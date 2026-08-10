using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Polls;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// The Poll aggregate's own persistence adapter, mirroring <see cref="SessionRepository"/>.
/// <see cref="LoadForUpdateAsync"/> follows the same idiom: a raw, database-ordered lock statement
/// first, then an EF query for the locked row and its children — never the other way around.
/// </summary>
public sealed class PollRepository(AppDbContext context) : IPollRepository
{
    public void Add(Poll poll) => context.Polls.Add(poll);

    public void RemoveOptionAndCascadedVotes(PollOption deletedOption, IReadOnlyList<PollVote> cascadedVotes)
    {
        context.Set<PollVote>().RemoveRange(cascadedVotes);
        context.Set<PollOption>().Remove(deletedOption);
    }

    public void RemoveParticipationAndWithdrawnVotes(
        PollParticipant leftParticipant,
        IReadOnlyList<PollVote> withdrawnVotes)
    {
        context.Set<PollVote>().RemoveRange(withdrawnVotes);
        context.Set<PollParticipant>().Remove(leftParticipant);
    }

    public Task<Poll?> FindAsync(PollId id, CancellationToken cancellationToken) =>
        LoadWithChildren(context.Polls).FirstOrDefaultAsync(poll => poll.Id == id, cancellationToken);

    /// <summary>Untracked on purpose (mirrors <see cref="ISessionRepository.FindNoTrackingAsync"/>).</summary>
    public Task<Poll?> FindNoTrackingAsync(PollId id, CancellationToken cancellationToken) =>
        LoadWithChildren(context.Polls.AsNoTracking())
            .FirstOrDefaultAsync(poll => poll.Id == id, cancellationToken);

    public async Task<PollForUpdate?> LoadForUpdateAsync(PollId id, CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1
            FROM polls
            WHERE id = {id.Value}
            FOR NO KEY UPDATE
            """, cancellationToken);

        Poll? poll = await LoadWithChildren(context.Polls)
            .FirstOrDefaultAsync(poll => poll.Id == id, cancellationToken);

        if (poll is null)
        {
            return null;
        }

        List<NotificationMute> mutes = await context.NotificationMutes
            .Where(mute => mute.PollId == id)
            .ToListAsync(cancellationToken);

        return new PollForUpdate(poll, mutes);
    }

    private static IQueryable<Poll> LoadWithChildren(IQueryable<Poll> query) =>
        query
            .Include(poll => poll.Options)
            .Include(poll => poll.Participants)
            .Include(poll => poll.Votes)
            .Include(poll => poll.JoinRequests)
            .Include(poll => poll.GuestIdentities)
            .Include(poll => poll.GuestLinks);
}
