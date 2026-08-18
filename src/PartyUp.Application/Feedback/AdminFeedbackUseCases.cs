using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Feedback;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Feedback;

/// <summary>D-214: the admin-only list, newest first, with optional type and handled filters.</summary>
public sealed class ListFeedbackUseCase(IFeedbackReader feedback)
{
    public async Task<FeedbackListResult> ExecuteAsync(
        FeedbackType? type,
        bool? handled,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        FeedbackPage result = await feedback.ListAsync(
            type,
            handled,
            page,
            pageSize,
            cancellationToken);
        return new FeedbackListResult(result.Items, page, pageSize, result.Total);
    }
}

public sealed record FeedbackListResult(
    IReadOnlyList<FeedbackItem> Items,
    int Page,
    int PageSize,
    int Total);

/// <summary>
/// D-214's Handled toggle. An unknown id is <see langword="null"/> — the endpoint answers 404, the
/// same fail-closed shape every other admin by-id route uses.
/// </summary>
public sealed class SetFeedbackHandledUseCase(
    IFeedbackRepository feedback,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public Task<FeedbackItem?> ExecuteAsync(
        FeedbackItemId id,
        bool handled,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                FeedbackItem? item = await feedback.FindAsync(id, ct);
                item?.SetHandled(handled, clock);
                return item;
            },
            cancellationToken);
}
