# Game Session Scheduler — Implementation Plan

**Status:** v1 — adversarially reviewed
**Basis:** PRODUCT_SPEC.md (D-001..D-180) and the agreed stack.
**Constraint honored:** D-163 — all MVP features ship together in one public launch; milestones below are private/incremental builds toward that single launch.

## Stack (decided)

- ASP.NET Core (.NET 10) REST API, API-first, OpenAPI-documented, versioned (`/api/v1`).
- **Auth: ASP.NET Identity with cookie auth (BFF pattern) for the web client.** No OpenIddict/OIDC server in the MVP — one first-party client doesn't need one and it costs weeks of solo plumbing. An OIDC/token layer is added in the mobile phase; Identity underneath makes that additive, not a rework.
- PostgreSQL + EF Core + Npgsql + Npgsql.NodaTime; NodaTime everywhere in domain code.
- Hangfire (Hangfire.PostgreSql) for scheduled work, in-process.
- Web Push via VAPID behind `INotificationDispatcher`; iOS Safari push not pursued (D-175).
- React + TypeScript + Vite, TanStack Query, Tailwind + Radix/shadcn, react-i18next (en, pt-BR, es).
- Single VM (Hetzner/DO), Docker Compose: Caddy (auto-TLS) + app + Postgres; nightly `pg_dump` offsite with **30-day retention** (bounded retention is a deletion-compliance requirement, not just hygiene).

## Architecture principles

1. **API-first.** The web client is client #1; nothing session-critical lives only in the frontend.
2. **Domain core as pure code.** Session/poll/waitlist state machines are plain C# with no EF/HTTP dependencies, exhaustively unit-tested against the spec's decision log.
3. **Materialized status + sweeper.** Session/poll status is a stored column that queries trust. A frequent Hangfire sweeper (≈1 min) applies time-based transitions (Confirmed→Completed, →Expired, poll close/expiry) and emits their notifications. One source of truth; no per-query timestamp predicates re-deriving state. Domain tests and the sweeper share the same transition code.
4. **Visibility as one engine.** Session visibility (D-018, D-019, D-047, blocking, Anonymous User) is a single query/service with its own test suite, not per-endpoint ad-hoc filters.
5. **Notifications are part of every feature, not a later phase.** Domain events, recipient computation (visibility-scoped, guest-excluded D-167), and in-app notification records ship with the feature that raises them. Later phases add transports (Web Push) — never emission points.
6. **Recipient-locale rendering.** Notification and email text renders in the *recipient's* locale (en/pt-BR/es), templated from day one (D-060, D-146).
7. **Identity tombstones + render-time masking.** Deleted users become tombstone rows (PII nulled, FK-stable) so participation history survives (D-092). Blocking-pair anonymization (D-076/D-086) is render-time masking in one identity-presentation service. Two mechanisms, one service, domain-tested.

## Solution layout

```
/src
  Gss.Domain          — entities, state machines, visibility rules, overlap engine (pure)
  Gss.Application     — use cases, domain events, notification recipients, authorization
  Gss.Infrastructure  — EF Core, Npgsql, Identity, Hangfire, Web Push, Steam, email
  Gss.Api             — controllers/minimal APIs, OpenAPI, versioning
/web                  — React app
/deploy               — compose files, Caddy config, backup + restore runbooks
/tests                — Domain.Tests, Application.Tests, Api.IntegrationTests (Testcontainers)
```

## Phase 0 — Walking skeleton + risk spikes

Goal: CI green and the highest-risk unknowns de-risked before feature work. The deploy pipeline is authored here but not executed — no VM exists until development is done (its go-live tasks live in Phase 9).

