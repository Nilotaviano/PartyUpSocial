# Phase 8 Design — Compliance, localization, hardening

Status: CONVERGED rev 6 (5 adversarial review rounds, finding count 20→19→16→5→4, zero Blockers after
round 2; execution pending owner ratification of §2 questions — S4's e2e and S10's authoring are startable now).
Scope source: IMPLEMENTATION_PLAN.md §Phase 8 (lines 141–151). File:line claims verified at HEAD `bec37a6`.

## 1. Shape of the phase

Phase 8 is an audit-and-close phase, not a feature phase: most decisions (D-060, D-072, D-092, D-138, D-139,
D-146, D-151, D-161, D-177..D-180) are already Confirmed and partially shipped. The work is (a) three genuinely
new surfaces — dashboard queries, admin UI, legal pages + footer — and (b) five audits that close TRACEABILITY
placeholders with named `_Dnnn` tests or recorded dispositions. Every slice lands its TRACEABILITY.md row edits
in the same commit as its tests (CLAUDE.md convention).

**Ordering is build-then-audit.** Build slices S1→S2→S3 (owner-sequenced: D-178 queries before admin UI,
owner 2026-08-10), S4, S5 create new surfaces; audit slices S6 (i18n), S7 (a11y), S8 (browser matrix),
S9 (security) close *after* S1–S5 land, because they certify the whole surface including the new admin routes,
footer, and legal pages. Each build slice still ships its own i18n-parity and axe-clean duties per A-082 —
the audits certify, they don't mop up. S10 (k6 authoring) is order-independent; S11 closes the phase.
While Q1/Q2/Q6 answers are pending, the startable work is S4's e2e half and S10's authoring.

## 2. Owner questions (block the slices that cite them)

- **Q1 — RATIFIED 2026-08-12: `last_seen_at` column.** The owner chose the column over the derived-only
  default: S1 authors a migration adding `users.last_seen_at` (touched on authenticated requests, throttled —
  write only when the stored value is older than ~10 minutes, so it is not a per-request write), active users
  = `last_seen_at` within the window, D-179 last activity reads it directly. The no-event-pipeline reading of
  D-161 and the `status IN (Confirmed, Completed)` success predicate are ratified with it. S5's privacy
  policy must describe the last-activity collection. Original question preserved below for the record.
  *(superseded)* — analytics reading + "active users"/"last activity" (blocks S1, S2, and S5's analytics paragraph).
  (a) D-161/§8.6 say the MVP "*records* minimal first-party product *events*" (PRODUCT_SPEC.md:396,653); this
  design proposes **no event pipeline** — all five D-178 metrics derive from existing domain tables. That is a
  spec reinterpretation ("events" → "derived measures over existing product data") and needs a ratified spec
  touch; S5's privacy-policy analytics description depends on which reading wins. The same spec touch records
  one measurement disposition: `SessionStatus` is point-in-time (Confirmed→Completed after end, D-056; no
  `confirmed_at` column exists), so success = `status IN (Confirmed, Completed)` and a Confirmed-then-Canceled
  session is unrecoverable from the column — accepted as a small undercount, unless the owner promotes an
  ever-confirmed marker to a migration here.
  (b) "Active users"/"last activity" derivation: user is active in a window if they created a session/poll,
  RSVPed (`Participation.RespondedAt`), or voted (`PollVote.UpdatedAt`) in it; last activity = max of those.
  **Known blind spot:** availability rows carry no timestamps (AvailabilityBlock exposes only Start/End), so
  availability edits are invisible to this definition — accepted, or the owner instead approves a
  `last_seen_at` column (migration + new collection ⇒ privacy-policy text changes). Windows: 7- and 30-day.
- **Q2 — RATIFIED 2026-08-12: proposed default (`/admin` by URL, no nav entry).** IMPLEMENTATION_PLAN.md:149 flags a spec touch. *Proposed default:*
  routes under `/admin` (SPA), no nav entry rendered for anyone — admins reach it by URL; the D-177 404 posture
  makes nav gating unnecessary and leak-proof. Spec row proposal accompanies S3.
- **Q3 — RATIFIED 2026-08-12: evergreen-engine policy as proposed (no macOS lane).** "Current + previous" versions are not
  expressible with Playwright's single pinned build per engine. *Proposed default:* evergreen-engine policy —
  Playwright-pinned Chromium/WebKit/Firefox lanes + a browserslist/Vite build-target floor covering
  current+previous of the four families + owner real-device spot checks; open-source engines stand in for
  branded Chrome/Edge/Safari. Recorded against TRACEABILITY.md:165.
- **Q4 — token-gated routes in the axe audit (blocks part of S7).** Six auth routes plus `/email-unsubscribe`
  sit outside the fixture seed (AuditedRoutes.cs:22-25). The fixture is black-box (separate API process; no DI
  handle), so it cannot mint tokens via SecurityTokenService. *Proposed default:* **insert pre-hashed token
  rows over the fixture's own Npgsql connection** (the same connection S3 uses for the admin role grant) —
  one reusable audit account serves every token-gated theory case. Driving the issuing endpoints instead is
  ruled out as the default by two suite-global costs the per-user limiters don't capture: every extra
  registration trips the fixture's 65-second shared-rate-window barrier (~13 min of pure sleep across the
  ~14 token-bearing light+dark cases, on a ~21-min lane), and the consumption legs sit in the IP-partitioned
  `auth-ip` dimension (20/15min) that fresh accounts do nothing for. Mail-drop harvesting stays only where an
  issuing flow's own UI is itself under audit. Each case still navigates in a throwaway context (tokens are
  single-use; some routes mutate the account). Documented manual audit only for `/auth/callback` (external
  redirect leg).
