using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace PartyUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAvatarSources : Migration
    {
        private static readonly string[] AvatarSourceUniqueColumns = ["user_id", "kind"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // fk_users_platform_links_avatar_source_platform_link_id was added by hand in
            // AddUserAggregate (the column carries no EF navigation), so the model snapshot never
            // tracked it and the scaffolded diff below would otherwise leave it pointing at
            // platform_links after the rename.
            migrationBuilder.DropForeignKey(
                name: "fk_users_platform_links_avatar_source_platform_link_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "avatar_url",
                table: "steam_identities");

            migrationBuilder.RenameColumn(
                name: "avatar_source_platform_link_id",
                table: "users",
                newName: "avatar_source_id");

            migrationBuilder.CreateTable(
                name: "avatar_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    token = table.Column<string>(type: "text", nullable: false),
                    bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    fetched_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_avatar_sources", x => x.id);
                    table.ForeignKey(
                        name: "fk_avatar_sources_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_avatar_sources_token",
                table: "avatar_sources",
                column: "token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_avatar_sources_user_id_kind",
                table: "avatar_sources",
                columns: AvatarSourceUniqueColumns,
                unique: true);

            // A row written between the AddUserAggregate and this deploy can still carry an
            // avatar_source_id left over from the renamed avatar_source_platform_link_id column --
            // pointing at a platform_links row, not an avatar_sources one -- and the AddForeignKey
            // below would abort startup migration on it. Idempotent and re-runnable: reset any such
            // row to a catalog glyph first, deterministically (AssignCatalogAvatars' own idiom), so
            // the constraint always finds a clean column.
            migrationBuilder.Sql("""
                UPDATE users
                   SET avatar_kind = 0,
                       avatar_emoji = (ARRAY[
                           E'\U0001F3AE', E'\U0001F3B2', E'\U0001F579\uFE0F', E'\U0001F47E',
                           E'\U0001F0CF', E'\u265F\uFE0F', E'\U0001F3AF', E'\U0001F3C6',
                           E'\U0001F680', E'\U0001F409', E'\U0001F98A', E'\U0001F419',
                           E'\U0001F427', E'\U0001F989', E'\U0001F41D', E'\U0001F335',
                           E'\U0001F344', E'\U0001F319', E'\u2B50', E'\U0001F525',
                           E'\u26A1', E'\U0001F3A7', E'\U0001F355', E'\u2615'
                       ])[(abs(('x' || substr(md5(id::text), 1, 8))::bit(32)::bigint) % 24) + 1],
                       avatar_source_id = NULL
                 WHERE avatar_source_id IS NOT NULL;
                """);

            migrationBuilder.AddForeignKey(
                name: "fk_users_avatar_sources_avatar_source_id",
                table: "users",
                column: "avatar_source_id",
                principalTable: "avatar_sources",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_users_avatar_sources_avatar_source_id",
                table: "users");

            migrationBuilder.DropTable(
                name: "avatar_sources");

            migrationBuilder.RenameColumn(
                name: "avatar_source_id",
                table: "users",
                newName: "avatar_source_platform_link_id");

            migrationBuilder.AddColumn<string>(
                name: "avatar_url",
                table: "steam_identities",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "fk_users_platform_links_avatar_source_platform_link_id",
                table: "users",
                column: "avatar_source_platform_link_id",
                principalTable: "platform_links",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
