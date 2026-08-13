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
availability-calendar keyboard create + occurrence-scope dialog, poll vote, and the
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

## Known engine divergence (open)

`AvailabilityCalendarFlowTests.KeyboardOnlyCreateEditDelete_D132` passes on Chromium and **fails on
Firefox 151**: after the keyboard paint commits a one-time block, focusing that busy gridcell and
pressing `Enter` does not open the "Edit availability" editor (30s timeout, local dev-topology run
2026-08-12). The sibling `OccurrenceScopeDialogIsReachableWithoutAPointer_D143` performs the same
focus + `Enter` on a *recurring-series* cell and passes on Firefox, so the divergence is specific to
the one-time-block editor path. It is a genuine finding of this two-engine coverage, not a lane
defect; the S8 slice's write set excludes `web/src`, so the fix is triaged separately and the D-143
sibling carries the calendar seat in the firefox lane meanwhile.

## Residual owner duties (not automatable here)

- Branded Chrome, Edge, and Safari spot checks on real hardware, current and previous release.
- Real iOS Safari and Android Chrome checks (the touch lane emulates; it does not run the shipping OS).
- Both are recurring per-release checks, not one-time sign-off, and stay open against D-138.
