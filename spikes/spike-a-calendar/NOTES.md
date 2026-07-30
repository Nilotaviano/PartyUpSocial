# Spike A — friction log (raw scorecard material)

Honest notes from building both variants, 2026-07-30. Everything below was actually hit, not
speculated, unless marked "expected".

## Ecosystem / setup

- **FullCalendar v7 shipped and npm hands you a broken mix.** `npm install @fullcalendar/react`
  now resolves to the v7 connector (7.0.2, Temporal-polyfill peer dep, different internals) while
  `@fullcalendar/core`/`timegrid`/`interaction` still resolve to 6.1.21. The v7 connector silently
  nests its own core@7 — two FullCalendars in one bundle. Had to pin `@fullcalendar/react@6.1.21`
  explicitly. A Phase 5 adoption decision must pick v6 (mature docs, this spike) vs v7 (rewrite,
  Temporal API) deliberately; this spike evaluates **v6**.
- `npm audit`: 2 high-severity findings, both the react-router RSC-mode CSRF advisory
  (GHSA-qwww-vcr4-c8h2) — irrelevant to this client-only spike (no RSC/server actions), and
  react-router isn't part of what's being scored anyway.
- Build size: total JS bundle 495 KB (151 KB gzip) — react + router + FullCalendar v6. The custom
  grid needs none of the FullCalendar share of that; FullCalendar is the bulk of it.

## FullCalendar variant

What worked with near-zero effort:

- Pointer drag-select (mouse) and touch long-press drag: `selectable` + `slotDuration:"00:15:00"`
  + `selectLongPressDelay:150` and it just works, including the drag mirror. Minutes of work.
- `eventInteractive: true` makes event elements tabbable and Enter fires `eventClick` — the
  keyboard *edit/delete* path (Tab → Enter → dialog) cost nothing.
- `selectAllow` to constrain selection to a single day: one callback.

The keyboard/SR layer — this is where it gets hacky, item by item:

1. **There is no keyboard selection model at all.** Everything below is bolted on beside
   FullCalendar, not integrated into it.
2. **The "cursor" is a lie.** I keep `{day, slot}` React state and visualize it by calling
   `calendar.select()` — i.e. the cursor is FullCalendar's *selection highlight*, not focus. Real
   DOM focus never leaves the wrapper `div`. A screen reader's virtual cursor sees nothing at the
   highlighted slot; the only SR feedback is our own `aria-live` narration. Contrast: the custom
   grid gives the SR a genuinely focused, labelled gridcell at every step.
3. **Programmatic API fires public callbacks.** `calendar.select()` / `unselect()` invoke the same
   `select`/`unselect` callbacks as user gestures, so every programmatic call is wrapped in a
   `suppress` ref flag to stop the cursor from creating blocks in an infinite feedback loop. Ugly
   and fragile — any future code path that forgets the flag reintroduces the loop.
4. **`role="application"` on the wrapper** so arrow keys reach our handler instead of the SR
   virtual cursor. This flips SR users into focus/forms mode inside the calendar — a heavyweight,
   controversial move that a real accessibility review may reject.
5. **Phantom mirror event.** With `selectMirror: true` (wanted for pointer drag), the keyboard
   cursor renders as a fake "event" box showing e.g. "9:45 - 10:00". Visually confusing; turning
   the mirror off degrades pointer drag feedback. Not resolved, just documented.
6. **Cursor/selection state desyncs cosmetically.** Clicking anywhere else unselects (FullCalendar
   behavior), the highlight vanishes, and the cursor only reappears on the next keypress. The
   `unselect` callback also has to guess whether it was the user or us (same suppress flag).
7. **FullCalendar's slot DOM is presentation-only** (`<td>`s, no gridcell semantics, no labels), so
   none of it is reusable for AT; the a11y layer is 100% parallel bookkeeping. If the layer's math
   drifts from FullCalendar's rendering (timezone, first-day, DST), announcements will lie.

Verdict feel: pointer UX is excellent for free; the a11y layer is ~150 lines of side-channel state
that fights the library's design. It works in this spike, but it is scaffolding bolted to a black
box, and every FullCalendar upgrade risks silently breaking it (see: v7 rewrite already out).

## Custom grid variant

What worked:

- Roving tabindex + per-cell `aria-label` ("Tuesday 14:15") is the textbook pattern and fell out
  naturally: focus *is* the cursor, no parallel state, no role=application, SRs announce the cell
  label on every arrow move for free (live region only needed for selection extent + results).
- One Pointer Events code path (`pointerdown`/`setPointerCapture`/`pointermove`) served mouse AND
  touch unchanged — emulated CDP touch drag at 390 px worked first try, no long-press needed.
- Native `<dialog>` gave modality, Escape, and focus-restore-to-invoker for free (after Save,
  focus lands back on the edited block with zero code — the dialog did it).
- Total variant-specific code ≈ 330 lines incl. rendering; zero dependencies beyond React.

Friction / honest gaps:

1. **`touch-action: none` kills touch scrolling on the grid itself.** As specified, any touch on
   the grid starts a selection, so you cannot pan the 1344 px-tall grid by swiping it; scrolling
   works only via the time-gutter/scrollbar. Production needs a modifier: long-press-to-select
   with `touch-action: pan-y` (like FullCalendar), or a dedicated select mode toggle. This is the
   variant's biggest open problem and it is a *design* problem, not a code one.
2. **672 gridcells re-render on every keystroke/drag step.** Fine at spike scale (no perceptible
   lag), but production wants `memo` per row or cell-class computation outside React.
3. **ARIA grid semantics are approximate.** No `aria-rowindex`/`colindex`, no
   columnheader/rowheader elements (day headers are `aria-hidden`; context rides on each cell's
   label), `aria-selected` present only on selected cells, and blocks are overlay `<button>`s that
   are siblings of the grid, not gridcells — an SR walking the grid in browse mode won't encounter
   blocks in place; they are separate tab stops. Screams "works in practice, audit before calling
   it AA".
4. **No drag auto-scroll**: pointer-dragging to the edge of the scroll container doesn't scroll it.
   FullCalendar has this built in.
5. Repeated identical live-region messages need the clear-then-set-after-50 ms hack (shared by
   both variants — `announcer.tsx`).
6. Multi-day selection was deliberately constrained to a single day column (matches the specified
   announcement format); horizontal Shift+Arrow is ignored. A product decision, not a limitation
   of the approach.

Verdict feel: everything the a11y criteria wanted was the *natural* implementation here, not a
bolt-on. The costs are the things calendars are quietly good at: touch scroll vs. select conflict,
auto-scroll, per-cell perf at scale, and (out of spike scope but Phase 5-relevant) all-day rows,
overlap layout, localization of the time axis.

## Test-harness note (not a variant defect)

Synthetic `dispatchEvent` keyboard bursts in one JS task get auto-batched by React 19 — handlers
read stale state and the sequence collapses. Real (trusted, one-per-task) key events behave
correctly; all pass-criteria runs below used trusted Playwright input.
