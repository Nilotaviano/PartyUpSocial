# Calendar / availability rework — design

**Status:** §§1–7 SHIPPED 2026-08-10 (commits `6527873`..`bd180f7`, lane repaired in `c1ce6bf`),
pushed and gate-green. No new D-numbers: every behavior here implements decisions already Confirmed.
Body-drag to move was descoped by the owner (see §5). §8 (the styling/readability half plus Space
activation) designed 2026-08-12 — the close of the owner's original report.

## 1. Why

Owner report: hard to create/adjust windows, hard to read at a glance, missing views, unpolished.
Investigation found two of those are **spec violations already shipped**, not preferences:

- **Recurring availability cannot be created.** `WeekGridPage.tsx:366`'s `handleCreateBlock`
  always calls `createAvailabilityBlock`; `createAvailabilitySeries` is called from nowhere in the
  UI. Contradicts **D-009** ("one-time *and recurring*") and §7 ("then choosing one-time or weekly
  recurrence").
- **A series cannot be deleted.** `deleteAvailabilitySeries` (`availabilityQueries.ts:114`) is
  likewise uncalled; only single occurrences can be removed. Contradicts **D-025** ("supports
  removing one occurrence or the entire series").

The domain, endpoints and client helpers for both already ship. Only the UI is missing.

Separately fixed ahead of this doc (commit `e1a3dd2`): the block editor was reachable only by
`Enter` — no pointer path — so no mouse or touch user could edit or delete a window at all.

## 2. Scope boundary

**In:** hour-range clamp, sticky header/gutter, availability summary panel, series create + delete,
drag-resize/move, commit step with recurrence choice, undo.

**Out, deliberately:** month view, now-line, zoom, FullCalendar, and any change to the layer/filter
model (**D-111**, **D-117** stand). FullCalendar was evaluated and rejected: PartyUp's grid is a
*painting* surface while FullCalendar is event-display-first, so the hard parts (layered friend
availability with D-076 masking, poll-option instants, series-vs-occurrence editing, D-061/A-081
zone-anchored wall-clock, Tabletop Club tokens) stay custom either way; it adds ~200–300 kB to a
bundle already over the 500 kB warning, and **A-013** discourages the dependency. Its wins —
month/day views and navigation chrome — are out of scope.

## 3. Viewport (fixes "too big a scroll area", "time orientation")

`WeekGrid` renders `startHour = 0` to `endHour = 24` at `SLOT_MINUTES = 15` — **96 rows**, and
`WeekGridPage` overrides neither.

- *Superseded 2026-08-13 (owner ruling, styling overhaul): the grid is no longer an internal
  scrollport — it renders at natural height and the document scrolls.* Default range
  **08:00–24:00** (64 rows), with the "earlier hours" control revealing 00:00–08:00.
- **Sticky day-header row** against the page viewport at ≥`sm`; below `sm` the grid box is a
  horizontal scroll container (372px minimum width vs the 360px floor), which captures sticky
  descendants — the header does not stick there.
- There is no initial scroll: the page loads at the top with the 08:00 window visible.
- Sticky positioning must not disturb the grid's ARIA row/column semantics — the named regression
  risk of this section.

## 4. Availability summary panel (closes D-025; fixes "missing views")

Lists the **viewer's own** availability as rules — recurring as `Tue & Thu · 20:00–23:00`, one-off
as `Sat 16 Aug · 14:00–18:00` — each row with edit and delete. **Deleting a series row deletes the
whole series**, behind a confirm stating it removes all future occurrences. Per-occurrence deletion
stays in the grid, where "this one" is the natural gesture.

Desktop: beside the grid. Mobile: a collapsible section **above** it, so a phone user meets the
summary before the scroll.

**Contract change.** Both `GET /me/availability` and `GET /calendar` return expanded occurrences
carrying `origin.seriesId`, not series definitions, and no `GET /series` endpoint exists. Deriving
rules by grouping a visible week's occurrences would silently omit a series whose `effectiveFrom` is
in the future or whose only occurrence that week is skipped — a summary that hides rules fails at
its one job.

So **`MyAvailabilityLayer` gains a `Series` list, surfaced on `MyAvailabilityLayerResponse`**. That
one addition serves both endpoints: `GetCalendarViewUseCase` *calls* `ReadMyAvailabilityUseCase`
rather than re-implementing it, and the calendar page is the consumer that matters here — it queries
`getCalendarView`, not `getMyAvailability` (`WeekGridPage.tsx:178`). The data is already in hand:
`ReadMyAvailabilityUseCase` holds `data.Series` and passes it to the projection today, so no new
query is added. `AvailabilitySeriesView` already carries `dayOfWeek`, `startTime`,
`durationMinutes`, `zoneId`, `effectiveFrom`, `effectiveUntil`.

No new route, so the op-id and antiforgery inventories are unchanged; `gen:api` must be rerun.

## 5. Direct manipulation (fixes "adjusting an existing window")

Drag the top or bottom edge to resize, snapping to the 15-minute grid.

**Moving a block by dragging its body is out of scope** (owner, 2026-08-10): resize covers the stated
complaint, and a body-drag would be a second drag model competing with the paint gesture on the same
surface, with real touch risk. A block is moved by editing its times.

**Handles appear only on a selected block** (tapped or focused). A bare finger-drag across the grid
must remain a page scroll — permanent edge handles would turn every block into a scroll trap. This
preserves the `touch-action` gating that `e1a3dd2` and P5-TOUCH-01 depend on. Pointer capture holds
the gesture for its duration.

**A resize or move on a recurring occurrence routes through `OccurrenceScopeDialog`** — "only this
occurrence" (the **D-025** default) vs "all future occurrences". Committing a drag straight to the
API would let an edge-nudge silently rewrite a series; this is the section's correctness risk.

Keyboard stays the editor dialog with typed times — §7 requires a non-drag alternative and that one
already works. No arrow-key nudging: a second interaction model, no complaint behind it.

## 6. Commit step (fixes "the recurrence step", "the commit")

Releasing a drag opens a small step: the range as text (`Thu 20:00–23:00`), a **one-time / repeats
weekly** choice, and Save. One step satisfies §7's recurrence choice and doubles as the save
confirmation.

- Weekly → `createAvailabilitySeries`; one-time → `createAvailabilityBlock`.
- Dismissing before Save discards the drag with no API call.
- After Save, a toast with **Undo** deleting what was just created. `components/ui/toast.tsx` ships.

Creation costs one extra step. Accepted by the owner over silent saves that cannot recur, undo, or
be trusted.

## 7. Verification

Vitest: summary panel (including a series absent from the visible week), commit step both branches,
undo, resize/move, and the scope-dialog route on a recurring occurrence. Touch arms carry
`_P5TOUCH01`, continuing the automated cover for the checkpoint deferred to Phase 8. All new strings
in en, pt-BR, es. Backend: the response-shape change needs its inventory assertions extended, never
loosened.

**TRACEABILITY:** D-009 and D-025 move from spec-satisfied-in-domain to spec-satisfied-end-to-end;
both gain client tests.

## 8. Readability + styling pass (closes "hard to read", "unpolished"; adds Space)

Designed 2026-08-12, one adversarial review round applied. The glance-readability problem has
one root cause: `WeekGrid` paints every 15-minute cell independently, so a three-hour window is
twelve identically-styled stacked cells with per-cell borders, not one band. Each of the
~476-672 cells also re-scans every occurrence/merged/friend/session/poll array on every render
(O(cells x entries), unmemoized). The fix stays inside the shipped DOM shape - one
`role="gridcell"` per slot survives, since the roving-tabindex keyboard model, the touch state
machines, and the axe lane all hang off it. **No new fills and no new color tokens**: the
ratified `free`/`busy` + `free-ink`/`busy-ink` pairs (PHASE5_1_DESIGN.md, WeekGrid.tsx:850-853)
stay the only cell backgrounds - the axe lane scans `/calendar` with seeded availability and
`AxeExclusions` may only shrink - so band styling is border/rounding/label work on the existing
fills, never a third fill under `text-free-ink` spans.

- **Bands, not stacks.** Band metadata (start/middle/end per entry span) computes in `WeekGrid`
  via `useMemo` keyed on the entry-array prop identities plus `weekStart`, `startHour`,
  `endHour`; `WeekGridPage` memoizes the derived arrays it passes so those identities are
  stable. Band geometry follows `resizePreview` during a drag (the fill already does,
  WeekGrid.tsx:677-681 - shape must not disagree with fill). Spans **clamp to the visible slot
  range** exactly like the resize handles do (WeekGrid.tsx:695-704): the first/last visible slot
  acts as band start/end for label and rounding, and a clamped edge renders flat (rounded = true
  end, flat = continues past the viewport - the continuation affordance). Start cells render the
  label once; middle/end cells suppress the label and the internal 15-minute horizontal border;
  the 60-minute line keeps a stronger border token than the 15-minute line (the uniform border
  grid is most of the "wall of lines" read). When a block is **selected**, the full-cell resize
  handles occupy the start/end cells (WeekGrid.tsx:789,803), so the label shifts to the second
  cell of a multi-slot band; single-slot bands accept occlusion - the editor dialog is the
  information surface there. The same memo replaces the per-cell linear scans with an indexed
  lookup. No DOM restructuring, no ARIA change; each cell keeps its per-slot accessible name.
- **Precedence, stated.** The **merged** layer owns the cell fill (`busy`, as today - an own
  occurrence never paints separately from its merged projection, WeekGridPage.tsx:304-309).
  Sessions/poll options keep their existing inline spans but render titles at span start only.
  The friend layer keeps the existing single-glyph collapse (WeekGrid.tsx:820-828) - **no
  per-friend hues** (the disc-seed palette is three *fills*, two of which are the grid's own
  backgrounds, and masked friends share one label by design) - but the glyph row renders at
  friend-span start only, with the current collapse as the N>1 cap. Stacking order in a cell
  stays today's DOM order: fill, friend glyph row, session/poll spans, busy dot.
- **Space activation.** The cell's `handleKeyDown` gains a `' '` case that mirrors the Enter
  branch **exactly** (its behavior is state-dependent: start selection / commit / open editor),
  with `preventDefault()` so the page never scrolls - the ARIA grid pattern's expectation.
  `handleResizeKeyDown` additionally swallows Enter and Space (`preventDefault` +
  `stopPropagation`): a native button's Space otherwise synthesizes a click that bubbles into
  the cell's `handleClick` and re-runs activation on top of the new key branch.
- **Out, still:** month view, now-line, zoom, FullCalendar (the §2 boundary stands); any change
  to interaction models beyond Space; new color tokens.

Verification: existing WeekGrid/WeekGridPage suites stay green where behavior is unchanged; new
tests - Space mirrors Enter in each state (`_D071_D132`, the tags the existing keyboard suite
uses), resize-handle Space does not double-activate, band start/middle/end metadata incl. the
visible-range clamp (unit), label renders once per band and shifts when selected, friend glyph
at span start only. Axe lane must stay green. All new strings (if any) in en/pt-BR/es; no new
dependencies (A-013); `gen:api` untouched (no wire change).
