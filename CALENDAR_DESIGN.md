# Calendar / availability rework — design

**Status:** Approved by owner 2026-08-10. No new D-numbers: every behavior here implements
decisions already Confirmed.

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

- Default range **07:00–24:00** (68 rows), with an "earlier hours" control expanding to the full 24.
  Both props already exist; this is a default plus a toggle.
- **Sticky day-header row** (vertical scroll) **and sticky time gutter** (horizontal scroll). The
  gutter matters most on a phone, where scrolling sideways currently loses the hour labels.
- Initial scroll lands on the earliest of (the viewer's earliest window that week, else **18:00**),
  not midnight.
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

Drag the top or bottom edge to resize, drag the body to move; both snap to the 15-minute grid.

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
