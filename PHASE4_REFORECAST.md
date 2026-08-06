# Phase 4 Launch-Date Reforecast

**Owner:** S6 (`PHASE4_DESIGN.md` §15, `IMPLEMENTATION_PLAN.md:96`'s checkpoint).
**Contract:** cite CI/build/test/browser evidence, not a narrative confidence statement. D-163 bars scope reduction — nothing in this document proposes cutting anything already Confirmed in `PRODUCT_SPEC.md`.

## 1. Baseline

- **Design accepted:** commit `4f2cd81` ("docs: add Phase 4 technical design (accepted after 3 review rounds)"), 2026-08-05T00:51:03-03:00, after 3 adversarial design-review rounds (`.foreman/ledger.md` "Design convergence record").
- **Baseline inspected commit** at design time: `8ee8a25` (`PHASE4_DESIGN.md` header).
- **Original planned size** (`IMPLEMENTATION_PLAN.md`'s pre-delivery "Sizing" table, Phase 4 row): **4–6 weeks**, solo part-time.

## 2. Actual elapsed time, S1–S6

All six slices were implemented, verified, and committed inside a single foreman-orchestrated session on **2026-08-05** — the entire slice program ran in one calendar day, not the 4–6 calendar weeks the original part-time-solo estimate assumed. Every timestamp below is a `git log` commit time on `main`; no timestamp is estimated.

| Slice | First commit | Last commit (gate closed) | Elapsed (first→last) | Rework rounds |
|---|---|---|---|---|
| S1 persistence | `7ffbfd5` 01:39:54 | `315dfe3` 09:55:57 | 8h16m (includes an ~8h overnight gap between `7ffbfd5` 01:39 and the `16b01e5` 09:44 fix — see §3) | 1 (design-erratum write-set fix, did not count against the seat) |
| S2 commands/scopes/notify/handoff | `e85179e` 10:47:04 | `bf1464f` 14:35:40 | 3h48m | 3 verification rounds (FAIL → FAIL → PASS_WITH_NOTES) across 7 commits |
| S3 Hangfire jobs | `8694171` 15:11:45 | `84805e8` 15:58:40 | 0h47m | 1 round (PASS_WITH_NOTES, notes closed same wave) |
| S4 HTTP/OpenAPI/guest security | `e6bcf59` 17:15:03 | `26fc4b1` 19:32:16 | 2h17m | 3 verification rounds (FAIL → FAIL narrow → PASS_WITH_NOTES) across 8 commits and 10 lettered fix waves — the heaviest rework of the phase |
| S5 registered web | `289a4e6` 20:02:13 | `235ba26` 20:42:23 | 0h40m | 2 verification rounds (FAIL → PASS_WITH_NOTES) across 4 commits |
| S6 guest web + closeout | this ticket | (this commit) | — | — |

**Total calendar span, S1 start → S5 close:** `7ffbfd5` 01:39:54 → `235ba26` 20:42:23 = **19h02m**. S6 extends this span; its own end time is the commit timestamp of this slice's closing commit (see the git log at push time — this document does not guess it).

**Inter-slice gaps** (dispatch/verify handoff and lead review, not implementation time): S1→S2 51m, S2→S3 36m, S3→S4 1h16m, S4→S5 30m.

## 3. Focused work time vs. calendar time

The single large gap is inside S1: `7ffbfd5` (01:39:54) to the `16b01e5` fix commit (09:44:39) is 8h05m, consistent with an overnight pause rather than active work (the ledger's T-P4-S1 attempt-2 entry describes same-day resumption, not multi-day elapsed work). Subtracting that gap:

- **Calendar span, S1 start → S5 close:** 19h02m.
- **Overnight-gap-adjusted span:** 19h02m − 8h05m ≈ **10h57m**.

This document does not further decompose "focused work hours" below the gap-adjusted figure: no independent time-tracking log exists beyond commit timestamps, and inferring finer-grained active/idle splits from commit spacing alone would be a guess, not a citation. **Any hour-level figure finer than the gap-adjusted 10h57m is UNKNOWN.**

## 4. Planned vs. actual

| | Planned (original sizing) | Actual |
|---|---|---|
| Elapsed calendar time | 4–6 weeks | 1 calendar day (2026-08-05), S1–S5; S6 same day |
| Basis of the planned figure | solo, part-time human implementation | foreman-orchestrated Claude worker/verifier seats, human-supervised, same session |

**This ratio is not a valid extrapolation input for Phases 5–9.** The original 4–6 week estimate assumed one part-time human writing and testing the code directly. What actually happened was AI-agent implementation (Claude sonnet workers) under adversarial same-family blind verification (Claude opus), with the human as dispatcher/approver — a different production function, not a faster version of the same one. Treating "Phase 4 took 1 day instead of 5 weeks" as a ~35x multiplier and applying it to Phases 5–9 would overstate confidence with false precision. §6 below uses the *rework-round* and *defect-density* evidence from §5 to adjust the **original human-pace estimate**, not to replace it with an agent-pace one, because the human-pace estimate is what `IMPLEMENTATION_PLAN.md`'s existing Phase 5–9 sizing already assumes and what a claimed public launch date must be defensible against if the AI-assisted cadence is not sustained (illness, tooling regression, model/context changes, or a return to solo human implementation for any reason).

## 5. Defects and reruns by slice (verification-round evidence)

Source: `.foreman/ledger.md`, "Implementation program" table, session `2026-08-05`.

| Slice | Verification rounds | Distinct defect classes found | Notable severity |
|---|---|---|---|
| S1 | 1 write-set correction (pre-verification, design erratum) | 1 (test-plan/write-set gap, not code) | LOW — design doc bug, not implementation |
| S2 | 3 (FAIL, FAIL, PASS_WITH_NOTES) | ~9 across two fix waves (lock-order defects, join-request visibility bypass, distinct-signal gaps) | **HIGH** — a join-request write-path visibility/block-boundary bypass (round-1) and an inherited-access regression (round-2) |
| S3 | 1 (PASS_WITH_NOTES, closed same wave) | 3 notes (atomic discovery/recheck, durable attempts, comment fixes) | MEDIUM |
| S4 | 3 (FAIL, FAIL narrow, PASS_WITH_NOTES) | ~15 across 10 lettered fix waves (F1–F9, G1–G6) | **HIGH** — a lost-update race on edit version-checking (F1) and a CI-only regression from a synchronous Hangfire registration call reaching a non-Testing environment |
| S5 | 2 (FAIL, PASS_WITH_NOTES) | 9 (H1–H9) plus 3 notes (N1–N4) | **HIGH** — an A-070 clear-list no-op affecting all three clearable fields (H1) |
| S6 | this ticket's own gate | see this ticket's report | — |

**Reading:** S2 and S4 carried materially more rework than S1/S3/S5 — both own security- or correctness-load-bearing surfaces (the six-step lock order and block-race orderings in S2; the guest bearer-credential package and antiforgery/rate-limit contract in S4). This is the same shape the original plan flagged: `IMPLEMENTATION_PLAN.md`'s sizing note already named "the Phase 4 checkpoint" as a re-forecast point precisely because concurrency- and security-sensitive slices are where solo/part-time estimates are least reliable.

## 6. Gate-pass evidence

- **Unit gate** (`dotnet build PartyUp.sln`; `dotnet test PartyUp.sln --filter "Category!=Integration"`): 0 warnings; 1053 Domain.Tests + 243 Application.Tests + 13 Api.IntegrationTests (non-`Category=Integration` subset) green at HEAD as of this slice; `PartyUp.BrowserTests` is present in the solution and correctly excluded by the filter (0 tests match `Category!=Integration` in that assembly).
- **WSL full suite** (`dotnet test PartyUp.sln` against the Testcontainers/dev-Postgres integration lane): green at 84805e8 (S3 close, 1641 tests) and re-run green through 235ba26 (S5 close, 1053+243+390 = 1686 tests including Api.IntegrationTests). See this ticket's own report for the S6-inclusive run.
- **Browser gate** (`dotnet test tests/PartyUp.BrowserTests`, Windows host, real API + Vite dev server + local dev Postgres): `SessionRegisteredFlowTests` 6/6 green across repeated clean runs. `SessionGuestFlowTests` builds and is correctly excluded from the unit-only filter, but is blocked from running green by a pre-existing `web/vite.config.ts` dev-proxy defect discovered while writing this slice (its `/g` proxy entry's string-prefix match also intercepts `/guest/*` SPA routes, so the guest page 404s in local `dotnet run`+`npm run dev` before React Router ever sees it — reproduced with `curl`, not a Phase 4 application bug). `vite.config.ts` is owned by S4's write set, not S6's; see this ticket's report and `TRACEABILITY.md`'s S6 section for the full reproduction and the one-line fix.
- **CI/CD:** S4's CI regression (Hangfire recurring-job registration reaching a non-Testing environment without a reachable Postgres) was caught by the user reporting a live CI failure on already-pushed commits, fixed in the same slice (`f642293`), and confirmed green; S1–S5 are committed on `main` locally, with S4's fix range (`6bda605..26fc4b1`) pushed to `origin` after the CI regression (`.foreman/ledger.md` T-P4-S4 entry). S6's own push status is out of this ticket's scope (no commit/push instruction was given).

## 7. Remaining phases (5–9): re-forecast

Using the original per-phase sizing (`IMPLEMENTATION_PLAN.md`, pre-Phase-4-delivery "Sizing" table) as the human-pace baseline, adjusted only by what Phase 4's actual defect/rework evidence supports:

| Phase | Original estimate | Re-forecast basis | Adjusted estimate |
|---|---|---|---|
| 5 — Availability + recommendations + calendar | 5–6 wk | Already flagged as a likely overrun risk pre-Phase-4 ("Auth (2), calendar (5), and the Phase 8 remediation pile are the likeliest overruns" — unchanged assumption; Phase 4's own heaviest rework (S2, S4) fell on concurrency/security surfaces, and Phase 5 (calendar/availability) is a comparable concurrency-and-correctness-dense surface). No Phase-4 evidence *reduces* this risk. | **5–7 wk** (widened, not narrowed — same reasoning that flagged it originally, now with S2/S4's rework density as a concrete precedent for "concurrency-sensitive slices run over") |
| 6 — Polls | 3–4 wk | No Phase-4 evidence changes this; polls reuse the guest-link/visibility machinery Phase 4 hardened, which is a mild *de-risking* factor (the guest-credential package, A-064/A-065/A-074, is now built and tested, not net-new for Phase 6). | **3–4 wk** (unchanged; the de-risking is real but not quantifiable enough to narrow the range) |
| 7 — Notification transports + preferences | 2 wk | Unchanged; Phase 4 emitted every in-app notification event this phase transports, so Phase 7's scope is additive (transport wiring), matching the original estimate's premise. | **2 wk** (unchanged) |
| 8 — Compliance, localization, hardening | 4–5 wk | Unchanged high-risk flag from the original plan stands; Phase 4 added ~30 new WCAG/D-139 evidence points across S5/S6 (component-level, not audit-level), which is exactly the kind of partial credit the original estimate already assumed would exist before the Phase 8 audit. | **4–5 wk** (unchanged) |
| 9 — Go live, private beta → public launch | 2 wk+ | Unchanged. | **2 wk+** (unchanged) |

**Total remaining (Phases 5–9), re-forecast: roughly 16–20 weeks (4–5 months) part-time**, versus the original 16–19 weeks. The only material change is widening Phase 5's upper bound by one week, based on S2/S4's concurrency-surface rework density — everything else is carried forward unchanged because Phase 4's evidence neither confirms nor refutes the other phases' original estimates.

## 8. Critical path

Unchanged from the original plan's implicit ordering (Phase 5 depends on Phase 4's session/roster data model; Phase 6 depends on the guest-link package Phase 4 shipped; Phase 7 depends on Phase 4's notification-event emission; Phase 8 depends on all prior UI existing to audit; Phase 9 depends on everything). Phase 4 did not surface a new cross-phase dependency, and did not remove one — `PHASE4_DESIGN.md`'s §12 serialization notes (shared-file ordering within Phase 4) were themselves fully internal to Phase 4 and are closed by S6.

## 9. Launch-date range and assumptions

Given:
- Phases 0–3 already delivered (`TRACEABILITY.md`'s "Phase 3 completion summary" section; Phase 2 completion recorded 2026-08-01 per session progress notes — Phase 3's exact completion date is not separately logged in a document this reforecast can cite, so it is treated as on or before Phase 4's 2026-08-05 design baseline without a more precise date).
- Phase 4 delivered 2026-08-05 (this reforecast's own baseline).
- Remaining Phases 5–9 re-forecast at 16–20 weeks part-time (§7), assuming the **original solo-part-time cadence**, not the single-day AI-assisted cadence Phase 4 actually ran at (§4's explicit caveat).

**Launch-date range: 2026-11-25 through 2026-12-23** (16–20 weeks from the 2026-08-05 Phase 4 baseline), **under the part-time-human-cadence assumption**.

**Assumptions this range depends on:**
1. The remaining phases are implemented at the original part-time-solo cadence the sizing table assumed — if the same AI-assisted foreman/worker workflow that delivered Phase 4 in one day continues to be used for Phases 5–9, this range is almost certainly too pessimistic, but this document declines to forecast an AI-assisted-cadence launch date because Phase 4 is the only phase with that cadence's evidence, and one phase's throughput is not a reliable predictor of five more phases with materially different technical shape (calendar/recurrence logic in Phase 5, payment/compliance-adjacent work in Phase 8).
2. No scope reduction (D-163) — this range assumes every remaining phase ships its full documented scope.
3. Phase 5's calendar/availability engine and Phase 8's compliance/hardening remediation remain the two highest-variance items, consistent with the original plan's own risk flag, now reinforced by Phase 4's S2/S4 rework density as a concrete (if indirect) precedent.
4. No Testcontainers/CI infrastructure regression recurs (S4's CI-only Hangfire-registration regression was closed the same slice; the pattern — code that behaves correctly against locally available infrastructure but fails against CI's absence of a reachable localhost Postgres — is now a known failure class, but is not guaranteed absent in future phases).
