using PartyUp.Application.Abstractions;
using PartyUp.Application.SteamCatalog;

namespace PartyUp.Application.Tests.SteamCatalog;

/// <summary>
/// STEAM_CATALOG_DESIGN.md §3: the 2-char floor and the <c>catalogAvailable</c> flag, tested against
/// a fake <see cref="ISteamCatalogRepository"/> -- no Postgres, no pg_trgm, no Docker (the real
/// similarity ranking is a Postgres-only integration concern per the design's §6 test split).
/// </summary>
public sealed class SearchSteamCatalogUseCaseTests
{
    [Fact]
    public async Task QueryShorterThanTwoChars_ReturnsAnEmptyResultSet_D194()
    {
        FakeSteamCatalogRepository repository = new() { HasAnyApp = true };
        SearchSteamCatalogUseCase useCase = new(repository);

        SteamCatalogSearchResultSet result = await useCase.ExecuteAsync("a", default);

        Assert.Empty(result.Results);
        Assert.False(repository.SearchCalled);
    }

    [Fact]
    public async Task NullQuery_ReturnsAnEmptyResultSet_D194()
    {
        FakeSteamCatalogRepository repository = new() { HasAnyApp = true };
        SearchSteamCatalogUseCase useCase = new(repository);

        SteamCatalogSearchResultSet result = await useCase.ExecuteAsync(null, default);

        Assert.Empty(result.Results);
        Assert.False(repository.SearchCalled);
    }

    [Fact]
    public async Task QueryAtTwoChars_SearchesAndReturnsTheRepositoryResults_D194()
    {
        FakeSteamCatalogRepository repository = new()
        {
            HasAnyApp = true,
            Results = [new SteamCatalogSearchResult(730, "Counter-Strike 2")],
        };
        SearchSteamCatalogUseCase useCase = new(repository);

        SteamCatalogSearchResultSet result = await useCase.ExecuteAsync("cs", default);

        Assert.True(repository.SearchCalled);
        Assert.Equal("cs", repository.LastTerm);
        Assert.Equal(SearchSteamCatalogUseCase.ResultLimit, repository.LastLimit);
        Assert.Equal(730, Assert.Single(result.Results).AppId);
    }

    [Fact]
    public async Task EmptyCatalog_ReportsCatalogUnavailable_EvenOnAQualifyingQuery_D194()
    {
        FakeSteamCatalogRepository repository = new() { HasAnyApp = false };
        SearchSteamCatalogUseCase useCase = new(repository);

        SteamCatalogSearchResultSet result = await useCase.ExecuteAsync("party", default);

        Assert.False(result.CatalogAvailable);
    }

    [Fact]
    public async Task EmptyCatalog_ReportsCatalogUnavailable_OnATooShortQueryToo_D194()
    {
        FakeSteamCatalogRepository repository = new() { HasAnyApp = false };
        SearchSteamCatalogUseCase useCase = new(repository);

        SteamCatalogSearchResultSet result = await useCase.ExecuteAsync("p", default);

        Assert.False(result.CatalogAvailable);
        Assert.Empty(result.Results);
    }

    private sealed class FakeSteamCatalogRepository : ISteamCatalogRepository
    {
        public bool HasAnyApp { get; init; }
        public IReadOnlyList<SteamCatalogSearchResult> Results { get; init; } = [];
        public bool SearchCalled { get; private set; }
        public string? LastTerm { get; private set; }
        public int LastLimit { get; private set; }

        public Task<IReadOnlyList<SteamCatalogSearchResult>> SearchAsync(
            string term, int limit, CancellationToken cancellationToken)
        {
            SearchCalled = true;
            LastTerm = term;
            LastLimit = limit;
            return Task.FromResult(Results);
        }

        public Task<bool> HasAnyAppAsync(CancellationToken cancellationToken) => Task.FromResult(HasAnyApp);

        public Task UpsertBatchAsync(
            IReadOnlyList<SteamCatalogAppUpsert> apps, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SteamCatalogState> GetStateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SetStateAsync(
            NodaTime.Instant? watermark,
            NodaTime.Instant lastRunAt,
            bool lastRunSucceeded,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
