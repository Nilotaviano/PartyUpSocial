using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodaTime;
using Npgsql;
using NpgsqlTypes;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Infrastructure.Email;

/// <summary>
/// D-189 §4: one batched query joining the identity store and <c>users</c> — the invite fan-out
/// (up to ~50 recipients) must not do a per-recipient round trip. Every requested user row is
/// returned (the organizer contributes only a display name); the email column is null unless the
/// account holds a confirmed address, because an unconfirmed address may belong to someone who
/// never consented.
/// </summary>
public sealed class EmailRecipientReader(AppDbContext context) : IEmailRecipientReader
{
    public async Task<IReadOnlyList<EmailRecipient>> GetAsync(
        IReadOnlyList<UserId> userIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
        {
            return [];
        }

        Guid[] ids = userIds.Select(id => id.Value).Distinct().ToArray();

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
                SELECT u.id,
                       CASE WHEN a.email_confirmed THEN a.email END AS email,
                       u.display_name, u.locale, u.time_zone_id,
                       u.email_notifications_enabled
                  FROM users u
                  LEFT JOIN auth_users a ON a.id = u.id
                 WHERE u.id = ANY(@userIds)
                """;
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            command.Parameters.Add(new NpgsqlParameter("userIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            {
                Value = ids,
            });

            var results = new List<EmailRecipient>(ids.Length);
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(ReadRecipient(reader));
            }

            return results;
        }
        finally
        {
            if (closeWhenDone)
            {
                await context.Database.CloseConnectionAsync();
            }
        }
    }

    private static EmailRecipient ReadRecipient(DbDataReader reader)
    {
        UserId userId = UserId.From(reader.GetGuid(0));
        string? email = reader.IsDBNull(1) ? null : reader.GetString(1);
        string displayName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        Locale? locale = reader.IsDBNull(3) ? null : Locale.Create(reader.GetString(3));
        DateTimeZone? zone = reader.IsDBNull(4) ? null : DateTimeZoneProviders.Tzdb[reader.GetString(4)];
        bool emailNotificationsEnabled = reader.GetBoolean(5);

        return new EmailRecipient(userId, email, displayName, locale, zone, emailNotificationsEnabled);
    }
}