- **Q5 — RATIFIED 2026-08-12: WSL-compose dress rehearsal first** (prod-VM run decided after rehearsal
  results); S10's threshold proposal ratified as-is (`http_req_failed` <1%; my-participation p95 <800ms burst
  / <1000ms under sweeper; overlaps p95 <500ms / <700ms; sweeper tick within the 55s window) — record as
  ratified in `deploy/load/README.md`. Seed execution + any limiter override remain owner-gated at run time. Environment (prod VM / clone /
  WSL-compose rehearsal), rate-limit override, seed SQL execution, and pass/fail thresholds (spec §11 has none)
  are owner calls. S10 authors scripts + seed SQL + a threshold proposal; the run is owner-gated.
- **Q6 — INPUTS RATIFIED 2026-08-12** (final sign-off of the nine drafted documents still pending after S5
  drafts them): retention = **30-day backup retention** (matches the shipped nightly pg_dump rotation) and
  **shared session content persists until account deletion / D-092 effects** — closes PRODUCT_SPEC.md:464's
  TBD with a spec row in S5; LGPD controller = **the owner as natural person**, contact
  **privacy@partyup.social** (owner creates the mailbox/alias before S5 drafts flip from draft status; the
  footer contact link publishes it). LGPD
  controller identity and contact point for the pt-BR text (this is what the footer contact link publishes —
  §8.6's "published support/contact info" is a mobile-store-submission requirement and stays deferred with the
  mobile phase), **the data-retention periods** — both backup retention (the IMPLEMENTATION_PLAN.md:147
  age-out target) and the PRODUCT_SPEC.md:464 To-be-decided shared-session-content retention, which are
  separate owner calls — and final sign-off of all nine documents. S5 drafts; owner ratifies.
- **Q7 — RATIFIED 2026-08-12: app-side headers + owner-applied HSTS in Caddy, `__Host-` deferred.** *Proposed default:* app-side CSP,
  X-Content-Type-Options, X-Frame-Options/frame-ancestors, Permissions-Policy; HSTS in Caddy (owner-applied).
  `__Host-` cookie-prefix migration is **explicitly deferred** — the one-container single-origin topology
  already enforces what the prefix guarantees (AuthCookieSetup.cs:28-33), and the rename forces a prod re-login.

