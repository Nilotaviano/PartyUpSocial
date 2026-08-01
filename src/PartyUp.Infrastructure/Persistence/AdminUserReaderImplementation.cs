using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Infrastructure.Persistence;

public sealed class AdminUserReader(AppDbContext context) : IAdminUserReader
{
    public async Task<AdminUserPage> ListAsync(
        string? query,
        int page,
        int pageSize,
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
            string? pattern = string.IsNullOrWhiteSpace(query)
                ? null
                : $"%{EscapeLikePattern(query.Trim())}%";
            int total = await CountAsync(connection, pattern, cancellationToken);
            IReadOnlyList<AdminUserRecord> users = await ReadPageAsync(
                connection,
                pattern,
                page,
                pageSize,
                cancellationToken);
            return new AdminUserPage(users, total);
        }
        finally
        {
            if (closeWhenDone)
            {
                await context.Database.CloseConnectionAsync();
            }
        }
    }

    public async Task<AdminUserRecord?> GetAsync(
        UserId id,
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
            await using DbCommand command = CreateCommand(
                connection,
                """
                SELECT u.id, u.username, u.display_name, a.email, u.created_at,
                       u.lifecycle, u.banned_at
                FROM users AS u
                LEFT JOIN auth_users AS a ON a.id = u.id
                WHERE u.id = @id
                """);
            AddParameter(command, "id", DbType.Guid, id.Value);
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
        }
        finally
        {
            if (closeWhenDone)
            {
                await context.Database.CloseConnectionAsync();
            }
        }
    }

    private async Task<int> CountAsync(
        DbConnection connection,
        string? pattern,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = CreateCommand(
            connection,
            """
            SELECT count(*)::int
            FROM users AS u
            LEFT JOIN auth_users AS a ON a.id = u.id
            WHERE @pattern IS NULL
               OR u.username ILIKE @pattern ESCAPE '\'
               OR u.display_name ILIKE @pattern ESCAPE '\'
               OR a.email ILIKE @pattern ESCAPE '\'
            """);
        AddParameter(command, "pattern", DbType.String, pattern);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private async Task<IReadOnlyList<AdminUserRecord>> ReadPageAsync(
        DbConnection connection,
        string? pattern,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = CreateCommand(
            connection,
            """
            SELECT u.id, u.username, u.display_name, a.email, u.created_at,
                   u.lifecycle, u.banned_at
            FROM users AS u
            LEFT JOIN auth_users AS a ON a.id = u.id
            WHERE @pattern IS NULL
               OR u.username ILIKE @pattern ESCAPE '\'
               OR u.display_name ILIKE @pattern ESCAPE '\'
               OR a.email ILIKE @pattern ESCAPE '\'
            ORDER BY u.created_at DESC, u.id
            LIMIT @page_size OFFSET @offset
            """);
        AddParameter(command, "pattern", DbType.String, pattern);
        AddParameter(command, "page_size", DbType.Int32, pageSize);
        AddParameter(command, "offset", DbType.Int64, (long)(page - 1) * pageSize);

        var users = new List<AdminUserRecord>();
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(ReadUser(reader));
        }

        return users;
    }

    private DbCommand CreateCommand(DbConnection connection, string commandText)
    {
        DbCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        return command;
    }

    private static void AddParameter(
        DbCommand command,
        string name,
        DbType type,
        object? value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static AdminUserRecord ReadUser(DbDataReader reader) =>
        new(
            UserId.From(reader.GetGuid(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetFieldValue<Instant>(4),
            (UserLifecycle)reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<Instant>(6));

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
