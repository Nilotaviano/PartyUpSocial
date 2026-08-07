using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Availability;

/// <summary>D-116/D-136: the Home surface's top five suggestions, with a display-bound "See all" flag.</summary>
public sealed record HomeSuggestionsResult(IReadOnlyList<OverlapWindow> Suggestions, bool HasMore);

/// <summary>
/// PHASE5_DESIGN.md §5.2: the identical path <see cref="FindOverlapWindowsUseCase"/> takes with the
/// all-friends selection (D-110), with <see cref="OverlapEngine.HomeSuggestions"/> applied last —
/// never a second ranking. <see cref="OverlapEngine.FindWindows"/>'s ranking (Available desc, Maybe
/// desc, earliest start, longest) already orders the pool <c>HomeSuggestions</c> filters and takes
/// five from.
/// </summary>
public sealed class GetHomeSuggestionsUseCase(FindOverlapWindowsUseCase overlapSearch)
{
    public async Task<HomeSuggestionsResult> ExecuteAsync(UserId viewerId, CancellationToken cancellationToken)
    {
        OverlapSearchResult search = await overlapSearch.ExecuteAsync(
            viewerId, friendIds: null, range: null, cancellationToken);

        IReadOnlyList<OverlapWindow> suggestions = OverlapEngine.HomeSuggestions(search.Windows);
        int qualifying = search.Windows.Count(OverlapEngine.IsHomeSuggestion);

        return new HomeSuggestionsResult(suggestions, HasMore: qualifying > suggestions.Count);
    }
}
