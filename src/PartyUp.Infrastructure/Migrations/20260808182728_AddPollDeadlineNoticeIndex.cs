using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PartyUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPollDeadlineNoticeIndex : Migration
    {
        private static readonly string[] PollDeadlineOnceColumns = ["poll_id", "recipient_user_id"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_notifications_poll_deadline_once",
                table: "notifications",
                columns: PollDeadlineOnceColumns,
                unique: true,
                filter: "type = 15");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_notifications_poll_deadline_once",
                table: "notifications");
        }
    }
}
