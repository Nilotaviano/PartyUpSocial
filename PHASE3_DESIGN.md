# PartyUp — Phase 3 Technical Design (Friends and Blocking)

**Status:** initial implementable draft for adversarial review.
**Basis:** `PRODUCT_SPEC.md` (D-001..D-180), `ARCHITECTURE.md` (A-001..A-053), ratified `DOMAIN_MODEL.md`, `IMPLEMENTATION_PLAN.md` Phase 3, `TRACEABILITY.md`, and the binding P3-DESIGN r1 rulings recorded in §3.
**Audience:** workers who have this design and the repository, but none of the design conversation. Paths, transaction boundaries, response shapes, tests, and slice gates are explicit for that reason.

New architecture decisions are numbered **A-054..A-063**. They are proposed here; §15 assigns every behavior-changing `DOMAIN_MODEL.md` and `TRACEABILITY.md` delta to the same commit as the first slice that implements or tests that behavior. The closing slice (§15.9) applies only additive architecture/plan summaries and final cross-document reconciliation.

---

## 1. Scope and non-scope

### 1.1 In scope

1. Persist `Friendship`, `Block`, and `Notification` aggregates with Guid v7 keys, snake_case mappings, DB-backed pair uniqueness, and forward-only migrations (A-005/A-006).
2. Friendship request send, crossed-request auto-accept, accept, reject, outgoing cancel, accepted-friend list, incoming/outgoing request lists, and confirmed unfriend (D-015/D-105).
3. Exact-username profile links, friend-code input/resolution, stable-code QR sharing, and the SPA routes `/u/{username}` and `/add/{friendCode}` (D-031/D-128/D-144, A-015/A-035).
4. Profile-page disclosure: username only before acceptance; full profile for self/accepted friends; authorized rosters retain full identity under the ratified profile-page-only reading of D-068.
5. Block/unblock and the caller's private blocked-users management list. Blocking atomically removes any friendship/request and invokes the future-proof content-effect seam (D-065/D-074/D-076/D-077/D-086).
6. Steam `GetFriendList` import, local Steam-ID matching, invitation links for unmatched Steam friends, and explicit `private_profile`, `zero_friends`, `zero_matches`, `not_configured`, and `upstream_failure` result states (D-049).
7. Transactional in-app records for received friend requests and Steam matches, with no notification-center or unread UI (D-105/A-016).
8. Phase 2 deletion-scope completion for Phase 3 rows: friendships and pending requests, blocks in both directions, and notification rows addressed to the deleted user are removed only at grace expiry (D-092/D-148).
9. Dedicated friends, profile/add, Steam import, blocked-user settings, and actionable onboarding-friends UI; en/pt-BR/es parity and WCAG 2.2 AA from the first slice (D-060/D-127/D-139/D-146).
10. OpenAPI contracts and generated TypeScript client updates for every new endpoint (A-011).

### 1.2 Explicitly not in scope

- Session persistence, session UI, RSVP, reminders, or real session block effects (Phase 4). Phase 3 ships the stable scope/executor contract and an empty session provider.
- Availability or friend-overlap queries (Phase 5). Phase 3's accepted-friend read model is their later input.
- Poll persistence/UI or real poll block effects (Phase 6). The same seam receives a poll provider then.
- Notification-center reads, `ReadAt` mutation, unread badge, grouping, preferences, Web Push, or any transport (Phase 7). The table is write-only in Phase 3.
- Admin friend-count endpoints or admin UI (Phase 8). The schema and indexes make accepted-friend count a trivial query (D-179).
- Friend-code rotation, fuzzy username search, stranger recommendations, public matchmaking, in-app camera scanning, later-registration Steam correlation, or other-platform imports.
- Any database or Azure operation during implementation. Workers author migrations and code; they do not apply migrations to a shared/live database or touch Azure.

### 1.3 Exit criteria

Phase 3 is complete only when the Phase 3 tests in §14 are green, the OpenAPI-generated client is current, all three locales have identical keys, and the following observable flows work through the SPA and HTTP API: profile/code/QR resolution; every request action including crossed requests; friend list and unfriend; block/list/unblock; every Steam result state; the onboarding friends step; transactional friend/Steam notifications; and deletion-grace expiry cleanup. The Phase 4/6 block legs must remain empty but contract-tested, not simulated with Phase 3 tables.

---

## 2. Verified current state

- `src/PartyUp.Domain/Friendships/Friendship.cs` already owns canonical pair ordering, `Pending`/`Accepted`, `RequestedBy`, `RequestedAt`, `AcceptedAt`, the received-request event, and addressee-only acceptance. Rejection/removal remain row deletion.
- `src/PartyUp.Domain/Friendships/Block.cs` already owns the ordered blocker/blocked pair and mutual hiding semantics. `BlockEffects.Apply` already deletes friendships logically, removes organizer/counterpart participation from supplied live sessions/polls, lapses requests, cancels reminders, and is idempotent.
- `ProfileVisibility`, `LookupVisibility`, `VisibilityEngine`, `IdentityMasking`, and `RecipientComputer` already express the Phase 1 security and recipient rules. `RecipientComputer` currently documents a later-registration Steam notification; ruling 13 removes that clause (§16).
- `Notification` and `NotificationType` already define structured-only rows. `FriendRequestReceived = 23` and `SteamImportMatch = 24`; acceptance and blocking produce no notification request.
- `AppDbContext` currently maps user/auth/Steam/deletion rows only. No friendship, block, or notification `DbSet`/configuration/migration exists.
- Phase 2's `UnitOfWork` is one real EF transaction and is the required A-016 boundary. `DeletionScopeProvider` currently supplies only the username row and empty session/poll/friendship/block collections.
- `ISteamProfileClient` and `SteamProfileClient` fetch one player summary only. `steam_identities.steam_id64` is already unique and is the local matching key.
- `RateLimitPolicies` is one global `PartitionedRateLimiter.CreateChained` limiter. Its existing `lookup` member is 30/user/minute for username availability; the Phase 3 dimensions extend the same mechanism.
- `DomainExceptionMapper` maps `DomainError.Blocked` to 403 and includes the domain message. Phase 3 lookup/friend/block paths therefore must not pass a block-derived `DomainException` to that mapper (§8.4).
- `PublicBaseUrl` already exists and is used for external links. `web/package.json` has no QR dependency. `/onboarding/friends` is a localized placeholder, and the current wizard does not activate the profile until its final button.

---

## 3. Binding interpretation rulings

These are ratified inputs to this design, not open questions. Each ruling that changes existing Domain semantics is recorded in `DOMAIN_MODEL.md` in the same commit as its first implementing slice; its `TRACEABILITY.md` citations land with the tests that establish it.

| # | Binding interpretation | Design consequence |
|---|---|---|
| 1 | Send/accept/reject/cancel-outgoing are the request actions; reject/cancel delete silently; sending against an incoming pending request auto-accepts. | §7 state machine and §9 endpoints. Crossed send is `200`, creates no second request notification, and acceptance is silent (D-015/D-105). |
| 2 | Either party may unfriend; delete the accepted row silently; confirm in UI; shared sessions are unchanged. | `DELETE /friends/{friendshipId}` plus a required dialog. Only blocking invokes content effects (D-065/D-077). |
| 3 | Settings lists blocks the viewer created and permits unblock; this is management, not lookup/discovery. | `GET /blocks` deliberately bypasses `LookupVisibility` only after proving `blocker_id = viewer`; it is never linked as a profile/search result. |
| 4 | QR payload is `<PublicBaseUrl>/add/{friendCode}`; profile link is `/u/{username}`; code normalization trims, uppercases, and strips spaces/dashes; no web camera scanner. | §8.2 and §12.2. QR is client-rendered from the configured origin, never a hard-coded production URL. |
| 5 | D-068 is profile-page-only; authorized rosters retain full identity. | `ProfileDisclosure.UsernameOnly` governs profile/add views. Phase 4/6 renderers continue to use `IdentityMasking`. |
| 6 | Lookup/resolution and request-send need conservative dedicated rate dimensions. | A-059/§9.7: 20/user/minute lookup, 10/user/hour send; Steam also has an upstream-protection ceiling. |
| 7 | Friend/lookup/block paths never expose `DomainError.Blocked`; block-influenced denial is the same 404 as absence. | A-055/§8.4: one hidden-user resolver and one `user_not_found` response helper, no domain detail. |
| 8 | Phase 3 persists only friendships, blocks, notifications; session/poll block legs use an executor seam with empty scopes until Phases 4/6. | A-057/§10.1. Phase 4 adds a session provider and makes all invite/join attachment commands cite `P3-LK-01`; Phase 6 does the same for polls, without changing the block use case or domain call. |
| 9 | Grace expiry removes friendships/requests and block rows in both directions. | §10.2 extends `DeletionScopeProvider` and the deletion-result applier; Phase 3 notifications addressed to the user are purged too, per existing Domain §11 step 5. |
| 10 | Dedicated Friends is the actionable request surface. | `/friends` has friend/incoming/outgoing sections; notification records do not substitute for this screen. |
| 11 | Phase 3 persists notifications transactionally but has no center/unread semantics. | No notification HTTP read/write endpoint and no badge. `read_at` is mapped but untouched until Phase 7. |
| 12 | Steam uses `GetFriendList`; matches suggest friendship; unmatched friends receive the importer's own add URL; five empty/error states are first-class. | §11 response union always returns `200` for those five provider outcomes. |
| 13 | No later-registration/link correlation notification. | No import-state table, background matcher, or hook on Steam linking/registration. Existing Domain documentation is corrected in the same commit as Slice 6. |
| 14 | Every behavior adds D-numbered duties and upgrades Phase-1-only traceability rows. | §14.5 is additive, and each implementing slice appends its own citations in the same commit. |
| 15 | No Phase 3 admin friend-count endpoint; only queryable schema. | Both user columns are indexed and the D-179 integration duty proves the count query shape. |
| 16 | Steam match notifications are first-observation-only per `(importer, matched user)`. | A-060 and the partial notification uniqueness index prevent repeated explicit imports from creating notification spam while still returning the current match. |
| 17 | Profile activation precedes the optional onboarding friends education/actions, whose completion is not separately persisted. | A-063 keeps friendship APIs Active-only, makes Active reload deterministic, and adds no unauthorized state/table. |
| 18 | D-105's silent-block rule takes precedence over session/poll events derived solely from block-driven removals in every phase. | `BlockEffectsExecutor` permanently drains and suppresses those derived events after applying the aggregate mutations; reminder cancellations still execute. Named carry item `P4-NOTIFY-01` requires Phase 4 to reverify that permanent contract when its session notification surface exists; it does not authorize a suppression swap or an event-disposition port. |

The first-observation Steam rule and activation-before-optional-friends rule are unconditional throughout this design. Implementing slices do not carry alternative branches for either ruling.

---

## 4. Architectural decisions and invariants

### A-054 — Every two-user graph command serializes on the two `users` rows in canonical order

Send, accept, reject, cancel, unfriend, block, and unblock perform every authoritative decision inside their A-016 transaction after locking both existing `users` rows with one `SELECT ... WHERE id IN (...) ORDER BY id FOR UPDATE`. Send/block-by-username already know the candidate pair from hidden-safe resolution. Before any user-row lock, an id-addressed friendship action may issue only a no-tracking projection whose predicate includes `friendship.id = resourceId AND (user_lo_id = actorId OR user_hi_id = actorId)`; an id-addressed block action uses `block.id = resourceId AND blocker_id = actorId`. Thus an unauthorized id reveals no counterpart ids and cannot lock unrelated users. The probe reads no status beyond the ownership predicate. After the user locks, the command re-reads and locks the friendship/block row, verifies its id still maps to that actor-authorized pair, and only then decides state. A missing, unauthorized, changed, or failed re-read returns the same documented resource-specific 404 shape.

This gives a lock even when no relationship row exists, makes crossed requests deterministic, serializes request/block/deletion races, and avoids an advisory-lock hash namespace. The non-authoritative id probe prevents the opposite lock orders that would arise if an action locked its relationship row merely to discover the pair. The DB constraints remain mandatory backstops, not substitutes. The established deletion executor locks its subject user first; a pair command either commits before that lock (and its new row is in deletion scope) or waits, sees the tombstone, and returns the hidden 404.

Lock order after the user pair is: friendship rows by `(user_lo_id, user_hi_id, id)`; block rows by `(LEAST(blocker_id, blocked_id), GREATEST(blocker_id, blocked_id), blocker_id, blocked_id, id)`; session parents by id; poll parents by id. No Phase 3 or inheriting Phase 4/6 code may acquire these in another order.

Steam import is the set-valued extension of the same rule. After the upstream call and initial Steam-ID-to-local-user projection, an attempt locks the importer plus every projected candidate `users` row in one global id order. Under those locks it re-reads the importer's current linked Steam identity and every imported Steam ID's current `steam_identities` owner, as well as block/lifecycle/friendship eligibility. If any candidate ownership re-read produces a different candidate-user lock set, the attempt rolls back with no writes and repeats projection plus globally ordered locking; it never incrementally locks a newly discovered candidate. If the importer was unlinked, the attempt returns `steam_not_linked`; if the importer was relinked to a different Steam ID, the stale upstream result is discarded and the import restarts its network call outside the transaction. Steam link, unlink, and relink transactions must lock their affected `users` row before changing A-030's atomic auth-login/platform-link/`steam_identities` facts. The final stable ownership and eligibility evaluation under every required user lock, immediately before notification insertion and commit, is the import linearization point, and the response is built from that locked snapshot. No Steam/network call occurs while database locks are held. Import-first may return its locked-snapshot match; unlink/relink/block-first cannot disclose the stale PartyUp identity or create its match notification.

### A-055 — One hidden-user resolver owns all fail-closed profile, code, request, Steam-match, and block decisions

Application owns `IUserGraphResolver`; Infrastructure implements exact-username, normalized-friend-code, and Steam-ID candidate reads. It loads the candidate, pairwise block facts, accepted/pending relationship, lifecycle, and ban facts, then invokes `LookupVisibility`/`ProfileVisibility`. A hidden result carries no reason. Its block-target operation has one safety-only exception: a banned counterpart already present in the actor's accepted/pending pair may be blocked from that relationship surface; an unrelated banned username remains hidden. This is not general lookup disclosure.

Endpoints map `Hidden` and missing to the same response: 404 `user_not_found`, with the same helper, serializer, fields, and headers. They never throw `DomainError.Blocked`; they never include the domain message; logs may record only `hidden_or_missing`, viewer id, operation id, and trace id—never “blocked by.” The valid-identifier query shape is the same for blocked and missing candidates; timing is not padded, but the response bytes contain no distinguishing fact.

### A-056 — Friendship, block, and notification persistence share the Phase 2 scoped transaction

Repositories are scoped over the existing `AppDbContext`. A command drains the relevant domain event, computes recipients, materializes `Notification` rows, applies explicit deletes returned by domain services, and returns only after `UnitOfWork` commits. There is no outbox or post-commit dispatcher in Phase 3 because there is no transport. A failure inserting a required notification rolls back the relationship mutation (A-016).

### A-057 — Content block effects are invoked through one stable executor seam

`BlockUserUseCase` calls only `IBlockEffectsExecutor.ExecuteAsync(Block, pairFriendships, CancellationToken)`. The Application implementation depends on `IBlockEffectScopeProvider.LoadForUpdateAsync(UserId first, UserId second, CancellationToken)`, `IFriendshipRepository`, `IReminderScheduleWriter`, and `IClock`; it loads the complete tracked scope, calls `BlockEffects.Apply`, removes returned friendships, applies every returned reminder cancellation, and permanently drains/suppresses every session/poll domain event raised solely by those block-driven mutations before returning. It never hands those derived events to notification materialization in any phase: D-105 makes blocking silent. This permanent disposition is part of the executor contract in §10.1; there is no event-disposition port and later phases must not introduce one for these events. Phase 3 registers `EmptyBlockEffectScopeProvider` plus `NoSessionReminderScheduleWriter` (which throws if called, exposing a misconfigured non-empty scope). Phases 4/6 replace only the scope-provider and reminder-writer adapters; the endpoint, use case, executor, suppression contract, and domain call do not change. `P4-NOTIFY-01` requires Phase 4 to reverify the permanent suppression when it adds session notification materialization, not to swap or revisit it.

### P3-LK-01 — Shared block/attachment lock protocol (cross-phase convention)

This named convention is part of the Phase 4/6 command contract, not merely an implementation detail of blocking. `BlockUserUseCase` and every future command that creates, restores, approves, invites, joins, or otherwise makes organizer/counterpart participation or a join request live in a session or poll must use one A-016 transaction and acquire the same locks in the same order:

