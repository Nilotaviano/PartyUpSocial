using PartyUp.Domain.Feedback;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// D-214's write side. There is no submitter-facing read path, so the only lookup here is the
/// admin's by-id fetch behind the Handled toggle.
/// </summary>
public interface IFeedbackRepository
{
    void Add(FeedbackItem item);

    Task<FeedbackItem?> FindAsync(FeedbackItemId id, CancellationToken cancellationToken);

    /// <summary>
    /// D-216: null <c>submitter_user_id</c> on every row the deleted account submitted, retaining the
    /// rows themselves. Returns how many were anonymized.
    /// </summary>
    Task<int> AnonymizeForUserAsync(UserId submitterUserId, CancellationToken cancellationToken);
}

/// <summary>D-214's admin-only read model: the paged list and the dashboard's live unhandled count.</summary>
public interface IFeedbackReader
{
    Task<FeedbackPage> ListAsync(
        FeedbackType? type,
        bool? handled,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<int> CountUnhandledAsync(CancellationToken cancellationToken);
}

public sealed record FeedbackPage(IReadOnlyList<FeedbackItem> Items, int Total);
