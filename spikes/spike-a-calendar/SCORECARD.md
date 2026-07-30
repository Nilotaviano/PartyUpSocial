# Spike A scorecard — FullCalendar v6 vs custom grid

Scored 2026-07-30 from the automated pass (Playwright trusted input + CDP touch emulation, results in README.md) and the friction log (NOTES.md). Decision recorded as **A-018** in ARCHITECTURE.md §9.

| Criterion | FullCalendar v6 | Custom grid |
|---|---|---|
| Keyboard-only create/edit/delete | Pass, but via a bolt-on virtual cursor: parallel `{day,slot}` state, `calendar.select()` abused as focus highlight, suppress-flag around every programmatic call. No real DOM focus ever reaches a slot. | Pass, natively: roving tabindex, focus *is* the cursor, Shift+Arrow/Enter/Escape, native `<dialog>` restores focus after edit. |
| Screen-reader semantics | Structural gap: slot DOM is presentation-only `<td>`s; needs `role="application"` (flips SRs into forms mode — likely rejected in a11y review); the only SR feedback is our own live region. | Real focused, labelled gridcells announce on every move; live region only for selection extent + results. ARIA still approximate (no row/colindex, blocks are sibling overlays) — needs audit, but the foundation is right. |
| Touch drag @390px | Pass — built-in long-press drag + mirror, minutes of work. | Pass — one Pointer Events path for mouse+touch. Open problem: `touch-action:none` kills touch-scroll on the grid; production needs long-press-to-select or a mode toggle (design work, not architecture risk). |
| Pointer/mouse UX | Excellent for free: drag mirror, edge auto-scroll, constraint callbacks. | Manual: no drag auto-scroll yet; mirror equivalent trivial. |
| Maintenance risk | High: a11y layer is scaffolding bolted to a black box; any upgrade can silently break it — and v7 (Temporal rewrite) already shipped, npm hands out a broken v6/v7 mix today (react connector had to be pinned). | Low: ~330 lines owned code, zero deps beyond React. Perf wants memoization at scale (672 cells re-render per keystroke). |
| Bundle | Dominates the 495 KB JS / 151 KB gzip bundle. | ≈ React alone. |

## Decision: custom grid

The spike's falsifying question was accessibility, and it discriminated cleanly: everything the a11y criteria demanded was the *natural* implementation in the custom grid and a fragile parallel-bookkeeping layer in FullCalendar. FullCalendar's genuine advantages (pointer polish, auto-scroll, long-press) are bounded, replicable feature work; its a11y gap is structural. WCAG 2.2 AA is product scope (D-139), not polish — the foundation must be the variant where AT semantics are real.

Carried into Phase 5 as known work: long-press-to-select touch mode (scroll conflict), drag auto-scroll, cell memoization, full ARIA grid audit (row/colindex, headers, in-grid block semantics), overlap layout.

## Outstanding confirmation (does not reopen the decision unless it fails)

- Human NVDA pass on the custom grid (README "Verified by" column) — the FC variant needs no further testing.
- Real-device touch feel at 390px.
