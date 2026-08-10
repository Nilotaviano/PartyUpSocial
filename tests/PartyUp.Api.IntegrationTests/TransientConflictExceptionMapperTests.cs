using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PartyUp.Api.ErrorHandling;

namespace PartyUp.Api.IntegrationTests;

/// <summary>
/// A-069's exhaustion arm. The retry ladder in <c>UnitOfWork.ExecuteAsync</c> re-runs a
/// serialization failure, deadlock, or lock timeout in a fresh transaction; what survives it is
/// contention the server could not resolve. Before this mapper existed, that reached
/// <c>InternalExceptionMapper</c> and answered <c>500 internal_error</c>, so the
/// <c>503 transient_conflict</c> A-069 documents — and <c>PartyUpOpenApiTransformer</c> declares —
/// was contract with no implementation behind it. These assert the mapper itself; the end-to-end
/// HTTP arm still rides the contention tests in <c>Friends/FriendPairLockContentionTests</c>.
/// </summary>
public sealed class TransientConflictExceptionMapperTests
{
    private static PostgresException Postgres(string sqlState) =>
        new(messageText: "contended", severity: "ERROR", invariantSeverity: "ERROR", sqlState: sqlState);

    public static TheoryData<string> RetriedSqlStates() => new()
    {
        PostgresErrorCodes.SerializationFailure,
        PostgresErrorCodes.DeadlockDetected,
        PostgresErrorCodes.LockNotAvailable,
    };

    [Theory]
    [MemberData(nameof(RetriedSqlStates))]
    public async Task EverySqlStateTheLadderRetriesAnswers503TransientConflictWithRetryAfter_A069(
        string sqlState)
    {
        HttpContext context = await HandleAsync(Postgres(sqlState));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("1", context.Response.Headers.RetryAfter);
        Assert.Equal("transient_conflict", await ReadCodeAsync(context));
    }

    /// <summary>
    /// The raw lock SQL throws a bare <see cref="PostgresException"/>, but an ordinary
    /// <c>SaveChanges</c> conflict arrives wrapped — the mapper must match the same states in both
    /// shapes, exactly as the retry predicate it mirrors does.
    /// </summary>
    [Fact]
    public async Task AnEfWrappedConflictIsMappedTheSameAsABareOne_A069()
    {
        HttpContext context = await HandleAsync(
            new DbUpdateException("save failed", Postgres(PostgresErrorCodes.DeadlockDetected)));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("transient_conflict", await ReadCodeAsync(context));
    }

    [Fact]
    public async Task ANonTransientPostgresFailureIsLeftToTheNextHandler_A069()
    {
        HttpContext context = new DefaultHttpContext();
        bool handled = await new TransientConflictExceptionMapper().TryHandleAsync(
            context,
            Postgres(PostgresErrorCodes.UniqueViolation),
            CancellationToken.None);

        Assert.False(handled);
        Assert.True(context.Response.Headers.RetryAfter.Count == 0);
    }

    [Fact]
    public async Task AnUnrelatedExceptionIsLeftToTheNextHandler_A069()
    {
        HttpContext context = new DefaultHttpContext();
        bool handled = await new TransientConflictExceptionMapper().TryHandleAsync(
            context,
            new InvalidOperationException("unrelated"),
            CancellationToken.None);

        Assert.False(handled);
    }

    private static async Task<HttpContext> HandleAsync(Exception exception)
    {
        DefaultHttpContext context = new();
        context.Response.Body = new MemoryStream();
        bool handled = await new TransientConflictExceptionMapper().TryHandleAsync(
            context,
            exception,
            CancellationToken.None);

        Assert.True(handled);
        return context;
    }

    private static async Task<string?> ReadCodeAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using JsonDocument document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.GetProperty("code").GetString();
    }
}