## 3. Slices

### S1 — D-178 dashboard queries + D-151/D-161 measurement (backend; Q1)

All five D-178 metrics (registrations, active users, sessions/polls created, confirmation rate, 30-day repeat
scheduling) computed from existing tables — `users.created_at`, materialized status columns (never re-derived
from timestamps, A-067), and the D-160 linkage (`Session.OriginPollId` ↔ `Poll.CreatedSessionId`). No event
table, no trackers — verified by a policy test asserting the web bundle loads no third-party analytics.
- Confirmation-rate semantics per PRODUCT_SPEC.md:485: denominator = every created fixed session **with
  `origin_poll_id IS NULL`** plus every created poll, incl. cancellations — poll-derived sessions count only
  through their originating poll, so a finalized poll contributes exactly 1, never 2 (fixture asserts this,
  `_D178`/`_D160`). **Numerator/success predicate:** `status IN (Confirmed, Completed)` — status is
  point-in-time and every past success has moved Confirmed→Completed (D-056); a poll succeeds when its
  `CreatedSessionId` session satisfies that predicate. Confirmed-then-Canceled undercount accepted per Q1(a)'s
  recorded disposition. Repeat scheduling = % of organizers creating again within 30 days of their *first*
  creation.
- New: `GET /api/v1/admin/dashboard` (RequireAdmin ⇒ 404 fail-closed inherited), Application-layer
  `GetUsageDashboardUseCase` with SQL-backed read model. Active-users per the ratified Q1 column: S1 authors
  the `users.last_seen_at` migration (EF, forward-only) plus the throttled touch on authenticated requests
  (~10-min write floor); it applies on prod startup via the normal `Database:MigrateOnStartup` path.
- Tests: `_D178` integration (per metric, incl. cancellation-in-denominator and poll-success-only-via-
  Confirmed), `_D151` (definitions), `_D161` (no-tracker policy). A-071 op-id + problem-code inventories
  extended; `gen:api` regenerated.
- TRACEABILITY: close D-178 (:219); repoint D-161 (:188) at the no-tracker test with its privacy-transparency
  half pending S5 (D-161 is "Confirmed with policy constraint" — the constraint half is the S5 privacy-policy
  description); repoint D-151 (:178, "Policy | 9") at the shipped measures with Phase 9 consuming them.

### S2 — D-179 per-user stats + D-180 content removal (backend; Q1)

- Extend the admin projection (AdminUser.cs) with registered date (exists), last activity (per Q1(b)), sessions
  organized/joined (sessions/participants tables), friend count (query exists per `…_D179`, expose it). Email
  stays masked.
- Replace the 501 stub (AdminEndpoints.cs:98-104). Content removal = the executor's steps 1–3 as shipped,
  which is the three PRODUCT_SPEC.md:457 effects **plus their two riders**: cancel owned not-started sessions,
  expire owned Polling/Closed polls, remove future participation, lapse the subject's pending join requests,
  cancel the subject's reminders (the reminder cancel is a necessary consequence of removing participation;
  the join-request lapse is accepted and recorded in the Q1/S2 spec-touch note). **Excluded** (the account
  stays a normal live banned account): friendships, blocks, Completed-history anonymization,
  PII/credential/avatar/push/notification purge, username release, journal row.