1. Lock the organizer/counterpart `users` pair in global user-id order. A bulk invitation/attachment locks the organizer plus every counterpart in one globally ordered set, not pair-by-pair.
2. Lock all applicable friendship rows by `(user_lo_id, user_hi_id, id)`; then all either-direction block rows by `(LEAST(blocker_id, blocked_id), GREATEST(blocker_id, blocked_id), blocker_id, blocked_id, id)`.
3. Lock session parents by `session.id` order; then poll parents by `poll.id` order. A single-parent command still follows this type order and may not lock its parent before steps 1–2; a bulk command locks each parent set globally, never request-input order.
4. Load the complete children needed by the command/domain rule, re-evaluate either-direction block and lifecycle eligibility under those locks, then mutate and commit. A block makes the attachment fail closed; no command may rely on an earlier visibility check.

No network call occurs while these locks are held. If attachment owns the user pair first, block waits and its scope query sees and removes/lapses the committed attachment. If block owns the pair first, attachment waits, re-checks the committed block, and creates nothing. Phase 4 session slices and Phase 6 poll slices must cite `P3-LK-01`; they may add parent-specific locks inside step 3 but may not invert or bypass steps 1–4.

### A-058 — Profile disclosure and management disclosure are separate read models

Profile/add/Steam surfaces use `ProfileVisibility`; before acceptance their identity payload has exactly `username`. The outbound blocked list is a private management read authorized by `blocker_id = current user` and returns the counterpart's current username only. It never returns a profile URL, display name, avatar, platform data, friend code, or inbound block rows.

### A-059 — Phase 3 adds three chained rate-limit members

`friend-lookup` is 20 permits/user/minute, `friend-request-send` is 10/user/hour, and `steam-friend-import` is 5/user/15 minutes. They compose with the existing 300/IP/minute default. `BlockUser` consumes `friend-lookup` because exact-username block targeting is a lookup; its lease is acquired before reading the target or body-dependent state and remains target-independent. The limiter keys only on the authenticated `NameIdentifier`; it never reads a body or database. Exact route membership and the 429 shape are in §9.7.

### A-060 — Steam import is live, non-correlating, and notification-idempotent

The adapter calls `ISteamUser/GetFriendList/v0001`; a 401/private result is `PrivateProfile` only after a keyed `GetPlayerSummaries` probe succeeds, otherwise it is `UpstreamFailure`. Successful friend IDs are optionally enriched through batched summaries, matched against the existing `steam_identities` table, and never persisted as an import row. Optional enrichment failure degrades persona/avatar fields to null and never changes a successful friend-list result into `upstream_failure`. Eligibility under the globally ordered user locks is the import linearization point. A partial notification index unconditionally makes each importer/matched-user notification first-observation-only. There is no hook when another user later registers or links Steam.

### A-061 — QR generation is a web-only SVG dependency

Add the small `react-qr-code` npm dependency and render the exact add URL as SVG in the SPA. Do not add a .NET package and do not hand-roll QR encoding: standards/error-correction work is security-insensitive but easy to get wrong, while an SVG component is inspectable, printable, and avoids canvas pixel/scale problems. The actual URL is always printed and copyable next to the SVG, so the QR is not the sole accessible representation. Pin the resolved version in `web/package-lock.json` and run the repository dependency audit in the implementation slice.

### A-062 — `PublicBaseUrl` is the one share-link origin

Application exposes a validated `PublicLinkBuilder` using the existing `PublicBaseUrl`, trimmed of its trailing slash. It builds `/u/{escaped current stored username}` and `/add/{canonical friend code}`; username resolution remains case-insensitive through the registry canonical form. API responses carry those absolute share URLs so web and future mobile clients do not recreate deployment configuration. Friend codes remain discovery identifiers, never authorization credentials (A-015/A-035).

### A-063 — The onboarding friends step follows profile activation without new persisted onboarding state

The Phase 2 wizard submits `POST /onboarding/complete` when advancing from platform linking to friends, refreshes session/antiforgery state, then renders `/onboarding/friends` for the now-Active user. A server-validated `/u/{username}` or `/add/{code}` continuation survives anonymous registration and incomplete-account onboarding; after activation the final step hydrates its profile action without auto-sending a request. The step also offers discovery/share/import actions and a “Finish”/“Skip for now” route to `/app`. Direct entry or reload by any Active user deterministically renders `/onboarding/friends`; it never redirects merely because activation already occurred. Completion of this optional education screen is not persisted. If the post-completion refresh fails, the route retains the continuation in a localized retry state and invokes no friend API until session and antiforgery refresh succeeds. This preserves the D-127 order and the ratified rule that `Onboarding` users cannot send/receive requests.

---

## 5. Database schema and migration

One forward-only migration, `AddFriendGraph`, creates all three tables. DDL below is normative in columns, constraints, and indexes; generated migration syntax may differ.

### 5.1 `friendships`

```sql
CREATE TABLE friendships (
    id uuid PRIMARY KEY,
    user_lo_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    user_hi_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    requested_by uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    status smallint NOT NULL,
    requested_at timestamptz NOT NULL,
    accepted_at timestamptz NULL,
    CONSTRAINT ck_friendships_canonical CHECK (user_lo_id < user_hi_id),
    CONSTRAINT ck_friendships_requester CHECK (requested_by IN (user_lo_id, user_hi_id)),
    CONSTRAINT ck_friendships_status CHECK (status IN (0, 1)),
    CONSTRAINT ck_friendships_accept_time CHECK (
        (status = 0 AND accepted_at IS NULL) OR
        (status = 1 AND accepted_at IS NOT NULL)
    ),
    CONSTRAINT ux_friendships_pair UNIQUE (user_lo_id, user_hi_id)
);
CREATE INDEX ix_friendships_lo_status ON friendships(user_lo_id, status);
CREATE INDEX ix_friendships_hi_status ON friendships(user_hi_id, status);
CREATE INDEX ix_friendships_pending_requested_by ON friendships(requested_by, requested_at DESC)
    WHERE status = 0;
```

`FriendshipConfiguration` maps `FriendshipId`/`UserId`, NodaTime `Instant`, enum values `Pending=0`, `Accepted=1`, field access, and PostgreSQL `xmin` as an optimistic concurrency token. A-054 is the normal command guard; `xmin` catches any out-of-contract tracked update.

Accepted friend count for D-179 is a two-index query: count `status=1` rows where `user_lo_id=@id` plus the equivalent `user_hi_id` count. No admin endpoint ships.

### 5.2 `blocks`

```sql
CREATE TABLE blocks (
    id uuid PRIMARY KEY,
    blocker_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    blocked_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL,
    CONSTRAINT ck_blocks_not_self CHECK (blocker_id <> blocked_id),
    CONSTRAINT ux_blocks_ordered_pair UNIQUE (blocker_id, blocked_id)
);
CREATE INDEX ix_blocks_blocker_created ON blocks(blocker_id, created_at DESC);
CREATE INDEX ix_blocks_blocked ON blocks(blocked_id);
```

The uniqueness is ordered as required by the Domain. A-054 normally prevents opposite-direction rows from being created through the API: the first block makes the other caller hidden. The schema still permits both ordered rows because the binding invariant is ordered uniqueness, and deletion/visibility queries must handle both safely.

### 5.3 `notifications`

```sql
CREATE TABLE notifications (
    id uuid PRIMARY KEY,
    recipient_user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    type smallint NOT NULL,
    session_id uuid NULL,
    poll_id uuid NULL,
    actor_user_id uuid NULL REFERENCES users(id) ON DELETE RESTRICT,
    option_start_time timestamptz NULL,
    created_at timestamptz NOT NULL,
    read_at timestamptz NULL,
    CONSTRAINT ck_notifications_type CHECK (type BETWEEN 0 AND 24),
    CONSTRAINT ck_notifications_friend_shape CHECK (
        type NOT IN (23, 24) OR
        (actor_user_id IS NOT NULL AND session_id IS NULL AND poll_id IS NULL AND option_start_time IS NULL)
    )
);
CREATE INDEX ix_notifications_recipient_created
    ON notifications(recipient_user_id, created_at DESC, id DESC);
CREATE UNIQUE INDEX ux_notifications_steam_match_once
    ON notifications(recipient_user_id, actor_user_id)
    WHERE type = 24;
```

The table maps the complete Phase 1 notification shape now so Phases 4/6 do not redesign it. `session_id`/`poll_id` intentionally have no FK until those tables exist; their owning migrations add the FKs. `read_at` is persisted for the already-ratified model but Phase 3 exposes no operation that reads or writes it. Notification enum values are append-only.

### 5.4 Migration and model gates

- `AppDbContext` adds `DbSet<Friendship>`, `DbSet<Block>`, and `DbSet<Notification>` and applies three configuration classes.
- Every application-generated id is created before persistence via the existing `*.New()` Guid v7 factories.
- Migration tests apply every migration to empty PostgreSQL, inspect all named constraints/indexes, and migrate a representative row set. Tests never execute against a user/shared/live database.
- A constraint violation is translated only after the use case has applied A-054. `ux_friendships_pair` maps to the same deterministic state result the locked re-read produces; it is never exposed as provider SQL text.

---

## 6. Application layer and transaction contracts

### 6.1 Ports

Application owns these contracts. Infrastructure implements persistence/external-I/O ports; `BlockEffectsExecutor` is the Application orchestration implementation of `IBlockEffectsExecutor`:

| Port | Required contract |
|---|---|
| `IUserGraphResolver` | Resolve exact canonical username, normalized friend code, or Steam IDs with viewer-relative relationship/block/disclosure; hidden carries no reason. |
| `IFriendPairLocker` | Lock and return both `User` rows in canonical id order inside the current transaction; return hidden/missing if either cannot participate in the requested operation. |
| `IFriendshipRepository` | Find canonical pair; find by id; add/remove; list accepted and pending projections for one user. Mutating methods require A-054 locks already held. |
| `IBlockRepository` | Find either-direction rows for a pair; find outbound block by id/owner; add/remove; list outbound management projections. |
| `INotificationRepository` | Add structured rows; add Steam match if absent under the partial unique index; delete rows addressed to a deletion subject. No Phase 3 read API. |
| `IBlockEffectsExecutor` | The sole block-use-case seam: load locked content scope, call `BlockEffects.Apply`, delete returned friendship rows, and apply reminder cancellations before returning. Implementation lives in Application. |
| `IBlockEffectScopeProvider` | Load and lock complete relevant session/poll aggregates as specified in §10.1. Phase 3 implementation is empty. |
| `IReminderScheduleWriter` | Apply each returned `SessionReminderCanceled` idempotently. Phase 3's adapter throws if invoked; Phase 4 replaces it with the real scheduler adapter before supplying sessions. |
| `IDeletionResultApplier` | Delete `DeletedFriendships`, `DeletedBlocks`, and each implemented `PurgedData` class in the same deletion transaction. Phase 3 implements `Notifications`; later phases append handlers. |
| `ISteamFriendListClient` | Return the provider-result union in §11.1; never leak API key/URL; cancellation propagates, provider failures become typed states. |
| `IPublicLinkBuilder` | Build absolute `/u` and `/add` links from validated `PublicBaseUrl`; no caller hard-codes an origin. |

### 6.2 Use cases

`Application/Friends/` owns `GetFriendShare`, `GetProfile`, `ResolveFriendCode`, `ListFriends`, `ListFriendRequests`, `SendFriendRequest`, `AcceptFriendRequest`, `RejectFriendRequest`, `CancelFriendRequest`, `Unfriend`, `ListBlockedUsers`, `BlockUser`, `UnblockUser`, and `ImportSteamFriends` use cases plus response records. HTTP and React types are generated from endpoint DTOs; Domain/Application do not depend on ASP.NET or EF.

Every mutating use case uses `IUnitOfWork.ExecuteAsync`. Unsafe endpoints supply no transaction of their own. Every read is `AsNoTracking` and uses one viewer-scoped projection; no endpoint builds visibility rules ad hoc.

### 6.3 Notification materialization

`SendFriendRequestUseCase` drains `FriendRequestReceived`, calls `RecipientComputer.ForFriendshipEvent(…, RecipientFilter.None)`, converts every returned request with `Notification.From`, and adds it before commit. Crossed auto-accept drains the existing `FriendshipAccepted` event but recipient computation returns empty. Accept/reject/cancel/unfriend/block/unblock therefore insert no row (D-105).

`ImportSteamFriendsUseCase` creates `SteamImportMatch` requests for eligible matched suggestions and inserts them with `AddSteamMatchIfAbsent`. The result list is returned whether the row was new or already existed. The API response is not returned until the notification transaction commits. Any required Steam-match notification write failure aborts the whole import transaction and produces the documented 500: no success result and no notification row from that invocation, including earlier inserts from a multi-match batch, may commit.

No Phase 3 code renders notification text. Future rendering uses recipient locale and `IdentityMasking`; actor IDs remain stable through tombstoning and block changes.

---

## 7. Friendship lifecycle and concurrency

### 7.1 State machine

The user-facing request actions are exactly **send / accept / reject / cancel-outgoing**; unfriend applies only after acceptance.

```text
None
  -- send --------------------------------------------------> Pending(requested_by = actor)
Pending outgoing
  -- cancel (requester) ------------------------------------> None [row deleted, silent]
  -- accept/reject by requester ----------------------------> invalid
Pending incoming
  -- accept (addressee) ------------------------------------> Accepted [silent]
  -- reject (addressee) ------------------------------------> None [row deleted, silent]
  -- send by addressee (crossed request) -------------------> Accepted [silent]
Accepted
  -- unfriend by either ------------------------------------> None [row deleted, silent]
Any existing state
  -- block by either ---------------------------------------> no Friendship row + Block row [silent]
Blocked
  -- unblock by blocker ------------------------------------> None [nothing restored]
```

### 7.2 Send algorithm (D-015/D-105)

1. Resolve `targetUsername` exactly and fail hidden/missing as §8.4.
2. Begin A-016 transaction and lock both users by A-054. Re-evaluate that the target still owns the submitted canonical username, plus lifecycle, ban, and block facts, under the locks. A concurrent username change therefore makes the stale request return `user_not_found` (D-128) rather than targeting by a superseded link.
3. If a block exists in either direction, return hidden 404. If an Accepted friendship exists, return 409 `friendship_already_exists`.
4. If a Pending row requested by the actor exists, return 409 `friend_request_already_exists`.
5. If a Pending row requested by the target exists, call `Accept(actor, clock)` and return the accepted resource with `200`; do not create a received-request notification.
6. Otherwise call `Friendship.Request`, add the row and its required notification, and return the pending resource with `201`.

### 7.3 Action authorization

- Accept/reject require a Pending row whose `AddresseeId` is the actor.
- Cancel requires a Pending row whose `RequestedBy` is the actor.
- Unfriend requires an Accepted row involving the actor.
- An id not found, a row not involving the actor, or the wrong directional action returns the same resource-specific 404. A known involved row that changed from Pending to Accepted while the request was in flight returns 409 `friend_request_not_pending`.
- Reject/cancel/unfriend physically remove the tracked row. No historical rejection/removal status is stored and no cooldown is introduced.

### 7.4 Race matrix

| Race | Discipline and result |
|---|---|
| A sends while B sends | Both lock the same user rows in the same order. First creates Pending; second observes incoming Pending and accepts. Final state is one Accepted row. Unique pair is the backstop. |
| Same actor double-sends | Serialized. First is `201`; second is 409 `friend_request_already_exists`. |
| Accept vs reject | Serialized on users and friendship. Accept-first returns 200, reject returns 409, and the final row is Accepted. Reject-first returns 204, accept returns 404, and no friendship row remains. In both orderings the one original `FriendRequestReceived` notification remains and no new notification is written. |
| Accept vs cancel | Serialized on users and friendship. Accept-first returns 200, cancel returns 409, and the final row is Accepted. Cancel-first returns 204, accept returns 404, and no friendship row remains. In both orderings the one original `FriendRequestReceived` notification remains and no new notification is written. |
| Accept vs unfriend on the same Pending id | If unfriend locks first, it observes Pending and returns 404 `friendship_not_found` with no write; accept then returns 200 and the final row is Accepted. If accept locks first, accept returns 200 and unfriend then returns 204, leaving no row. Neither ordering creates a notification; exactly the one pre-existing `FriendRequestReceived` row remains. |
| Reject vs re-request by the original requester | If re-send locks first, it returns 409 `friend_request_already_exists` with no write; reject then returns 204, leaving no friendship row and exactly the original one notification. If reject locks first, it returns 204; re-send then returns 201 with a new Pending row and exactly one new notification, for two total including the original request's row. |
| Any friendship command vs block | Same pair locks. If block wins, it deletes the relationship and later commands return hidden 404. If a friendship command wins, block runs next and deletes its result. No commit contains both friendship and a new block for the pair. |
| Block vs future session invite/join attachment | `P3-LK-01` serializes the user pair, graph rows, then parent. Attachment-first is committed and then removed/lapsed by block; block-first makes attachment fail closed and create nothing. The Phase 4 duties in §14.3 activate with those command surfaces; Phase 3 does not claim executable coverage. |
| Same/opposite-direction block creation | Same pair locks. First creates one block row and applies block effects exactly once; second is hidden, returns 404, writes nothing, and applies no effects. No friendship remains. Ordered DB uniqueness catches an out-of-contract duplicate. |
| Pair command vs deletion execution | Both require the subject `users` row. Command-first rows are loaded/deleted by deletion; deletion-first leaves a tombstone and command returns hidden 404. |
| Concurrent Steam imports | Notification partial uniqueness plus `AddSteamMatchIfAbsent` prevents duplicate rows; both responses contain the suggestion. |
| Steam import vs block/deletion | Import's stable ownership-and-eligibility evaluation under all user locks is its linearization point. Block/deletion-first means no matched result/notification. Import-first may return a matched result even if the competing command commits after import commit but before response serialization; the later command still wins current graph visibility. The response/rows must match one of those two orderings. |
| Steam import vs importer/candidate unlink or relink | Import and A-030 link mutations serialize on affected `users` rows. Import-first may return only the ownership snapshot it committed against. Unlink/relink-first is observed by the under-lock ownership re-read: an importer unlink returns `steam_not_linked`, an importer relink restarts against the new Steam ID, and candidate ownership change removes/reclassifies the stale match. A changed candidate lock set causes a write-free retry; no ordering returns a stale PartyUp identity or notification. |

