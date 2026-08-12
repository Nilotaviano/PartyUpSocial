using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PartyUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAvatarToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "avatar_token",
                table: "users",
                type: "text",
                nullable: true,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "avatar_token",
                table: "users");
        }
    }
}
