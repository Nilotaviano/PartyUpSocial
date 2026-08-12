using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace PartyUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SteamCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Compose pins postgres:17-alpine everywhere, so the db owner can create this trusted
            // extension without superuser (STEAM_CATALOG_DESIGN.md §2).
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.CreateTable(
                name: "steam_apps",
                columns: table => new
                {
                    app_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    last_modified = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_steam_apps", x => x.app_id);
                });

            migrationBuilder.CreateTable(
                name: "steam_catalog_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    watermark = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    last_run_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    last_run_succeeded = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_steam_catalog_state", x => x.id);
                });

            // EF has no primitive for a functional-expression GIN index (STEAM_CATALOG_DESIGN.md §2).
            migrationBuilder.Sql(
                "CREATE INDEX ix_steam_apps_name_trgm ON steam_apps USING gin (lower(name) gin_trgm_ops);");

            // The single advisory row, created empty (§1/§2) -- the job's first run finds a row to
            // update rather than needing an insert-or-update branch on every write.
            migrationBuilder.Sql(
                "INSERT INTO steam_catalog_state (id, watermark, last_run_at, last_run_succeeded) " +
                "VALUES (1, NULL, NULL, false);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "steam_apps");

            migrationBuilder.DropTable(
                name: "steam_catalog_state");

            migrationBuilder.Sql("DROP EXTENSION IF EXISTS pg_trgm;");
        }
    }
}
