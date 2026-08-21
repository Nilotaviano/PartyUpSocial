using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace PartyUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestIdentityUpgrade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "upgraded_at",
                table: "guest_identities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "upgraded_to_user_id",
                table: "guest_identities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "fk_guest_identities_users_upgraded_to_user_id",
                table: "guest_identities",
                column: "upgraded_to_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_guest_identities_users_upgraded_to_user_id",
                table: "guest_identities");

            migrationBuilder.DropColumn(
                name: "upgraded_at",
                table: "guest_identities");

            migrationBuilder.DropColumn(
                name: "upgraded_to_user_id",
                table: "guest_identities");
        }
    }
}
