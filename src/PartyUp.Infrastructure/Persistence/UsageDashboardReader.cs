using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Persistence;

public sealed class UsageDashboardReader(AppDbContext context) : IUsageDashboardReader
{
    private const short Confirmed = (short)SessionStatus.Confirmed;
    private const short Completed = (short)SessionStatus.Completed;

    public async Task<UsageWindowMeasures> ReadWindowAsync(
        Instant since,
        CancellationToken cancellationToken)
    {
        return await ExecuteAsync(
            $"""
            SELECT
              (SELECT count(*) FROM users WHERE created_at >= @since),
              (SELECT count(*) FROM users WHERE last_seen_at >= @since),
              (SELECT count(*) FROM sessions WHERE created_at >= @since),
              (SELECT count(*) FROM polls WHERE created_at >= @since),
              (SELECT count(*) FROM sessions
                 WHERE created_at >= @since AND origin_poll_id IS NULL),
              (SELECT count(*) FROM sessions
                 WHERE created_at >= @since AND origin_poll_id IS NULL
                   AND status IN ({Confirmed}, {Completed})),
              (SELECT count(*) FROM polls p
                 WHERE p.created_at >= @since
                   AND EXISTS (
                     SELECT 1 FROM sessions s
                     WHERE s.id = p.created_session_id
                       AND s.status IN ({Confirmed}, {Completed})))
            """,
            command => AddParameter(command, "since", since),
            reader =>
            {
                int registrations = Count(reader, 0);
                int activeUsers = Count(reader, 1);
                int sessionsCreated = Count(reader, 2);
                int pollsCreated = Count(reader, 3);
                int fixedSessions = Count(reader, 4);
                int confirmedFixedSessions = Count(reader, 5);
                int succeededPolls = Count(reader, 6);
                return new UsageWindowMeasures(
                    registrations,
                    activeUsers,
                    sessionsCreated,
                    pollsCreated,
                    fixedSessions + pollsCreated,
                    confirmedFixedSessions + succeededPolls);
            },
            cancellationToken);
    }

    public async Task<RepeatSchedulingMeasures> ReadRepeatSchedulingAsync(
        Duration within,
        CancellationToken cancellationToken)
    {
        // D-151/D-160: a scheduling act is a fixed session or a poll. A poll-derived session is the
        // same act as its poll, so it is excluded here exactly as it is from the confirmation
        // denominator — otherwise every finalization would read as a repeat.
        return await ExecuteAsync(
            """
            WITH creations AS (
              SELECT organizer_id AS user_id, created_at FROM sessions WHERE origin_poll_id IS NULL
              UNION ALL
              SELECT organizer_id AS user_id, created_at FROM polls
            ),
            firsts AS (
              SELECT user_id, min(created_at) AS first_at FROM creations GROUP BY user_id
            )
            SELECT count(*),
                   count(*) FILTER (WHERE EXISTS (
                     SELECT 1 FROM creations c
                     WHERE c.user_id = f.user_id
                       AND c.created_at > f.first_at
                       AND c.created_at <= f.first_at + @within))
            FROM firsts f
            """,
            command => AddParameter(command, "within", within),
            reader => new RepeatSchedulingMeasures(Count(reader, 0), Count(reader, 1)),
            cancellationToken);
    }

    private async Task<T> ExecuteAsync<T>(
        string commandText,
        Action<DbCommand> bind,
        Func<DbDataReader, T> read,
        CancellationToken cancellationToken)
    {
        DbConnection connection = context.Database.GetDbConnection();
        bool closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
        {
            await context.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = commandText;
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            bind(command);
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The usage query returned no row.");
            }

            return read(reader);
        }
        finally
        {
            if (closeWhenDone)
            {
                await context.Database.CloseConnectionAsync();
            }
        }
    }

    private static int Count(DbDataReader reader, int ordinal) =>
        Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
