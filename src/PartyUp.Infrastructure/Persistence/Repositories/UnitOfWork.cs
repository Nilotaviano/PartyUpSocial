using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using PartyUp.Application.Abstractions;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// A-069: session/user row locks under Phase 4 paths use <c>FOR NO KEY UPDATE</c>, which still
/// conflicts with the friend path's <c>FOR UPDATE</c> — so a residual deadlock class remains between
/// them. <see cref="ExecuteAsync{T}"/> therefore retries a serialization failure (<c>40001</c>),
/// deadlock (<c>40P01</c>) or expired <c>lock_timeout</c> (<c>55P03</c>, raised by
/// <see cref="FriendPairLocker"/>'s bounded pair lock)
/// a bounded number of times, in a fresh transaction each attempt, rather than
/// surfacing the first one as an unhandled 500. The retry predicate matches the error both as an EF
/// <see cref="DbUpdateException"/>-wrapped <see cref="PostgresException"/> (ordinary <c>SaveChanges</c>
/// failures) and as a bare <see cref="PostgresException"/> (the raw lock/<c>ON CONFLICT</c> SQL of
/// §6.1/§7.2, which never reaches <c>SaveChanges</c>).
/// </summary>
public sealed class UnitOfWork(AppDbContext context) : IUnitOfWork
{
    /// <summary>A-069's exact backoff ladder; the last rung repeats for any attempt beyond it.</summary>
    private static readonly int[] BackoffFloorsMilliseconds = [20, 50, 100];

    private const int MaxAttempts = 3;

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is not null)
        {
            // A nested call runs directly, with no transaction and therefore no retry boundary of its
            // own — the outermost ExecuteAsync is the only place a retry may happen (A-069).
            return await work(cancellationToken);
        }

        for (int attempt = 1; ; attempt++)
        {
            await using IDbContextTransaction transaction =
                await context.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                T result = await work(cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (DbUpdateException exception) when (
                exception.InnerException is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation,
                    ConstraintName: "ux_friendships_pair",
                })
            {
                await transaction.RollbackAsync(CancellationToken.None);
                context.ChangeTracker.Clear();
                throw new FriendshipPairUniqueConflictException(exception);
            }
            catch (Exception exception) when (attempt < MaxAttempts && IsTransientConflict(exception))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                context.ChangeTracker.Clear();
                await Task.Delay(JitteredBackoff(attempt), cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                context.ChangeTracker.Clear();
                throw;
            }
        }
    }

    /// <summary>
    /// SQL state <c>40001</c> (serialization failure), <c>40P01</c> (deadlock detected) or
    /// <c>55P03</c> (lock not available — a bounded <c>lock_timeout</c> expired, e.g. the friend
    /// pair lock), either shape.
    /// </summary>
    private static bool IsTransientConflict(Exception exception) => exception switch
    {
        DbUpdateException { InnerException: PostgresException inner } => IsTransientSqlState(inner),
        PostgresException postgres => IsTransientSqlState(postgres),
        _ => false,
    };

    private static bool IsTransientSqlState(PostgresException exception) =>
        exception.SqlState is PostgresErrorCodes.SerializationFailure
            or PostgresErrorCodes.DeadlockDetected
            or PostgresErrorCodes.LockNotAvailable;

    private static TimeSpan JitteredBackoff(int attempt)
    {
        int floor = BackoffFloorsMilliseconds[Math.Min(attempt - 1, BackoffFloorsMilliseconds.Length - 1)];
        int jitterMilliseconds = Random.Shared.Next(0, floor / 2 + 1);
        return TimeSpan.FromMilliseconds(floor + jitterMilliseconds);
    }
}