---

## 8. Profile, lookup, friend-code, and sharing contracts

### 8.1 Profile disclosure (D-032/D-068/D-180)

The API defines these exact response schemas:

- `UsernameOnlyProfile`: `{ disclosure: "username_only", username, relationship }`—no user id, display name, avatar, handles, platform links, friend code, timestamps, or lifecycle fields.
- `FullProfile`: `{ disclosure: "full", username, displayName, avatar, platforms, relationship }` for self or an Accepted friend. `platforms` carries public platform/handle data already defined by D-032; it never contains auth-provider facts or Steam API keys.

- `ProfileView`: a discriminated `oneOf` of `UsernameOnlyProfile` and `FullProfile`, with `disclosure` as the discriminator.
- `UsernameProfileResolution`: `{ profile: ProfileView }`.
- `FriendCodeResolution`: `{ profile: ProfileView }`.

`relationship` is viewer-owned control state, not subject identity: `none`, `incoming_pending`, `outgoing_pending`, or `accepted`, plus `friendshipId` for the latter three. A blocked/hidden subject gets no union member at all. A banned subject is Full for self/accepted friends and hidden otherwise (A-043/D-180).

Both counterpart-resolution envelopes contain exactly the `profile` property. They never return the counterpart's `friendCode`, `addUrl`, `profileUrl`, or any other stable-code derivative, even for an accepted friend. Only the caller-owned `GET /friend-share` response may carry a friend code or share URLs.

The profile-page-only D-068 interpretation stops here. Phase 4/6 authorized rosters and voter grids continue to render display name/avatar through `IdentityMasking`; they do not call this profile DTO.

### 8.2 Canonical identifiers and links

- Username: trim and canonicalize with existing `UsernameRegistry.Canonicalize` for lookup; URLs use the currently claimed username. A username change makes the old `/u` URL return 404 immediately (D-128).
- Friend-code input: `Trim`, remove every ASCII space (`U+0020`) and hyphen (`-`), then `ToUpperInvariant`; validate exactly ten symbols from `FriendCodeFormat.Alphabet`. Other Unicode whitespace/punctuation is invalid rather than silently rewritten.
- QR payload/add link: `IPublicLinkBuilder.Add(friendCode)` → `<PublicBaseUrl-without-trailing-slash>/add/<canonical-code>`.
- Profile share link: `IPublicLinkBuilder.Profile(username)` → `<PublicBaseUrl-without-trailing-slash>/u/<escaped-current-username>`.
- Resolution grants no access and creates no state. A recipient must authenticate/onboard and explicitly send a request. The friend code is therefore an identifier, not an A-015 bearer token.

### 8.3 Resolution flows

`/u/:username` calls the exact-username profile endpoint. `/add/:friendCode` normalizes locally for display, calls the friend-code endpoint, then renders the same profile action card. A code can be pasted in the Friends/onboarding input with spaces/dashes; after successful resolution the address bar may replace to the canonical `/add/{code}` route. No GET mutates relationship state.

QR generation happens only after the caller-owned `GET /friend-share` response supplies the stable code and API-supplied absolute `addUrl`. There is no scanning permission, camera code, or decode endpoint.

### 8.4 Fail-closed response

For a valid username/code/Steam match candidate, all of these conditions collapse to hidden: missing row; onboarding/tombstoned subject; banned subject without accepted/self access; a block in either direction; or a visibility query that cannot prove disclosure. Profile/code/send/block APIs return:

```json
{
  "type": "about:blank",
  "title": "Not Found",
  "status": 404,
  "instance": "/api/v1/<the-request-path>",
  "code": "user_not_found",
  "traceId": "<request trace id>"
}
```

`detail` is omitted. The standard request-specific `instance` and `traceId` vary equally for real-missing and block-hidden requests; every other byte and header is produced by the same helper. No `blocked`, `forbidden`, direction, or relationship fact appears in status, code, body, or response headers.

This non-disclosure contract applies to every target-bearing route, not only profile lookup: username and friend-code resolution; send-to-blocker; block-target creation; accept, reject, cancel, or unfriend by an id made stale by a block; and Steam classification of a blocked local account versus the same Steam friend having no local account. For each comparison, status, normalized body bytes, headers, database/notification side effects, and consumption of every applicable chained limiter member are identical. A 429 is target-independent because limiter acquisition happens before body, route-target, Steam-candidate, or database resolution; the same route consumes or rejects the same leases regardless of whether its target is missing, blocked in either direction, or visible.

---

## 9. HTTP API — complete inventory

All routes are under `/api/v1`, use minimal APIs, have stable operation ids in `ApiOperationIds`, require the existing cookie auth and `RequireOnboarded` policy, and participate in the shared antiforgery filter for unsafe methods. Responses are explicitly registered in OpenAPI. JSON enums are documented as the exact strings below even if persistence enums are numeric.

### 9.1 Cross-cutting problem contract

All failures use `application/problem+json` with `type`, `title`, `status`, `instance`, `code`, and `traceId`; validation may add `errors`. Phase 3 endpoints use these exact mappings:

| Status/code | Meaning |
|---|---|
| 400 `validation_failed` | Malformed JSON or field validation not covered by a more specific code; `errors` keys are request-property names. |
| 400 `friend_code_invalid` | Normalized code is not ten allowed symbols. |
| 400 `friend_request_self` | Actor targets their own current username. |
| 400 `block_self` | Actor targets themselves for blocking. |
| 400 `antiforgery_failed` | Existing unsafe-request filter rejected the token. |
| 401 `unauthorized` | No valid session. No endpoint redirects to HTML. |
| 403 `onboarding_required` | Authenticated caller is not Active/PendingDeletion with a complete profile. This is caller state, not a block result. |
| 404 `user_not_found` | Missing or hidden lookup/request/block target (§8.4). |
| 404 `friend_request_not_found` | Request id missing, not actor-owned, wrong direction, or removed by a block/deletion. |
| 404 `friendship_not_found` | Accepted friendship id missing/not actor-owned or removed by a block/deletion. |
| 404 `block_not_found` | Block id missing or not an outbound block owned by the actor. |
| 409 `friend_request_already_exists` | Same actor already has an outgoing Pending row. |
| 409 `friendship_already_exists` | Pair is already Accepted. |
| 409 `friend_request_not_pending` | Actor-owned request became Accepted before this action acquired locks. |
| 409 `steam_not_linked` | Caller has no Steam platform/auth identity from which to import. |
| 415 `unsupported_media_type` | A request with a body uses a missing or unsupported `Content-Type`. |
| 429 `rate_limited` | A chained rate dimension rejected; includes `Retry-After`. |
| 500 `internal_error` | Unexpected failure. It contains no exception/provider/SQL detail. |

Any block-influenced branch uses only the three 404 codes above according to the resource addressed; `DomainError.Blocked` never reaches `DomainExceptionMapper` on these routes.

Route ids are parsed through the Phase 3 endpoint helper rather than relying on framework Guid-binding short circuits: a malformed `friendshipId` returns the documented `friend_request_not_found` or `friendship_not_found` for that route, and a malformed `blockId` returns `block_not_found`, all as problem+json. Missing/invalid body properties and malformed JSON use 400 `validation_failed`; a missing or unsupported body content type uses 415 `unsupported_media_type`. Both binding failures use exactly the required fields from §9.1, never `detail` or the legacy `invalid_request` code. Thus every endpoint-owned failure reaches the table above instead of an undocumented bare 400/404/415.

This normalization requires one narrow global delta because `RequestBindingExceptionMapper` runs before route-local handlers: Slice 5 changes its output for every route, including Phase 2 routes, to the uniform shapes above. Existing Phase 2 success behavior, validation rules, status codes other than malformed-body 400/unsupported-media-type 415, and route-specific problem codes do not change. A dedicated Phase 2 regression duty proves the global delta is limited to binding-error shape uniformity.

### 9.2 Profile and resolution

| Verb and route | Operation id | Success | Endpoint-specific failures |
|---|---|---|---|
| `GET /friend-share` | `GetFriendShare` | `200 { friendCode, addUrl, profileUrl }` for the caller; all URLs are absolute and server-built | Common 401/403/429/500. |
| `GET /users/by-username/{username}` | `GetUserProfile` | `200 UsernameProfileResolution` (§8.1) | `404 user_not_found`; common 401/403/429/500. Invalid/empty canonical usernames also use this 404. |
| `GET /friend-codes/{friendCode}` | `ResolveFriendCode` | `200 FriendCodeResolution` (§8.1); contains no friend code or URL | `400 friend_code_invalid`; `404 user_not_found`; common 401/403/429/500. |

### 9.3 Friend and request reads

| Verb and route | Operation id | Success | Endpoint-specific failures |
|---|---|---|---|
| `GET /friends` | `ListFriends` | `200 { items: FriendItem[] }`; accepted only, sorted by display name then username | Common 401/403/429/500. |
| `GET /friend-requests` | `ListFriendRequests` | `200 { incoming: FriendRequestItem[], outgoing: FriendRequestItem[] }`; each sorted newest first | Common 401/403/429/500. |

`FriendItem` is `{ friendshipId, username, displayName, avatar, platforms }`. `FriendRequestItem` is `{ friendshipId, username, requestedAt }`; it deliberately carries username-only identity before acceptance.

### 9.4 Friendship commands

| Verb and route | Operation id | Success | Endpoint-specific failures |
|---|---|---|---|
| `POST /friend-requests` body `{ targetUsername }` | `SendFriendRequest` | `201 FriendshipResource` for a new Pending row with no `Location` header; `200 FriendshipResource` with `status:"accepted"` for crossed auto-accept | 400 `friend_request_self`/`validation_failed`; 404 `user_not_found`; 409 `friend_request_already_exists`/`friendship_already_exists`; common unsafe/auth/rate/500. |
| `POST /friend-requests/{friendshipId}/accept` | `AcceptFriendRequest` | `200 FriendshipResource` with `status:"accepted"` | 404 `friend_request_not_found`; 409 `friend_request_not_pending`; common unsafe/auth/rate/500. |
| `POST /friend-requests/{friendshipId}/reject` | `RejectFriendRequest` | `204` | 404 `friend_request_not_found`; 409 `friend_request_not_pending`; common unsafe/auth/rate/500. |
| `DELETE /friend-requests/{friendshipId}` | `CancelFriendRequest` | `204` | 404 `friend_request_not_found`; 409 `friend_request_not_pending`; common unsafe/auth/rate/500. |
| `DELETE /friends/{friendshipId}` | `Unfriend` | `204` | 404 `friendship_not_found`; common unsafe/auth/rate/500. |

`FriendshipResource` is `{ friendshipId, status:"pending"|"accepted", direction:"outgoing"|"incoming"|"accepted", counterpartUsername, requestedAt, acceptedAt? }`. It never exposes counterpart user id.

### 9.5 Blocking

| Verb and route | Operation id | Success | Endpoint-specific failures |
|---|---|---|---|
| `GET /blocks` | `ListBlocks` | `200 { items: BlockedUserItem[] }`, outbound only, newest first | Common 401/403/429/500. |
| `POST /blocks` body `{ targetUsername }` | `BlockUser` | `201 BlockedUserItem` with no `Location` header | 400 `block_self`/`validation_failed`; 404 `user_not_found`; common unsafe/auth/rate/500. |
| `DELETE /blocks/{blockId}` | `UnblockUser` | `204` | 404 `block_not_found`; common unsafe/auth/rate/500. |

`BlockedUserItem` is exactly `{ blockId, username, createdAt }`. It is generated only after `blocker_id == currentUserId` is proven. It is not a `ProfileView`, carries no profile link, and inbound blocks never appear.

Blocking a current friend or pending counterpart is allowed even if that counterpart has since been banned, because the actor already has a direct relationship row and blocking is a safety action. An unrelated banned/hidden username returns `user_not_found`. Creating a block against an already hidden pair also returns `user_not_found`; unblock is performed only by block id from the private list.

### 9.6 Steam import

| Verb and route | Operation id | Success | Endpoint-specific failures |
|---|---|---|---|
| `POST /friends/imports/steam` | `ImportSteamFriends` | Always `200 SteamFriendImportResult` for `ready`, `private_profile`, `zero_friends`, `zero_matches`, `not_configured`, or `upstream_failure` | 409 `steam_not_linked`; common unsafe/auth/rate/500. |

The five provider empty/error states are not `ProblemDetails`; they are renderable data. `upstream_failure` says only `retryable:true` and exposes no Steam status/body/URL. Local transaction/persistence faults remain 500 because the response cannot truthfully claim notifications committed.

### 9.7 Rate-limit route membership and 429

Add these members to `RateLimitPolicies.Dimensions` and keep the existing chained/global design:

| Dimension | Limit/key | Operation ids |
|---|---|---|
| `friend-lookup` | 20/minute/authenticated user | `GetUserProfile`, `ResolveFriendCode`, `BlockUser`, `ImportSteamFriends` |
| `friend-request-send` | 10/hour/authenticated user | `SendFriendRequest` only |
| `steam-friend-import` | 5/15 minutes/authenticated user | `ImportSteamFriends` in addition to `friend-lookup` |
| existing `default` | 300/minute/IP | Every `/api/v1` operation, including all above |

Accept/reject/cancel/unfriend/unblock are bounded by `default`; `BlockUser` must satisfy both `friend-lookup` and `default`, while only re-request spam needs the dedicated send ceiling. `BlockUser` acquires the target-independent lookup lease before target resolution, exactly like the other target-bearing lookup operations. A route under two members must satisfy both. No endpoint calls `RequireRateLimiting`.

The existing rejection shape remains normative:

```json
{
  "type": "about:blank",
  "title": "Too Many Requests",
  "status": 429,
  "instance": "/api/v1/<the-request-path>",
  "code": "rate_limited",
  "traceId": "<request trace id>"
}
```

`Retry-After` contains whole seconds from the rejecting lease. The SPA maps the code to localized copy and may show the duration; it never retries a mutating request automatically.

### 9.8 OpenAPI duties

- Every route calls `.WithName`, `.Produces<T>`, and every applicable `.ProducesProblem` status from the tables.
- `ProfileView` and `SteamFriendImportResult` are discriminated `oneOf` schemas, not nullable bags. `UsernameProfileResolution` and `FriendCodeResolution` are distinct named object envelopes whose sole required property is `profile: ProfileView`; neither schema permits additional properties, a friend code, or any URL.
- The existing cookie scheme and antiforgery header transformer applies unchanged.
- Run `npm run gen:api` while the implementation API is running after each endpoint slice; commit `web/src/api/schema.d.ts` with the endpoint change.
- No response exists solely to simplify React. Absolute share links, relationship state, and first-class provider results are also useful to future mobile clients.

---

## 10. Block effects, deletion, and notification boundaries

### 10.1 Stable block-effect seam (D-065/D-074/D-077)

`BlockUserUseCase`, inside the A-016 transaction, performs this order:

1. Resolve target and lock both users (A-054); re-evaluate submitted-username ownership and hidden/relationship state.
2. Load the pair's tracked friendship row and either-direction block rows.
3. Create `Block` but do not save yet.
4. Call `IBlockEffectsExecutor.ExecuteAsync`. The executor calls `IBlockEffectScopeProvider.LoadForUpdateAsync`; after the pair/graph locks already acquired by steps 1–2, the provider must continue `P3-LK-01`:
   - select every materialized non-terminal session organized by either party where the other has a non-Removed participation or a Pending join request;
   - include logically ended but unswept parents so `BlockEffects` performs its clock check;
   - lock session parent rows by `id` before loading their complete participations/join requests;
   - do the analogous poll selection/parent lock/complete load;
   - never filter children needed by the domain service;
   - return tracked aggregates in deterministic id order.
5. The executor calls `BlockEffects.Apply(block, pairFriendships, scope.Sessions, scope.Polls, clock)` exactly once, removes every `DeletedFriendship` through the repository, sends every `CanceledReminder` to `IReminderScheduleWriter`, then drains and discards every domain event raised on the tracked session/poll aggregates by those block-driven mutations before returning. Tracked session/poll mutations need no separate copy/write model. In particular, a derived `SessionUnconfirmed` is never materialized as a notification in any phase; D-105's explicit silent-block rule permanently controls this executor contract.
6. The use case adds the block and materializes no notification.
7. Commit.

