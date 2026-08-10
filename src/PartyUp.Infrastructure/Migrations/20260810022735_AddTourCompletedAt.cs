using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace PartyUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTourCompletedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "tour_completed_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            // TOUR_DESIGN.md §1/§4: the tour is a first-login feature, not a launch announcement, so
            // only accounts whose onboarding is already complete are backfilled — a still-onboarding
            // account keeps null and gets the tour like any first-login user. "Onboarding complete"
            // mirrors CurrentUserAuthorizationHandler's own "onboarded" predicate exactly: lifecycle
            // Active(1) or PendingDeletion(2) (both reachable only once CompleteOnboarding has run,
            // except PendingDeletion also reachable straight from Onboarding — hence the explicit
            // profile-completeness columns below rather than lifecycle alone) with every required
            // profile field populated (avatar_kind=1 is Platform, always a set avatar regardless of
            // avatar_emoji; avatar_kind=0/null with a non-empty avatar_emoji is a set Emoji avatar —
            // Avatar.IsUnset's own condition negated).
            migrationBuilder.Sql("""
                UPDATE users
                SET tour_completed_at = now()
                WHERE lifecycle IN (1, 2)
                  AND username IS NOT NULL
                  AND display_name IS NOT NULL
                  AND time_zone_id IS NOT NULL
                  AND locale IS NOT NULL
                  AND (avatar_kind = 1 OR (avatar_emoji IS NOT NULL AND avatar_emoji <> ''));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "tour_completed_at",
                table: "users");
        }
    }
}
