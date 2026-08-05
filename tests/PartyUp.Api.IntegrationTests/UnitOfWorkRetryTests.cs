using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PartyUp.Application.Abstractions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests;

/// <summary>
/// A-069's bounded retry gate: <see cref="IUnitOfWork.ExecuteAsync{T}"/> retries a serialization
/// failure (<c>40001</c>) or deadlock (<c>40P01</c>) a bounded number of times, in a fresh transaction
/// each attempt, and surfaces the failure once the bound is exhausted rather than looping forever.
/// Each attempt forces a real <see cref="PostgresException"/> deterministically (a PL/pgSQL
/// <c>RAISE EXCEPTION ... USING ERRCODE</c>) rather than relying on racy real contention, so the
/// attempt count is exact.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UnitOfWorkRetryTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("40001")]
    [InlineData("40P01")]
    public async Task DeadlockAndSerializationFailureRetryThriceThenSurface_A069(string sqlState)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        int attempts = 0;

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() =>
            unitOfWork.ExecuteAsync(async cancellationToken =>
            {
                attempts++;

                // The SQL state is fixed test input from [InlineData] ("40001"/"40P01"), never caller
                // input, and cannot travel as a bind parameter inside a dollar-quoted DO body.
#pragma warning disable EF1002
                await context.Database.ExecuteSqlRawAsync(
                    $"""
                    DO $$
                    BEGIN
                        RAISE EXCEPTION 'forced transient conflict for A-069' USING ERRCODE = '{sqlState}';
                    END $$;
                    """,
                    cancellationToken);
#pragma warning restore EF1002
                return 0;
            }, default));

        Assert.Equal(sqlState, exception.SqlState);
        Assert.Equal(3, attempts);
    }
}