Phase 3's `EmptyBlockEffectScopeProvider` obeys the interface and returns `([], [])`; a contract test proves the use case still invokes the executor, provider, and domain service. An executor test with a real in-memory session scope proves block-driven derived events are drained and produce no notification-materialization call. `NoSessionReminderScheduleWriter` is valid only because an empty scope cannot produce cancellations and throws if that premise is broken. Phase 4 replaces the provider with `SessionBlockEffectScopeProvider` and the writer with its real idempotent scheduler adapter; Phase 6 decorates/replaces the provider to add polls. Neither phase changes the endpoint, use-case sequence, executor, permanent D-105 suppression, `P3-LK-01` lock order, or `BlockEffects.Apply` call. No event-disposition port exists or may be added to route those derived events. Phase 4 carry item `P4-NOTIFY-01` must reverify the permanent suppression against its session notification materializer; it cannot continue-versus-replace, swap, or otherwise reopen that contract.

The provider selects only content organized by either member of the blocked pair. It must not load/remove a third-party-organized session or poll merely because both parties participate. Those rows coexist, continue counting normally, and are rendered pairwise through the existing `Viewer` block set and `IdentityMasking` (D-076/D-086). Infrastructure block reads always treat either ordered direction as a blocked pair.

The provider's content predicate is safe from an uncommitted attachment only because every attachment command also obeys `P3-LK-01`. Phase 4 must activate deterministic block-versus-invite and block-versus-join races before claiming its session wiring complete; Phase 6 applies the same convention to poll participation. Those future duties are written in §14.3 but are not Phase 3 coverage.

`UnblockUserUseCase` resolves an outbound block by id, locks both users, calls `BlockEffects.Unblock` (which returns none), deletes the block row, and commits. It never loads content and restores nothing.

### 10.2 Deletion-scope extension (D-092/D-148)

`DeletionScopeProvider.LoadAsync(subject)`—already called after `users(subject)` is locked—now locks shared graph rows with `SELECT ... FOR UPDATE` and loads them tracked. Friendships are locked in `(user_lo_id, user_hi_id, id)` canonical-pair order. Blocks are locked in `(LEAST(blocker_id, blocked_id), GREATEST(blocker_id, blocked_id), blocker_id, blocked_id, id)` order. Every deletion executor uses friendship rows before block rows; it may not rely on unordered EF materialization.

- every friendship where `user_lo_id = subject OR user_hi_id = subject`, regardless of Pending/Accepted;
- every block where `blocker_id = subject OR blocked_id = subject`;
- the existing username entry;
- empty session/poll lists until their phases.

`ExecuteDeletionForUserUseCase` calls `IDeletionResultApplier.ApplyAsync(result, subject)` before credential purge/journal commit. The Phase 3 applier removes `result.DeletedFriendships` and `result.DeletedBlocks`, and deletes `notifications WHERE recipient_user_id = subject` because `result.PurgedData` includes `Notifications`. Notifications where the subject is only `actor_user_id` remain structured and later render through the tombstone mask.

All deletes, the user tombstone, credential purge, and journal append remain in the existing single per-user transaction. No cleanup runs when deletion is requested or during grace. A successful login cancellation before expiry therefore leaves every Phase 3 row untouched.

If both members of one relationship reach grace expiry concurrently, each executor first owns its subject-user lock and then contends for shared graph rows in the deterministic order above. The winner deletes each shared friendship/block row once. The loser waits, treats a row already deleted by the committed winner as an already-satisfied deletion effect rather than an `xmin` conflict, continues its own tombstone/purge/journal work, and also succeeds. Final state has both users tombstoned, no graph row between them, and one successful deletion journal entry per subject.

### 10.3 Notification behavior (D-105/A-016)

| Cause | Row(s) now | User-visible Phase 3 surface |
|---|---|---|
| New friend request | One `FriendRequestReceived` addressed to target, actor=requester, same transaction | Target acts from `/friends` incoming list; no notification center. |
| Crossed request auto-accept | None | Both see accepted friend after query invalidation. |
| Accept/reject/cancel/unfriend/block/unblock | None | Lists/profile update only. |
| Session/poll events derived solely from block-driven removals | Permanently drained and suppressed by `BlockEffectsExecutor`; reminder cancellations still apply | None in any phase under D-105; `P4-NOTIFY-01` reverifies this when session notifications exist and cannot replace it. |
| Eligible Steam match | At most one `SteamImportMatch` per importer+matched user (decided ruling P3-01) | Import result card; no center. |
| Deletion grace expiry | Delete rows addressed to deleting user | No UI; structured actor refs elsewhere survive/tombstone-mask. |

No `ReadAt` command, unread count query, grouping, preference, or transport dispatch is allowed in Phase 3.

---

## 11. Steam friend import (D-049/D-105)

### 11.1 Infrastructure client contract

`ISteamFriendListClient.GetAsync(steamId64, ct)` returns one of:

- `NotConfigured` when `Steam:ApiKey` is blank. Steam sign-in remains configured independently, as Phase 2 requires.
- `PrivateProfile` only when `GetFriendList` returns 401/private and a keyed `GetPlayerSummaries` probe for the importer succeeds, proving the API key is valid.
- `Success(IReadOnlyList<SteamFriend>)`, including an empty list. `SteamFriend` contains `SteamId64`, optional public persona name, and optional avatar URL. Missing or failed enrichment leaves the optional fields null without changing this result.
- `UpstreamFailure` when the primary `GetFriendList` call times out, has a transport failure, returns a non-private non-success status, or has malformed friend-list JSON; it also covers a 401/private response whose keyed `GetPlayerSummaries` validity probe does not succeed, including an invalid API key.

The primary request is `https://api.steampowered.com/ISteamUser/GetFriendList/v0001/?key=…&steamid=…&relationship=friend`. On its 401/private response, the adapter calls keyed `ISteamUser/GetPlayerSummaries/v0002` for the importer as a success probe before classifying the result: a successful, valid response yields `PrivateProfile`; any non-success/transport/timeout/malformed probe result yields `UpstreamFailure`, never `PrivateProfile`. After a successful friend-list response, enrichment batches that same summaries endpoint rather than issuing one request per friend. If optional enrichment times out, fails transport/status/parse validation, or omits a player summary, the adapter preserves the successful friend-list IDs and returns null persona/avatar for every friend that was not safely enriched; it never maps optional enrichment failure to `upstream_failure`. `SteamApiClient.Sanitize` remains the only logging path; no exception, telemetry, result, or problem body contains the API key or raw upstream body. Caller cancellation from either HTTP request propagates rather than being rewritten as `upstream_failure` or degraded data.

### 11.2 Matching and filtering

For `Success`:

1. Deduplicate Steam IDs and discard the importer's own id.
2. Query `steam_identities` in one set-based no-tracking projection to identify local candidate user ids.
3. Start an ownership-validation transaction and lock the importer plus every initially projected local candidate in global user-id order (A-054).
4. While those user locks are held, re-read the importer's current linked Steam ID and the current `steam_identities` owner for every deduplicated imported Steam ID. If the importer is now unlinked, return `steam_not_linked` with no writes. If the importer is linked to a different Steam ID, roll back, discard the stale upstream result, and restart from the network call outside locks. If candidate ownership now implies any different user-id lock set, roll back with no notification/response state and retry steps 2–4 using a fresh projection and one globally ordered lock acquisition; never append locks to the stale set.
5. Once importer identity and candidate ownership are stable under all required user locks, reload candidate lifecycle plus pairwise friendship/block facts. The final `LookupVisibility.CanMatchSteamImport` evaluation immediately before writes/commit is the import linearization point. Construct the response from this locked ownership-and-eligibility snapshot even if another transaction commits after import commit and before serialization.
6. An Active, unbanned, unblocked local candidate with neither an Accepted friendship nor a Pending pair becomes an actionable `SteamMatchedUser { username }`—username only before acceptance.
7. A known visible local candidate whose pair is already Accepted or Pending is omitted from both `matches` and `unmatched`; that user is already actionable/visible on `/friends`, so import supplies no second action or invitation path. A Steam id with no local candidate, or whose local candidate is hidden because it is onboarding, tombstoned, banned, or blocked in either direction, becomes `SteamUnmatchedFriend` with optional Steam display/avatar data and the importer's own absolute `/add/{friendCode}` invitation URL.

This card taxonomy is exhaustive after self-removal and deduplication: eligible visible candidates produce actionable match cards; known visible Accepted/Pending candidates produce no card; absent or hidden candidates produce indistinguishable unmatched invitation cards. Hidden accounts classify exactly like absent accounts in response fields, effects, and limiter behavior—never as omitted or marked “blocked”—so Steam matching leaks no PartyUp membership or block state.

### 11.3 Result union

| `state` | Exact condition and fields |
|---|---|
| `ready` | At least one eligible `matches` entry. Includes `matches`, `unmatched`, `invitationUrl`. |
| `private_profile` | Typed private result with empty arrays and invitation URL. The client maps the state to its own localized help copy. |
| `zero_friends` | Successful list with zero Steam friends. Includes empty arrays and invitation URL. |
| `zero_matches` | Steam friends exist but no eligible visible local matches. Includes the exhaustive `unmatched` array—which may be empty when every friend was omitted as an already Accepted/Pending PartyUp user—and invitation URL. |
| `not_configured` | API key absent. Includes no provider detail and invitation URL so sharing still works. |
| `upstream_failure` | Typed provider/parse/timeout failure. Includes `retryable:true` and invitation URL; no status/body. |

Every state is HTTP 200 and has localized page copy. The API never claims that an unmatched person is unregistered. There is no refresh job, persisted import, webhook, later-registration hook, or notification to the matched user.

---

## 12. Web UI, routing, onboarding, i18n, and accessibility

### 12.1 Routes and guards

```text
/friends                       RequireAuth + RequireOnboarded; friends + incoming/outgoing requests
/u/:username                   RequireAuth + RequireOnboarded; exact profile disclosure/action card
/add/:friendCode               RequireAuth + RequireOnboarded; code resolution/action card
/friends/import/steam          RequireAuth + RequireOnboarded; Steam import states/results
/settings/blocked              RequireAuth + RequireOnboarded; outbound block management
/onboarding/friends            RequireAuth; requires complete draft, activates profile before rendering actions
```

The authenticated application shell in `web/src/App.tsx` persistently exposes a Friends navigation link to `/friends` from `/app` and every onboarded route. The existing profile/account settings surfaces persistently expose a Blocked users settings link to `/settings/blocked`. Both entries use localized en/pt-BR/es labels, remain keyboard reachable, expose the current destination with `aria-current="page"`, and do not depend on onboarding-only cards, deep links, or Steam state. Slice 8 owns these navigation changes and route-reachability tests.

`RequireAuth` preserves the complete path/query in `next`. The web `friendContinuation` helper accepts only a same-origin relative `/u/{username}` or `/add/{friendCode}` path with exactly one identifier segment and no query/fragment; it validates usernames with the existing username rules, normalizes friend codes by §8.2, canonicalizes the path, and rejects absolute URLs, protocol-relative values, traversal/encoded separators, extra segments, malformed identifiers, and unrelated routes. Login, registration, age-gate, magic-link/external callbacks, `RequireOnboarded`, and every onboarding navigation carry only that validated continuation as encoded `next`, never as an external URL. After authentication an Active account returns to the validated `/u` or `/add` destination. An incomplete account returns to its earliest missing onboarding profile step with either continuation intact. Invalid continuation input is discarded to the existing safe default. No protected content flashes while session/profile queries load.

The server is authoritative for email-mediated continuation. A shared Application `FriendContinuation` validator applies the same two-route grammar before either `POST /auth/register` or `POST /auth/magic-link` issues a token. `RegisterRequest` and `MagicLinkRequest` gain nullable `continuation`; invalid values are dropped rather than reflected. `IssueTokenRequest`/`TokenRedemption` and the serialized email-token payload gain nullable `Continuation`, independently of `PendingRegistration`, so both existing-account magic links and pre-account registration/magic links carry it. The email model renders the already-validated value in the fragment as `#token=<raw>&next=<percent-encoded-relative-path>` for all three-locale `EmailVerification` and `MagicLink` HTML/text templates (`&amp;next=` in HTML source, `&next=` in text); neither token nor continuation enters a query string. The fragment `next` lets the hash-clearing page retain navigation through an age-gate retry, but it is not authority.

`VerifyEmailUseCase` and `ConsumeMagicLinkUseCase` propagate only the continuation recovered from the redeemed token. Their success path returns it as nullable `continuation` on `SessionResponse`; `CreateSessionAsync` accepts that server value only for these two token-consume operations. The web clears the fragment before render, posts the token, and uses the response continuation—not local/session storage or the fragment copy—to choose the Active destination or seed the incomplete-account onboarding URL. Thus a link opened in a different tab/browser context still carries the destination, and a forged fragment `next` cannot override the token payload.

External-provider continuation validation is extended at the server boundary in `src/PartyUp.Api/Auth/ReturnToValidator.cs`. Its four existing fixed paths remain exact allowlist entries; it additionally accepts only `/u/{username}` and `/add/{code}` with exactly one non-empty identifier segment, no query or fragment, no encoded or literal separator/traversal, and no absolute or protocol-relative form. The username must satisfy the existing username grammar; the friend code must normalize and canonicalize under §8.2. Every other value falls back to the existing safe default, so the dynamic routes add no open-redirect surface. Both Google and Steam callback completions carry only this sanitized/canonical value: Active accounts continue to the friend route, while incomplete accounts continue to their earliest onboarding step with the same validated friend route retained.

### 12.2 Screens and interaction

**Friends screen:** one page heading; three labelled sections/tabs with counts: Friends, Incoming, Outgoing. Incoming rows offer Accept/Reject; outgoing offers Cancel; friend rows link to `/u/{username}`, offer Unfriend and Block. Unfriend always opens a focus-trapped confirmation dialog explaining that shared sessions are unchanged. Shared mutation helpers receive the affected canonical username and, when the action came from a resolved add route/card, canonical friend code. Every successful graph mutation invalidates `['friends']`, `['friendRequests']`, `['profile', canonicalUsername]`, `['friendCode', canonicalCode]` when known, and `['blocks']` when applicable.

**Profile/add screen:** username-only card before acceptance; full profile for accepted/self; relationship-specific controls: Send request, Accept/Reject, Cancel request, Unfriend, Block. A 404 renders the same localized generic not-found view for missing/blocked. It never says the user may have blocked the viewer.

**Share card:** loads `GET /friend-share` and shows the owner's friend code, copyable absolute add URL, SVG QR, and copyable profile URL. Friend-code input accepts formatted paste, explains normalization, and preserves freeform input when resolution fails. There is no camera button and the client never reconstructs `PublicBaseUrl`.

**Blocked settings:** lists username and block date from outbound rows with an Unblock button. Rows are not profile links. Empty state explains where blocking actions are available.

**Steam import:** button explicitly initiates the POST; no automatic import on page load. The six states in §11.3 have separate headings/actions. Friend-result cards have exactly two forms: actionable match cards offer Send request and Block; unmatched invitation cards offer Copy invitation link. Known Accepted/Pending PartyUp users render neither form, and hidden users render the same unmatched form as absent users. `upstream_failure` offers Retry; `private_profile` explains Steam privacy; `not_configured` says import is unavailable but sharing remains available. Because the import response is mutation-local rather than a query, shared Send success reconciles every matching card in the current result to `outgoing_pending` from the returned friendship and removes/disables its Send action; shared Block success removes that counterpart's cards immediately so a now-hidden identity is not retained. A later explicit import replaces the entire result from the server.

### 12.3 D-127 onboarding replacement

Advancing from Link platforms performs the existing atomic profile completion. On completion success, the client navigates to `/onboarding/friends` while retaining any validated `/u/{username}` or `/add/{canonicalCode}` continuation, refreshes session and antiforgery state, and renders friend actions only after that refresh proves the account Active. The final step resolves the username continuation through the exact-profile endpoint or seeds/resolves the canonical friend code through the code endpoint only after the Active refresh; it never calls a friend API before activation and never sends a request automatically. The user therefore lands on the intended username-only/full profile action card and explicitly chooses the relationship action. Friends then renders:

1. “Share your code” card with friend code, add URL, QR, and copy action.
2. Friend-code input with Resolve then Send request.
3. Profile-link copy action.
4. Steam import action when a Steam identity is linked; when it is not, a localized optional-link explanation, not an error.
5. “Finish setup” and “Skip for now,” both navigating to `/app` without persisting a new flag; either validated continuation has already been consumed by hydration on this step, not dropped during registration/onboarding.

