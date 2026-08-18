using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace PartyUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFeedbackItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "feedback_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    submitter_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<short>(type: "smallint", nullable: false),
                    message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    route = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    locale = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    app_version = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    handled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    handled_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_feedback_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_feedback_items_users_submitter_user_id",
                        column: x => x.submitter_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_feedback_items_created_at",
                table: "feedback_items",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_feedback_items_submitter_user_id",
                table: "feedback_items",
                column: "submitter_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_feedback_items_unhandled",
                table: "feedback_items",
                column: "handled",
                filter: "handled = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "feedback_items");
        }
    }
}
