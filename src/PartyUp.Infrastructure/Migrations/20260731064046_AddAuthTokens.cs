using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace PartyUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auth_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<int>(type: "integer", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    email_normalized = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    request_ip_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    pending_locale = table.Column<string>(type: "text", nullable: true),
                    pending_age_gate = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    payload = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_auth_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_auth_tokens_email_normalized",
                table: "auth_tokens",
                column: "email_normalized");

            migrationBuilder.CreateIndex(
                name: "ix_auth_tokens_expires_at",
                table: "auth_tokens",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_auth_tokens_user_id",
                table: "auth_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_auth_tokens_token_hash",
                table: "auth_tokens",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_tokens");
        }
    }
}