If profile completion fails, the wizard stays on Link platforms and never renders active-only friend tools. If completion committed but the session/antiforgery refresh fails, the browser stays on `/onboarding/friends` with either validated continuation intact, renders a localized alert with Retry, does not resubmit completion, and makes no friend API call until refresh succeeds. If `/onboarding/friends` is entered with an incomplete profile, it redirects to the earliest incomplete step while retaining that continuation. Direct entry or reload by an already Active user always renders the optional setup/share page and rehydrates a retained valid continuation; activation alone never redirects this route to `/app`.

### 12.4 TanStack Query contract

Stable keys:

```text
['friends']
['friendRequests']
['profile', canonicalUsername]
['friendCode', canonicalCode]
['blocks']
```

Steam import is an explicit mutation result, not cached server state. No global friend store exists. Mutation success performs the profile/code invalidations and Steam-card reconciliation specified in §12.2; a 401 continues to invalidate `['session']` through the shared client. Unsafe requests use the existing CSRF middleware and are not automatically replayed after 429/409.

### 12.5 i18n key inventory

Add a seventh `friends` namespace for `en`, `pt-BR`, and `es`; update `web/src/i18n/index.ts` and the existing key-parity test. These dotted keys are the complete Phase 3 inventory (interpolation variables are part of the contract):

```text
friends.pages.list
friends.pages.profile
friends.pages.add
friends.pages.importSteam
friends.pages.blocked
friends.sections.friends
friends.sections.incoming
friends.sections.outgoing
friends.sections.share
friends.sections.findByCode
friends.actions.sendRequest
friends.actions.resolve
friends.actions.accept
friends.actions.reject
friends.actions.cancelRequest
friends.actions.unfriend
friends.actions.confirmUnfriend
friends.actions.block
friends.actions.unblock
friends.actions.importSteam
friends.actions.retry
friends.actions.copyCode
friends.actions.copyAddLink
friends.actions.copyProfileLink
friends.actions.copyInvitationLink
friends.actions.finishSetup
friends.actions.skipForNow
friends.fields.friendCode
friends.fields.username
friends.labels.friendCode
friends.labels.qrCode
friends.labels.blockedOn
friends.status.pendingIncoming
friends.status.pendingOutgoing
friends.status.friends
friends.status.requestSent
friends.status.requestAccepted
friends.status.requestRejected
friends.status.requestCanceled
friends.status.unfriended
friends.status.blocked
friends.status.unblocked
friends.empty.friends
friends.empty.incoming
friends.empty.outgoing
friends.empty.blocked
friends.profile.usernameOnly
friends.profile.notFound
friends.share.addLinkDescription
friends.share.profileLinkDescription
friends.qr.description
friends.qr.fallback
friends.dialogs.unfriendTitle
friends.dialogs.unfriendBody
friends.steam.readyTitle
friends.steam.privateTitle
friends.steam.privateHelp
friends.steam.zeroFriendsTitle
friends.steam.zeroFriendsBody
friends.steam.zeroMatchesTitle
friends.steam.zeroMatchesBody
friends.steam.notConfiguredTitle
friends.steam.notConfiguredBody
friends.steam.upstreamFailureTitle
friends.steam.upstreamFailureBody
friends.steam.matchDescription
friends.steam.unmatchedDescription
friends.onboarding.title
friends.onboarding.body
friends.onboarding.steamOptional
friends.onboarding.refreshFailed
friends.live.copied
friends.live.listUpdated
friends.live.requestUpdated
friends.live.blockUpdated
errors.user_not_found
errors.friend_code_invalid
errors.friend_request_self
errors.block_self
errors.friend_request_not_found
errors.friendship_not_found
errors.block_not_found
errors.friend_request_already_exists
errors.friendship_already_exists
errors.friend_request_not_pending
errors.steam_not_linked
errors.rate_limited
```

Remove `onboarding.messages.friendsPhaseThree`; replace it with the new friends-namespace content. Existing common/auth error keys remain unchanged.

### 12.6 WCAG 2.2 AA duties (D-139)

- Sections/tabs use semantic headings and either true links or a keyboard-operable tablist with correct `aria-selected`/panel relationships; content remains reachable without pointer input.
- Every action has a unique accessible name including username where repeated; icon-only copy/menu controls have localized labels.
- Persistent Friends and Blocked users navigation entries have localized accessible names, visible focus, and `aria-current="page"` on their active destinations.
- Mutation status uses a polite live region; validation/failure uses `role="alert"`; focus moves to the page heading after route resolution and to the first error summary on failed submit.
- Confirmation dialogs use the existing Radix Dialog focus trap, initial focus on Cancel, Escape close, and focus restoration. Destructive action is not conveyed by color alone.
- QR SVG is `aria-hidden`; adjacent selectable link text and description are the accessible equivalent. Copy success is announced.
- Loading skeletons are not focusable; buttons expose disabled/busy state; list changes do not unexpectedly move focus.
- Steam/private/empty/error states use text plus icons, never color alone. External Steam help links identify that they open an external site.
- Responsive layouts preserve 44×44 CSS-pixel pointer targets, visible focus, 200% zoom, reflow at 320 CSS px, and no horizontal scrolling for codes/URLs (wrap with copy controls).

---

## 13. Configuration, security, and packages

### 13.1 Configuration

No new secret is introduced. Reuse:

- `PublicBaseUrl`—required in deployed environments; validated as an absolute `http`/`https` origin with no query/fragment. Production is `https://partyup.social`; code never embeds that literal.
- `Steam:ApiKey`—optional secret. Blank produces `not_configured` for friend import but does not disable Steam authentication.
- `Steam:Enabled`—continues to gate sign-in exposure only; an existing linked user with `Enabled=false` and an API key may still use friend import unless product later rules otherwise. The import capability is determined by link + API key, not the login button flag.

No API key, full Steam request URL, raw upstream response, block direction, friend code, username path value, or user-authored profile data is added to structured logs. Friend codes and usernames are not bearer secrets, but logging stable discovery identifiers adds no operational value and contradicts the no-identifier logging contract.

Identifier-path redaction is mandatory in both logging layers and follows the existing A-044 scrub pattern rather than relying on endpoint code. In `deploy/Caddyfile`, after the global query scrub, the access-log filter recognizes SPA `/u/` and `/add/` and API `/api/v1/users/by-username/` and `/api/v1/friend-codes/` prefixes and replaces the entire remaining path suffix with `REDACTED`. This covers valid one-segment routes plus malformed extra segments, traversal attempts, and percent-encoded input as opaque text; no identifier-bearing form is decoded or logged before scrubbing.

In `Program.cs`, `UseSerilogRequestLogging` uses a centralized `FriendPathLogSanitizer` in `EnrichDiagnosticContext` to overwrite the emitted `RequestPath` property with the same four route-family shapes (`/u/REDACTED`, `/add/REDACTED`, `/api/v1/users/by-username/REDACTED`, `/api/v1/friend-codes/REDACTED`). `IncludeQueryInRequestPath = false` remains in force for A-044. The original `Path`, `PathBase`, route values, username, friend code, and raw URI must not be added under another property or exception message. Non-sensitive paths remain unchanged so operational grouping still works.

### 13.2 Security review checklist

- All unsafe endpoints are under the existing API antiforgery filter.
- All endpoints use `RequireOnboarded`; the onboarding friends page activates the profile before calling them.
- Block checks happen again under A-054 locks, after initial resolution.
- Id-addressed preliminary probes include actor membership/outbound ownership before any user lock and retain the same resource 404.
- Hidden/missing uses §8.4 and never the generic Blocked→403 mapper.
- Management list proves outbound ownership in the database predicate; filtering after loading is insufficient.
- No request body is read by rate-limit partition functions.
- Steam IDs and friend lists are returned only to the importer; matched PartyUp users see no import event.
- The import invitation URL is the importer's own stable add link and grants no access.
- DB constraints and integration concurrency tests back every logical uniqueness claim.

### 13.3 Package additions

`Directory.Packages.props` was inspected as A-013 requires: no .NET QR package exists or is needed, and Phase 3 adds no .NET package. Add `react-qr-code` to `web/package.json` and lock it in `web/package-lock.json`. Implementation must select the current non-vulnerable stable release compatible with React 19, run the repository npm audit, and record any accepted advisory rather than bypassing it. No Steam SDK package is added; the existing hand-rolled HTTP adapter remains A-019-compatible.

### 13.4 Deployment owner action

**OWNER-ACTION P3-DEPLOY-01:** CD updates the application image but does not synchronize `deploy/` to the VM. Before Phase 3 routes are exposed, the owner must manually copy the reviewed `deploy/Caddyfile` to the VM's bind-mounted Caddyfile, validate it with the deployed Caddy version, reload Caddy, and drive one request through each of `/u/<marker>`, `/add/<marker>`, `/api/v1/users/by-username/<marker>`, and `/api/v1/friend-codes/<marker>`. The emitted Caddy JSON events must contain the route family plus `REDACTED` and must contain neither marker nor encoded marker. This is the same manual rollout class as Phase 2 A-044; merging the repository file alone does not complete deployment.

---

## 14. Test plan

Every behavior-bearing test name ends in its duty's decision number: `Method_Expected_D###`. Tests stay in the existing three .NET projects plus Vitest; no fourth .NET project. Application tests use fakes only. SQL constraints, HTTP, row-level transactions, concurrency, migrations, and Infrastructure adapters belong in `PartyUp.Api.IntegrationTests`.

### 14.1 Domain tests (`tests/PartyUp.Domain.Tests`)

Existing Phase 1 tests remain authoritative and are not replaced. Add/rename only where the ruling changes the asserted contract:

| Test duty | D |
|---|---|
| `ProfileVisibilityTests.For_AuthorizesUsernameOnlyOnProfileBeforeAcceptance_D068` | D-068 |
| `IdentityMaskingTests.ForRoster_KeepsFullIdentityForAnAuthorizedNonFriend_D068` (ratifies the other half) | D-068 |
| `FriendRecipientTests.SteamImportMatch_TargetsOnlyTheImporterAtImportTime_D049` (remove “later registration” wording) | D-049 |
| Existing `BlockEffectsTests` remain the pure D-065/D-074/D-076/D-077/D-086 contract the seam invokes. | listed Ds |

### 14.2 Application tests (`tests/PartyUp.Application.Tests/Friends` plus the `Auth`, `Accounts`, and `Email` folders)

| Test duty | D |
|---|---|
| `SendFriendRequestUseCaseTests.Execute_CreatesOnePendingCanonicalPair_D015` | D-015 |
| `SendFriendRequestUseCaseTests.Execute_AutoAcceptsAnIncomingPendingRequest_D015` | D-015 |
| `SendFriendRequestUseCaseTests.Execute_DoesNotNotifyCrossedAcceptance_D105` | D-105 |
| `SendFriendRequestUseCaseTests.Execute_MaterializesTheReceivedNotificationInTheUnitOfWork_D105` | D-105 |
| `RejectFriendRequestUseCaseTests.Execute_DeletesTheIncomingRowSilently_D105` | D-105 |
| `CancelFriendRequestUseCaseTests.Execute_DeletesTheOutgoingRowSilently_D105` | D-105 |
| `UnfriendUseCaseTests.Execute_AllowsEitherPartyAndChangesNoContentScope_D065` | D-065 |
| `UnfriendUseCaseTests.Execute_CreatesNoNotification_D105` | D-105 |
| `ProfileUseCaseTests.Execute_ReturnsOnlyUsernameBeforeAcceptance_D068` | D-068 |
| `ProfileUseCaseTests.Execute_ReturnsFullProfileToAnAcceptedFriend_D032` | D-032 |
| `ProfileUseCaseTests.Execute_HidesBannedStrangerButReturnsFullToAcceptedFriend_D180` | D-180 |
| `ProfileUseCaseTests.Execute_HidesEitherDirectionOfBlockAsMissing_D065` | D-065 |
| `ListFriendRequestsUseCaseTests.Execute_ReturnsExactUsernameOnlyIncomingAndOutgoingShapes_D068` | D-068 |
| `ResolveFriendCodeUseCaseTests.Execute_NormalizesSpacesDashesCaseAndResolves_D144` | D-144 |
| `ResolveFriendCodeUseCaseTests.Execute_ReturnsHiddenForABlockedCounterpart_D065` | D-065 |
| `ResolveFriendCodeUseCaseTests.Execute_ReturnsNoCounterpartCodeOrShareUrl_D068` | D-068 |
| `GetFriendShareUseCaseTests.Execute_BuildsTheStableAddAndCurrentUsernameLinks_D144` | D-144 |
| `BlockUserUseCaseTests.Execute_DeletesPendingOrAcceptedFriendship_D065` | D-065 |
| `BlockUserUseCaseTests.Execute_CreatesNoNotification_D105` | D-105 |
| `BlockUserUseCaseTests.Execute_AlwaysInvokesTheBlockEffectsExecutor_D077` | D-077 |
| `BlockEffectsExecutorTests.Execute_LoadsScopeAppliesDomainDeletesRowsAndForwardsReminders_D077` | D-077 |
| `BlockEffectsExecutorTests.Execute_DrainsBlockDerivedSessionEventsWithoutNotificationMaterialization_D105` | D-105 |
| `BlockUserUseCaseTests.Execute_WithThePhase3EmptyScope_ChangesNoSessionOrPoll_D077` | D-077 |
| `UnblockUserUseCaseTests.Execute_DeletesOnlyTheOutboundBlockAndRestoresNothing_D065` | D-065 |
| `UnblockUserUseCaseTests.Execute_CallsBlockEffectsUnblockWithoutLoadingEffectScope_D065` | D-065 |
| `ListBlockedUsersUseCaseTests.Execute_ReturnsOnlyRowsTheViewerCreated_D065` | D-065 |
| `ImportSteamFriendsUseCaseTests.Execute_ExcludesBlockedBannedPendingAcceptedAndTombstonedMatches_D049` | D-049 |
| `ImportSteamFriendsUseCaseTests.Execute_OmitsAcceptedAndPendingFromMatchesAndUnmatchedCards_D049` | D-049 |
| `ImportSteamFriendsUseCaseTests.Execute_ClassifiesHiddenAndAbsentAsEquivalentUnmatchedInvitationCards_D049` | D-049/D-065 |
| `ImportSteamFriendsUseCaseTests.Execute_UsesTheImportersOwnAddLinkForEveryUnmatchedFriend_D049` | D-049 |
| `ImportSteamFriendsUseCaseTests.Execute_InvitationLinkContainsTheStableFriendCode_D144` | D-144 |
| `ImportSteamFriendsUseCaseTests.Execute_ReturnsAllFiveFirstClassProviderStates_D049` | D-049 |
| `ImportSteamFriendsUseCaseTests.Execute_NotifiesOnlyTheImporterAndCreatesNoCorrelationState_D105` | D-105 |
| `DeletionResultApplierTests.Apply_DeletesFriendshipsBlocksAndRecipientNotificationsButPreservesActorOnlyRows_D092` | D-092 |
| `FriendContinuationTests.Validate_AcceptsOnlyCanonicalUsernameAndFriendCodeRoutes_D031` | D-031 |
| `RegisterWithPasswordUseCaseTests.Execute_PutsValidatedContinuationInTokenAndEmailFragment_D031` | D-031 |
| `RequestMagicLinkUseCaseTests.Execute_PutsValidatedContinuationInExistingAndPreAccountTokens_D031` | D-031 |
| `ConsumeMagicLinkUseCaseTests.Execute_ReturnsOnlyTheRedeemedTokenContinuation_D031` | D-031 |
| `VerifyEmailUseCaseTests.Execute_ReturnsOnlyTheRedeemedTokenContinuation_D031` | D-031 |
| `EmailTemplateRendererTests.Render_FriendContinuationStaysInTheFragmentInEveryLocale_D031` | D-031/A-044 |

### 14.3 Integration tests (`tests/PartyUp.Api.IntegrationTests/Friends` plus named existing auth/logging files, real PostgreSQL where persistence is involved)

All Phase 3 duties below live under the heading path. The two explicitly deferred session rows live in future `tests/PartyUp.Api.IntegrationTests/Sessions/SessionBlockAttachmentRaceTests.cs` and are owned/activated only by the Phase 4 fixed-session command slice.

