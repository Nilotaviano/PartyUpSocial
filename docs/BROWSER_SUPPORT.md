# Browser support policy (D-138)

Ratified as PHASE8_DESIGN.md §2 **Q3 on 2026-08-12**; closes TRACEABILITY.md's D-138 row.

## Policy

PartyUp supports the **current and previous release of Chrome, Edge, Firefox, and Safari**, desktop and
mobile (D-138). "Current + previous" is not directly expressible with Playwright, which ships one pinned
build per engine, so the decision is discharged by three mechanisms together rather than by a version
matrix in CI:

1. **Evergreen engine lanes.** The open-source engines stand in for the branded browsers they ship
   inside — Chromium for Chrome and Edge, WebKit for Safari, Gecko for Firefox. Each CI lane runs the
   Playwright-pinned build of its engine, which tracks close to current.
2. **A build-target floor.** `web/vite.config.ts`'s `build.target` (`chrome133`, `edge133`,
   `firefox135`, `safari18`) is the syntax/feature floor the production bundle is compiled down to.
   Each name is a **floor**, chosen at or below its family's current-minus-one release — every
   supported browser is newer than its floor. It is dependency-free (esbuild targets; no `browserslist`
   package, no `@vitejs/plugin-legacy`) and is reviewed each phase.
3. **Owner real-device spot checks.** Automation runs headless engines on Linux runners; it does not
   run branded Chrome/Edge/Safari, real iOS or Android, or any assistive technology. Those stay an
   owner duty (see Residual below).

Explicitly **not** in scope: a macOS runner lane, a mobile-Chromium lane. Emulated mobile touch is
already covered by the WebKit pointer lane (`AvailabilityCalendarPointerTests`), and Q3 ruled the
remaining mobile half discharged by this policy plus the owner checks.

## CI lanes

The `browser` job in `.github/workflows/ci.yml` is one job per engine; `PARTYUP_BROWSER_ENGINE` tells
`PlaywrightFixture` which single engine to boot, so each lane installs and caches only its own binary.

| Lane | Engine | Selection | Scope |
| --- | --- | --- | --- |
| `browser (chromium)` | Chromium (default when the variable is unset) | `Category=Integration&Browser!=WebKit` | Every untagged flow class plus the axe audit |
| `browser (webkit)` | WebKit | `Browser=WebKit` trait | Pointer/touch classes, which launch their own WebKit |
| `browser (firefox)` | Firefox | `FullyQualifiedName~…` list | Curated subset of the *same* untagged flow classes |

The firefox lane runs auth (the real register + onboard path every flow test performs), RSVP,
availability-calendar keyboard create/edit/delete + occurrence-scope dialog, poll vote, and the
notification-permission fallback. Running those on a
second engine is **intended two-engine coverage, not an accidental double-run**: the chromium lane's
filter is unchanged, and the `Browser!=` trait exclusion continues to apply only to engine-specific
classes. Curation is per test method, not per class, because each extra registration costs
`PlaywrightFixture`'s 65-second shared-rate-window barrier.

`Engine/BrowserEngineLaneTests` is the liveness guard for all three lanes: it reads the engine back off
the live `IBrowser` (`BrowserType.Name`) and asserts it matches what the environment requested, so a
misspelled variable that silently reran Chromium, or a filter that matched no flow test, fails the lane
instead of passing an empty one. `PlaywrightFixture` likewise throws on an unrecognized engine name
rather than falling back to Chromium.

## Engine divergences found by this lane

**Closed — the one-time-block editor (Phase 8 S7, 2026-08-12).** The lane's first finding was
`AvailabilityCalendarFlowTests.KeyboardOnlyCreateEditDelete_D132` failing on Firefox 151 while
passing on Chromium: after the keyboard paint committed a one-time block and the editor saved a
moved block, `Enter` on the busy gridcell no longer opened "Edit availability". Root-caused to real
app behaviour rather than a Gecko keyboard quirk — `WeekGridPage.tsx` closed the editor on the
mutation's own response without awaiting the calendar refetch, and `BlockEditorDialog`'s
`onCloseAutoFocus` then returned focus to the cell that had opened it, moments before the refetch
repainted the block onto *different* cells. `Enter` on that now-free cell starts painting a new
selection instead (proved by the live region reading "Selection started at 7:00 AM." at the failure
point). Chromium's faster refetch won the race; Firefox lost it under load. Fixed by awaiting the
invalidation before closing the editor (the shipped `createBlock` idiom), so focus is never returned
into a stale grid. The test now runs in the firefox lane and is green on both engines.

**Open — Gecko sub-pixel grid-cell height.** `AvailabilityCalendarFlowTests.GridCellsMeetGeometryFloor_D132`
measures `43.999992px` for a `min-h-11` (44px) cell under Firefox 151 and fails its `>= 44` floor;
Chromium reports exactly 44. This is a float artifact of Gecko's fractional layout, not a target-size
defect (WCAG 2.2 AA's own 2.5.8 minimum is 24×24, and the rendered target is 44px to the viewer), so
the assertion is left un-weakened and the fact stays a chromium-lane fact — the firefox lane selects
the calendar seat per method, not per class. Re-check if Gecko's rounding changes.

## Residual owner duties (not automatable here)

- Branded Chrome, Edge, and Safari spot checks on real hardware, current and previous release.
- Real iOS Safari and Android Chrome checks (the touch lane emulates; it does not run the shipping OS).
- Both are recurring per-release checks, not one-time sign-off, and stay open against D-138.
