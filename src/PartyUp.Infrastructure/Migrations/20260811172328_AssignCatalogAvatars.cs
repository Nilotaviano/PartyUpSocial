using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PartyUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AssignCatalogAvatars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AVATARS_DESIGN.md §2.2 (D-050): every row lands on a glyph that is a pure function of
            // its id, so the statement is re-runnable and yields the same catalog assignment every
            // time. ('x' || 8 hex digits)::bit(32) is Postgres' hex bit-string literal syntax;
            // abs() guards the bit(32) -> bigint conversion against a negative high bit, and the
            // +1 lands on Postgres' 1-based array subscript. The array order is AvatarCatalog.Glyphs
            // order, asserted glyph for glyph by AvatarCatalogBackfillMigrationTests.
            // avatar_source_platform_link_id is nulled because an Emoji avatar and a source link are
            // mutually exclusive shapes; the ON DELETE SET NULL foreign key itself is untouched and
            // stays for wave B.
            migrationBuilder.Sql("""
                UPDATE users
                   SET avatar_kind = 0,
                       avatar_emoji = (ARRAY[
                           E'\U0001F3AE', E'\U0001F3B2', E'\U0001F579\uFE0F', E'\U0001F47E',
                           E'\U0001F0CF', E'\u265F\uFE0F', E'\U0001F3AF', E'\U0001F3C6',
                           E'\U0001F680', E'\U0001F409', E'\U0001F98A', E'\U0001F419',
                           E'\U0001F427', E'\U0001F989', E'\U0001F41D', E'\U0001F335',
                           E'\U0001F344', E'\U0001F319', E'\u2B50', E'\U0001F525',
                           E'\u26A1', E'\U0001F3A7', E'\U0001F355', E'\u2615'
                       ])[(abs(('x' || substr(md5(id::text), 1, 8))::bit(32)::bigint) % 24) + 1],
                       avatar_source_platform_link_id = NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The overwritten glyphs are not recorded anywhere, so down cannot restore them.
        }
    }
}