| Test duty | D |
|---|---|
| `FriendGraphMigrationTests.Migrate_CreatesEveryConstraintAndIndex_D015` | D-015 |
| `FriendshipConstraintTests.Insert_RejectsBothOrientationsOfTheSamePair_D015` | D-015 |
| `BlockConstraintTests.Insert_AllowsOnlyOneOrderedPairAndRejectsSelf_D065` | D-065 |
| `NotificationConstraintTests.Insert_EnforcesFriendShapesAndSteamUniqueness_D105` | D-105 |
| `FriendActionAuthorizationTests.FriendshipIdProbeRequiresActorMembershipBeforeUserLocks_D065` | D-065 |
| `BlockActionAuthorizationTests.BlockIdProbeRequiresOutboundOwnershipBeforeUserLocks_D065` | D-065 |
| `FriendRequestConcurrencyTests.Send_FromBothUsersConcurrently_EndsAcceptedOnce_D015` | D-015 |
| `FriendRequestConcurrencyTests.Send_TwiceConcurrently_CreatesOneRequestAndOneNotification_D105` | D-105 |
| `FriendRequestConcurrencyTests.AcceptRacingUnfriend_BothOrderingsMatchTheRaceMatrix_D015` | D-015 |
| `FriendRequestConcurrencyTests.RejectRacingRerequest_BothOrderingsHaveExactNotificationCounts_D105` | D-105 |
| `FriendRequestConcurrencyTests.AcceptRacingReject_BothOrderingsAssertFinalRowStatusAndNotificationCount_D105` | D-105 |
| `FriendRequestConcurrencyTests.AcceptRacingCancel_BothOrderingsAssertFinalRowStatusAndNotificationCount_D105` | D-105 |
| `FriendRequestConcurrencyTests.UsernameChangesBeforePairLock_StaleNameCreatesNothing_D128` | D-128 |
| `FriendBlockConcurrencyTests.BlockRacingAccept_LeavesBlockAndNoFriendship_D065` | D-065 |
| `FriendBlockConcurrencyTests.OppositeBlocksRacing_ExposeNoDirectionAndLeaveNoFriendship_D065` | D-065 |
| `FriendBlockConcurrencyTests.SameDirectionBlocksRacing_CreateOneRowOneSuccessOneHidden404AndApplyEffectsOnce_D065` | D-065 |
| `DeletionFriendRaceTests.GraceExpiryRacingSend_LeavesNoGraphRow_D092` | D-092 |
| `DeletionFriendRaceTests.TwoMembersExpireConcurrently_BothSucceedAndDeleteSharedRowsOnce_D092` | D-092 |
| `FriendRequestTransactionTests.NotificationInsertFailure_RollsBackTheRequest_D105` | D-105/A-016 |
| `SteamNotificationConcurrencyTests.ConcurrentImports_CreateOneMatchNotification_D105` | D-105 |
| `SteamImportTransactionTests.NotificationWriteFailure_RollsBackAllRowsAndReturnsNoSuccess_D105` | D-105/A-016 |
| `SteamImportConcurrencyTests.BlockRacingImport_ProducesOneOfTwoLinearizedOrderings_D065` | D-065 |
| `SteamImportConcurrencyTests.UnlinkOrRelinkRacingImport_RevalidatesImporterAndCandidateOwnershipInBothOrderings_D049` | D-049/A-030 |
| `ProfileEndpointTests.BlockedAndMissingUsernameResponses_AreByteEquivalentExceptTraceMetadata_D065` | D-065 |
| `ProfileEndpointTests.BannedStrangerIs404AndAcceptedFriendGetsFullProfile_D180` | D-180 |
| `FriendCodeEndpointTests.BlockedAndMissingCodeResponses_AreByteEquivalentExceptTraceMetadata_D065` | D-065 |
| `FriendRequestEndpointTests.SendToBlockerAndMissingTarget_AreByteEffectAndLimiterEquivalent_D065` | D-065 |
| `FriendActionEndpointTests.BlockStaleAndAbsentIds_AreEquivalentForAcceptRejectCancelUnfriend_D065` | D-065 |
| `BlockEndpointTests.HiddenAndMissingTargets_AreByteEffectAndLimiterEquivalent_D065` | D-065 |
| `SteamImportEndpointTests.BlockedAndAbsentLocalCandidates_AreByteEffectAndLimiterEquivalent_D065` | D-065 |
| `ProfileEndpointTests.PreAcceptanceResponseContainsNoFieldBeyondUsernameAndRelationship_D068` | D-068 |
| `FriendCodeEndpointTests.CounterpartResolutionContainsNoCodeOrShareUrl_D068` | D-068 |
| `ProfileEndpointTests.OldUsernameFailsAndStableCodeStillResolvesAfterChange_D128` | D-128 |
| `FriendRequestEndpointTests.CrossedSend_Returns200WithOneAcceptedPair_D015` | D-015 |
| `FriendRequestEndpointTests.CrossedSend_CreatesNoAcceptanceNotification_D105` | D-105 |
| `FriendRequestEndpointTests.PendingListsHaveExactUsernameRelationshipAndFriendshipIdShape_D068` | D-068 |
| `FriendRequestEndpointTests.RejectCancelAndUnfriend_AreSilent_D105` | D-105 |
| `FriendRequestEndpointTests.StalePendingAction_ReturnsFriendRequestNotPending_D015` | D-015 |
| `BlockEndpointTests.Block_InvokesTheEmptyPhase3EffectScope_D077` | D-077 |
| `BlockEndpointTests.Unblock_CallsDomainUnblockAndNeverLoadsEffectScope_D065` | D-065 |
| `DeletionSweepTests.ExpiryDeletesRecipientNotificationsAndPreservesActorOnlyRowsWithTombstoneMask_D092` | D-092 |
| `DeletionSweepTests.BeforeExpiry_ChangesNoFriendGraphRow_D148` | D-148 |
| `DeletionSweepTests.FailureAfterResultApplier_RollsBackEveryPhase3RowAndJournal_D092` | D-092/A-016 |
| `SteamFriendListClientTests.Get_MapsPrivateEmptyFailureAndMalformedResponses_D049` | D-049 |
| `SteamFriendListClientTests.Get_DisambiguatesInvalidKeyFromPrivateListWithPlayerSummaryProbe_D049` | D-049 |
| `SteamFriendListClientTests.Get_EnrichmentFailureReturnsSuccessWithNullPersonaAndAvatar_D049` | D-049 |
| `SteamFriendListClientTests.Get_ScrubsTheApiKeyFromLogsAndExceptions_D049` | D-049 |
| `SteamImportEndpointTests.ProviderStates_Are200DataNotProblemResponses_D049` | D-049 |
| `RateLimitTests.FriendLookupHasItsOwnAuthenticatedUserDimension_D031` | D-031 |
| `RateLimitTests.BlockUserConsumesTargetIndependentFriendLookupAndDefaultLeases_D065` | D-065 |
| `RateLimitTests.FriendRequestSendHasItsOwnAuthenticatedUserDimension_D105` | D-105 |
| `RateLimitTests.SteamImportMustSatisfyLookupImportAndDefaultDimensions_D049` | D-049 |
| `RateLimitTests.TargetStateCannotChange429ResponseOrLeaseConsumption_D065` | D-065 |
| `RateLimitTests.RateLimitedProblemIncludesInstance_D031` | D-031 |
| `FriendCountQueryTests.AcceptedCount_IsQueryableFromBothCanonicalSides_D179` | D-179 |
| `FriendEndpointProblemTests.EveryKnownFailure_UsesItsDocumentedProblemCode_D065` | D-065 |
| `RequestBindingProblemTests.MalformedJson_UsesValidationFailedWithoutDetail_D031` | D-031 |
| `RequestBindingProblemTests.UnsupportedContentType_Uses415ProblemContractWithoutDetail_D031` | D-031 |
| `RequestBindingProblemTests.ExistingPhase2Route_ChangesOnlyToUniformBindingShape_D031` | D-031 |
| `AuthCookieTests.UnauthenticatedRequest_UsesExact401UnauthorizedProblemContract_D031` | D-031/A-028 |
| `AuthCookieTests.IncompleteProfileRequest_UsesExact403ProblemContract_D127` | D-127/A-026/A-028 |
| `FriendEndpointOpenApiTests.EveryOperation_DeclaresItsSuccessAndFailureShapes_D031` | D-031 |
| `FriendEndpointOpenApiTests.ProtectedOperations_DeclareUnauthorizedAndOnboardingRequiredProblemCodes_D031` | D-031/A-028 |
| `FriendEndpointOpenApiTests.BodyOperations_Declare400And415ProblemShapes_D031` | D-031 |
| `FriendEndpointOpenApiTests.ResolutionEnvelopesAreExactAndExcludeShareFields_D068` | D-068 |
| `FriendEndpointOpenApiTests.CreationResponsesDeclareNoDeadLocationHeader_D031` | D-031 |
| `SecurityTokenServiceTests.EmailTokens_RoundTripCanonicalFriendContinuations_D031` | D-031 |
| `RegistrationFlowTests.EmailLink_ContinuesToUsernameOrFriendCodeAfterVerification_D031` | D-031 |
| `MagicLinkFlowTests.EmailLink_ContinuesExistingAndIncompleteAccounts_D031` | D-031/D-127 |
| `ExternalRedirectTests.GoogleCallback_PreservesFriendRoutesForActiveAndIncompleteAccounts_D031` | D-031/D-127 |
| `SteamCallbackTests.Callback_PreservesFriendRoutesForActiveAndIncompleteAccounts_D031` | D-031/D-127 |
| `RequestLoggingTests.FriendIdentifierRoutes_EmitOnlyRedactedRequestPath_D031` | D-031/A-044 |
| `CaddyAccessLoggingTests.FriendIdentifierRoutes_EmitOnlyRedactedUri_D031` | D-031/A-044 |
| `SessionBlockAttachmentRaceTests.InviteRacingBlock_ObeysSharedLockOrdering_D077` **Phase-4-activated; not executable/claimed in Phase 3** | D-077 |
| `SessionBlockAttachmentRaceTests.JoinRacingBlock_ObeysSharedLockOrdering_D077` **Phase-4-activated; not executable/claimed in Phase 3** | D-077 |

The byte-equivalence tests normalize only the inherently request-specific `traceId` and `instance`; status, headers, media type, title, code, field presence, and absence of detail must be identical. Every named non-disclosure duty also compares committed graph/notification side effects and the acquired/rejected lease count for each applicable limiter member. The parameterized stale-action duty covers accept, reject, cancel, and unfriend separately; the target-independent 429 duty runs missing, either-direction blocked, and visible targets through the same route partition. The id-probe duties inspect the generated/intercepted preliminary SQL and prove its actor predicate runs before any `users FOR UPDATE`. The same-direction block race forces both requests past pre-lock resolution, then asserts exactly one 201, one hidden 404 loser, one block row, no friendship, one executor/domain invocation, and exact delete/reminder/event-suppression counts. The Steam block race forces both valid orderings from A-054/§11.2: block-first returns no match and writes no match notification; import-first may return the locked-snapshot match, after which block commits and current lookup is hidden. The parameterized Steam identity race forces import-first and unlink/relink-first for both the importer mapping and candidate ownership mappings; it asserts changed candidate lock sets take a write-free retry and no response/notification discloses stale ownership. The successful deletion duties seed recipient rows, actor-only rows, and controls; after expiry they assert recipient rows are deleted, actor-only rows remain FK-valid through the tombstoned user reference, and rendering masks that actor as `Anonymous User`. The deletion rollback duty seeds Pending and Accepted friendships touching the subject, outbound and inbound blocks, recipient notifications plus an actor-only notification that would normally survive, and unaffected control rows; it injects failure in the credential-purge step immediately after `IDeletionResultApplier` returns, then asserts the user/credentials/username and every seeded Phase 3 row equal the complete pre-transaction state and no deletion-journal entry exists. The two external-provider continuation duties are each parameterized over `/u/{username}` and `/add/{code}` plus Active and incomplete accounts; Google and Steam callback completion must preserve only the server-sanitized canonical friend route, with hostile variants falling back safely. The request-binding duties assert every required problem field, status/content type/code, absence of `detail`, OpenAPI 400/415 declarations, and the explicitly bounded Phase 2 global-mapper delta. The two logging duties drive every §13.1 route family, including encoded and malformed extra-segment markers, through the real Serilog request sink and pinned Caddy config respectively and assert the emitted JSON event—not merely sanitizer helper output—contains no marker. The two session duties belong to the Phase 4 fixed-session command slice and activate only when those invite/join surfaces exist.

### 14.4 Web tests (Vitest + React Testing Library)

| Test duty | D |
|---|---|
| `FriendsPageTests.RequestActions_RenderAndMutateEveryLifecycleAction_D015` | D-015 |
| `FriendsPageTests.Unfriend_RequiresConfirmationAndShowsNoNotificationCopy_D105` | D-105 |
| `FriendRequestsTests.PendingCards_ExposeOnlyUsername_D068` | D-068 |
| `ProfilePageTests.PreAcceptance_RendersOnlyTheUsernameInTheAccessibilityTree_D068` | D-068 |
| `AddFriendPageTests.FormattedCode_NormalizesButPreservesInputAfterFailure_D144` | D-144 |
| `AddFriendPageTests.ResolvedProfile_AllowsAnExplicitFriendRequest_D031` | D-031 |
| `AddFriendPageTests.SendAndBlock_InvalidateCanonicalCodeAndReconcileActionCard_D031` | D-031 |
| `ShareCardTests.QrValue_EqualsTheApiSuppliedStableCodeAddUrl_D144` | D-144 |
| `ShareCardTests.Render_HasVisibleFallbackLinksAndNoCameraControl_D031` | D-031 |
| `BlockedUsersPageTests.Render_ManagesOutboundRowsWithoutProfileLinks_D065` | D-065 |
| `BlockedUsersPageTests.BlockAndUnblock_AreAvailableInTheWebMvp_D059` | D-059 |
| `SteamImportPageTests.Render_DistinguishesEveryProviderAndEmptyState_D049` | D-049 |
| `SteamImportPageTests.HiddenLocalCandidate_IsNeverLabeledAsRegistered_D065` | D-065 |
| `SteamImportPageTests.SendAndBlock_ReconcileOrRemoveMatchingActionCards_D065` | D-065 |
| `OnboardingFriendsStepTests.CompletionSuccess_RefreshesThenRendersFriendActions_D127` | D-127 |
| `OnboardingFriendsStepTests.RefreshFailure_StaysOnRouteRetriesAndCallsNoFriendApi_D127` | D-127 |
| `OnboardingFriendsStepTests.ActiveUserReload_RendersTheOptionalFriendsPage_D127` | D-127 |
| `OnboardingFriendsStepTests.FinishOrSkip_EndsTheOnboardingSequence_D127` | D-127 |
| `FriendRouteGuardTests.AnonymousAdd_RegistrationPreservesValidatedContinuation_D031` | D-031 |
| `FriendRouteGuardTests.AnonymousUsername_RegistrationPreservesValidatedContinuation_D031` | D-031 |
| `AuthEmailLinkTests.OpenedInAnotherTab_UsesTokenContinuationWithoutLocalState_D031` | D-031 |
| `OnboardingFriendsStepTests.IncompleteAccount_UsernameAndAddContinuationsHydrateAfterActivation_D127` | D-127 |
| `FriendsAccessibilityTests.KeyboardAndScreenReaderPaths_MeetComponentDuties_D139` | D-139 |
| `AppNavigationTests.AuthenticatedEntries_ReachFriendsAndBlockedRoutes_D127` | D-127 |
| `AppNavigationTests.PersistentEntries_HaveLocalizedAccessibleLabelsAndCurrentState_D139` | D-139 |
| `I18nKeyTests.FriendsNamespace_HasEnglishPortugueseAndSpanishParity_D060` | D-060 |
| `AddFriendPageTests.ResolveAction_UsesTheLocalizedKeyInEveryLocale_D060` | D-060 |
| `I18nKeyTests.FriendsNamespace_HasBrowserAndAccountLocaleKeyParity_D146` | D-146 |
| `FriendRouteGuardTests.ProfileAndAdd_PreserveNextThroughAuthentication_D031` | D-031 |
| `FriendRouteGuardTests.ProfileAndAdd_CompleteOnboardingBeforeRendering_D127` | D-127 |

### 14.5 `TRACEABILITY.md` updates

Updates are additive: retain every Phase 1/2 citation and append each Phase 3 duty in the same commit as the slice that adds the cited test. The closing slice may reconcile phase-summary prose but may not supply the first traceability citation for behavior already implemented.

