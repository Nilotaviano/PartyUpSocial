using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodaTime;
using Npgsql;
using NpgsqlTypes;
using PartyUp.Application.Auth;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Infrastructure.Auth;

public sealed class SecurityTokenService(AppDbContext context, IClock clock) : ISecurityTokenService
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = CreatePayloadJsonOptions();

    public async Task<IssuedToken> IssueAsync(
        IssueTokenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Ttl <= Duration.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Token TTL must be positive.");
        }

        byte[] tokenBytes = RandomNumberGenerator.GetBytes(32);
        string rawToken = Base64UrlEncode(tokenBytes);
        byte[] tokenHash = SHA256.HashData(tokenBytes);
        Guid tokenId = Guid.CreateVersion7();
        Instant now = clock.GetCurrentInstant();
        Instant expiresAt = now + request.Ttl;
        bool consumeOutstanding = request.Purpose is
            AuthTokenPurpose.EmailChange or AuthTokenPurpose.Reauthentication;
        string? payload = request.Intent is null
            ? null
            : JsonSerializer.Serialize(request.Intent, PayloadJsonOptions);

        const string sql = """
            WITH consumed AS (
                UPDATE auth_tokens
                   SET consumed_at = @now
                 WHERE @consumeOutstanding
                   AND purpose = @purpose
                   AND user_id = @userId
                   AND consumed_at IS NULL
                   AND expires_at > @now
                RETURNING id
            )
            INSERT INTO auth_tokens
                (id, purpose, token_hash, user_id, email_normalized, created_at, expires_at,
                 consumed_at, request_ip_hash, pending_locale, pending_age_gate, payload)
            VALUES
                (@id, @purpose, @tokenHash, @userId, @emailNormalized, @now, @expiresAt,
                 NULL, @requestIpHash, @pendingLocale, @pendingAgeGate, @payload)
            """;

        await ExecuteNonQueryAsync(sql, command =>
        {
            Add(command, "id", NpgsqlDbType.Uuid, tokenId);
            Add(command, "purpose", NpgsqlDbType.Integer, (int)request.Purpose);
            Add(command, "tokenHash", NpgsqlDbType.Bytea, tokenHash);
            Add(command, "userId", NpgsqlDbType.Uuid, request.UserId?.Value);
            Add(command, "emailNormalized", NpgsqlDbType.Text, request.EmailNormalized);
            Add(command, "now", NpgsqlDbType.TimestampTz, now);
            Add(command, "expiresAt", NpgsqlDbType.TimestampTz, expiresAt);
            Add(command, "requestIpHash", NpgsqlDbType.Bytea, request.RequestIpHash);
            Add(command, "pendingLocale", NpgsqlDbType.Text, request.Pending?.Locale);
            Add(command, "pendingAgeGate", NpgsqlDbType.Boolean,
                request.Pending?.AgeGateAttested ?? false);
            Add(command, "payload", NpgsqlDbType.Jsonb, payload);
            Add(command, "consumeOutstanding", NpgsqlDbType.Boolean, consumeOutstanding);
        }, cancellationToken);

        return new IssuedToken(tokenId, rawToken, expiresAt);
    }

    public async Task<TokenRedemption> RedeemAsync(
        RedeemTokenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryHashRawToken(request.RawToken, out byte[]? tokenHash))
        {
            return NotFound(request.Purpose);
        }

        Instant now = clock.GetCurrentInstant();
        const string sql = """
            WITH candidate AS (
                SELECT t.id, t.purpose, t.user_id, t.email_normalized,
                       t.pending_locale, t.pending_age_gate, t.payload,
                       (t.user_id IS NULL
                        AND NOT EXISTS (
                            SELECT 1
                              FROM auth_users a
                             WHERE a.normalized_email = t.email_normalized
                        )) AS is_pre_account
                  FROM auth_tokens t
                 WHERE t.token_hash = @tokenHash
                   AND t.purpose = @purpose
                   AND t.consumed_at IS NULL
                   AND t.expires_at > @now
                   AND (@binderHash IS NULL OR t.payload->>'binderHash' = @binderHash)
            ),
            redeemed AS (
                UPDATE auth_tokens t
                   SET consumed_at = @now
                  FROM candidate c
                 WHERE t.id = c.id
                   AND t.consumed_at IS NULL
                   AND (@ageGateAttested IS NULL OR @ageGateAttested OR t.pending_age_gate
                        OR NOT c.is_pre_account)
                RETURNING t.id
            )
            SELECT c.is_pre_account,
                   (@ageGateAttested IS NULL OR @ageGateAttested OR c.pending_age_gate
                    OR NOT c.is_pre_account) AS age_gate_satisfied,
                   r.id AS redeemed_id,
                   c.purpose,
                   c.user_id,
                   c.email_normalized,
                   c.pending_locale,
                   c.pending_age_gate,
                   c.payload
              FROM candidate c
              LEFT JOIN redeemed r ON r.id = c.id
            """;

        DbConnection connection = context.Database.GetDbConnection();
        bool closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await context.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            Add(command, "tokenHash", NpgsqlDbType.Bytea, tokenHash);
            Add(command, "purpose", NpgsqlDbType.Integer, (int)request.Purpose);
            Add(command, "now", NpgsqlDbType.TimestampTz, now);
            Add(command, "binderHash", NpgsqlDbType.Text, request.BinderHash);
            Add(command, "ageGateAttested", NpgsqlDbType.Boolean, request.AgeGateAttested);

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return NotFound(request.Purpose);
            }

            bool ageGateSatisfied = reader.GetBoolean(1);
            bool redeemed = !reader.IsDBNull(2);
            AuthTokenPurpose purpose = (AuthTokenPurpose)reader.GetInt32(3);
            UserId? userId = reader.IsDBNull(4)
                ? null
                : UserId.From(reader.GetGuid(4));
            string? email = reader.IsDBNull(5) ? null : reader.GetString(5);
            string? pendingLocale = reader.IsDBNull(6) ? null : reader.GetString(6);
            bool pendingAgeGate = reader.GetBoolean(7);
            string? payload = reader.IsDBNull(8) ? null : reader.GetString(8);
            var pending = pendingLocale is null
                ? null
                : new PendingRegistration(pendingLocale, pendingAgeGate);
            ExternalIntentPayload? intent = payload is null
                ? null
                : JsonSerializer.Deserialize<ExternalIntentPayload>(payload, PayloadJsonOptions);

            if (!redeemed)
            {
                return ageGateSatisfied
                    ? NotFound(request.Purpose)
                    : new TokenRedemption(
                        false,
                        TokenRedemptionFailure.RequiresAgeGate,
                        purpose,
                        userId,
                        email,
                        pending,
                        intent,
                        default);
            }

            return new TokenRedemption(
                true,
                null,
                purpose,
                userId,
                email,
                pending,
                intent,
                now);
        }
        finally
        {
            if (closeConnection)
            {
                await context.Database.CloseConnectionAsync();
            }
        }
    }

    private async Task ExecuteNonQueryAsync(
        string sql,
        Action<DbCommand> configure,
        CancellationToken cancellationToken)
    {
        DbConnection connection = context.Database.GetDbConnection();
        bool closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await context.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            configure(command);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (closeConnection)
            {
                await context.Database.CloseConnectionAsync();
            }
        }
    }

    private static void Add(
        DbCommand command,
        string name,
        NpgsqlDbType type,
        object? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, type)
        {
            Value = value ?? DBNull.Value,
        });
    }

    private static TokenRedemption NotFound(AuthTokenPurpose purpose) =>
        new(false, TokenRedemptionFailure.NotFound, purpose, null, null, null, null, default);

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryHashRawToken(string rawToken, out byte[]? hash)
    {
        hash = null;
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return false;
        }

        string base64 = rawToken.Replace('-', '+').Replace('_', '/');
        base64 += (base64.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => "invalid",
        };

        try
        {
            byte[] bytes = Convert.FromBase64String(base64);
            if (bytes.Length != 32)
            {
                return false;
            }

            hash = SHA256.HashData(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static JsonSerializerOptions CreatePayloadJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

internal sealed class AuthToken
{
    public Guid Id { get; set; }

    public AuthTokenPurpose Purpose { get; set; }

    public byte[] TokenHash { get; set; } = [];

    public UserId? UserId { get; set; }

    public string? EmailNormalized { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant ExpiresAt { get; set; }

    public Instant? ConsumedAt { get; set; }

    public byte[]? RequestIpHash { get; set; }

    public string? PendingLocale { get; set; }

    public bool PendingAgeGate { get; set; }

    public string? Payload { get; set; }
}
