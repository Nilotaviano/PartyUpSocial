using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

// Every CreateIndex call below passes its column-name array once, at a single non-repeated call
// site — CA1861 fires anyway because it does not special-case EF's generated migration shape.
#pragma warning disable CA1861

namespace PartyUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPolls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "poll_options",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    poll_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_time = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    suggested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    suggested_by_guest_identity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_poll_options", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "polls",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    platform = table.Column<short>(type: "smallint", nullable: false),
                    game_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    game_steam_app_id = table.Column<long>(type: "bigint", nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    min_attendance = table.Column<int>(type: "integer", nullable: false),
                    max_capacity = table.Column<int>(type: "integer", nullable: true),
                    is_private = table.Column<bool>(type: "boolean", nullable: false),
                    deadline_choice = table.Column<short>(type: "smallint", nullable: false),
                    deadline = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    selected_option_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    selection_window_end = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_polls", x => x.id);
                    table.CheckConstraint("ck_polls_capacity", "max_capacity IS NULL OR max_capacity >= min_attendance");
                    table.CheckConstraint("ck_polls_duration", "duration_minutes BETWEEN 15 AND 1440 AND duration_minutes % 15 = 0");
                    table.CheckConstraint("ck_polls_game_shape", "(game_steam_app_id IS NULL OR platform = 0) AND (game_steam_app_id IS NULL OR game_name IS NOT NULL)");
                    table.CheckConstraint("ck_polls_min_attendance", "min_attendance >= 1");
                    table.ForeignKey(
                        name: "fk_polls_poll_options_selected_option_id",
                        column: x => x.selected_option_id,
                        principalTable: "poll_options",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_polls_sessions_created_session_id",
                        column: x => x.created_session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_polls_users_organizer_id",
                        column: x => x.organizer_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "poll_participants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    poll_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    guest_identity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    first_response_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_poll_participants", x => x.id);
                    table.CheckConstraint("ck_poll_participants_participant", "(user_id IS NOT NULL) <> (guest_identity_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_poll_participants_guest_identities_guest_identity_id",
                        column: x => x.guest_identity_id,
                        principalTable: "guest_identities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_poll_participants_polls_poll_id",
                        column: x => x.poll_id,
                        principalTable: "polls",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_poll_participants_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "poll_votes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    poll_id = table.Column<Guid>(type: "uuid", nullable: false),
                    option_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    guest_identity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    choice = table.Column<short>(type: "smallint", nullable: false),
                    submitted_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_poll_votes", x => x.id);
                    table.CheckConstraint("ck_poll_votes_participant", "(user_id IS NOT NULL) <> (guest_identity_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_poll_votes_guest_identities_guest_identity_id",
                        column: x => x.guest_identity_id,
                        principalTable: "guest_identities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_poll_votes_poll_options_option_id",
                        column: x => x.option_id,
                        principalTable: "poll_options",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_poll_votes_polls_poll_id",
                        column: x => x.poll_id,
                        principalTable: "polls",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_poll_votes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_join_requests_poll_pending_requester",
                table: "join_requests",
                columns: new[] { "poll_id", "requester_user_id" },
                unique: true,
                filter: "poll_id IS NOT NULL AND status = 0");

            migrationBuilder.CreateIndex(
                name: "ux_poll_options",
                table: "poll_options",
                columns: new[] { "poll_id", "start_time" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_poll_participants_poll_guest",
                table: "poll_participants",
                columns: new[] { "poll_id", "guest_identity_id" },
                unique: true,
                filter: "guest_identity_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_poll_participants_poll_user",
                table: "poll_participants",
                columns: new[] { "poll_id", "user_id" },
                unique: true,
                filter: "user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_poll_votes_option_guest",
                table: "poll_votes",
                columns: new[] { "option_id", "guest_identity_id" },
                unique: true,
                filter: "guest_identity_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_poll_votes_option_user",
                table: "poll_votes",
                columns: new[] { "option_id", "user_id" },
                unique: true,
                filter: "user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_polls_organizer_status_deadline",
                table: "polls",
                columns: new[] { "organizer_id", "status", "deadline" });

            migrationBuilder.CreateIndex(
                name: "ix_polls_status_deadline_id",
                table: "polls",
                columns: new[] { "status", "deadline", "id" });

            migrationBuilder.AddForeignKey(
                name: "fk_guest_identities_polls_poll_id",
                table: "guest_identities",
                column: "poll_id",
                principalTable: "polls",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_guest_links_polls_poll_id",
                table: "guest_links",
                column: "poll_id",
                principalTable: "polls",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_join_requests_polls_poll_id",
                table: "join_requests",
                column: "poll_id",
                principalTable: "polls",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_notifications_polls_poll_id",
                table: "notifications",
                column: "poll_id",
                principalTable: "polls",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_sessions_polls_origin_poll_id",
                table: "sessions",
                column: "origin_poll_id",
                principalTable: "polls",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_poll_options_polls_poll_id",
                table: "poll_options",
                column: "poll_id",
                principalTable: "polls",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_guest_identities_polls_poll_id",
                table: "guest_identities");

            migrationBuilder.DropForeignKey(
                name: "fk_guest_links_polls_poll_id",
                table: "guest_links");

            migrationBuilder.DropForeignKey(
                name: "fk_join_requests_polls_poll_id",
                table: "join_requests");

            migrationBuilder.DropForeignKey(
                name: "fk_notifications_polls_poll_id",
                table: "notifications");

            migrationBuilder.DropForeignKey(
                name: "fk_sessions_polls_origin_poll_id",
                table: "sessions");

            migrationBuilder.DropForeignKey(
                name: "fk_poll_options_polls_poll_id",
                table: "poll_options");

            migrationBuilder.DropTable(
                name: "poll_participants");

            migrationBuilder.DropTable(
                name: "poll_votes");

            migrationBuilder.DropTable(
                name: "polls");

            migrationBuilder.DropTable(
                name: "poll_options");

            migrationBuilder.DropIndex(
                name: "ux_join_requests_poll_pending_requester",
                table: "join_requests");
        }
    }
}
