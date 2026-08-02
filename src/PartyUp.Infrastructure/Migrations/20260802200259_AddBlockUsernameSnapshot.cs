using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PartyUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBlockUsernameSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "blocked_username_snapshot",
                table: "blocks",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE blocks AS b
                SET blocked_username_snapshot = u.username
                FROM users AS u
                WHERE u.id = b.blocked_id
                """);

            migrationBuilder.AlterColumn<string>(
                name: "blocked_username_snapshot",
                table: "blocks",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(30)",
                oldMaxLength: 30,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "blocked_username_snapshot",
                table: "blocks");
        }
    }
}