| Decision | Required phase/citation update |
|---|---|
| D-015 | `1 (domain) / 3 (persistence/API/UI)`; append crossed-request application, concurrency, endpoint, and web tests. |
| D-031 | `1 (identifier) / 3 (profile/code/QR/Steam flows)`; append profile/add/share route tests plus server-token/email-link and anonymous-registration `/u/{username}` and `/add/{code}` continuation duties. |
| D-032 | Keep Phase 1/2 profile/platform citations; append Phase 3 full-friend profile test. |
| D-049 | `1 (filter) / 3 (Steam import)`; append matching, six result-state, invalid-key-versus-private success-probe, API, and web tests; remove later-registration expectation. |
| D-059 | `1 (domain) / 3 (registered-user block management)`; append block API/settings tests. |
| D-060/D-146 | Append Phase 3 key-parity test for the `friends` namespace. |
| D-065 | `1 (domain) / 3 (friend graph persistence/API/UI)`; append block race, two-ordering import race, actor-filtered id-probe, hidden-response, and management tests. |
| D-068 | `1 (domain) / 3 (profile API/UI)`; append username-only profile payload/DOM, exact pending-request-list shape, `FriendRequestsTests.PendingCards_ExposeOnlyUsername_D068`, and roster-interpretation duties. |
| D-074 | `1 (domain) / 3 (executor seam) / 4 (session persistence)`; cite empty-scope invocation now and keep real confirmation re-evaluation open to Phase 4. |
| D-076/D-086 | `1 (domain masking) / 3 (block persistence/seam) / 4 (session render) / 6 (poll render)`; Phase 3 must not claim roster UI completion. |
| D-077 | `1 (domain effects) / 3 (executor seam) / 4 (session wiring + P3-LK-01 invite/join races) / 6 (poll wiring)`; cite the block use-case seam tests now and reserve the two session attachment race citations for their Phase 4 activation, never Phase 3. |
| D-092 | `1 (domain) / 2 (skeleton) / 3 (friend/block/notification cleanup) / 4..6 (remaining effects) / 8 (verify)`; append deletion integration/race tests. |
| D-105 | `1 (recipient rules) / 3 (transactional records + actionable Friends UI) / 7 (center/transport)`; append transaction/silence/web tests. |
| D-127 | Keep Phase 2 ordering/profile citations and append the real final-step tests, including incomplete-account `/u/{username}` and `/add/{code}` continuation hydration only after activation. |
| D-128 | Append old-username/new-code resolution integration test. |
| D-139 | Append Phase 3 component accessibility tests; Phase 8 still owns the full audit. |
| D-144 | Change to `1 (format) / 3 (QR + lookup)` and append resolution/QR tests. |
| D-148 | Append the Phase 3 grace-does-nothing/expiry-cleans test; retain Phase 1/2 citations. |
| D-179 | Keep `2 (partial) / 8`; add a note that Phase 3 schema/query test makes friend count available but exposes no endpoint. |
| D-180 | Retain Phase 2 ban citations; append both Application and API banned-stranger-versus-accepted-friend profile duties. |

---

## 15. Implementation slices

Every slice is independently reviewable, honors the paths listed as its write set, and ends with zero warnings. Unless a slice explicitly changes web code, its gates are `dotnet build PartyUp.sln` and relevant unit/integration tests; web slices additionally run `npm run lint && npm run build && npm test`. Integration tests use the repository Testcontainers setup, never a manually configured/shared database. Documentation-only workers do not run these gates; implementation workers do.

Authority coupling is part of every slice's write set: the commit that first implements a behavior-changing interpretation must also apply its assigned `DOMAIN_MODEL.md` delta and the `TRACEABILITY.md` citations for tests added in that commit, as mapped by §16. These authority edits are mandatory even when not repeated in an `Owned paths` line. They may not be deferred to the closing slice. Slice 1 implements first-observation Steam notification uniqueness, and Slice 7 implements activation before optional friends actions; neither slice has an alternative branch.

**Exhaustive ownership audit:** every named §6–11 port/use case/artifact and every §14 duty has an owner below. Response records are colocated with their named Application use case; endpoint DTOs and operation ids are owned with `FriendEndpoints.cs`/`ApiRoutes.cs`. Slice 1 owns migration/constraint/count duties; Slice 2 owns read/resolution ports, use cases, D-068/D-180 Domain duties, and their Application tests; Slice 3 owns friendship-command Application/concurrency/transaction/probe duties; Slice 4 owns block/executor/event-suppression/deletion Application/integration/probe/rollback duties; Slice 5 owns every non-Steam endpoint/OpenAPI/rate duty, the global request-binding mapper delta, server token/email/external-auth continuation validation and callback duties, and two-layer path-log redaction; Slice 6 owns every Steam Domain/Application/adapter/transaction/endpoint duty plus the A-030 link/unlink locking changes and identity-race duties; Slice 7 consumes the server continuation and owns profile/add/share/auth/onboarding/i18n web duties; Slice 8 owns Friends/blocked/Steam/a11y web duties, persistent authenticated navigation/settings entries, route reachability, and mutation-result reconciliation. Existing Phase 1 `BlockEffectsTests` are retained, not edited. The two explicitly marked `SessionBlockAttachmentRaceTests` are owned by the future Phase 4 fixed-session command slice and cannot be cited as Phase 3 coverage.

### 15.1 Slice 1 — persistence foundation

**Scope:** map the three aggregates and author `AddFriendGraph`; no endpoints/use cases.

**Owned paths:** `src/PartyUp.Infrastructure/Persistence/AppDbContext.cs`; new `Persistence/Configurations/{Friendship,Block,Notification}Configuration.cs`; generated `src/PartyUp.Infrastructure/Migrations/*AddFriendGraph*`; generated `src/PartyUp.Infrastructure/Migrations/AppDbContextModelSnapshot.cs`; `tests/PartyUp.Api.IntegrationTests/Friends/{FriendGraphMigration,FriendshipConstraint,BlockConstraint,NotificationConstraint,FriendCountQuery}Tests.cs`.

**Gates:** the first-observation Steam notification ruling and its same-commit Domain/traceability delta are applied; migration applies from zero and current head; constraints/indexes match §5; Guid/Instant/enum round trips; D-179 count query plan uses the two user/status indexes; full build/integration suite green.

### 15.2 Slice 2 — repositories, pair locking, reads, and link building

**Scope:** add Application ports/response records and Infrastructure repositories/resolver; implement A-054/A-055/A-062; no HTTP.

**Owned paths:** `src/PartyUp.Application/Abstractions/*Friend*.cs`, `*Block*.cs`, `*Notification*.cs`, `IUserGraphResolver.cs`, `IPublicLinkBuilder.cs`; `src/PartyUp.Application/Friends/{GetFriendShare,GetProfile,ResolveFriendCode,ListFriends,ListFriendRequests,ListBlockedUsers}UseCase.cs` and their colocated response records; `src/PartyUp.Infrastructure/Persistence/Repositories/{FriendshipRepository,BlockRepository,NotificationRepository,FriendPairLocker,UserGraphResolver}.cs`; `src/PartyUp.Infrastructure/Friends/PublicLinkBuilder.cs`; `src/PartyUp.Infrastructure/DependencyInjection.cs`; `tests/PartyUp.Domain.Tests/Visibility/{ProfileVisibility,IdentityMasking}Tests.cs`; `tests/PartyUp.Application.Tests/Friends/{GetFriendShare,Profile,ResolveFriendCode,ListFriends,ListFriendRequests,ListBlockedUsers}UseCaseTests.cs`.

**Gates:** same-commit Domain/traceability deltas cover D-068 profile-only disclosure and the implemented lookup/management/link rulings; hidden reasons cannot escape resolver; blocked management is outbound-only; canonical code/link tests; pair lock SQL orders ids; build/unit tests green.

### 15.3 Slice 3 — friendship commands and transactional notifications

**Scope:** implement send/crossed accept/accept/reject/cancel/unfriend and received-request A-016 rows.

**Owned paths:** `src/PartyUp.Application/Friends/{Send,Accept,Reject,Cancel,Unfriend}*UseCase.cs`; any shared `FriendGraphCommandResult.cs`; repository extensions from Slice 2; `tests/PartyUp.Application.Tests/Friends/{Send,Accept,Reject,Cancel}FriendRequestUseCaseTests.cs`; `tests/PartyUp.Application.Tests/Friends/UnfriendUseCaseTests.cs`; `tests/PartyUp.Api.IntegrationTests/Friends/{FriendRequestConcurrency,FriendRequestTransaction,FriendActionAuthorization}Tests.cs`.

**Gates:** same-commit Domain/traceability deltas cover the request-action rulings; §7 race matrix, including accept/reject, accept/cancel, accept/unfriend, and reject/re-request in both forced orderings with final row presence/status and exact notification counts, is green against PostgreSQL; the friendship-id probe proves actor membership before any user lock and keeps the same resource 404; one request notification on new send; none on crossed/accept/reject/cancel/unfriend; rollback on notification failure; full .NET build/tests green.

### 15.4 Slice 4 — block/unblock seam and deletion cleanup

**Scope:** implement block/unblock, empty effect provider, deletion-scope/result-applier extensions; the outbound list read is already owned by Slice 2.

**Owned paths:** `src/PartyUp.Application/Friends/{BlockUser,UnblockUser,BlockEffectsExecutor}*.cs`; `src/PartyUp.Application/Abstractions/{IBlockEffectsExecutor,IBlockEffectScopeProvider,IReminderScheduleWriter,IDeletionResultApplier}.cs`; `src/PartyUp.Infrastructure/Friends/{EmptyBlockEffectScopeProvider,NoSessionReminderScheduleWriter,DeletionResultApplier}.cs`; `src/PartyUp.Infrastructure/Deletion/DeletionScopeProvider.cs`; `src/PartyUp.Application/Deletion/ExecuteDeletionForUserUseCase.cs`; DI; `tests/PartyUp.Application.Tests/Friends/{BlockUser,UnblockUser}UseCaseTests.cs`; `tests/PartyUp.Application.Tests/Friends/BlockEffectsExecutorTests.cs`; `tests/PartyUp.Application.Tests/Deletion/DeletionUseCaseTests.cs`; `tests/PartyUp.Application.Tests/Deletion/DeletionResultApplierTests.cs`; `tests/PartyUp.Api.IntegrationTests/Friends/{FriendBlockConcurrency,BlockActionAuthorization,DeletionFriendRace,DeletionSweep}Tests.cs`.

`tests/PartyUp.Application.Tests/Deletion/DeletionUseCaseTests.cs` updates its existing use-case fixture and constructor fakes to supply the new `IDeletionResultApplier` dependency and verify the applier wiring without weakening, renaming, or removing any existing D-092 or D-148 duty.

**Gates:** the same-commit `DOMAIN_MODEL.md` §1/§12 and traceability deltas cover the user-pair → graph → parent lock order, retained parent-before-occupancy rule, block seam, D-105 precedence, and grace-expiry graph scope; block/friend, opposite-direction block, and same-direction block races, with the same-direction loser a hidden 404 and exact one-row/one-effect counts; the outbound block-id probe proves actor ownership before any user lock; block invokes the executor/empty provider/domain call, drains derived session/poll events without notification materialization, and forwards reminder cancellations, while unblock invokes only `BlockEffects.Unblock` and never loads scope; the Phase 3 reminder writer throws on any unexpected cancellation; grace period changes nothing; expiry removes both relationship directions, outbound/inbound blocks, and recipient notifications while preserving FK-safe actor-only notification rows for tombstone masking in the same transaction; concurrent expiry of both members succeeds with shared rows deleted once; a forced failure after `IDeletionResultApplier` restores the complete seeded Phase 3 pre-transaction state and leaves no journal entry; `P3-LK-01` and its Phase-4-activated invite/join races remain explicit future gates, not claimed Phase 3 coverage; Phase 4 and Phase 6 cannot close their attachment slices until their commands reconcile with the amended Domain §1 order and prove the parent is locked before occupancy reads; full .NET suite green.

### 15.5 Slice 5 — HTTP, OpenAPI, and rate limits

**Scope:** map all non-Steam §9 routes, exact problem helpers, operation ids, and A-059 dimensions; normalize global malformed/unsupported body binding to §9.1 without otherwise changing Phase 2 behavior; extend registration/magic-link token, email, and Google/Steam external-auth flows with server-validated friend continuation; redact identifier paths in Serilog and Caddy; regenerate client.

**Owned paths:** new `src/PartyUp.Api/Endpoints/FriendEndpoints.cs` including non-Steam endpoint DTOs; `src/PartyUp.Api/{ApiRoutes,Program}.cs` including `ApiOperationIds`; `src/PartyUp.Api/Auth/{AuthCookieSetup,AuthorizationPolicies,ReturnToValidator}.cs`; new `src/PartyUp.Api/Logging/FriendPathLogSanitizer.cs`; new/updated `src/PartyUp.Api/ErrorHandling/FriendGraphProblems.cs`; `src/PartyUp.Api/ErrorHandling/RequestBindingExceptionMapper.cs` and any shared problem-shape file it requires; `src/PartyUp.Api/Security/RateLimitPolicies.cs`; `src/PartyUp.Api/Hosting/PartyUpOpenApiTransformer.cs` if needed; `src/PartyUp.Application/Auth/{IssueTokenRequest,TokenRedemption,FriendContinuation}.cs`; `src/PartyUp.Application/Accounts/{RegisterWithPassword,RequestMagicLink,ConsumeMagicLink,VerifyEmail}UseCase.cs`; `src/PartyUp.Application/Email/Templates/{EmailVerification,MagicLink}.{en,pt-BR,es}.{html,text}`; `src/PartyUp.Infrastructure/Auth/SecurityTokenService.cs`; `src/PartyUp.Api/Endpoints/AuthEndpoints.cs`; `deploy/Caddyfile`; new `tests/PartyUp.Application.Tests/Auth/FriendContinuationTests.cs`; `tests/PartyUp.Application.Tests/Accounts/{RegisterWithPasswordUseCase,ConsumeMagicLinkUseCase,VerifyEmailUseCase}Tests.cs`; `tests/PartyUp.Application.Tests/Email/EmailTemplateRendererTests.cs`; `tests/PartyUp.Api.IntegrationTests/AuthCookieTests.cs`; `tests/PartyUp.Api.IntegrationTests/{SecurityTokenService,RegistrationFlow,MagicLinkFlow,ExternalRedirect,SteamCallback,RequestLogging,CaddyAccessLogging,RateLimit,RequestBindingProblem}Tests.cs`; `tests/PartyUp.Api.IntegrationTests/Friends/{ProfileEndpoint,FriendCodeEndpoint,FriendRequestEndpoint,FriendActionEndpoint,BlockEndpoint,FriendEndpointProblem,FriendEndpointOpenApi}Tests.cs`; `web/src/api/schema.d.ts`.

**Gates:** every documented status and exact resolution envelope appears in OpenAPI; every protected operation declares the documented 401 `unauthorized` and 403 `onboarding_required` problem codes; real unauthenticated and incomplete-profile HTTP requests receive those respective statuses and exact `application/problem+json` §9.1 bodies; every body operation declares 400 and 415 problem shapes; real HTTP sends malformed JSON and unsupported/missing content types and receives the exact §9.1 contracts without `detail`; an existing Phase 2 body route proves the global mapper changes only binding-error shape uniformity; registration/magic-link token payloads and all localized email link bodies round-trip only a server-canonical `/u` or `/add` continuation, with token redemption as the response authority; `ReturnToValidator` retains its four exact fixed paths and accepts only the strict canonical friend-route grammar, with hostile forms falling back safely; Google and Steam callback duties each cover `/u` and `/add` for Active and incomplete accounts; 429 includes `instance`; creation responses emit no dead `Location`; unsafe routes reject missing CSRF; every target-bearing block-influenced response matches absence in bytes, headers, effects, and limiter consumption; no 403 `blocked`; `BlockUser` consumes target-independent `friend-lookup` plus default leases; captured Serilog and Caddy events redact all four §13.1 route families; OWNER-ACTION P3-DEPLOY-01 remains explicit until performed; `npm run gen:api`; .NET and web type build green.

### 15.6 Slice 6 — Steam import end-to-end

**Scope:** add live friend-list client, batched enrichment, matching, typed result union, notifications, endpoint, and upstream rate dimension.

**Owned paths:** `src/PartyUp.Application/Abstractions/{ISteamFriendListClient,IUserAccountGateway}.cs`; `src/PartyUp.Application/Friends/ImportSteamFriendsUseCase.cs` and its response records; `src/PartyUp.Application/Accounts/{CompleteExternalSignIn,UnlinkSteam,UnlinkAuthMethod}UseCase.cs` and `src/PartyUp.Application/Profile/UnlinkPlatformUseCase.cs` only as required for the A-030 user-lock protocol; `src/PartyUp.Infrastructure/Identity/UserAccountGateway.cs`; `src/PartyUp.Infrastructure/Steam/{SteamFriendListClient,SteamApiClient}.cs`; `src/PartyUp.Infrastructure/DependencyInjection.cs`; `src/PartyUp.Api/Endpoints/FriendEndpoints.cs`; `src/PartyUp.Api/ApiRoutes.cs`; `src/PartyUp.Api/Security/RateLimitPolicies.cs`; `tests/PartyUp.Domain.Tests/Notifications/FriendRecipientTests.cs`; `tests/PartyUp.Application.Tests/Friends/ImportSteamFriendsUseCaseTests.cs`; `tests/PartyUp.Api.IntegrationTests/Friends/{SteamFriendListClient,SteamNotificationConcurrency,SteamImportTransaction,SteamImportConcurrency,SteamImportEndpoint}Tests.cs`; `tests/PartyUp.Api.IntegrationTests/RateLimitTests.cs`; regenerated `web/src/api/schema.d.ts`.

**Gates:** same-commit Domain/traceability deltas remove later correlation and record the Steam contract; real adapter fixtures prove a successful `GetPlayerSummaries` validity probe distinguishes private list from invalid key and cover empty/malformed friend-list errors plus enrichment degradation to null; all six `state` values return 200; no key/upstream body leak; hidden and absent local candidates are response/effect/limiter-equivalent; both forced block/import linearization orderings satisfy §7.4/§11.2; forced import-vs-unlink/relink tests cover importer and candidate ownership mappings in both orderings, including a changed-lock-set write-free retry; all Steam link/unlink paths lock the affected user before changing A-030 facts; no persisted import state/later hook; concurrent import notification uniqueness; injected notification-write failure returns no success and commits no partial row; all backend/client-generation gates green.