- Repo scaffold, CI (build + test), CD authored and gated off (GitHub Actions → GHCR → SSH compose pull; fires only when `DEPLOY_ENABLED` + VM secrets exist — Phase 9), Caddy TLS config, health endpoint, Postgres compose, one EF migration applied on startup, Data Protection keys volume config.
- Backups authored: nightly pg_dump sidecar with 30-day retention purge, backup + restore runbooks (restore includes re-applying account deletions newer than the snapshot). Offsite target and the tested restore are Phase 9 go-live tasks.
- **Spike A — Accessible drag calendar.** Prototype week-grid drag-select in FullCalendar *and* a minimal custom grid. Pass criteria (falsifiable): create/edit/delete a block keyboard-only; screen-reader announcements for selection and result; touch-drag on a 390px viewport. Scorecard picks the foundation.
- **Spike B — Steam only.** OpenID 2.0 login + GetFriendList/GetPlayerSummaries + app-catalog access. Must test a **private-friends-list profile** (Steam's default), since empty imports will be common. PS/Xbox catalogs are *not* spiked — no viable public APIs; they launch as free text (D-176).
- **Spike C — Web Push.** VAPID subscribe + deliver on Chrome/Firefox/Android Chrome; service worker skeleton; permission-prompt flow per D-159.

Exit criteria: CI green on `main`; spike scorecards recorded as decisions.

## Phase 1 — Domain core (no UI)

The hardest 20% of the spec, built as pure code with tests before any endpoint exists.

- Entities: User (incl. tombstone form), Friendship, Block, Session, Participation (status × waitlist), GuestIdentity/GuestLink, Poll, PollOption, PollVote, AvailabilityBlock (one-time + weekly), Notification, PushSubscription.
- **Session state machine:** Pending/Going/Maybe/Declined; Confirmed on min Going (D-053); Going-waitlist grace (D-074); capacity semantics Going+Maybe (D-084, D-091, D-121); start-time reset (D-063); post-start rules (D-081, D-089, D-106, D-168); Completed/Expired/Canceled (D-056, D-164); join requests (D-098, D-173).
- **Poll state machine:** deadlines, 12-option cap with rejection error (D-080, D-171), suggestion flow, finalization mapping + capacity allocation by first-response timestamp (D-119, D-172), late finalization (D-153), 48h expiry (D-088, D-170).
- **Visibility engine:** inherited visibility set (D-018), privacy flip retention (D-043, D-047), blocking effects (D-065, D-076, D-077, D-086), guest exclusion from graph (§5). Identity-masking service for both tombstones and blocked pairs.
- **Availability/overlap engine:** block merge (D-101), weekly recurrence anchored to owner zone with DST wall-clock preservation (§8.5, D-061), overlap ranking (D-052), 7-day default window (D-108).
- Sweeper transition functions (principle 3) live here, shared by jobs and tests.
- Time handling: NodaTime `Instant` for fixed things; `(IsoDayOfWeek, LocalTime, Period, DateTimeZone)` for weekly blocks; occurrence expansion service.

Exit criteria: every **behavior-bearing** Confirmed decision (D-053..D-180) has at least one domain test naming its ID; UI-only decisions (e.g., D-103, D-110, D-135) tracked in a traceability table and verified in their UI phase instead.

## Phase 2 — Accounts, auth, onboarding

- Identity, cookie-based: email+password, passwordless email (magic link), Google, Steam OpenID 2.0 (D-067). Sign in with Apple is deferred to the mobile phase per D-067 — the paid Apple Developer account isn't opened until iOS publish. No token server; session cookie + antiforgery for the SPA (BFF-lite).
- Transactional email provider selected; sending domain is **mail.partyup.social** (subdomain isolates root-domain reputation); **SPF/DKIM/DMARC configured and inbox-tested** — magic-link auth is dead if mail lands in spam.
- Onboarding per D-127: age gate → auth → username → display name → avatar (emoji or platform) → time zone → optional linking → friends.
- Username rules: uniqueness, case-insensitivity, 30-day change cooldown + 30-day reservation (D-128, D-145); stable friend code generation.
- Auth-method linking; last-method unlink error (D-137); email recovery (D-067, D-078).
- Profile constraints + basic profanity filter: normalized multi-language wordlist (en/pt-BR/es), accepted as imperfect — it exists chiefly for store review (D-096/D-097); timebox it.
- **Account deletion skeleton only:** in-app request plus the functional public request/confirmation pages → 7-day cancelable grace, sign-out-everywhere, login-cancels (D-072, D-148, D-158, D-165). Per-entity deletion effects (friendships, sessions, polls, history anonymization — D-092) are added by the phase that owns each entity; Phase 8 verifies completeness and surfaces the public route in the footer/store listing.
- **Admin foundations (D-177, D-180):** seeded admin role membership, `BannedAt` on User, ban/unban commands — ban reuses the D-165 security-stamp sign-out, gates login, and joins the tombstone/onboarding lookup-exclusion predicates; the optional content-removal path reuses the deletion executor's effect steps without the tombstone. Admin UI waits for Phase 8.
- First React slices: onboarding, profile, settings; API client generated from OpenAPI; i18n plumbing in from the start, including email templates (principle 6).

## Phase 3 — Friends and blocking

- **Delivered:** request discovery/actions through profile links, canonical friend codes, client-rendered QR, and API-authoritative share URLs; pre-acceptance profiles and pending cards expose username only (D-031/D-068/D-144). Server-validated `/u/{username}` and `/add/{code}` continuations survive registration email links, Google/Steam callbacks, incomplete onboarding, and activation into the final optional friends step.
- **Delivered:** friend/block persistence, API and UI; outbound block management from at-block username snapshots; mutual fail-closed lookup; friendship removal; bilateral deletion cleanup; and the always-invoked locked-scope executor seam (D-065/D-076/D-077/D-086). Phase 3 has empty session/poll scopes, so real content effects and masked roster rendering remain Phase 4/6 work.
- **Delivered:** explicit-only Steam friend-list import with private/empty/no-match/unconfigured/upstream states, match suggestions, importer-owned invite links, lock-time identity revalidation, and no later-registration/link correlation (D-049).
- **Delivered:** friend-request and first-observation Steam-match notification rows commit transactionally (D-105). Notification center, unread state, push, and preferences remain Phase 7. Blocking itself emits no notification or cause/actor; required derived session/poll state notifications survive when later phases materialize them with masking.
- **Delivered:** grace expiry removes Pending/Accepted friendships, blocks in both directions, and notifications addressed to the deleted user while preserving actor-only rows for tombstone masking (D-092/D-148).

## Phase 4 — Fixed-time sessions end-to-end (delivered)

Phase 4 shipped end-to-end across six slices (S1–S6), documented in `PHASE4_DESIGN.md` and reconciled in `TRACEABILITY.md`. `PHASE4_REFORECAST.md` records the §15 launch-date checkpoint this section used to promise; the checkpoint has been taken, not merely planned.

- **Persistence (S1):** one migration creates every Phase 4 table, including the reminder handoff and the notifications delta; `SELECT … FOR NO KEY UPDATE` parent locks precede every occupancy read; multi-row locks are database-ordered (A-068); `IUnitOfWork` retries `40001`/`40P01` a bounded number of times (A-069).
- **Commands, real scopes, notifications, reminder handoff (S2):** create/edit apply the D-129/D-130/D-141/D-142 defaults and bounds; Steam catalog-or-free-text and PS/Xbox/N-A free text (D-040/D-041/D-058/D-176); every invite/join/approval/promotion path proves `P4-LK-01`'s six-step lock order and both block-race orderings (attachment-first and block-first); in-app notification records commit in the same transaction as the state change (A-016), with block-derived events carrying no block actor/cause and blocked-pair masking (D-105); reminder schedule/cancel changes persist through the same transactional handoff, not a fire-and-forget enqueue.
- **Jobs (S3):** a Hangfire due-sweep and reminder-due job apply the shared, idempotent materialized-status transition functions on a real repeating schedule; duplicate delivery is suppressed with `ON CONFLICT DO NOTHING`; a fifth failed attempt dead-letters the handoff (A-066).
- **HTTP/OpenAPI/guest security (S4):** the full session and guest-link surface is live under `/api/v1`, with exact operation ids, closed problem-code mapping, and antiforgery on every unsafe route. The guest bearer-link package is delivered as designed: `GET /g/{token}` is the sole token-bearing URL, makes no domain write, and exchanges into a path-scoped, `HttpOnly`, `SameSite=Lax` cookie (A-064/A-065); the token never appears in `Location`, Referer, Serilog, or Caddy access logs (A-044); revocation is immediate on organizer removal and on session cancellation (D-044/D-174); multiple links and multiple device credentials for one link remain independently usable with last-write-wins (D-115/D-126); rate limiting partitions by the guest-link route value with an IP fallback (D-150/A-073).
- **Registered web (S5):** create/edit/invite/detail/list screens run entirely on the generated `openapi-fetch` client; the A-070 clear-list convention covers max capacity, notes, and game; dirty-form discard uses a focused, accessible confirmation dialog (D-152/D-162); upcoming badges in-progress sessions and history holds only the Completed final roster with masked-guest rendering (D-076/D-095/D-114); all three locales (en, pt-BR, es) ship with full key parity and one message per documented problem code (D-060/D-146); keyboard/live-region/focus duties extend the existing D-139 evidence row.
- **Guest web and closeout (S6):** `GuestSessionPage.tsx` is the single component behind the tokenless `/guest/:guestLinkId` and `/guest/invalid` routes, rendering the live claim/respond view or the canceled/revoked/ended terminal notice from the predeclared `guest.*` locale keys; times render in the browser-detected zone only (D-169). A real `tests/PartyUp.BrowserTests` Playwright project proves the registered flow end to end against a real running API and Postgres (create → invite → respond → approve → promote → cancel, the dirty-form dialog, keyboard-only RSVP, live status text, browser-zone rendering, all three locales) and the guest flow (302/cookie/tokenless exchange, claim needing only a display name, rename, two-device last-write-wins, organizer removal revoking the link, cancellation revoking it) — 12/12 green across repeated runs. `web/vite.config.ts`'s `/g` proxy entry uses a `^/g/` regex so it no longer intercepts `/guest/*` SPA routes; see `TRACEABILITY.md`'s S6 section for detail.
- Deletion effects: future-session participation removal, owned-session cancellation, history anonymization via tombstone (D-092) — proved in S2.

**Phase 4 checkpoint taken:** see `PHASE4_REFORECAST.md` for the baseline date, elapsed calendar/work time, gate-pass dates, defects/reruns by slice, and the resulting Phase 5–9 estimate and launch-date range. No scope was reduced (D-163).

## Phase 5 — Availability + recommendations + calendar (delivered)

Phase 5 shipped end-to-end across eight slices (S1-S8), documented in `PHASE5_DESIGN.md` and reconciled in `TRACEABILITY.md`.

- **Browser + axe lane (S1):** the Playwright lane runs on CI's built topology, gated by a dedicated `browser` job's own solution build and SPA placement; axe (`Deque.AxeCore.Playwright`, WCAG 2.2 A/AA tags) ran over every Phase 4 route at S1 close and recorded zero findings, so the exclusion list stayed empty through Phase 5 close (`P5-AXE-02`, discharged with nothing to dispose); a narrow WebKit pointer step runs alongside the Chromium flow+axe step, both filtered so no class runs twice.
- **Availability persistence, commands, zone migration (S2-S3):** one-time blocks and weekly series on the 15-minute grid (D-071, D-132, D-133), occurrence-scoped edits (only-this / this-and-future, D-134/D-143), a time-zone change migrates every series' `ZoneId` inside the profile's own transaction, preserving local clock time and touching no block (D-061); every availability write locks the owner's row first, then the touched tables in one Postgres-imposed order per table (A-075); deletion effects remove availability through the shared executor.
- **Read stack (S4):** overlap search and the top-5 suggestion ranking (D-035, D-052, D-108, D-110, D-116) never disclose a per-id resolution of a client-supplied friend selection (A-078); the calendar's four declared layers, Discover (`scope=discover`, A-077 (c)/(d), never an already-attached session), and the actionable inbox (`P5-HOME-01`) all reuse the one session-visibility SQL fragment (A-077); blocked-pair rosters are masked server-side (D-076) closing the Phase 4 gap.
- **HTTP/OpenAPI (S5):** the full availability/calendar/Home/Discover surface ships under `/api/v1` with exact operation ids and a closed problem-code mapping (`P5-HTTP-01`); `npm run gen:api` runs last, after the A-079 payload budget is measured and held.
- **Week grid, agenda, layers, dirty guard (S6):** a custom keyboard-and-pointer week grid (`role="grid"`, roving tabindex, live-region selection announcements) proves keyboard-only create/edit/delete and the occurrence scope dialog reachable without a pointer (`P5-CAL-01`); the WebKit pointer lane proves long-press selection, drag auto-scroll, and 15-minute snapping under real touch pointer events (`P5-TOUCH-01`, automated half); the dirty-form guard gains router interception alongside its existing `beforeunload` half.
- **Find-a-time, Home, Discover (S7):** Home renders D-103's actionable-inbox-first region order, wired to the shipped Phase 4 respond/approve/reject mutations, then the declared-empty polls band, upcoming, Find-a-time + top-5 suggestions, and the discoverable region; Discover ships on all three D-149 surfaces, every row linking to the shipped session detail route (D-114).
- **Closeout (S8):** a real-browser Discover→detail→join-request proof; blocked-pair-masking and ordinal-assignment carries closed; `P5-AXE-02` discharged in full (zero findings, zero exclusions); `P5-TOUCH-01`'s human real-device/NVDA checkpoint re-deferred to Phase 8 by explicit owner decision, 2026-08-07 — the automated WebKit lane stands as Phase 5's interim evidence; every `TRACEABILITY.md` row closed to a named test, none reading `Client | 5`.
- Prefill session (D-102 session arm) — shipped in Phase 5 (A-083, unconditional); see the Phase 6 row below for the poll arm (§1.4 decision 1 of `PHASE5_DESIGN.md`, a relocation not a reduction, D-163).

## Phase 6 — Polls

- Poll creation (full details + 2..12 options, deadlines), voting grid (options × Available/Maybe/Unavailable — **second a11y hotspot; explicit keyboard/SR task**), suggestions, option deletion, deadline close, organizer selection, expiry, finalization → session with capacity allocation (whole §7.3/8.1 poll block), sweeper jobs for deadline/expiry.
- Prefill poll (D-102, poll arm — the session arm shipped in Phase 5, A-083; relocated here by owner decision, a relocation not a reduction, D-163).
- Poll join requests during Polling (D-118), guest voting, private guest-only polls (D-125). Registered-user attachment commands inherit `P3-LK-01` and cannot close until block-versus-join/invite orderings plus parent-before-occupancy reads are proven.
- Poll notification events complete (D-120, D-170), including the poll extension of masked block-derived state materialization with no block cause/actor; deletion effect added: owned-poll expiry (D-092).

## Phase 7 — Notification transports + preferences

Transactional in-app rows already exist at each owning mutation. This phase adds read/unread UX, preferences, and transports; it does not reinterpret D-105 silence or disclose block cause/actor:

- Web Push delivery for the full §8.4 list; contextual enable prompt (D-147, D-159); subscription lifecycle.
- **Idempotent dispatch:** send-log keyed on (event, recipient, channel) checked before delivery — Hangfire is at-least-once and deploys kill mid-flight jobs; duplicates must be swallowed, late delivery accepted.
- Per-session mute (D-090, D-113); reminder preference Off/15m/30m/1h/3h/12h/24h, default 1h (D-082); reminder delivery job.
- Notification center UI + unread badge.

## Phase 8 — Compliance, localization, hardening

- Translation pass: pt-BR + es for UI, **notifications, and emails**; locale formatting audit (D-060, D-146).
- WCAG 2.2 AA audit across all screens; remediation (D-139). Calendar and poll grid get dedicated SR/keyboard sweeps.
- **Cross-browser matrix pass:** current + previous Chrome, Edge, Firefox, Safari, incl. mobile variants (D-138).
- Legal documents as a real work item, started during Phase 7: privacy policy, ToS, community standards — three locales, LGPD-aware (§8.6); footer/store-listing surfacing for Phase 2's functional public deletion-request route (D-072); compliance re-verification of the shipped platform unlink + data-revocation flow.
- **Deletion completeness verification:** end-to-end test that grace expiry produces every D-092 effect and backups age out per retention policy.
- Minimal first-party analytics events (D-151, D-161), and the **admin area** consuming them: usage dashboard (D-178), user list with stats + ban/unban UI incl. the optional content-removal action (D-179, D-180) — role-gated under `/api/v1/admin`.
- Security pass: authz review of every endpoint against the visibility engine, guest-link handling re-check, OWASP basics, dependency audit.
- Load sanity on the VM (k6: RSVP burst, overlap calc with many friends/blocks, sweeper under load).

## Visual design pass — owner-sequenced, independent of Phases 4–8

- Build and apply the app-wide palette, typography, spacing, motion, and component token system from the PartyUp thesis and owner-ratified Rallly/Luma/Sleeper references recorded in `PHASE3_DESIGN.md` §19.
- Preserve the shipped Phase 3 constraints until migration: request cards remain username-only, accepted friends stay avatar-forward, QR/share remains the calm signature surface, masking looks ordinary, and WCAG 2.2 AA/reduced-motion/i18n remain the floor.

---

## Phase 9 — Go live, private beta → public launch

- **Go live, before the first beta invite.** Done 2026-07-30 (early, during Phase 1): Hetzner VM provisioned (Ubuntu 26.04, Docker + compose, ufw 80/443 tcp + 443/udp, fail2ban, SSH hardened, 2G swap, `partyup` deploy user, `/opt/partyup` with deploy files + `.env`), DNS A record for partyup.social, GHCR login on the VM, repo secrets `VM_HOST`/`VM_USER`/`VM_SSH_KEY`, `DEPLOY_ENABLED=true`. First `main` auto-deploy over TLS verified 2026-07-30 (CI+CD green, `https://partyup.social/health` → 200). Still open: offsite backup target (rclone) live, restore drill against a real dump.
- Private testing with the real friend group (allowed by D-163) behind an invite switch; public registration off until launch.
- Bug triage, restore drill #2, uptime monitor, log review.
- Public v1: everything labeled initial release, at once (D-163).

## Cross-cutting rules

- Every phase ships its UI; no backend-only phases after Phase 1.
- Integration tests against real Postgres via Testcontainers; state machines stay in domain unit tests.
- OpenAPI is the mobile contract: no web-client-only conveniences in endpoints.
- Migrations forward-only from Phase 2 on.
- Decision-log traceability table maintained: every D-number → phase + test/verification.

## Deferred by design (mobile phase, later)

OIDC/token auth layer for native clients, FCM/APNs channel, store age-signal APIs (D-093), report/moderation tooling (D-097), store policy recheck (§8.6), Steam catalog sync details (§8.2 TBD), Apple Developer account + Sign in with Apple (account opened only at iOS publish; App Store guideline 4.8 makes SIWA mandatory once Google login ships in the app, so it lands with the mobile release).

## Sizing (solo, part-time — honest version)

| Phase | Size |
|---|---|
| 0 | 2–3 wk |
| 1 | 3–4 wk |
| 2 | 6–8 wk |
| 3 | 2 wk |
| 4 | 4–6 wk |
| 5 | 5–6 wk |
| 6 | 3–4 wk |
| 7 | 2 wk |
| 8 | 4–5 wk |
| 9 | 2 wk+ |

**Total: roughly 9–14 months part-time.** D-163 forbids scope cuts, so the levers are: the Phase 4 checkpoint (re-forecast), Spike A choosing the cheaper calendar foundation early, and keeping Phase 2 free of token-server scope. Auth (2), calendar (5), and the Phase 8 remediation pile are the likeliest overruns.
