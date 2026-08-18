using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Feedback;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Persistence.Repositories;

public sealed class FeedbackRepository(AppDbContext context) : IFeedbackRepository, IFeedbackReader
{
    public void Add(FeedbackItem item) => context.FeedbackItems.Add(item);

    public Task<FeedbackItem?> FindAsync(FeedbackItemId id, CancellationToken cancellationToken) =>
        context.FeedbackItems.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

    // D-216: a set-based UPDATE rather than a load-and-mutate — the executor is a system actor with
    // no need for the rows themselves, and the retention is the point (nothing is deleted).
    public Task<int> AnonymizeForUserAsync(
        UserId submitterUserId,
        CancellationToken cancellationToken) =>
        context.FeedbackItems
            .Where(item => item.SubmitterUserId == submitterUserId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.SubmitterUserId, (UserId?)null),
                cancellationToken);

    public async Task<FeedbackPage> ListAsync(
        FeedbackType? type,
        bool? handled,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        IQueryable<FeedbackItem> query = context.FeedbackItems.AsNoTracking();

        if (type is { } requestedType)
        {
            query = query.Where(item => item.Type == requestedType);
        }

        if (handled is { } requestedHandled)
        {
            query = query.Where(item => item.Handled == requestedHandled);
        }

        int total = await query.CountAsync(cancellationToken);
        List<FeedbackItem> items = await query
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new FeedbackPage(items, total);
    }

    public Task<int> CountUnhandledAsync(CancellationToken cancellationToken) =>
        context.FeedbackItems.CountAsync(item => !item.Handled, cancellationToken);
}
