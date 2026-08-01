using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace PartyUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFriendGraph : Migration
    {
        private static readonly string[] BlockerCreatedColumns = ["blocker_id", "created_at"];
        private static readonly string[] FriendshipHiStatusColumns = ["user_hi_id", "status"];
        private static readonly string[] FriendshipLoStatusColumns = ["user_lo_id", "status"];
        private static readonly string[] PendingRequestedByColumns = ["requested_by", "requested_at"];
        private static readonly string[] RecipientCreatedColumns = ["recipient_user_id", "created_at", "id"];
        private static readonly string[] SteamMatchColumns = ["recipient_user_id", "actor_user_id"];
        private static readonly bool[] AscendingDescending = [false, true];
        private static readonly bool[] AscendingDescendingDescending = [false, true, true];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blocks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    blocker_id = table.Column<Guid>(type: "uuid", nullable: false),
                    blocked_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blocks", x => x.id);
                    table.UniqueConstraint("ux_blocks_ordered_pair", x => new { x.blocker_id, x.blocked_id });
                    table.CheckConstraint("ck_blocks_not_self", "blocker_id <> blocked_id");
                    table.ForeignKey(
                        name: "fk_blocks_users_blocked_id",
                        column: x => x.blocked_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_blocks_users_blocker_id",
                        column: x => x.blocker_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "friendships",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_lo_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_hi_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    requested_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_friendships", x => x.id);
                    table.UniqueConstraint("ux_friendships_pair", x => new { x.user_lo_id, x.user_hi_id });
                    table.CheckConstraint("ck_friendships_accept_time", "(status = 0 AND accepted_at IS NULL) OR (status = 1 AND accepted_at IS NOT NULL)");
                    table.CheckConstraint("ck_friendships_canonical", "user_lo_id < user_hi_id");
                    table.CheckConstraint("ck_friendships_requester", "requested_by IN (user_lo_id, user_hi_id)");
                    table.CheckConstraint("ck_friendships_status", "status IN (0, 1)");
                    table.ForeignKey(
                        name: "fk_friendships_users_requested_by",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_friendships_users_user_hi_id",
                        column: x => x.user_hi_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_friendships_users_user_lo_id",
                        column: x => x.user_lo_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<short>(type: "smallint", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    poll_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    option_start_time = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.CheckConstraint("ck_notifications_friend_shape", "type NOT IN (23, 24) OR (actor_user_id IS NOT NULL AND session_id IS NULL AND poll_id IS NULL AND option_start_time IS NULL)");
                    table.CheckConstraint("ck_notifications_type", "type BETWEEN 0 AND 24");
                    table.ForeignKey(
                        name: "fk_notifications_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_notifications_users_recipient_user_id",
                        column: x => x.recipient_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_blocks_blocked",
                table: "blocks",
                column: "blocked_id");

            migrationBuilder.CreateIndex(
                name: "ix_blocks_blocker_created",
                table: "blocks",
                columns: BlockerCreatedColumns,
                descending: AscendingDescending);

            migrationBuilder.CreateIndex(
                name: "ix_friendships_hi_status",
                table: "friendships",
                columns: FriendshipHiStatusColumns);

            migrationBuilder.CreateIndex(
                name: "ix_friendships_lo_status",
                table: "friendships",
                columns: FriendshipLoStatusColumns);

            migrationBuilder.CreateIndex(
                name: "ix_friendships_pending_requested_by",
                table: "friendships",
                columns: PendingRequestedByColumns,
                descending: AscendingDescending,
                filter: "status = 0");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_recipient_created",
                table: "notifications",
                columns: RecipientCreatedColumns,
                descending: AscendingDescendingDescending);

            migrationBuilder.CreateIndex(
                name: "ux_notifications_steam_match_once",
                table: "notifications",
                columns: SteamMatchColumns,
                unique: true,
                filter: "type = 24");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blocks");

            migrationBuilder.DropTable(
                name: "friendships");

            migrationBuilder.DropTable(
                name: "notifications");
        }
    }
}
