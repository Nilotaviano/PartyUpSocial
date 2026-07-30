# Spike A — accessible drag calendar

Decides the Phase 5 calendar foundation (IMPLEMENTATION_PLAN.md Phase 0): week-grid drag-select
availability entry prototyped **both** in FullCalendar (v6) and as a minimal custom CSS grid, so
the two can be scored against falsifiable pass criteria. Throwaway code; friction log in
[NOTES.md](NOTES.md) is the scorecard raw material.

## Run

```sh
npm install
npm run dev        # http://localhost:5178 (strictPort)
```

Routes: [/fullcalendar](http://localhost:5178/fullcalendar) ·
[/customgrid](http://localhost:5178/customgrid). Single current week, 15-minute slots (D-132),
blocks are plain `{day, start, end}` — no recurrence, no persistence (state resets on reload).

`npm run build` type-checks and bundles.

## Controls

Both variants:

- **Pointer:** drag vertically on the grid to select, release to create. FullCalendar on touch:
  long-press (~150 ms) then drag. Custom grid on touch: drag immediately.
- **Keyboard:** Arrows move (PageUp/PageDown = 1 h), **Shift+Up/Down** selects, **Enter** creates,
  **Escape** cancels. Click or Enter on a block opens the edit dialog (start/end selects, Save /
  Delete / Cancel). Custom grid blocks additionally: **E** = edit, **Delete/Backspace** = remove.
- Selection extent and create/edit/delete results are announced via an `aria-live="polite"`
  region.

## Pass criteria

Automated column = Playwright run against the dev server on 2026-07-30 (trusted keyboard input;
touch via CDP `Input.dispatchTouchEvent` in a 390×844 `hasTouch` context). "Verified by" is left
for the human interactive pass — criterion 2 in particular needs a real screen reader (NVDA /
VoiceOver), which automation cannot honestly claim.

| # | Criterion | Variant | Automated self-test | Verified by |
|---|-----------|---------|--------------------|-------------|
| 1 | Create, edit, delete a block keyboard-only | FullCalendar | Pass — created Mon 09:00–10:00 via Shift+Arrows+Enter; Tab→Enter opened dialog; edited to 10:15; deleted via dialog | |
| 1 | Create, edit, delete a block keyboard-only | Custom grid | Pass — created Tue 10:00–11:00; E opened dialog, edited to 11:15; Delete key removed it, focus returned to grid | |
| 2 | SR announcements for selection + create/edit/delete | FullCalendar | Partial — live-region text verified for all five message kinds; no real SR run | |
| 2 | SR announcements for selection + create/edit/delete | Custom grid | Partial — live-region text verified incl. per-cell labels on arrow moves; no real SR run | |
| 3 | Touch-drag creation on 390 px viewport | FullCalendar | Pass (emulated) — long-press drag created Wed 10:00–11:00 | |
| 3 | Touch-drag creation on 390 px viewport | Custom grid | Pass (emulated) — drag created Wed 10:00–11:45; note grid itself can't be touch-scrolled (NOTES.md) | |
