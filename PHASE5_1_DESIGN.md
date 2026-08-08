# Phase 5.1 — Visual identity: "Tabletop Club"

Owner-ratified direction (2026-08-07, moodboard C): cardstock surfaces, confident 2px ink outlines, offset solid shadows, sticker chips, chunky friendly display type. Sleeper's social warmth turned up; Rallly-grade legibility preserved in planning grids per PHASE3_DESIGN §19. Dark mode ships in this phase (owner decision, same date). Binding constraints from the IMPLEMENTATION_PLAN Phase 5.1 row: WCAG 2.2 AA with the axe lane green and zero exclusions on every route, all three locale layouts intact, no functional or API-contract change under a styling commit — the sole scoped exception being the client-only theme preference (§6 — infrastructure in S1, user control in S5).

## 1. Tokens (Tailwind v4 `@theme` in `web/src/index.css`)

Names are semantic; components never use raw palette utilities (`bg-slate-50` etc. all die in the rollout). **Rule: every state/action fill token ships an `-ink` companion; text on such a fill always uses that companion. Text on the surface tokens `page`/`card` uses `ink`/`ink-soft`.** Dark values live in an **unlayered** `[data-theme=dark]` CSS-variable override block (unlayered beats `@theme`'s layered `:root` regardless of order) — therefore tokens are declared with `@theme` (never `@theme inline`, which inlines values and kills the cascade override). A `@custom-variant dark (&:where([data-theme=dark], [data-theme=dark] *))` is declared for the rare per-component dark tweak. `color-scheme` is set in `@layer base` (`:root { color-scheme: light }`, `[data-theme=dark] { color-scheme: dark }` — it cannot live inside `@theme`) so native controls (the four `datetime-local` inputs, scrollbars) follow.

### Light ("daylight table")

| Token | Value | Use |
|---|---|---|
| `--color-page` | `#F6F2E3` | app background (cardstock) |
| `--color-card` | `#FFFDF5` | cards, dialogs, header |
| `--color-ink` | `#20301C` | text, 2px outlines, offset shadows |
| `--color-ink-soft` | `#5F6B58` | secondary text |
| `--color-primary` / `-primary-ink` | `#7CB518` / `#1B2408` | primary buttons, active nav |
| `--color-accent` / `-accent-ink` | `#FFD447` / `#1B2408` | sticker chips, highlights |
| `--color-danger` / `-danger-ink` | `#C43F1F` / `#FFFFFF` | destructive, errors |
| `--color-free` / `-free-ink` | `#D8EFB8` / `#33520A` | availability Available |
| `--color-maybe` / `-maybe-ink` | `#FFF0B8` / `#6E5500` | availability Maybe |
| `--color-busy` / `-busy-ink` | `#EFECE0` / `#57534B` | availability Busy/blocked (ink dark enough to hold 4.5:1 under the selection wash) |
| `--color-focus` | `#2B4EAD` | focus rings only — nothing else is blue |
| `--color-shadow` | `#20301C` | offset-shadow color (not `--shadow-*`: that namespace emits box-shadow utilities) |
| `--color-qr` / `-qr-ink` | `#FFFDF5` / `#1B2408` | QR chip + module colors — **neither overridden in dark** (scannability) |

`#E4572E` (moodboard tomato) is brand-moment graphics only (logo, empty-state art) — 3.28:1 on page passes non-text 3:1 but **never text**, and never interactive (collides with danger semantics).

### Dark ("club after dark")

Same names overridden under `[data-theme=dark]`:

page `#161D10` · card `#212B18` · ink `#EDE8D4` · ink-soft `#A8B09B` · primary `#8FCB2B` (primary-ink stays `#1B2408`) · accent `#FFD447` / accent-ink `#1B2408` · danger `#FF8A65` / danger-ink `#1B2408` · free `#2F4713`/`#C9E89A` · maybe `#4A3E0C`/`#F2DE8E` · busy `#2A2E24`/`#B3AFA3` · focus `#9DB4F5` · shadow `rgba(237,232,212,0.25)` — the solid-black offset motif is invisible on dark surfaces, so dark keeps the offset geometry with a light-side ink-alpha shadow instead. On accent-filled elements (chips, discs) the dark `ink` outline reads 1.16:1 against `#FFD447`, so their border uses `accent-ink` in dark — accepted cost: the outline motif reads as fill-edge there (accent vs dark page 12.13:1 keeps the chip crisp). Every text pair must pass 4.5:1 (3:1 large text / non-text UI); the verifier recomputes per slice, never trusts this table.

### Spacing / radius / shadow scale

- Spacing rhythm: Tailwind's 4px scale stands; page gutters `px-4 sm:px-8`, section gap `gap-6`, card padding `p-5`, dense lists `p-3`. No arbitrary spacing values in components.
- Radius tokens: `--radius-card: 1.125rem` (cards, dialogs) · `--radius-control: 0.875rem` (buttons, inputs) · chips pill (`9999px`).
- Shadow recipe (via `--color-shadow`): card `4px 4px 0` · dialog `6px 6px 0` · control `3px 3px 0` · pressed `1px 1px 0` — defined once as utilities/component classes in S1; components never inline offsets.

## 2. Type

- **Display / headings / buttons / chips:** Gabarito (500–800), `@fontsource-variable/gabarito`.
- **Body / forms / meta:** Nunito Sans (400–800), `@fontsource-variable/nunito-sans`.
- **Codes (invite/guest links):** existing mono stack.
- Scale: display 2.25rem/1.05, h1 1.75rem, h2 1.375rem, h3 1.125rem (Gabarito); body 1rem/1.55, meta 0.8125rem. S1 imports latin + latin-ext subsets and verifies both files exist in the packages (pt-BR/es coverage); no layout may depend on Latin-only glyph widths.

## 3. Component system

- **Card:** `bg-card`, `border-2 border-ink`, radius-card, shadow-card. Dialogs same with shadow-dialog.
- **Buttons:** 2px ink border, radius-control, Gabarito 600, shadow-control; `:active` translates `+2px,+2px` with pressed shadow ("press the piece down"). Variants: primary, neutral (bg-card), danger (danger/danger-ink), ghost (borderless — dense contexts only). **The shipped `min-h-11` and `focus-visible:outline-*` utilities stay on every interactive element** — the D-139 accessibility tests assert those classes (3 assertion sites across `SessionAccessibility`/`FriendsAccessibility`; the utilities themselves appear at ~137/~95 source sites and none are removed); the global focus rule below is additive, never a replacement.
- **Chips ("stickers"):** pill, 2px ink border, Gabarito 700 0.75rem, accent/accent-ink default; state chips use availability pairs.
- **Grid cells (WeekGrid/Find-a-time):** discipline zone — no offset shadows, no 2px borders inside the grid. Shipped cell states are `free | busy | selected` only (Maybe is a Phase 6 poll state; its `maybe` pair is reserved here, and its non-color mark is Phase 6 design's problem). **State identity is never color-alone:** Available = solid free fill; Busy = busy fill **keeping the shipped `•` glyph mark** (`web/src/features/availability/WeekGrid.tsx:483`), restyled to `busy-ink` — free vs busy fills are near-equal luminance, so the glyph carries the distinction. `selected` = the cell's **underlying availability fill unchanged** plus an `ink`/**6%** wash painted **beneath** the ink layers (glyphs/titles composite on top; washed pairs recomputed: busy-ink on washed busy ≥4.5 both themes with the darkened light busy-ink, free-ink on washed free 6.0+/5.9+) and a 2px `ink` ring drawn around the *selection band*, not per cell (a 4-slot drag is one box, not four — achieved CSS-only via `data-selection-edge` attributes marking band-boundary cell edges; no JS overlay). Selection can cover busy cells (`extendSelection` and Shift+Arrow have no busy guard), and the exclusive `data-state` at `web/src/features/availability/WeekGrid.tsx:428` cannot express fill precedence alone — S4 adds `data-busy` and `data-selection-edge` attributes (markup-only) so CSS keeps the busy fill under selection; the `•` glyph and all layer inks render unchanged. Ring is `ink`, not `focus` (focus stays keyboard-focus-only, outer outline, per §1); this selection ring is the *one sanctioned exception* to the no-2px-borders-in-grid rule, which otherwise bans resting outlines. 1px `ink`/25% hairlines between cells (decorative; state never rests on them). Session titles render on free *and* busy fills — `free-ink` passes on both (7.2+/7.5+). Calendar layer text migrates to tokens with mappings verified against shipped code: session titles inside cells `free-ink` on free (both themes); inherited sessions keep their italic, also `free-ink`; block markers = the `•` in `busy-ink`; friend-layer text `ink` (not `ink-soft` — 4.55:1 on free is too thin) — S4 maps each explicitly.
- **Grid geometry gate:** the shipped browser test asserts only relative row ordering, which passes at any height — it is *not* a geometry tripwire. The font swap lands in **S1**, so **S1** adds the `boundingBox()` assertion — cell height ≥44px and all rows equal-height (no pixel-pinned baseline; fonts legitimately move metrics, the 44px floor is the durable duty) — guarding S1–S4 alike (a test addition, not a functional change). The geometry and axe gates are both .NET/Playwright — **every slice's "axe green" gate means running the WSL browser lane**, not Vitest alone.
- **Avatars:** the D-050 emoji or linked-platform image (PRODUCT_SPEC:87,542 — initials forbidden; the spec permits *no* avatar, for which the shipped neutral `'●'` placeholder stays). Disc: background rotates among `free` / `accent` / `card` (each has an `-ink` companion; the `'●'` renders in that companion), 2px `ink` ring (the `card` disc's `●` uses `ink`); stacked `-8px` with a `card`-colored outer separator ring. "Anonymous User" masking renders in the busy pair — deliberately ordinary (D-076 posture).
- **QR/share (ShareCard):** stays the calm Luma-composed object per §19. The QR always renders `--color-qr-ink` modules on a `--color-qr` chip with full quiet zone in **both** themes — never inverted, never on dark (scannability).
- **Focus:** global additive `focus-visible` rule (2px `focus` ring, 2px offset) as belt-and-suspenders under the retained per-element utilities. Never removed, never ink-colored.
- **Motion:** button press (transform 80ms); dialog pop (scale .96→1, 120ms, on an inner wrapper — never the centered element whose `-translate-x-1/2 -translate-y-1/2` transform `AuthMethods.test.tsx` asserts); one signature moment — session-confirmed / onboarding-finish gets a single 300ms "sticker slap" (scale 1.06→1 + shadow settle). All new motion inside `prefers-reduced-motion: no-preference` (shipped skeletons already use `motion-safe:animate-pulse`; no gap exists there).
- **Copy:** out of scope. Shipped strings and their i18n keys are untouched; warmth arrives via visuals only. (A copy pass, three locales, is a future item.)

## 4. Rollout slices (each: styling-only, axe zero-exclusion green, Vitest suite green with §5's narrow selector exception)

1. **S1 Foundation:** fontsource packages + subsets, `@theme` tokens + dark override block + `color-scheme` + `@custom-variant dark`, base `ui/` components, App shell/nav. Theme infrastructure: `data-theme` root attribute, `prefers-color-scheme` default, localStorage override, no-flash inline script in `index.html`. §6 exception ships here.
2. **S2 Auth + onboarding + deletion + guest pages.**
3. **S3 Home + sessions (list/detail/create/edit/invite) + discover.**
4. **S4 Calendar: WeekGrid, agenda, layers, find-a-time** + layer-token mapping (geometry assertion already guarding since S1).
5. **S5 Friends + profile + settings**, including the theme toggle control (system/light/dark radio) — its labels are the phase's only new i18n keys, added in all three locales (key-parity gate).
6. **S5.5 Responsive sweep** (owner directive 2026-08-08 after a live iPhone overflow report): no route may scroll horizontally at 360px and up. Known defect: the App.tsx header nav is a no-wrap flex row that overflows phone widths — on <sm it becomes a horizontally scrollable chip row (the nav scrolls, never the page); desktop unchanged. Audit every surface for fixed widths, nowrap rows, and dialog min-widths; WeekGrid's 44px floor already fits 390px (64+7×44=372) and must not be shrunk.
7. **S6 Dark-theme audit pass** all surfaces + recomputed contrast sweep both themes.
8. **S7 Closeout:** axe sweep — `AuditedRoutes` gains a theme dimension (each route audited light and dark; runtime doubles, accepted); browser lane; three-locale visual check; TRACEABILITY + doc deltas.

## 5. Out of scope

No new features, no API/OpenAPI change, no copy changes. Test edits limited to: selectors that assert a Tailwind class purely as a *state proxy* (replace with semantic queries, noted per commit) — the D-139 class assertions are duties, not proxies, and stay satisfied per §3; plus the S1 geometry assertion and S7 route-matrix additions, which strengthen gates.

## 6. Theme preference exception

The dark-mode decision adds one user-visible control: theme select (system/light/dark) in settings, persisted in localStorage only — no server, no contract. Spec delta proposed to the owner: new decision **D-181 "Client-side theme preference (system default, light/dark override), no server persistence"** added to PRODUCT_SPEC before S5 merges; TRACEABILITY row lands with the S5 test (`..._D181`). Infrastructure ships in S1; this section is the phase's single scoped non-styling change.
