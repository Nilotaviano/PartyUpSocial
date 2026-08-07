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
    public partial class AddAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "availability_series",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_of_week = table.Column<short>(type: "smallint", nullable: false),
                    start_time = table.Column<LocalTime>(type: "time without time zone", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    zone_id = table.Column<string>(type: "text", nullable: false),
                    effective_from = table.Column<LocalDate>(type: "date", nullable: false),
                    effective_until = table.Column<LocalDate>(type: "date", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_availability_series", x => x.id);
                    table.CheckConstraint("ck_availability_series_duration", "duration_minutes BETWEEN 15 AND 1440 AND duration_minutes % 15 = 0");
                    table.CheckConstraint("ck_availability_series_effective_range", "effective_until IS NULL OR effective_until >= effective_from");
                    table.CheckConstraint("ck_availability_series_grid", "extract(second from start_time) = 0 AND extract(minute from start_time)::int % 15 = 0");
                    table.ForeignKey(
                        name: "fk_availability_series_users_owner_id",
                        column: x => x.owner_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "availability_blocks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_time = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    end_time = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    source_series_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_occurrence_date = table.Column<LocalDate>(type: "date", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_availability_blocks", x => x.id);
                    table.CheckConstraint("ck_availability_blocks_grid", "date_trunc('minute', start_time) = start_time AND date_trunc('minute', end_time) = end_time AND extract(minute from start_time)::int % 15 = 0 AND extract(minute from end_time)::int % 15 = 0");
                    table.CheckConstraint("ck_availability_blocks_provenance", "(source_series_id IS NULL) = (source_occurrence_date IS NULL)");
                    table.CheckConstraint("ck_availability_blocks_range", "end_time > start_time");
                    table.ForeignKey(
                        name: "fk_availability_blocks_availability_series_source_series_id",
                        column: x => x.source_series_id,
                        principalTable: "availability_series",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_availability_blocks_users_owner_id",
                        column: x => x.owner_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "availability_occurrence_skips",
                columns: table => new
                {
                    series_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurrence_date = table.Column<LocalDate>(type: "date", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_availability_occurrence_skips", x => new { x.series_id, x.occurrence_date });
                    table.ForeignKey(
                        name: "fk_availability_occurrence_skips_availability_series_series_id",
                        column: x => x.series_id,
                        principalTable: "availability_series",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_availability_blocks_owner_start_end",
                table: "availability_blocks",
                columns: new[] { "owner_id", "start_time", "end_time" });

            migrationBuilder.CreateIndex(
                name: "ix_availability_blocks_source_series_occurrence",
                table: "availability_blocks",
                columns: new[] { "source_series_id", "source_occurrence_date" });

            migrationBuilder.CreateIndex(
                name: "ix_availability_series_owner_effective",
                table: "availability_series",
                columns: new[] { "owner_id", "effective_from", "effective_until" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "availability_blocks");

            migrationBuilder.DropTable(
                name: "availability_occurrence_skips");

            migrationBuilder.DropTable(
                name: "availability_series");
        }
    }
}
