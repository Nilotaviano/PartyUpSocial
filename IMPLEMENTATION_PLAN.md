# Game Session Scheduler — Implementation Plan

**Status:** v1 — adversarially reviewed
**Basis:** PRODUCT_SPEC.md (D-001..D-176) and the agreed stack.
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

Exit criteria: every **behavior-bearing** Confirmed decision (D-053..D-176) has at least one domain test naming its ID; UI-only decisions (e.g., D-103, D-110, D-135) tracked in a traceability table and verified in their UI phase instead.

## Phase 2 — Accounts, auth, onboarding

- Identity, cookie-based: email+password, passwordless email (magic link), Google, Steam OpenID 2.0 (D-067). Sign in with Apple is deferred to the mobile phase per D-067 — the paid Apple Developer account isn't opened until iOS publish. No token server; session cookie + antiforgery for the SPA (BFF-lite).
- Transactional email provider selected; sending domain is **mail.partyup.social** (subdomain isolates root-domain reputation); **SPF/DKIM/DMARC configured and inbox-tested** — magic-link auth is dead if mail lands in spam.
- Onboarding per D-127: age gate → auth → username → display name → avatar (emoji or platform) → time zone → optional linking → friends.
- Username rules: uniqueness, case-insensitivity, 30-day change cooldown + 30-day reservation (D-128, D-145); stable friend code generation.
- Auth-method linking; last-method unlink error (D-137); email recovery (D-067, D-078).
- Profile constraints + basic profanity filter: normalized multi-language wordlist (en/pt-BR/es), accepted as imperfect — it exists chiefly for store review (D-096/D-097); timebox it.
- **Account deletion skeleton only:** request → 7-day cancelable grace, sign-out-everywhere, login-cancels (D-148, D-158, D-165). Per-entity deletion effects (friendships, sessions, polls, history anonymization — D-092) are added by the phase that owns each entity; Phase 8 verifies completeness.
- First React slices: onboarding, profile, settings; API client generated from OpenAPI; i18n plumbing in from the start, including email templates (principle 6).

## Phase 3 — Friends and blocking

- Friend requests via profile link, friend code, QR (client-rendered friend code, D-144); pre-acceptance profile = username only (D-068).
- Blocking end-to-end: mutual invisibility, friendship removal, session effects, masked rendering (D-065, D-076, D-077, D-086).
- Steam friend-list import (from Spike B): match suggestions + invite links for unmatched (D-049). **Empty-import is a first-class UX path** — explain Steam privacy setting, offer invite links — not an error state.
- Friend-request + Steam-match notifications (D-105): domain events + in-app records live now (principle 5).
- Deletion effect added: friendship removal at grace expiry.

## Phase 4 — Fixed-time sessions end-to-end

- Create/edit per §8.1 defaults and constraints (D-129, D-130, D-141, D-142); platform + game: Steam catalog-or-free-text, PS/Xbox/N-A free text (D-040, D-041, D-058, D-176).
- Invitations, RSVP, join requests + approval, waitlist + manual promotion, confirmation transitions — domain core already tested; this phase wires HTTP + UI + sweeper jobs.
- **Notification emission complete for all §8.4 session events** (in-app channel live; push transport arrives Phase 7). Includes edit-rule side effects (D-122, D-123) and reminder *scheduling/rescheduling* bookkeeping on RSVP and time changes.
- **Guest links as bearer credentials — security package:** ≥128-bit random tokens; first visit exchanges URL token for a guest session cookie (token not re-sent on navigation); `Referrer-Policy: no-referrer`; Caddy access-log scrubbing for guest paths; GET never mutates (link-preview bots must not consume or alter anything); rate limiting (D-150); revocation (D-044, D-174); last-write-wins multi-device (D-126); browser-detected timezone (D-169).
- Session detail, roster, edit flows; dirty-form discard confirmation (D-162); upcoming list; Completed history with final roster (D-095).
- Deletion effects added: future-session participation removal, owned-session cancellation, history anonymization via tombstone (D-092).

