# First-login tour: implementation design

Scope = a first-login walkthrough of the app chrome (owner-requested 2026-08-09). Proposes **one new spec decision, D-182** (§5); touches no existing D-number's behavior; anchors to the shipped chrome exactly as Phase 7 left it (`App.tsx` `AuthenticatedShell` nav). Bare `§n` below means a section of this document.

## 1. Behavior (the D-182 contract)

- **Trigger:** the tour auto-opens on `/app` when the shell is mounted (the route already sits behind `RequireOnboarded` — no onboarding predicate of its own), the profile query has **succeeded**, and its `tourCompleted` is false. Pending or errored profile ⇒ no tour (fail-closed; also what keeps every shipped `App.test.tsx` arm — which never mocks `/api/v1/me` — unaffected). Never on any other route; never for guests (no account, no shell).
- **Completion = finishing or skipping.** Both POST the completion (idempotent, first instant wins) and optimistically mark the cached profile completed. There is **no persisted device-local flag** — completion is account-grained, so a second account on the same browser gets its own tour, and a reload mid-tour (never finished, never skipped) legitimately shows it again. **The once-per-page-load bound is an in-memory dismissed ref, not the query cache:** the default `QueryClient` refetches `['my-profile']` on window focus (the repo documents this itself, `NotificationBell.tsx:9-11`), so a failed POST + refocus would re-derive an open tour from data alone; the ref suppresses re-open for the rest of the page load regardless of query state, and the next full load retries. Accepted residual: a user whose POST failed sees the tour once more per page load until one write lands.
- **Replay:** "Replay the tour" on `/settings/account` navigates to `/app` with router state `{ replayTour: true }`, which opens the tour regardless of the flag **and clears the dismissed ref** (without that, a user who skips the auto-tour and SPA-navigates to Replay in the same page load would click a dead button); the overlay consumes the state on open via the router-native `navigate(pathname, { replace: true, state: null })` (a raw `history.replaceState` would clobber react-router's own state bookkeeping) so a reload does not re-trigger. Completion semantics identical (the POST is a no-op for a completed account — first instant wins — and settles a never-completed one).
- **Existing accounts:** the migration backfills `tour_completed_at = now()` for every existing account **whose onboarding is already complete** — accounts still mid-onboarding at ship time keep null and get their tour like any first-login user. The tour is a *first-login* feature, not a launch announcement; completed accounts reach it through Replay. (The S1 dispatch-state backfill precedent: shipping must not spring new UI on the whole install base.)
- **PushEnableCard:** it evaluates on every authenticated page and needs a completed D-147 qualifying action, which a brand-new user lacks — collision with the tour is unlikely; when both do appear, the card sits inert under the dialog scrim. No change to its logic.

## 2. Stops

Nine, in nav order, each one sentence, in a new `tour` i18n namespace (en/pt-BR/es in the same commit; the namespace registers in `src/i18n/index.ts` AND in `i18n.keys.test.ts`'s hard-coded namespace array — without the latter the key-parity gate silently skips the new keys). Anchors are the shipped chrome elements only — the tour never leaves `/app` and never depends on page content below the header.

| # | Anchor (shipped element) | Teaching |
|---|---|---|
| 1 | Home link (`App.tsx:56`) | Dashboard: what needs attention, what's next |
| 2 | Friends | Add friends by code or link; the app is friends-first |
| 3 | Groups (D-198..D-210) | Name a squad and share sessions/polls with everyone in it at once |
| 4 | Sessions | Plan game nights: fixed time, RSVPs, waitlist |
| 5 | Polls | Vote on times; the winner becomes a session |
| 6 | Calendar | Paint when you're free so finding a shared time is easy |
| 7 | Discover (`App.tsx:113-124`) | Find joinable sessions friends have made visible to you (D-149/D-018 visibility — no stranger discovery, D-004) |
| 8 | Notifications bell (`NotificationBell`) | Invites and updates land here; push is opt-in in settings |
| 9 | Account menu trigger (`AccountMenu`'s avatar button) | Settings and sign-out live behind the avatar; the tour is replayable from settings |

D-183 moved this stop's anchor from the settings `NavLink` to the account-menu **trigger**: menu items are absent from the DOM while the menu is closed, so the tour must never depend on an open menu. Step count and the `settings` step id are unchanged.

## 3. Mechanics (web only, no new packages — two components don't justify a dependency)

- **`web/src/features/tour/`**: `TourOverlay` built on the shipped Radix dialog primitive (`components/ui/dialog.tsx`), mounted once in `AuthenticatedShell`, rendering only when `location.pathname === '/app'` and the §1 gate holds. The primitive's dialog semantics and focus trap carry the load, with two **explicit overrides** (Radix defaults differ): `onInteractOutside` prevented (dimmer clicks do nothing — Skip is the explicit exit) and `onCloseAutoFocus` targeted at the Home link (no trigger element exists to restore to); Esc stays at its default (skips). Every step renders `Dialog.Title` ("Step n of 8: {stop name}") + `Dialog.Description` (blurb) — the shipped Title+Description shape (`FriendList.tsx:62-70`, `ReauthPrompt.tsx:167-175`) and what axe's dialog-name rule wants.
- **Anchors:** `data-tour="..."` attributes on the nine chrome elements (`NotificationBell.tsx`'s `NavLink` — the fragment's first child — included; that file joins the write set). A deliberate second selector namespace: `data-testid` stays test-only, tour anchors are product markup.
- **Spotlight:** active anchor scrolled into view (the nav scrolls horizontally on small screens, `App.tsx:64`; `behavior: 'auto'` under `prefers-reduced-motion`, smooth otherwise) and lifted above the scrim (z-index + `outline-focus`-token ring, Tabletop Club tokens). Never color alone: ring + position + card arrow. **AT note:** `aria-modal` makes the spotlit item unreachable to assistive tech while open — by design; `Dialog.Title`/`Description` carry the full teaching, the spotlight is visual reinforcement.
- **Step changes** inside the open dialog announce via the Title/Description content living in an `aria-live="polite"` wrapper (content swap in an open dialog is not re-announced by itself). Spotlight/card movement transitions sit under `motion-safe:` like the primitive's own animation.
- **State:** step index local; `tourCompleted` rides the profile query already fetched on `/app` (no extra request).

## 4. Backend (one field, one operation)

- `users` gains nullable `tour_completed_at` (`timestamptz ↔ Instant`) as a **Domain `User` member** (a profile fact like `ReminderDefault`, not an infrastructure shadow — DOMAIN_MODEL.md updated in the same commit per CLAUDE.md); one migration adds the column and backfills per §1 (onboarded rows only). `MyProfileResponse` gains `tourCompleted` (bool; the column keeps the instant for free).
- **`CompleteTour`**: `POST /api/v1/me/tour-completed` → 204, idempotent, on the shipped `MeEndpoints` group with that group's authorization policy (registered surface only; guest MapGroup untouched, the shipped guest-isolation posture). Antiforgery inventory grows by one route. **Rate limiting: rides the `default` dimension** (300/min, IP-partitioned, `RateLimitPolicies.cs:374-380`) exactly as `PUT /me/reminder-default` actually does — the 33-dimension map is unchanged; a once-per-account idempotent write needs no user partition. Op-id inventory grows by exactly one.
- OpenAPI/mobile contract: field + operation are client-agnostic (no-web-only-conveniences rule — a native app runs the same tour off the same bit); `gen:api` rerun.

## 5. Spec change (proposed, needs ratification with this doc)

> **D-182** — After onboarding, a registered account's first visit to the app home starts a skippable 9-stop tour of the app chrome. Finishing or skipping marks the tour completed for the **account** (any device); it never auto-shows again. The tour is replayable from account settings. Accounts that had **completed onboarding** before the feature ships are treated as completed. | Confirmed
>
> Updated post-ship for D-198..D-210 (friend groups): a "Groups" stop was inserted after "Friends", bringing the count from eight to nine. `AccountMenu` trigger note and every other stop's mechanics are unchanged.

PRODUCT_SPEC.md gains this row (Confirmed on ratification, T2's docs work); TRACEABILITY.md gains the D-182 row citing this feature's tests.

## 6. Test-surface accommodation (the cost the feature imposes on shipped suites)

- **Browser lane:** 14 `RegisterAndOnboardAsync` call sites across 8 test files land fresh accounts on `/app`, where the tour now deterministically auto-opens (fresh account ⇒ profile fetch succeeds with flag false). The helper therefore ends by waiting for the overlay and dismissing it with the shipped click-and-await idiom — `RunAndWaitForResponseAsync` around the Skip click **with the 204 asserted** (`PollVotingFlowTests.cs:61-65`; arming a `WaitForResponse` after the click would race a fast 204) — before returning. A failed POST fails the assert loudly rather than leaking a reopening tour into later steps. No per-test edits.
- **The tour's own browser tests reach the overlay through Replay** (the fixture skip makes auto-open unreachable for lane accounts, by design): register → `/settings/account` → Replay → overlay. The axe arm re-opens via Replay **per theme** — the shipped theme swap reloads the page (`AxeAuditTests.cs:331-339`), which would otherwise audit a closed overlay silently. Costs: the extra registration pays the fixture's 65 s registration pacing (`PlaywrightFixture.cs:102-108`) — accepted; and `AxeAuditTests.cs:86-92`'s "zero register-ip headroom" comment is stale (the lane overrides the limit to 100, `PlaywrightFixture.cs:49`) — T1 corrects it so implementers stop detouring around a limit that isn't there.
- **Vitest:** no shared profile mock exists (each file rolls its own) and none is added: `App.test.tsx` mounts the shell without mocking `/api/v1/me`, so its profile query errors and the §1 fail-closed gate keeps the tour shut — its 8 `/app` arms are unaffected with zero edits. Tour tests mock the profile per-file like every other suite.
- **Axe with the overlay open:** the tour's browser test captures axe from its own UI-state (the `AxeAuditTests.cs:124-138` callback capability, re-opening per theme per the bullet above). The existing `/app` route audit keeps its current meaning (lane accounts complete the tour at registration via the fixture change).

## 7. Slices and gates

| # | Slice | Write set | Gate highlights |
|---|---|---|---|
| T1 | Backend + web + i18n + lane accommodation | Domain `User.TourCompletedAt` + DOMAIN_MODEL.md, migration (column + onboarded-only backfill) + `users` config, `MyProfileResponse.tourCompleted`, `CompleteTour` op + antiforgery/op-id inventories, `web/src/features/tour/**`, `App.tsx` + `NotificationBell.tsx` `data-tour` attributes, `/settings/account` replay link, locales ×3 + `i18n/index.ts` + `i18n.keys.test.ts` namespace registration, `PlaywrightFixture.RegisterAndOnboardAsync` deterministic skip + stale headroom-comment fix, new tour test files (vitest + browser; axe helper visibility widened if reused), `web/src/api/schema.d.ts` (generated), TRACEABILITY | integration: completion idempotency `_D182`, backfill completes onboarded rows and leaves mid-onboarding rows null, guest-surface absence, inventory extensions; vitest: §1 gate incl. pending/errored-profile fail-closed, finish AND skip both POST + dismissed-ref bound under a simulated focus refetch, replay state consumed once, step order incl. Discover, Esc, override behaviors (outside-click inert, close-focus target); browser: keyboard-only full 8-step walk via Replay, axe both themes with per-theme re-open, spotlight scroll-into-view narrow viewport, fixture-skip leaves every shipped lane test green |
| T2 | Closeout | PRODUCT_SPEC D-182 row Confirmed, TRACEABILITY | citation grep; full suites (lead gate) |

**Out of scope (explicitly not designed):** per-tab deep tours, step analytics, tour re-versioning on future features (a new decision if ever wanted), native-app variants.
