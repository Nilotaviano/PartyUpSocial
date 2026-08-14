# WCAG 2.2 AA audit — Phase 8 S7 (D-139)

What this records is the **manual-check half** of the D-139 audit: the success criteria axe-core
cannot decide. The automated half needs no prose — it is the browser lane itself
(`tests/PartyUp.BrowserTests/Accessibility/`), whose exclusion list is empty and stays empty.

Each row below states what was actually checked and by what, and what is deferred to a human. A
disposition of "deferred" is not a claim of conformance.

## What the automated lane covers

| Surface | Coverage |
| --- | --- |
| Routes | `AuditedRoutes.All` × light/dark, axe at `wcag2a, wcag2aa, wcag21a, wcag21aa, wcag22aa`. S7 added `/polls/:pollId/invite`, `/email-unsubscribe` in both its token and no-token states, and the five token-gated auth routes (Q4's pre-hashed `auth_tokens` row). |
| Interaction states | Steam game-picker popup open over results, the three availability dialogs, a live WeekGrid selection, a focused VotingGrid control, and the tour overlay mid-run — states no initial-render scan reaches. |
| Keyboard / SR behaviour | Real-browser facts, not axe: poll-grid keyboard-only vote + suggest + guest vote with the tally read back as text; WeekGrid create/edit/delete and selection-extent announcements; agenda list semantics. |
| Detection power | `NegativeControl_DeliberatelyViolatingDomIsReportedByAxe_D139` — the harness is proven able to fail. |

`/auth/callback` has **no automated audit at all** and is not silently skipped: it is the external
identity provider's redirect leg, reads state the provider put in the fragment and routes onward, so
there is no fixture-reachable state in which it renders a page of its own. Auditing it needs a real
provider round trip and is an owner manual check.

## Manual-check dispositions

### 2.3.3 / 2.2.2 — prefers-reduced-motion — **checked by tooling, no finding**

Every animation and transition in `web/src` is gated on the user preference: 24 `motion-safe:`
usages and, by grep over `web/src/**/*.tsx`, **zero** ungated `animate-*` or `transition-*`
utilities. Tailwind compiles `motion-safe:` to `@media (prefers-reduced-motion: no-preference)`, and
that media query is present in the built bundle (`web/dist/assets/index-*.css`), so the animations
are opt-in on the preference rather than opt-out. No CSS file declares its own animation outside
that gate.

*Deferred to a human:* nothing for this criterion beyond a spot check that the OS-level setting
visibly takes effect, which is covered by the owner's real-device pass (§4.1).

### 1.4.4 / 1.4.10 — 200% zoom and 400% reflow — **partially checked, mostly deferred**

Checked by tooling: the viewport meta is `width=device-width, initial-scale=1.0` with **no**
`maximum-scale` and **no** `user-scalable=no` anywhere in `web/`, so nothing in the app blocks zoom —
the most common way this criterion is failed outright. Layout is Tailwind flex/grid with `max-w-*`
containers and `sm:` breakpoints throughout, i.e. reflow-shaped rather than fixed-width.

Deferred to a human: whether every surface actually reflows to a single column at 400% without
two-dimensional scrolling. axe cannot decide this, and asserting it from Playwright would mean
pinning pixel geometry per route — a brittle proxy for a judgement about readability, not a proof of
it. The two surfaces most at risk are the week grid (horizontally scrollable by design, which is the
permitted exception for a two-dimensional data view) and the voting grid rows.

**This folds in the Phase 3 deferred 320px/200% pass**, which stays deferred with it — the same
check, the same judgement, the same owner.

### 1.4.12 — text spacing — **deferred**

Not checked. Requires applying the criterion's spacing overrides (line-height 1.5×, paragraph
spacing 2×, letter spacing 0.12em, word spacing 0.16em) and confirming no content is clipped or
overlapped. There is no tooling in this repo that decides it, and no assertion was written that
would pretend to.

### 1.4.1 — use of colour — **checked, no finding**

Not strictly a manual-only criterion, but axe does not decide it, so it is checked by named tests
instead: the voting grid pairs every fill with a glyph and a value in the accessible name; the week
grid's layer legend carries text labels with `aria-hidden` swatches
(`lovable/CalendarShell.test.tsx`'s `codes every calendar layer by text, never by its colour swatch
alone_D139`); the session roster and poll status use text, not colour alone.

## Findings this slice found and fixed

- **`color-contrast` 1.94:1, tour overlay mid-run (light).** The spotlit anchor is lifted above the
  scrim by `z-index`, but a nav link carries no background of its own, so the scrim-darkened page
  showed through its own text. Fixed with an opaque `::before` backdrop in `TourOverlay.tsx`'s
  spotlight class list — a pseudo-element rather than a `bg-*` utility so an active nav link keeps
  its own `bg-primary` fill. Re-scanned clean in both themes.
- **Keyboard focus returned into a stale grid (Firefox-exposed, D-132/D-138).** Recorded in
  `docs/BROWSER_SUPPORT.md`; fixed in `WeekGridPage.tsx`.

No finding was added to `AxeExclusions` — the list is empty and A-082 lets it only shrink.

## Open owner checkpoint (cannot be closed by code)

Per PHASE8_DESIGN.md §4.1, and recorded against D-139 in TRACEABILITY.md:

1. **NVDA** (or equivalent) pass over the week grid and the poll grid. Automation asserts the ARIA
   and live-region *text*; it does not run a screen reader, so nothing here proves what a real one
   announces.
2. **Real-device touch** pass on the week grid (P5-TOUCH-01) and the poll grid. The WebKit pointer
   lane emulates touch; it does not run the shipping OS.
3. **400% reflow, 200% zoom, 320px and text-spacing** passes, per the dispositions above.
4. `/auth/callback` manual audit behind a real external-auth round trip.
