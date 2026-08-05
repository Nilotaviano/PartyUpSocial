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
    public partial class AddFixedTimeSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "reminder_handoff_id",
                table: "notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "notification_mutes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    poll_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_mutes", x => x.id);
                    table.CheckConstraint("ck_notification_mutes_target", "(session_id IS NOT NULL) <> (poll_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_notification_mutes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    start_time = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    platform = table.Column<short>(type: "smallint", nullable: false),
                    game_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    game_steam_app_id = table.Column<long>(type: "bigint", nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    min_attendance = table.Column<int>(type: "integer", nullable: false),
                    max_capacity = table.Column<int>(type: "integer", nullable: true),
                    is_private = table.Column<bool>(type: "boolean", nullable: false),
                    origin_poll_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sessions", x => x.id);
                    table.CheckConstraint("ck_sessions_capacity", "max_capacity IS NULL OR max_capacity >= min_attendance");
                    table.CheckConstraint("ck_sessions_duration", "duration_minutes BETWEEN 15 AND 1440 AND duration_minutes % 15 = 0");
                    table.CheckConstraint("ck_sessions_game_shape", "(game_steam_app_id IS NULL OR platform = 0) AND (game_steam_app_id IS NULL OR game_name IS NOT NULL)");
                    table.CheckConstraint("ck_sessions_min_attendance", "min_attendance >= 1");
                    table.ForeignKey(
                        name: "fk_sessions_users_organizer_id",
                        column: x => x.organizer_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "guest_identities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    poll_id = table.Column<Guid>(type: "uuid", nullable: true),
                    display_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guest_identities", x => x.id);
                    table.CheckConstraint("ck_guest_identities_target", "(session_id IS NOT NULL) <> (poll_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_guest_identities_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "join_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    poll_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requester_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_join_requests", x => x.id);
                    table.CheckConstraint("ck_join_requests_target", "(session_id IS NOT NULL) <> (poll_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_join_requests_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_join_requests_users_requester_user_id",
                        column: x => x.requester_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "session_reminder_handoffs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    due_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    planned_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    delivered_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_attempt_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_reminder_handoffs", x => x.id);
                    table.CheckConstraint("ck_session_reminder_handoffs_delivered", "(state = 2) = (delivered_at IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_session_reminder_handoffs_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_session_reminder_handoffs_users_recipient_user_id",
                        column: x => x.recipient_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "guest_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    poll_id = table.Column<Guid>(type: "uuid", nullable: true),
                    token_hash = table.Column<string>(type: "character varying(43)", maxLength: 43, nullable: false),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    guest_identity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    claimed_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guest_links", x => x.id);
                    table.CheckConstraint("ck_guest_links_target", "(session_id IS NOT NULL) <> (poll_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_guest_links_guest_identities_guest_identity_id",
                        column: x => x.guest_identity_id,
                        principalTable: "guest_identities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_guest_links_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "session_participations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    guest_identity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    origin = table.Column<short>(type: "smallint", nullable: false),
                    is_waitlisted = table.Column<bool>(type: "boolean", nullable: false),
                    reminder_choice = table.Column<short>(type: "smallint", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    responded_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_participations", x => x.id);
                    table.CheckConstraint("ck_session_participations_participant", "(user_id IS NOT NULL) <> (guest_identity_id IS NOT NULL)");
                    table.CheckConstraint("ck_session_participations_reminder", "reminder_choice IS NULL OR user_id IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_session_participations_guest_identities_guest_identity_id",
                        column: x => x.guest_identity_id,
                        principalTable: "guest_identities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_session_participations_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_session_participations_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_notifications_session_reminder_once",
                table: "notifications",
                column: "reminder_handoff_id",
                unique: true,
                filter: "type = 12");

            migrationBuilder.CreateIndex(
                name: "ix_guest_links_session_state",
                table: "guest_links",
                columns: new[] { "session_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ux_guest_links_token_hash",
                table: "guest_links",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_join_requests_session_status_created_id",
                table: "join_requests",
                columns: new[] { "session_id", "status", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_join_requests_session_pending_requester",
                table: "join_requests",
                columns: new[] { "session_id", "requester_user_id" },
                unique: true,
                filter: "session_id IS NOT NULL AND status = 0");

            migrationBuilder.CreateIndex(
                name: "ux_notification_mutes_session_user",
                table: "notification_mutes",
                columns: new[] { "session_id", "user_id" },
                unique: true,
                filter: "session_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_session_participations_session_status_waitlist",
                table: "session_participations",
                columns: new[] { "session_id", "status", "is_waitlisted" });

            migrationBuilder.CreateIndex(
                name: "ix_session_participations_user_status_session",
                table: "session_participations",
                columns: new[] { "user_id", "status", "session_id" });

            migrationBuilder.CreateIndex(
                name: "ux_session_participations_session_guest",
                table: "session_participations",
                columns: new[] { "session_id", "guest_identity_id" },
                unique: true,
                filter: "guest_identity_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_session_participations_session_user",
                table: "session_participations",
                columns: new[] { "session_id", "user_id" },
                unique: true,
                filter: "user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_session_reminder_handoffs_state_due_id",
                table: "session_reminder_handoffs",
                columns: new[] { "state", "due_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_session_reminder_handoffs_session_recipient",
                table: "session_reminder_handoffs",
                columns: new[] { "session_id", "recipient_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sessions_organizer_status_start",
                table: "sessions",
                columns: new[] { "organizer_id", "status", "start_time" });

            migrationBuilder.CreateIndex(
                name: "ix_sessions_status_start_id",
                table: "sessions",
                columns: new[] { "status", "start_time", "id" });

            migrationBuilder.AddForeignKey(
                name: "fk_notifications_session_reminder_handoffs_reminder_handoff_id",
                table: "notifications",
                column: "reminder_handoff_id",
                principalTable: "session_reminder_handoffs",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_notifications_session_reminder_handoffs_reminder_handoff_id",
                table: "notifications");

            migrationBuilder.DropTable(
                name: "guest_links");

            migrationBuilder.DropTable(
                name: "join_requests");

            migrationBuilder.DropTable(
                name: "notification_mutes");

            migrationBuilder.DropTable(
                name: "session_participations");

            migrationBuilder.DropTable(
                name: "session_reminder_handoffs");

            migrationBuilder.DropTable(
                name: "guest_identities");

            migrationBuilder.DropTable(
                name: "sessions");

            migrationBuilder.DropIndex(
                name: "ux_notifications_session_reminder_once",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "reminder_handoff_id",
                table: "notifications");
        }
    }
}