- **Mechanism, both layers.** Domain: `DeletionExecutor.Execute` is all-or-nothing (RequireExecutable throws
  for non-PendingDeletion users; step 6 always tombstones) — extract steps 1–3 into a shared domain function
  over `DeletionScope` returning a smaller `ContentRemovalResult` (canceled sessions, expired polls, removals,
  lapsed join requests, canceled reminders) that `DeletionExecutor.Execute` widens into
  `DeletionExecutionResult` (whose `Journal` member is non-nullable — never fabricate a throwaway entry for
  the ban path). Application: extract `ExecuteDeletionForUserUseCase`'s **lock envelope** —
  `DiscoverAffectedUserIdsAsync` → `LockUsersAsync` union (A-068 ordering) → `FindForUpdateAsync` →
  `scopeProvider.LoadForUpdateAsync` → set-compare with the bounded `DeletionScopeChangedException` retry —
  into a shared helper parameterized by (a) the subject-eligibility predicate (PendingDeletion+grace-elapsed
  for deletion vs Banned for content removal — the shipped use case's guard returns false for a banned
  subject, so plain reuse is a silent no-op), (b) the post-commit tail (NDJSON journal ship vs none), and
  (c) a **post-lock, pre-guard subject action hook** — no-op for deletion; `user.Ban` + sign-out-everywhere
  for the ban path, applied to the entity the helper's own `FindForUpdateAsync` returned. The ban path never
  calls `FindForUpdateAsync` ahead of `LockUsersAsync` (that subject-first lock is exactly the §6.2 inversion
  the shipped envelope forbids — and what today's `BanUserUseCase` does). The
  ban path runs it with both notification materializers (excluding the subject; same copy as the
  deletion-derived events, so counterparts see no ban disclosure) and a **new applier overload** —
  `IDeletionResultApplier.ApplyContentRemovalAsync(ContentRemovalResult, …)`, implemented as the existing
  reminder-cancel tail only (no PurgedData branches, no friendship/block removal), with the
  `DeletionExecutionResult` overload delegating to it — the ban path never constructs a
  `DeletionExecutionResult` (its `Journal` member is non-nullable) and never skips the applier (that would
  silently drop the reminder-cancel effect). No tombstone/journal/username release. **Composition:** ban + content removal execute
  in one outermost `IUnitOfWork.ExecuteAsync` — `User.Ban` + sign-out applied inside it, never via a nested
  `BanUserUseCase.ExecuteAsync` (a nested `ExecuteAsync` loses the A-069 retry boundary); a failed removal
  leaves no ban committed. A single-row lock is not sufficient: steps 1–3 mutate other users' sessions/polls
  concurrently with the sweeper. A-016/A-067 hold throughout. Not reversed by unban.
- Tests: `_D179` (each stat), `_D180` (all five effects present; **absence** of every excluded effect —
  friendships and Completed history survive; unban does not restore; idempotence; a scope-changed concurrency
  retry assertion mirroring the `_D092` bounded-retry test; an A-068 lock-ordering assertion;
  failed-removal-leaves-no-ban atomicity). Inventories + `gen:api`.
- DOMAIN_MODEL.md:42 amended in the same commit: scope "do not remove content or history" to the `Ban`
  operation itself and record the separate optional content-removal action (five effects + explicit
  exclusions, not reversed by unban).
- TRACEABILITY: close D-179 (:220) and D-180 (:221).

### S3 — Admin UI (web) — after S1+S2 (Q2)

- `/admin` (dashboard) and `/admin/users` (+ detail) pages; TanStack Query on generated client; i18n `admin`
  namespace ×3 locales joining the parity oracle.
- Fail-closed parity with D-177: a 404 from admin endpoints renders a page-local `status === 404` branch with
  a `role="alert"` message from the `admin` namespace (the SessionDetailPage pattern — no shared not-found
  component exists and none is scoped here); no nav entry anywhere (Q2 default); no admin string reachable by
  non-admins.
- Ban = plain action; content removal = a separate destructive action with its own confirmation, never a
  checkbox on ban (IMPLEMENTATION_PLAN.md:149); unban does not mention restoration.
- A11y: admin routes join AuditedRoutes and ship axe-clean per A-082. Admin fixture mechanism: the seeder runs
  at host startup (AdminSeeder is an IHostedService keyed off `Admin:SeedUserIds`/`Admin:SeedEmails`), before
  browser-lane accounts exist — so the fixture grants the role row over its own Npgsql connection: insert into
  `auth_user_roles` joined to `auth_roles` where `normalized_name = 'ADMIN'` (the role row always exists —
  AdminSeeder calls `EnsureRoleExistsAsync` every startup). Admin status is resolved per request from the
  database (`CurrentUserProjectionReader` joins the role tables live), so the grant takes effect on the next
  request regardless of when the cookie was issued; the real constraint is that the granted account must be
  onboarded and profile-complete, or `RequireAdmin` still answers 404.
- Tests: RTL unit + one browser-lane flow (`_D177` no-leak; `_D178` dashboard render; `_D180`
  destructive-action separation).
- TRACEABILITY: append client evidence to D-177 (:218).

### S4 — Deletion-completeness e2e (D-092; Q6 for the backup half)

One integration test drives a real grace-expiry sweep over a maximally-seeded user and asserts the full effect
list together (~20 assertions: tombstone+PII null, username reservation D-145, owned not-started session
Canceled / in-progress retained, polls Expired with D-170 notifications, participation + join-request lapse,
reminders canceled, friendships+blocks gone both directions, availability cascade A-075, push
subscriptions/mutes/send-log purge order, recipient rows gone vs actor rows masked "Anonymous User", avatar
sources gone (incl. the A-084 plaintext proxy-token row — its sole revocation authority is source-row
deletion), credential purge, journal row + NDJSON ship, counterpart notifications excluding subject,
pre-expiry inertness D-148). Goes through `ExecuteDeletionForUserUseCase` — never bypasses the A-068 lock
protocol. Named `_D092`.
- Backup age-out (IMPLEMENTATION_PLAN.md:147 second half): blocked on the Q6 retention decision and the
  Phase 9 offsite target — repo side documents the runbook check; VM verification is owner work (§4). The
  IMPLEMENTATION_PLAN.md:147 row closes only to the e2e + documented runbook, with the VM check recorded open.
- TRACEABILITY: append e2e evidence to D-092 (:119).

### S5 — Legal documents + footer (D-072 surfacing; Q1, Q6)

- Nine documents: privacy policy, ToS, community standards × en/pt-BR/es, delivered as SPA routes `/privacy`,
  `/terms`, `/community-standards` rendering **locale-selected prose components — no markdown runtime, no new
  npm dependency** (A-013 posture); chrome strings join i18next. Content obligations from §8.6: collection/use/
  retention (per Q6 decision)/deletion/third-party sharing incl. Steam data, 7-day grace + login-cancels
  disclosures (D-148/D-158/D-165), analytics description matching the ratified Q1(a) reading, 13+ posture,
  LGPD-aware pt-BR as operative.
- Build the app footer (none exists): links to `/account-deletion` (D-072 surfacing, IMPLEMENTATION_PLAN.md:146),
  the three legal routes, and §8.6 support contact (Q6); i18n'd ×3; rendered on unauthenticated surfaces too
  (welcome, login, public deletion pages) since D-072's audience is logged-out users. Footer + legal routes
  join AuditedRoutes axe-clean per A-082.
- Unlink re-verification: audit that nothing Steam-derived survives `UnlinkSteamUseCase` beyond what the policy
  claims — persona-name copies, friend-import artifacts, Steam-sourced avatar (D-050/D-190), catalog-pairing
  rows; add a test if a gap is found, else record the disposition.
- Drafts ship behind footer links immediately, marked as drafts until Q6 sign-off flips them.
- TRACEABILITY: append footer evidence to D-072 (:99); append the analytics-description evidence to
  D-161 (:188), closing its transparency half.

### S6 — Translation + locale-formatting audit (D-060, D-146) — after S1–S5

- Fix the found display-path bug: PollCreatePage.tsx:472,478-481 uses `Intl.DateTimeFormat(undefined, …)` —
  browser locale, not `i18n.language`; a D-146 account override shows mixed locales. Test pinned `_D146`.
- Formatter audit: enumerate all `Intl.DateTimeFormat`/`toLocale*` display call sites (~28 today + S3/S5
  additions), assert each passes `i18n.language` (the two `en-US` internals — offset math, TZ label parts —
  are exempt); decide the server `es`→`es-ES` culture mapping (LocalizedInstantFormatter.cs:23) vs the client's
  bare `es` for a LatAm-leaning audience; align email vs UI datetime shapes.
- Translation *quality* review (not authoring — 835×2 keys exist, 0 missing, plus S3/S5 additions):
  native-register sweep of pt-BR (você, gaming terms) and es UI strings + 18×3 email + 25×3 push templates;
  fix findings; guest/unauthenticated surfaces verified in all three locales via browser detection alone.
- Close the Phase 7 residual: retag the mislabeled `_D060` notification-center test.
- TRACEABILITY: close the "8 (full audit)" cells on D-060 (:87) and D-146 (:173).

### S7 — WCAG 2.2 AA audit + remediation (D-139) — after S1–S5 (Q4)

Existing lane: 40 audited routes × light+dark, empty exclusion list. The audit adds what it misses — the delta
against App.tsx's route table is exactly the 2 missing routes + 6 token-gated auth routes:
- Missing routes: `/polls/:pollId/invite` (plainly navigable); `/email-unsubscribe` and the token-gated auth
  routes per Q4 default — `/email-unsubscribe` reads its token from the URL hash and renders only the
  invalid-link branch without one, so it is audited in both states (no-token and mail-drop-harvested token);
  S3/S5 routes will already be in AuditedRoutes per their slice duties.
- Interaction states (axe scans initial render only today): SteamGamePicker open, the three availability
  dialogs, tour overlay, WeekGrid mid-drag, VotingGrid focused — scripted state-then-scan tests.
- Poll grid gets its first real-browser keyboard/SR sweep: Playwright keyboard-only vote + suggest-option +
  guest voting, asserting roving tabindex and SR-observable tally text.
- Calendar SR additions: selection-extent announcements under Shift+Arrow growth, agenda-view semantics, layer
  legend non-color coding. **Constraint:** WeekGrid `touch-action` gating is load-bearing (P5-TOUCH-01) — no
  remediation may break scroll-until-long-press.
- Phase 7 residual routed here: prove `PollDetailPage`'s mute announcement directly (today proven only via
  `SessionDetailPage`'s shared implementation).
- Manual-check evidence recorded for prefers-reduced-motion, 400% reflow, text-spacing (axe can't); the Phase 3
  320px/200% deferred pass folds in here.
- Findings fixed in-slice (exclusion list only shrinks, A-082). Human NVDA + real-device touch = owner (§4).
- TRACEABILITY: close D-139 (:166) full-audit cell, recording the NVDA/real-device-touch checkpoint as an
  open owner item — the same honesty S4 applies to the backup VM check.

### S8 — Cross-browser matrix (D-138) — after S1–S5 (Q3)

- Parameterize PlaywrightFixture's hardcoded Chromium (`PARTYUP_BROWSER_ENGINE` env). The parameterized fixture
  boots **exactly one engine**, so every lane installs only its own — including the webkit lane, whose dual
  chromium install existed only because the fixture hardcoded Chromium; it shrinks to `webkit` once the lane
  sets the env var.
- **Lane selection mechanism:** the firefox lane runs a curated subset of the *same, untagged* flow classes
  (auth, RSVP, calendar create, poll vote, notifications) selected by FullyQualifiedName filter with the env
  set — running them on two engines is intended coverage, not a double-run. The `Browser!=…` trait-exclusion
  rule applies only to engine-specific classes (the WebKit pointer set). No mobile-chromium lane: Q3's policy
  plus the owner real-device checks (§4.2) discharge D-138's mobile half; the WebKit pointer lane already
  covers emulated mobile touch.
- Add browserslist/Vite `build.target` expressing current+previous of the four families; support-matrix
  statement in docs. Playwright cache keys extended for the firefox lane. Tests named `_D138`.
- TRACEABILITY: close D-138 (:165) per Q3 policy.

### S9 — Security pass — after S1–S5 (Q7)

- **Authz review as work, not artifact:** review every endpoint registration (144 at HEAD + S1/S2 additions;
  the four Testing-env-only `/_slice2` routes get a prod-unreachability test instead of a review row) for
  auth state, visibility-engine gate, fail-closed 404, rate-limit dimension, CSRF coverage. Gaps become fixes +
  missing fail-closed tests in-slice. Checked in: only an **exceptions ledger** (~a dozen rows) in
  `docs/SECURITY_REVIEW_PHASE8.md`: A-085 catalog search outside the engine, **A-084 avatar-proxy plaintext
  capability tokens** (verify the revocation contract: source-row deletion on unlink/re-link/ban/deletion),
  `/g` (A-064) + `/a` (A-084) transport exceptions — stay GET-only, `/health` + `/openapi` limiter scope,
  `/_slice2`.
- **Headers** (Q7 default): app-wide CSP + X-Content-Type-Options + frame-ancestors + Permissions-Policy.
  **CSP script-src disposition:** web/index.html ships a pre-paint inline theme script — the header carries
  its SHA-256, computed at startup from the built `wwwroot/index.html` (never `'unsafe-inline'` for scripts,
  never a hardcoded hash that rots on the next Vite build). `wwwroot` exists only in the container image
  (Dockerfile copies `/web/dist`; `dotnet run` and the integration host have none) — when absent, the hash is
  simply omitted; the SPA-document header assertion therefore lives in the CI browser lane's built topology,
  while API + `/g` + `/a` header assertions live in the integration suite. **style-src is `'self'
  'unsafe-inline'`** — Radix portals set inline positioning styles and the tour overlay sets a style
  attribute; hashes cannot cover style attributes. HSTS to Caddy (owner).
- **OWASP sweep:** SSRF posture of avatar-fetch + Steam calls (redirect/allowlist), avatar upload size/
  decompression limits, request body limits, account-enumeration parity of register/forgot/magic-link,
  InternalExceptionMapper leak check, ReturnToValidator vs A-063, limiter scope confirmation.
- **Dependency audit:** `dotnet list package --vulnerable --include-transitive` + `npm audit`; re-verify the
  two pinned GHSAs; ImageSharp (parses untrusted avatar bytes) patch level. .NET bumps in
  Directory.Packages.props only (CPM rule); npm bumps in web/package.json, no new runtime dependency
  (A-013 posture).
- Guest-link recheck per A-015/A-064/A-065: hashes at rest, fixed-time compare, no token logged on any route
  added since Phase 4, `guest_links.state` sole revocation authority.
- TRACEABILITY: record the pass disposition on the affected A-rows in ARCHITECTURE.md §9 (decision record);
  any surviving carry gets its own §10 "Still deferred" bullet.

### S10 — k6 load scripts (authoring only; execution owner-gated per Q5)

- `deploy/load/`: `rsvp-burst.js` (N VUs PUT my-participation on one session — contends the A-069
  `FOR NO KEY UPDATE` parent lock), `overlap.js` (omitted-selection overlap for a many-friends viewer, blocks shaped at seed time
  per D-065; regression means the A-079 two-query budget broke), `sweeper-load.js` (RSVP traffic while a
  500-candidate SessionDueSweepJob tick contends the same locks; an overlapping-tick throw is the A-066 signal,
  not a defect).
- Per-VU cookie jar + `pu.csrf` extraction (no bearer shortcut exists); seed SQL authored for the owner to run;
  threshold proposal (p95, error rate, sweeper tick < 55s window) for Q5 ratification.

### S11 — Closeout

- Every touched TRACEABILITY row closed to a named test or recorded disposition; no Phase 8 placeholder text
  survives — "Browser matrix." (:165), "Analytics scope." (:188), "Admin usage dashboard." (:219), D-139's
  "Phase 8 still owns the full audit" (:166), D-179's "verified in Phase 8" (:220), D-180's "501 until
  Phase 8" (:221), D-059's mobile-UGC audit (:86), and the P5-TOUCH-01 re-deferral text (TRACEABILITY.md:463
  human-checkpoint paragraph + ARCHITECTURE.md §10's Phase-0-remainder bullet) — disposed to the owner's §4.1
  result or explicitly re-carried to Phase 9. S11 also verifies D-161 (:188) carries both halves (S1
  no-tracker + S5 transparency).
- Phase 7 residuals dispositioned (recording only — no Phase 7 optimization work opens here): WebPushSender
  real-wire residual closed against live prod push evidence (VAPID active since 2026-08-09); per-row
  notification-read query batching explicitly re-accepted to backlog; migration-`Down()` question re-accepted
  (prod table small); reminder-preference 99/100 wire-value boundary and the tray-grouping ruling explicitly
  re-accepted; the `_D060` retag lands in S6 and the PollDetailPage mute announcement in S7.
- D-059 mobile-UGC store-policy audit recorded (doc pass over user-authored fields).
- IMPLEMENTATION_PLAN.md + TODO.md updated; owner checkpoint list (§4) handed over.

## 4. Owner-only checklist (cannot be closed by code)

1. P5-TOUCH-01: real-device touch + NVDA pass on the week grid (and poll grid per S7 scope).
2. D-138: real mobile Safari/Android Chrome spot checks; Firefox/Android push-delivery confirmation.
3. Legal: Q6 inputs (incl. retention period) + sign-off of nine documents; store-listing deletion-URL at
   mobile submission.
4. k6: Q5 environment + seed execution + run window + threshold ratification; revert any limit override.
5. VM: backup age-out verification (blocked on Q6 retention + Phase 9 offsite target), HSTS in Caddy, any
   proxy changes (cardinal rule 2).
6. Spec touches to ratify before their slices close: Q1(a) analytics reading + Q1(b) activity definition,
   Q2 admin placement, Q3 browser policy (proposed rows accompany the slices).
7. Migrations from S1/S2 (only if Q1 chooses a column): owner executes; scripts authored only.

## 5. Execution discipline

Per-slice: design-conformance + blind verification (foreman-verifier, original ticket verbatim), targeted tests
during work. Pre-push gates: full `scripts/wsl-gate.sh` (.NET) **and, for any slice that touches `web/` —
including S1's bundle policy test and S9's npm bumps — `npm run lint && npm run build && npm test` from
web/** — wsl-gate runs no web tests. Slices delivering browser-lane tests (S3, S7, S8) additionally run
`dotnet test tests/PartyUp.BrowserTests -p:PartyUpBrowserTests=true` locally against the **dev topology**
(the fixture spawns `dotnet run` + Vite; prerequisite is a running WSL dev Postgres and a current `web/`
tree — no wwwroot copy is involved). The **built topology** (`PARTYUP_BROWSER_TOPOLOGY=built`) needs Docker
and is CI-only — so the S9 SPA-document CSP assertion and any `MapFallbackToFile` behavior are verified on
CI, not pre-push. Any slice touching Domain updates DOMAIN_MODEL.md in the same commit, alongside its
TRACEABILITY row. No push without green gates. Conventional Commits; TRACEABILITY edits ride the slice commit. Sizing
honesty: IMPLEMENTATION_PLAN.md estimates 4–5 weeks part-time; S5 legal drafting and S7 remediation are the
likely overruns.