**Checkpoint after Phase 4: re-forecast the launch date** against actuals. This is the honesty gate D-163 otherwise removes.

## Phase 5 — Availability + recommendations + calendar

- Availability CRUD on the Spike-A foundation: drag + keyboard entry, 15-min increments, one-time/weekly, occurrence vs series semantics (D-132, D-133, D-134, D-143).
- Time-zone change migration of weekly blocks (D-061).
- Find-a-time: friend selection, date range, ranked overlaps (D-035, D-100, D-108), prefill session or poll (D-102), over-duration warning (D-109).
- Week-calendar + agenda views with layers/filters, reset-on-visit (D-104, D-111, D-117, D-135). Test Safari early — pointer-event quirks bite drag UIs.
- Home: pending items, polls needing votes, upcoming sessions, top-5 suggestions + See all (D-103, D-110, D-116, D-136); Discover view (D-149).

## Phase 6 — Polls

- Poll creation (full details + 2..12 options, deadlines), voting grid (options × Available/Maybe/Unavailable — **second a11y hotspot; explicit keyboard/SR task**), suggestions, option deletion, deadline close, organizer selection, expiry, finalization → session with capacity allocation (whole §7.3/8.1 poll block), sweeper jobs for deadline/expiry.
- Poll join requests during Polling (D-118), guest voting, private guest-only polls (D-125).
- Poll notification events complete (D-120, D-170); deletion effect added: owned-poll expiry (D-092).

## Phase 7 — Notification transports + preferences

Emission already exists everywhere (principle 5). This phase is transport and preference work only:

- Web Push delivery for the full §8.4 list; contextual enable prompt (D-147, D-159); subscription lifecycle.
- **Idempotent dispatch:** send-log keyed on (event, recipient, channel) checked before delivery — Hangfire is at-least-once and deploys kill mid-flight jobs; duplicates must be swallowed, late delivery accepted.
- Per-session mute (D-090, D-113); reminder preference Off/15m/30m/1h/3h/12h/24h, default 1h (D-082); reminder delivery job.
- Notification center UI + unread badge.

## Phase 8 — Compliance, localization, hardening

- Translation pass: pt-BR + es for UI, **notifications, and emails**; locale formatting audit (D-060, D-146).
- WCAG 2.2 AA audit across all screens; remediation (D-139). Calendar and poll grid get dedicated SR/keyboard sweeps.
- **Cross-browser matrix pass:** current + previous Chrome, Edge, Firefox, Safari, incl. mobile variants (D-138).
- Legal documents as a real work item, started during Phase 7: privacy policy, ToS, community standards — three locales, LGPD-aware (§8.6); public deletion-request route (D-072); platform unlink + data revocation.
- **Deletion completeness verification:** end-to-end test that grace expiry produces every D-092 effect and backups age out per retention policy.
- Minimal first-party analytics events (D-151, D-161).
- Security pass: authz review of every endpoint against the visibility engine, guest-link handling re-check, OWASP basics, dependency audit.
- Load sanity on the VM (k6: RSVP burst, overlap calc with many friends/blocks, sweeper under load).

## Phase 9 — Go live, private beta → public launch

- **Go live, before the first beta invite.** Done 2026-07-30 (early, during Phase 1): Hetzner VM provisioned (Ubuntu 26.04, Docker + compose, ufw 80/443 tcp + 443/udp, fail2ban, SSH hardened, 2G swap, `partyup` deploy user, `/opt/partyup` with deploy files + `.env`), DNS A record for partyup.social, GHCR login on the VM, repo secrets `VM_HOST`/`VM_USER`/`VM_SSH_KEY`, `DEPLOY_ENABLED=true`. Still open: first `main` auto-deploy over TLS (fires on the next push), offsite backup target (rclone) live, restore drill against a real dump.
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