### 15.7 Slice 7 — web discovery, profiles, sharing, and onboarding

**Scope:** add friends namespace, routes/guards, profile/add/share card, QR dependency, and replace the Phase 2 onboarding placeholder.

**Owned paths:** `web/package.json`, `web/package-lock.json`; `web/src/App.tsx`; `web/src/auth/{RequireAuth,RequireOnboarded}.tsx`; new `web/src/auth/friendContinuation.ts`; `web/src/features/auth/AuthScreens.tsx`; `web/src/features/friends/{ProfilePage,AddFriendPage,ShareCard,FriendCodeInput}.tsx`; `web/src/features/onboarding/OnboardingWizard.tsx`; `web/src/i18n/index.ts`; all `web/src/i18n/{en,pt-BR,es}/{friends,onboarding,errors}.json`; `web/src/App.test.tsx`; `web/src/auth/friendContinuation.test.ts`; `web/src/features/auth/{Register,AuthCallback,AuthEmailLink,AgeGate}.test.tsx`; `web/src/features/friends/{ProfilePage,AddFriendPage,ShareCard,FriendRouteGuard}.test.tsx`; `web/src/features/onboarding/OnboardingWizard.test.tsx`; `web/src/i18n/i18n.keys.test.ts`.

**Gates:** activation-before-optional-friends and its same-commit Domain/traceability delta are applied; QR encodes the caller-owned API absolute add URL; no counterpart resolution carries a friend code/share URL; no camera code; pre-accept DOM has username only; server-returned `/u/{username}` and `/add/{code}` continuations both survive anonymous registration email links, a new-tab/cross-tab open with no local state, and every incomplete-account onboarding step while hostile/invalid continuations are dropped; both hydrate the final action card only after activation; completion success refreshes before friend calls, refresh failure retains the continuation and stays retryable with no friend call, and Active reload renders the page; `friends.actions.resolve` renders from each locale and key parity holds; keyboard/focus/live-region tests; npm audit reviewed; lint/build/tests green.

### 15.8 Slice 8 — web Friends, blocking, and Steam import

**Scope:** actionable Friends page, confirmations, blocked settings, Steam state UI, persistent authenticated Friends/settings navigation, route reachability, query invalidation, responsive/a11y completion.

**Owned paths:** `web/src/features/friends/{FriendsPage,FriendList,FriendRequests,SteamImportPage}.tsx`; `web/src/features/settings/{AccountSettingsPage,BlockedUsersPage}.tsx` and `web/src/features/profile/ProfileSettingsPage.tsx`; shared query hooks under `web/src/features/friends/`; `web/src/App.tsx`; `web/src/App.test.tsx`; `web/src/features/friends/{FriendsPage,FriendRequests,SteamImportPage,FriendsAccessibility}.test.tsx`; `web/src/features/settings/{BlockedUsersPage,SettingsNavigation}.test.tsx`; all `web/src/i18n/{en,pt-BR,es}/{common,friends,settings}.json` keys required by the entries.

**Gates:** all request actions and empty states; `FriendRequestsTests.PendingCards_ExposeOnlyUsername_D068` proves the pending-card accessibility tree exposes username only, with no display name or avatar; canonical friend-code/profile query invalidation; Send/Block action-card reconciliation for explicit Steam mutation results; mandatory unfriend confirmation; no blocked-row profile links; all Steam states; no unread/notification UI; every authenticated onboarded route persistently reaches Friends and every existing settings surface reaches Blocked users; both entries have localized en/pt-BR/es accessible labels, keyboard focus, and correct active state; direct and clicked route-reachability tests render the intended guarded page; 320px/200% zoom manual check plus automated accessibility duties; lint/build/tests and full solution tests green.

### 15.9 Closing slice — additive authority summaries and full verification

**Scope:** append the consolidated architecture record and implementation-plan completion summaries only after code/tests verify the claims; reconcile already-present traceability phase summaries. No behavior-changing Domain interpretation or first test citation may originate here.

**Owned paths:** `ARCHITECTURE.md`, `IMPLEMENTATION_PLAN.md`, `TRACEABILITY.md` for additive summary/reconciliation only, and optional README/config documentation directly required by shipped Phase 3 behavior. `DOMAIN_MODEL.md` is not a closing-slice write: every required semantic delta already belongs to its first implementing slice. Do not edit `PHASE3_DESIGN.md` merely to claim completion; review findings update it only when substantively necessary.

**Gates:** append A-054..A-063 and the §16.3 plan summaries; verify every §16.2 semantic delta and every §14.5 test citation already landed in the same commit as its owning slice; verify each cited test name exists; run `dotnet build PartyUp.sln`, `dotnet test PartyUp.sln`, and from `web/` `npm run lint && npm run build && npm test`; confirm Git diff contains only intended Phase 3 files and no secrets/changelog comments.

---

## 16. Required document deltas and owning slices

### 16.1 `ARCHITECTURE.md`

Closing slice: append the consolidated decisions A-054..A-063 from §4. Add Phase 3 persistence/API/UI to “Done” only when §15 gates pass. Record that `DomainError.Blocked` remains a generic mapping for unrelated code, but friend/lookup/block use cases must collapse hidden results before exception mapping.

**Slice 5, same commit:** amend A-028's runtime response shape from the Phase 2 bare `{ code }` JSON 401/403 handlers to the exact §9.1 `application/problem+json` contract: 401 uses `unauthorized`, and an incomplete profile uses 403 `onboarding_required`, each with `type`, `title`, `status`, `instance`, `code`, and `traceId`. This is an explicit cross-phase response-shape delta and closes the Phase 2 BACKLOG item “401/403 not problem+json” for uniformity.

### 16.2 `DOMAIN_MODEL.md`

1. **Slice 2, same commit:** ratify §12-18 by removing “confirm” language: D-068 governs profile pages; authorized rosters/voter lists remain full identity with masking. Record the outbound block-management exception, exact link/code normalization, fail-closed block denials, and no Phase 3 admin endpoint.
2. **Slice 3, same commit:** record the four request actions/crossed acceptance, silent reject/cancel deletion, either-party unfriend, and no unfriend content effect.
3. **Slice 4, same commit:** amend `DOMAIN_MODEL.md` §1's concurrency rule to establish the shared user-pair → friendship/block graph → session/poll parent lock order for block and attachment commands, while retaining the existing requirement that a parent is locked before any occupancy read. Record the always-invoked block executor with empty Phase 3 session/poll scopes, unblock's no-scope/no-restoration rule, and grace-expiry deletion of friendship/request/block rows in both directions. Record the binding precedence that D-105's silent-block rule permanently suppresses session/poll domain events derived solely from block-driven removals while reminder cancellations still apply; identify `P4-NOTIFY-01` as Phase 4 re-verification of that invariant, not permission to swap suppression or add an event-disposition port. Phase 4 and Phase 6 must reconcile every attachment command with this amended §1 order and cannot close their session/poll attachment slices until tests prove the parent lock still precedes occupancy reads.
4. **Slice 6, same commit:** replace §10/§12-32's “at import and later registration/link” Steam notification with import-time only; record no correlation state/later hook and the Steam states/link/enrichment semantics.
5. **Slice 1, same commit (decided ruling P3-01):** state first-observation-only Steam match notification uniqueness and the partial index that enforces it.
6. **Slice 7, same commit (decided ruling P3-02):** state that profile activation precedes optional friends education/actions, Active reload renders the page, and no persisted completion flag exists.
7. **Slice 3, same commit:** record the actionable Friends surface versus Phase 7 notification-center UI when transactional request notifications are first implemented.
8. Keep the aggregate shapes unchanged; no new Friendship terminal status or import aggregate is introduced.

### 16.3 `IMPLEMENTATION_PLAN.md`

Closing slice: clarify the Phase 3 blocking bullet: Phase 3 ships block/friend persistence, API/UI, fail-closed lookup, management, notification/deletion integration, and the always-invoked locked-scope executor seam. Phase 4 wires real session effects/masking, requires every invite/join attachment command to cite `P3-LK-01`, and activates the two §14.3 block races before its session slice can close; Phase 6 inherits the same protocol for polls. This resolves the ownership tension without moving those tables forward.

Clarify the notification bullet: Phase 3 writes in-app rows but the notification center/unread/push remains Phase 7; D-105 permanently takes precedence for block-driven removals, whose derived session/poll events the executor drains and suppresses in every phase while reminder cancellations still apply. Add named Phase 4 carry item `P4-NOTIFY-01` to reverify that permanent executor contract when session notification materialization exists; Phase 4 must not swap the suppression and no event-disposition port is introduced. Clarify Steam matching is explicit-import-only with no later-registration correlation, and that server-validated profile-link and friend-code continuations survive registration email links, Google/Steam callbacks, and onboarding into the final friends step.

Add a new owner-ratified phase to the plan: **Visual design pass** — full token system (palette/type/spacing/motion) per §19's thesis and reference set (Rallly, Luma, Sleeper), applied app-wide; sequenced at the owner's discretion (no dependency on Phases 4–8). §19 constrains Phase 3's new screens until that phase runs. Add Phase 4 carry item `P4-REMIND-01`: the reminder-cancellation writer must persist schedule changes in the same scoped transaction (or an equivalent transactional handoff) with post-commit jobs revalidating current participation; rollback/crash-boundary duties gate the Phase 4 session slice (round-8 review, A-016/A-038 discipline).

### 16.4 `TRACEABILITY.md`

Each slice appends the §14.5 citations for the tests it adds in that same commit and retains all prior citations. The closing slice only reconciles additive phase-summary prose and verifies completeness. D-074/D-076/D-077/D-086 and D-092 must remain visibly multi-phase; Phase 3's seam is not mislabeled as persisted session/poll completion. D-105 must distinguish record emission now from center/transport later. D-179 remains Phase 8-owned despite the queryable schema.

---

## 17. Consolidated decision record

| ID | Decision | Rationale |
|---|---|---|
| A-054 | Pair commands lock both `users` rows in canonical order before relationship/block decisions; id probes filter actor membership/outbound ownership before any lock. Steam import re-reads importer identity and every candidate Steam-ID owner under its globally ordered user-lock set, retries without writes when that set changes, and linearizes only on a stable ownership/eligibility snapshot. A-030 Steam link/unlink/relink locks the affected user before changing identity facts. | A stable lock exists even when no relationship row does; unauthorized ids cannot lock unrelated users; crossed/block/deletion/import/identity races have explicit orderings without advisory hashes or stale PartyUp disclosure. |
| A-055 | One resolver collapses missing and every hidden/block-influenced candidate to the same 404. | Block relationships are security-sensitive and must never leak through the generic Blocked→403 mapper. |
| A-056 | Relationship mutation, deletes, and required notification rows use the existing scoped EF transaction. | A-016 crash consistency; no transport exists yet. |
| A-057 | Block use case calls one executor that owns locked scope loading, `BlockEffects.Apply`, returned deletes, reminder cancellations, and permanent draining/suppression of block-derived session/poll events under D-105; `P3-LK-01` is the shared lock order for block and every future organizer/counterpart attachment; Phase 3 adapters are empty/guarded, and no event-disposition port exists. | Phases 4/6 replace only provider/scheduler adapters and add attachment commands that cite the same protocol, preventing a concurrent invite/join from surviving a block without changing block orchestration or suppression; `P4-NOTIFY-01` reverifies the invariant when session notifications exist and cannot swap it. |
| A-058 | Profile disclosure and outbound-block management disclosure are separate read models. | Managing one's own block rows is not discovery, while pre-acceptance profile identity remains username-only. |
| A-059 | Add 20/min friend lookup (including exact-target `BlockUser`), 10/hour request send, and 5/15-min Steam import user dimensions to the chained limiter; acquire leases before target-dependent work. | Conservative abuse/upstream protection consistent with Phase 2's operation-id/user partitions, without making target existence affect lease behavior. |
| A-060 / P3-01 decided | Steam import is live/non-correlating; a successful keyed player-summary probe distinguishes a private list from an invalid key; optional enrichment failure yields null persona/avatar; importer and candidate identity ownership are revalidated under the final user locks; match notifications are unconditionally first-observation-only via notification uniqueness. | Primary friend-list failure or failed 401 validity probe can yield `upstream_failure`; no forbidden import state table, later-registration leak, stale unlink/relink disclosure, or repeated-import notification spam. |
| A-061 | Client-render QR through `react-qr-code`; no server QR or hand-rolled encoder. | Small inspectable SVG dependency, accessible text fallback, no unnecessary .NET package. |
| A-062 | Existing `PublicBaseUrl` is the sole origin for absolute profile/add links. | Deployment-correct web/mobile contract; no hard-coded production URL. |
| A-063 / P3-02 decided | Unconditionally activate and refresh before rendering the optional onboarding friends step; carry a server-validated `/u/{username}` or `/add/{code}` continuation through registration, email, Google/Steam callback, and onboarding flows and hydrate it after activation; Active direct entry/reload renders it; persist no extra step flag. | Strict dynamic-route grammar preserves the existing return-to allowlist's no-open-redirect posture; refresh failure retains the continuation and stays retryable without friend calls, while active-only friendability and D-127 ordering hold without a new table. |

---

## 18. Review checklist

An adversarial review should fail this design if any answer is implicit: exact route/status/code including malformed-body 400 and unsupported-media-type 415; hidden-versus-missing behavior; actor-filtered id probes; `P3-LK-01` pair/content/attachment lock order and the Domain §1/Phase 4/6 reconciliation gate; same-direction block exact effects; crossed notification behavior; permanent D-105 suppression of block-derived events and `P4-NOTIFY-01` re-verification with no swap or event-disposition port; block deletion/removal transaction, successful preservation and tombstone masking of actor-only notifications, and full post-applier rollback; Phase 4/6 seam contract and deferred race ownership; deletion of every Phase 3 row class; Steam private-list/key disambiguation, stable importer/candidate ownership under locks, unlink/relink retry orderings, linearization, exhaustive match/omit/unmatched taxonomy, and all states; persistent localized Friends/settings navigation and route reachability; QR payload/origin; D-068 profile and pending-list response fields; strict server-validated `/u` and `/add` continuation through email links, Google/Steam callbacks for Active/incomplete accounts, cross-tab authentication, and onboarding; two-layer identifier-path log redaction plus deployment owner action; D-180 banned profile behavior; localized Resolve action and i18n key parity; D-numbered tests; or slice ownership/gates. P3-01 and P3-02 are decided rulings in §17, with no implementation branch or open ratification item.

---

## 19. Visual direction (owner-ratified references; full design pass is its own future phase)

**Status.** Owner directive 2026-08-01: reference set = **Rallly** (rallly.co), **Luma** (lu.ma), **Sleeper**. A proper design pass — new tokens applied across the whole app — is **its own phase** (see §16 IMPLEMENTATION_PLAN delta). Phase 3 screens ship visually consistent with the existing Phase 2 styling; this section constrains *new* Phase 3 surfaces so they do not drift toward generic defaults, and records the north star the design-pass phase will execute.

**Thesis.** PartyUp is the calm space where a friend group decides when to play. The product's job is coordination clarity with game-night warmth — not a Discord-adjacent dark gamer shell, not a corporate calendar. From the references: Rallly's scheduling legibility (the grid is the hero; states are obvious at a glance), Luma's quiet event-page hierarchy (one subject per screen, generous whitespace, restrained accent), Sleeper's social warmth (friends feel like people, not rows — avatar-forward, personality in empty states, never sterile).

**Phase 3 binding constraints (new screens only, current token set):**
- The friends list (accepted friends only) is **avatar-forward**: identity block (avatar, display name, @username) is the primary visual unit, actions are secondary. No dense tables for people. Incoming/outgoing **request cards show `@username` only** — D-068 pre-acceptance contract (§8.1) governs and outranks any visual preference; a D-068 web test must assert the request card's accessibility tree exposes no display name or avatar.
- Every empty state directs the next action in product voice (e.g. Friends empty state leads with the friend-code share block, not an illustration of absence). No apologetic or filler copy; sentence case; verbs name outcomes ("Send request", toast "Request sent").
- One accent does the work per screen; destructive/block actions use the existing danger treatment and plain words ("Block @name" — never euphemisms), with the consequence sentence in body type, not fine print.
- The QR/share surface is composed like a Luma event card: code, QR, share link as one calm object — this is Phase 3's single signature moment; everything around it stays quiet.
- Masked identities ("Anonymous User") render with a neutral placeholder avatar and regular type — masking must look ordinary, never highlighted.
- Quality floor unchanged: WCAG 2.2 AA, visible focus, keyboard paths, reduced-motion respected, i18n key parity.

**Design-pass phase (deferred, needs its own brief):** full token system (palette/type/spacing/motion) derived from this thesis; candidate directions to explore — display face with warmth (not the default grotesque), light-first palette with a game-night accent range, Sleeper-grade personality in moments (onboarding finish, session confirmed) while Rallly-grade neutrality in planning grids. Explicitly out of scope for Phase 3 slices.
