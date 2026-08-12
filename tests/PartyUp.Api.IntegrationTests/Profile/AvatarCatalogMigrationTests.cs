using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using PartyUp.Application.Profile;
using PartyUp.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PartyUp.Api.IntegrationTests.Profile;

/// <summary>
/// AVATARS_DESIGN.md §2.2 (D-050): existing avatar values are not preserved — every users row ends
/// on a catalog glyph chosen deterministically from its id, with avatar_kind = Emoji and no avatar
/// source. Proven by migrating to the pre-catalog head, seeding rows the old way (the
/// TourCompletedAtBackfillMigrationTests idiom,
/// tests/PartyUp.Api.IntegrationTests/Profile/TourCompletionTests.cs:132-145), then migrating
/// forward. The statement under test is read back out of the migrations assembly rather than
/// re-typed here, so the array it ships and the array this test checks cannot drift apart.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AvatarCatalogBackfillMigrationTests : IAsyncLifetime
{
    private const string PreCatalogHead = "20260810022735_AddTourCompletedAt";
    private const string CatalogHead = "20260811172328_AssignCatalogAvatars";
    private const string OffCatalogGlyph = "\U0001F642";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Migrate_AssignsACatalogGlyphToEveryUser_D050()
    {
        await using AppDbContext context = CreateContext();
        IMigrator migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreCatalogHead);

        Guid offCatalogEmojiId = await SeedRawUserAsync(context, avatarKind: 0, avatarEmoji: OffCatalogGlyph);
        Guid literalGameId = await SeedRawUserAsync(context, avatarKind: 0, avatarEmoji: "game");
        Guid unsetId = await SeedRawUserAsync(context, avatarKind: null, avatarEmoji: null);
        Guid orphanedPlatformId = await SeedRawUserAsync(context, avatarKind: 1, avatarEmoji: null);
        Guid linkedPlatformId = await SeedRawUserAsync(context, avatarKind: 1, avatarEmoji: null);
        Guid platformLinkId = await SeedPlatformLinkAsync(context, linkedPlatformId);
        await SetAvatarSourceAsync(context, linkedPlatformId, platformLinkId);

        // Stop at the backfill's own head: its statement and the assertions below speak the
        // pre-wave-B schema (avatar_source_platform_link_id still exists here).
        await migrator.MigrateAsync(CatalogHead);

        Guid[] seeded =
        [
            offCatalogEmojiId,
            literalGameId,
            unsetId,
            orphanedPlatformId,
            linkedPlatformId,
        ];

        foreach (Guid id in seeded)
        {
            (int? kind, string? emoji, Guid? sourceId) =
                await ReadAvatarAsync(context, id, "avatar_source_platform_link_id");
            Assert.Equal(0, kind);
            Assert.NotNull(emoji);
            Assert.True(AvatarCatalog.Contains(emoji!), $"{emoji} is not a catalog glyph.");
            Assert.Null(sourceId);
        }

        (_, string? first, _) =
            await ReadAvatarAsync(context, offCatalogEmojiId, "avatar_source_platform_link_id");
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE users SET avatar_emoji = 'stale', avatar_kind = 1 WHERE id = @id",
            new Npgsql.NpgsqlParameter("id", offCatalogEmojiId));
        await context.Database.ExecuteSqlRawAsync(BackfillSql(context));
        (int? rerunKind, string? rerun, _) =
            await ReadAvatarAsync(context, offCatalogEmojiId, "avatar_source_platform_link_id");
        Assert.Equal(0, rerunKind);
        Assert.Equal(first, rerun);

        // The wave-B migrations (avatar_sources, the column swap, avatar_token) must carry these
        // backfilled rows to head with the emoji intact and no dangling source reference.
        await migrator.MigrateAsync();
        (int? headKind, string? headEmoji, Guid? headSourceId) =
            await ReadAvatarAsync(context, offCatalogEmojiId, "avatar_source_id");
        Assert.Equal(0, headKind);
        Assert.Equal(first, headEmoji);
        Assert.Null(headSourceId);
    }

    /// <summary>
    /// The 24 glyphs the migration hands out must be the 24 glyphs the allowlist admits, in the same
    /// order: the assignment is `glyphs[hash(id) % 24]`, so one transposed or variation-selector-less
    /// entry silently assigns a glyph the API then rejects on the user's next save. Postgres unnests
    /// the array expression lifted out of the shipped statement, which is the only copy of it.
    /// </summary>
    [Fact]
    public async Task MigrationArray_IsTheCatalog_GlyphForGlyph_D050()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        var shipped = new List<string>();
        DbConnection connection = await OpenAsync(context);
        await using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT glyph
                FROM unnest({CatalogArrayExpression(context)}) WITH ORDINALITY AS t(glyph, ord)
                ORDER BY ord
                """;
            await using DbDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                shipped.Add(reader.GetString(0));
            }
        }

        Assert.Equal(AvatarCatalog.Glyphs.ToArray(), shipped.ToArray());
    }

    /// <summary>
    /// The verbatim text of <c>AssignCatalogAvatars.Up</c>'s single <see cref="SqlOperation"/>, read
    /// off the compiled migration. Re-applying this proves idempotence against the statement that
    /// actually ships, not against a paraphrase of it that nothing keeps in sync.
    /// </summary>
    private static string BackfillSql(AppDbContext context)
    {
        IMigrationsAssembly migrations = context.GetService<IMigrationsAssembly>();
        TypeInfo migrationType = migrations.Migrations
            .Single(entry => entry.Key.EndsWith("_AssignCatalogAvatars", StringComparison.Ordinal))
            .Value;
        Migration migration = migrations.CreateMigration(
            migrationType,
            context.GetService<IDatabaseProvider>().Name);
        return migration.UpOperations.OfType<SqlOperation>().Single().Sql;
    }

    private static string CatalogArrayExpression(AppDbContext context)
    {
        string sql = BackfillSql(context);
        int start = sql.IndexOf("ARRAY[", StringComparison.Ordinal);
        int end = sql.IndexOf("])[", StringComparison.Ordinal);
        Assert.True(
            start >= 0 && end > start,
            "The backfill statement no longer contains a single ARRAY[...] subscript expression.");
        return sql[start..(end + 1)];
    }

    private static async Task<(int? Kind, string? Emoji, Guid? SourceId)> ReadAvatarAsync(
        AppDbContext context,
        Guid userId,
        string sourceColumn)
    {
        DbConnection connection = await OpenAsync(context);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText =
            $"SELECT avatar_kind, avatar_emoji, {sourceColumn} FROM users WHERE id = @id";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = userId;
        command.Parameters.Add(parameter);

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.IsDBNull(0) ? null : reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2));
    }

    private static async Task<DbConnection> OpenAsync(AppDbContext context)
    {
        DbConnection connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        return connection;
    }

    private static async Task<Guid> SeedRawUserAsync(
        AppDbContext context,
        int? avatarKind,
        string? avatarEmoji)
    {
        Guid id = Guid.CreateVersion7();
        string friendCode = $"PU{Guid.NewGuid():N}"[..9].ToUpperInvariant();

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users
                (id, lifecycle, username, display_name, time_zone_id, locale, friend_code,
                 age_gate_passed, age_gate_source, reminder_default, created_at,
                 avatar_kind, avatar_emoji, avatar_source_platform_link_id)
            VALUES
                ({id}, 1, {friendCode}, 'Player', 'America/Fortaleza', 'en', {friendCode},
                 true, 0, 3, now(),
                 {avatarKind}, {avatarEmoji}, NULL)
            """);
        return id;
    }

    private static async Task<Guid> SeedPlatformLinkAsync(AppDbContext context, Guid userId)
    {
        Guid id = Guid.CreateVersion7();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO platform_links (id, user_id, platform, handle)
            VALUES ({id}, {userId}, 0, 'handle')
            """);
        return id;
    }

    private static Task<int> SetAvatarSourceAsync(AppDbContext context, Guid userId, Guid platformLinkId) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE users SET avatar_source_platform_link_id = {platformLinkId} WHERE id = {userId}
            """);

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options);
    }
}
