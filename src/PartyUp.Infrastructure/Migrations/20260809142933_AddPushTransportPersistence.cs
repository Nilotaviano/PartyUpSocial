using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace PartyUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPushTransportPersistence : Migration
    {
        private static readonly string[] DispatchPendingColumns = ["created_at", "id"];

        private static readonly string[] UnreadColumns = ["recipient_user_id", "created_at", "id"];

        private static readonly bool[] UnreadDescending = [false, true, true];

        private static readonly string[] MutePollUserColumns = ["poll_id", "user_id"];

        private static readonly string[] PushSendOnceColumns = ["notification_id", "push_subscription_id"];

        private static readonly string[] ActiveEndpointColumns = ["user_id", "channel", "endpoint_hash"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "dispatch_state",
                table: "notifications",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            // PHASE7_DESIGN.md §1: shipping Phase 7 must not replay months of history as a push
            // storm, so every pre-existing row backfills to the terminal Skipped(2) state rather than
            // the column's own Pending(0) default.
            migrationBuilder.Sql("UPDATE notifications SET dispatch_state = 2;");

            migrationBuilder.CreateTable(
                name: "push_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    public_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    auth_secret = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    channel = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    endpoint_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_push_subscriptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_push_subscriptions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notification_push_sends",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    push_subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claimed_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_push_sends", x => x.id);
                    table.ForeignKey(
                        name: "fk_notification_push_sends_notifications_notification_id",
                        column: x => x.notification_id,
                        principalTable: "notifications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_notification_push_sends_push_subscriptions_push_subscriptio",
                        column: x => x.push_subscription_id,
                        principalTable: "push_subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sessions_origin_poll_id",
                table: "sessions",
                column: "origin_poll_id",
                filter: "origin_poll_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_dispatch_pending",
                table: "notifications",
                columns: DispatchPendingColumns,
                filter: "dispatch_state = 0");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_unread",
                table: "notifications",
                columns: UnreadColumns,
                descending: UnreadDescending,
                filter: "read_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_notification_mutes_poll_user",
                table: "notification_mutes",
                columns: MutePollUserColumns,
                unique: true,
                filter: "poll_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_notification_push_sends_once",
                table: "notification_push_sends",
                columns: PushSendOnceColumns,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_push_subscriptions_active_endpoint",
                table: "push_subscriptions",
                columns: ActiveEndpointColumns,
                unique: true,
                filter: "revoked_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_push_sends");

            migrationBuilder.DropTable(
                name: "push_subscriptions");

            migrationBuilder.DropIndex(
                name: "ix_sessions_origin_poll_id",
                table: "sessions");

            migrationBuilder.DropIndex(
                name: "ix_notifications_dispatch_pending",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "ix_notifications_unread",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "ux_notification_mutes_poll_user",
                table: "notification_mutes");

            migrationBuilder.DropColumn(
                name: "dispatch_state",
                table: "notifications");
        }
    }
}
