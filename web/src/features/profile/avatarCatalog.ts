/**
 * AVATARS_DESIGN.md §2.1 (D-050): the closed 24-glyph catalog, mirroring
 * `AvatarCatalog.Glyphs` (`src/PartyUp.Application/Profile/AvatarCatalog.cs`) index for index.
 * The server rejects any glyph outside it, so neither the order nor the glyphs may drift.
 * `slug` is never rendered: it is the i18n key suffix, `t('avatar:catalog.<slug>')`.
 */
export const AVATAR_CATALOG = [
  { glyph: '\u{1F3AE}', slug: 'gameController' },
  { glyph: '\u{1F3B2}', slug: 'dice' },
  { glyph: '\u{1F579}\uFE0F', slug: 'joystick' },
  { glyph: '\u{1F47E}', slug: 'alien' },
  { glyph: '\u{1F0CF}', slug: 'jokerCard' },
  { glyph: '\u265F\uFE0F', slug: 'chessPawn' },
  { glyph: '\u{1F3AF}', slug: 'dart' },
  { glyph: '\u{1F3C6}', slug: 'trophy' },
  { glyph: '\u{1F680}', slug: 'rocket' },
  { glyph: '\u{1F409}', slug: 'dragon' },
  { glyph: '\u{1F98A}', slug: 'fox' },
  { glyph: '\u{1F419}', slug: 'octopus' },
  { glyph: '\u{1F427}', slug: 'penguin' },
  { glyph: '\u{1F989}', slug: 'owl' },
  { glyph: '\u{1F41D}', slug: 'bee' },
  { glyph: '\u{1F335}', slug: 'cactus' },
  { glyph: '\u{1F344}', slug: 'mushroom' },
  { glyph: '\u{1F319}', slug: 'moon' },
  { glyph: '\u2B50', slug: 'star' },
  { glyph: '\u{1F525}', slug: 'fire' },
  { glyph: '\u26A1', slug: 'bolt' },
  { glyph: '\u{1F3A7}', slug: 'headphones' },
  { glyph: '\u{1F355}', slug: 'pizza' },
  { glyph: '\u2615', slug: 'coffee' },
] as const
