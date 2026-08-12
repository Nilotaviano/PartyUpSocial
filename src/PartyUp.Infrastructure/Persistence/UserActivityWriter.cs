using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Persistence;

public sealed class UserActivityWriter(AppDbContext context) : IUserActivityWriter
{
    /// <summary>Name of the <c>users.last_seen_at</c> shadow property (UserConfiguration).</summary>
    public const string LastSeenProperty = "LastSeenAt";

    public async Task TouchLastSeenAsync(
        UserId id,
        Instant seenAt,
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
            command.CommandText = """
                UPDATE users
                SET last_seen_at = @seen_at
                WHERE id = @id
                  AND (last_seen_at IS NULL OR last_seen_at < @seen_at)
                """;
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            AddParameter(command, "seen_at", seenAt);
            AddParameter(command, "id", id.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (closeWhenDone)
            {
                await context.Database.CloseConnectionAsync();
            }
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
