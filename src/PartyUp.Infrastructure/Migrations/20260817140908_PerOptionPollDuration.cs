using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PartyUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PerOptionPollDuration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "duration_minutes",
                table: "poll_options",
                type: "integer",
                nullable: true);

            // Every existing option inherited the poll's single duration, so that is its own now.
            migrationBuilder.Sql(
                "UPDATE poll_options o SET duration_minutes = p.duration_minutes FROM polls p WHERE p.id = o.poll_id;");

            migrationBuilder.AlterColumn<int>(
                name: "duration_minutes",
                table: "poll_options",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_poll_options_duration",
                table: "poll_options",
                sql: "duration_minutes BETWEEN 15 AND 1440 AND duration_minutes % 15 = 0");

            migrationBuilder.DropCheckConstraint(
                name: "ck_polls_duration",
                table: "polls");

            migrationBuilder.DropColumn(
                name: "duration_minutes",
                table: "polls");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "duration_minutes",
                table: "polls",
                type: "integer",
                nullable: true);

            // The poll-level column cannot hold more than one length: the shortest option's wins.
            migrationBuilder.Sql(
                "UPDATE polls p SET duration_minutes = (SELECT MIN(o.duration_minutes) FROM poll_options o WHERE o.poll_id = p.id);");
            migrationBuilder.Sql("UPDATE polls SET duration_minutes = 120 WHERE duration_minutes IS NULL;");

            migrationBuilder.AlterColumn<int>(
                name: "duration_minutes",
                table: "polls",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_polls_duration",
                table: "polls",
                sql: "duration_minutes BETWEEN 15 AND 1440 AND duration_minutes % 15 = 0");

            migrationBuilder.DropCheckConstraint(
                name: "ck_poll_options_duration",
                table: "poll_options");

            migrationBuilder.DropColumn(
                name: "duration_minutes",
                table: "poll_options");
        }
    }
}
