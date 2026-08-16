using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace PartyUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFriendGroupsAndContentVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A conversion, not a drop/add: every stored privacy flag keeps its meaning
            // (false -> Public = 0, true -> Private = 1). The temporary default only fills the
            // column for existing rows; the model declares none, so it is dropped again below.
            migrationBuilder.AddColumn<short>(
                name: "visibility",
                table: "sessions",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.Sql("UPDATE sessions SET visibility = CASE WHEN is_private THEN 1 ELSE 0 END;");

            migrationBuilder.Sql("ALTER TABLE sessions ALTER COLUMN visibility DROP DEFAULT;");

            migrationBuilder.DropColumn(
                name: "is_private",
                table: "sessions");

            migrationBuilder.AddColumn<short>(
                name: "visibility",
                table: "polls",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.Sql("UPDATE polls SET visibility = CASE WHEN is_private THEN 1 ELSE 0 END;");

            migrationBuilder.Sql("ALTER TABLE polls ALTER COLUMN visibility DROP DEFAULT;");

            migrationBuilder.DropColumn(
                name: "is_private",
                table: "polls");

            migrationBuilder.CreateTable(
                name: "friend_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_friend_groups", x => x.id);
                    table.ForeignKey(
                        name: "fk_friend_groups_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "friend_group_members",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    invited_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_friend_group_members", x => new { x.group_id, x.user_id });
                    table.CheckConstraint("ck_friend_group_members_accept_time", "(status = 0 AND accepted_at IS NULL) OR (status = 1 AND accepted_at IS NOT NULL)");
                    table.CheckConstraint("ck_friend_group_members_status", "status IN (0, 1)");
                    table.ForeignKey(
                        name: "fk_friend_group_members_friend_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "friend_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_friend_group_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "poll_audience_groups",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    poll_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_poll_audience_groups", x => new { x.poll_id, x.group_id });
                    table.ForeignKey(
                        name: "fk_poll_audience_groups_friend_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "friend_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_poll_audience_groups_polls_poll_id",
                        column: x => x.poll_id,
                        principalTable: "polls",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "session_audience_groups",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_audience_groups", x => new { x.session_id, x.group_id });
                    table.ForeignKey(
                        name: "fk_session_audience_groups_friend_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "friend_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_session_audience_groups_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_sessions_visibility",
                table: "sessions",
                sql: "visibility IN (0, 1, 2)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_polls_visibility",
                table: "polls",
                sql: "visibility IN (0, 1, 2)");

            migrationBuilder.CreateIndex(
                name: "ix_friend_group_members_user",
                table: "friend_group_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_friend_groups_owner",
                table: "friend_groups",
                column: "owner_user_id");

            // D-199's per-owner, case-insensitive name space. EF has no expression-index primitive,
            // so the unique index is raw SQL (same treatment as ix_steam_apps_name_trgm).
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ux_friend_groups_owner_name ON friend_groups (owner_user_id, lower(name));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "friend_group_members");

            migrationBuilder.DropTable(
                name: "poll_audience_groups");

            migrationBuilder.DropTable(
                name: "session_audience_groups");

            migrationBuilder.DropTable(
                name: "friend_groups");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sessions_visibility",
                table: "sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_polls_visibility",
                table: "polls");

            // The reverse conversion: Groups collapses to private, the closest two-level meaning.
            migrationBuilder.AddColumn<bool>(
                name: "is_private",
                table: "sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE sessions SET is_private = (visibility <> 0);");

            migrationBuilder.Sql("ALTER TABLE sessions ALTER COLUMN is_private DROP DEFAULT;");

            migrationBuilder.DropColumn(
                name: "visibility",
                table: "sessions");

            migrationBuilder.AddColumn<bool>(
                name: "is_private",
                table: "polls",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE polls SET is_private = (visibility <> 0);");

            migrationBuilder.Sql("ALTER TABLE polls ALTER COLUMN is_private DROP DEFAULT;");

            migrationBuilder.DropColumn(
                name: "visibility",
                table: "polls");
        }
    }
}
