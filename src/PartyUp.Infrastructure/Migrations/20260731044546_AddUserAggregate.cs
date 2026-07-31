using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace PartyUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAggregate : Migration
    {
        private static readonly string[] PlatformLinkUniqueColumns = ["user_id", "platform"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    lifecycle = table.Column<int>(type: "integer", nullable: false),
                    username = table.Column<string>(type: "text", nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    time_zone_id = table.Column<string>(type: "text", nullable: true),
                    locale = table.Column<string>(type: "text", nullable: true),
                    friend_code = table.Column<string>(type: "text", nullable: true),
                    age_gate_passed = table.Column<bool>(type: "boolean", nullable: false),
                    age_gate_source = table.Column<int>(type: "integer", nullable: true),
                    reminder_default = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    username_changed_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    grace_ends_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    banned_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    avatar_emoji = table.Column<string>(type: "text", nullable: true),
                    avatar_kind = table.Column<int>(type: "integer", nullable: true),
                    avatar_source_platform_link_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<int>(type: "integer", nullable: false),
                    handle = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_links", x => x.id);
                    table.ForeignKey(
                        name: "fk_platform_links_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "username_registry",
                columns: table => new
                {
                    name_canonical = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reserved_until = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_username_registry", x => x.name_canonical);
                    table.ForeignKey(
                        name: "fk_username_registry_users_owner_id",
                        column: x => x.owner_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.AddForeignKey(
                name: "fk_users_platform_links_avatar_source_platform_link_id",
                table: "users",
                column: "avatar_source_platform_link_id",
                principalTable: "platform_links",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ux_users_friend_code ON users (upper(friend_code)) WHERE friend_code IS NOT NULL;");

            migrationBuilder.CreateIndex(
                name: "ux_platform_links_user_id_platform",
                table: "platform_links",
                columns: PlatformLinkUniqueColumns,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_username_registry_owner_id_active",
                table: "username_registry",
                column: "owner_id",
                unique: true,
                filter: "state = 0");

            migrationBuilder.CreateIndex(
                name: "ix_users_banned_at",
                table: "users",
                column: "banned_at",
                filter: "banned_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_users_grace_ends_at",
                table: "users",
                column: "grace_ends_at",
                filter: "lifecycle = 2");

            migrationBuilder.CreateIndex(
                name: "ix_users_lifecycle",
                table: "users",
                column: "lifecycle");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_users_platform_links_avatar_source_platform_link_id",
                table: "users");

            migrationBuilder.DropTable(
                name: "platform_links");

            migrationBuilder.DropTable(
                name: "username_registry");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
