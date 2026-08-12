-- ============================================================================================
-- PHASE8_DESIGN.md §3 S10 -- load-test seed data.
--
-- CARDINAL RULE 1: this repository's agents never execute SQL against any database. This script
-- is authored for the OWNER to run manually with psql against the target k6 environment's
-- Postgres (PHASE8_DESIGN.md §2 Q5 picks that environment -- prod VM / a clone / a WSL-compose
-- rehearsal -- before this is ever run). Read README.md's "Running the seed" section first.
--
-- What this creates (table/column names verified against the EF configurations under
-- src/PartyUp.Infrastructure/Persistence/Configurations/, cited inline below):
--   * `load-organizer@partyup.load`               -- owns every seeded session.
--   * :overlap_viewer_count `load-viewer-N@partyup.load` -- overlap.js's many-friends viewers.
--     Login is spread across this pool (round-robin by VU) rather than one shared email, so a
--     10-VU overlap.js run stays well inside the login-email rate limit (auth-ip 20/15min/IP,
--     login-email 10/15min/email -- RateLimitPolicies.cs, see README.md's rate-limit note).
--   * :friend_count  `load-friend-N@partyup.load`  -- accepted friends of every viewer.
--   * :blocked_count `load-blocked-N@partyup.load` -- candidates the viewer has BLOCKED. Per
--     D-065, blocking deletes the friendship outright -- a block and an active friendship never
--     coexist for the same pair -- so these rows get a `blocks` row and NO `friendships` row,
--     full stop. There is no per-request block step for overlap.js to script.
--   * :rsvp_burst_count `load-rsvp-N@partyup.load`  -- rsvp-burst.js's contenders, all invited
--     (Pending) to the ONE `load-rsvp-target` session so every VU's PUT .../my-participation
--     contends that session's A-069 parent lock (SessionRepository.cs:31-38).
--   * :sweeper_count `load-sweeper-N@partyup.load` -- sweeper-load.js's traffic source, each
--     invited (Pending) to EVERY session in the due-batch (see "Schedule offset" below).
--
-- Every seeded account shares ONE password. The password_hash literal below is a real ASP.NET
-- Core Identity v3 PasswordHasher<TUser> hash (PBKDF2-HMACSHA512, 100000 iterations, 16-byte
-- salt, 32-byte subkey -- Microsoft's shipped default, confirmed against
-- src/PartyUp.Infrastructure/Identity/UserAccountGateway.cs's own DummyPasswordHash literal,
-- which decodes to the same marker byte + PRF (byte 0 = 0x01 format version, bytes 1-4 = PRF id 2
-- for HMACSHA512) -- generated offline for this seed script only with
-- PasswordHasher<object>().HashPassword and verified round-trip with VerifyHashedPassword before
-- being pasted in. See README.md for the plaintext and where credentials/*.json (the k6 scripts'
-- CREDENTIALS_FILE input) come from.
--
-- Schedule offset (sweeper-load.js's due-batch):
--   `:due_offset_minutes` controls how soon after this script COMMITS the due-batch sessions
--   cross their end time. Every due-batch session gets `duration_minutes = 15` (the ck_sessions_
--   duration floor) and `start_time = now() - interval '15 minutes' + (:due_offset_minutes ||
--   ' minutes')::interval`, so `start_time + duration = now() + :due_offset_minutes minutes` --
--   the exact end-time test SessionDueCandidateStore.cs:40 evaluates. Pick :due_offset_minutes
--   to land inside the k6 run you are about to start (e.g. seed, then start sweeper-load.js
--   within a minute or two, with :due_offset_minutes = 3): the sweeper's Cron.Minutely tick
--   (RecurringJobRegistrationService.cs:26-29) will pick these sessions up on the first or
--   second tick after they cross end time, with sweeper-load.js's sustained RSVP traffic still
--   contending the same rows.
--
-- Safe to re-run: every insert is idempotent (ON CONFLICT DO NOTHING / DO UPDATE keyed on the
-- email/session-title namespace below), EXCEPT the due-batch sessions, which are timestamped
-- relative to `now()` at execution time -- re-running the script re-schedules a fresh due-batch
-- and leaves the previous batch's (now past-due) sessions in place as harmless history.
-- ============================================================================================

\set friend_count 60
\set blocked_count 5
\set overlap_viewer_count 5
\set rsvp_burst_count 25
\set sweeper_count 30
\set due_batch_count 40
\set due_offset_minutes 3

BEGIN;

-- Needed for uuid_generate_v5 (deterministic, idempotent seed ids below). gen_random_uuid() used
-- elsewhere in this script is core Postgres (13+), not extension-gated.
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

-- A fixed namespace UUID so every seeded id is deterministic and idempotent re-runs collide on
-- purpose instead of duplicating rows. Not a security-sensitive value -- load-test data only.
CREATE TEMP TABLE load_namespace AS
SELECT 'b3d1a7b0-2e6a-4a7b-9f0e-9a6b1c2d3e4f'::uuid AS ns;

CREATE TEMP TABLE load_accounts (
    id uuid PRIMARY KEY,
    role text NOT NULL,
    idx int NOT NULL,
    email text NOT NULL,
    username text NOT NULL,
    display_name text NOT NULL
);

-- One organizer, then the five generated pools (viewer, friend, blocked, rsvp, sweeper).
INSERT INTO load_accounts (id, role, idx, email, username, display_name)
SELECT uuid_generate_v5(ns, 'organizer'), 'organizer', 0,
       'load-organizer@partyup.load', 'load_organizer', 'Load Organizer'
FROM load_namespace
UNION ALL
SELECT uuid_generate_v5(ns, 'viewer-' || n), 'viewer', n,
       'load-viewer-' || n || '@partyup.load', 'load_viewer_' || n, 'Load Viewer ' || n
FROM load_namespace, generate_series(1, :overlap_viewer_count) AS n
UNION ALL
SELECT uuid_generate_v5(ns, 'friend-' || n), 'friend', n,
       'load-friend-' || n || '@partyup.load', 'load_friend_' || n, 'Load Friend ' || n
FROM load_namespace, generate_series(1, :friend_count) AS n
UNION ALL
SELECT uuid_generate_v5(ns, 'blocked-' || n), 'blocked', n,
       'load-blocked-' || n || '@partyup.load', 'load_blocked_' || n, 'Load Blocked ' || n
FROM load_namespace, generate_series(1, :blocked_count) AS n
UNION ALL
SELECT uuid_generate_v5(ns, 'rsvp-' || n), 'rsvp', n,
       'load-rsvp-' || n || '@partyup.load', 'load_rsvp_' || n, 'Load Rsvp ' || n
FROM load_namespace, generate_series(1, :rsvp_burst_count) AS n
UNION ALL
SELECT uuid_generate_v5(ns, 'sweeper-' || n), 'sweeper', n,
       'load-sweeper-' || n || '@partyup.load', 'load_sweeper_' || n, 'Load Sweeper ' || n
FROM load_namespace, generate_series(1, :sweeper_count) AS n;

-- ---- auth_users (Microsoft.AspNetCore.Identity.IdentityUser<Guid>, AuthUserConfiguration.cs) --
-- UserAccountGateway.cs:57-66 sets UserName/NormalizedUserName to the account id's "D" form, not
-- the display username -- that is what login-by-email + this table's normalized-email unique
-- index actually key off, so we replicate it exactly.
INSERT INTO auth_users (
    id, user_name, normalized_user_name, email, normalized_email, email_confirmed,
    password_hash, security_stamp, concurrency_stamp,
    phone_number, phone_number_confirmed, two_factor_enabled,
    lockout_end, lockout_enabled, access_failed_count
)
SELECT
    id,
    id::text,
    upper(id::text),
    email,
    upper(email),
    true,
    -- LoadTest!2026#Rsvp -- ASP.NET Core Identity v3 hash, generated offline (README.md).
    'AQAAAAIAAYagAAAAEHkQpLmlt+ndBVjap9F3GACkEU5ofX9GnEV3u4+yyzEidXLUDXU31Jzj0X4lDDNf+A==',
    gen_random_uuid()::text,
    gen_random_uuid()::text,
    NULL, false, false,
    NULL, true, 0
FROM load_accounts
ON CONFLICT (id) DO NOTHING;

-- ---- users (the domain aggregate, UserConfiguration.cs) ----------------------------------------
-- lifecycle = 1 (UserLifecycle.Active), age_gate_source = 0 (SelfAttested), avatar_kind = 0
-- (AvatarKind.Emoji) -- all combined with a non-null username/display_name/time_zone/locale so
-- User.IsProfileComplete is true and CurrentUserAuthorizationHandler's RequireOnboarded policy
-- (AuthorizationPolicies.cs:62-64) admits these accounts.
--
-- friend_code is derived from each account's own id via md5 + a two-symbol translate that maps
-- the only two hex digits outside FriendCodeFormat.Alphabet (0 and 1) onto two digits inside it
-- (2 and 9) -- md5 hex never produces the alphabet's other excluded letters (I, O), so the result
-- is always a well-formed 10-character code (FriendCodeFormat.cs) without needing pgcrypto's
-- random() in a loop.
INSERT INTO users (
    id, lifecycle, username, display_name, time_zone_id, locale, friend_code,
    age_gate_passed, age_gate_source, reminder_default, email_notifications_enabled,
    avatar_kind, avatar_emoji, avatar_source_id, avatar_token,
    created_at, username_changed_at, grace_ends_at, deleted_at, banned_at, tour_completed_at
)
SELECT
    id, 1, username, display_name, 'Etc/UTC', 'en-US',
    upper(substr(translate(md5(id::text), '01', '29'), 1, 10)),
    true, 0, 3, true,
    0, '🎮', NULL, NULL,
    now(), NULL, NULL, NULL, NULL, now()
FROM load_accounts
ON CONFLICT (id) DO NOTHING;

INSERT INTO username_registry (name_canonical, state, owner_id, reserved_until)
SELECT lower(username), 0, id, NULL
FROM load_accounts
ON CONFLICT (name_canonical) DO NOTHING;

-- ---- friendships (FriendshipConfiguration.cs) --------------------------------------------------
-- Canonical ordering (ck_friendships_canonical: user_lo_id < user_hi_id) is enforced with
-- LEAST/GREATEST rather than assuming the viewer sorts either way.
INSERT INTO friendships (id, user_lo_id, user_hi_id, requested_by, status, requested_at, accepted_at)
SELECT
    gen_random_uuid(),
    LEAST(viewer.id, friend.id),
    GREATEST(viewer.id, friend.id),
    viewer.id,
    1, -- FriendshipStatus.Accepted
    now(), now()
FROM (SELECT id FROM load_accounts WHERE role = 'viewer') AS viewer
CROSS JOIN (SELECT id FROM load_accounts WHERE role = 'friend') AS friend
ON CONFLICT ON CONSTRAINT ux_friendships_pair DO NOTHING;

-- ---- blocks (BlockConfiguration.cs) -- D-065: block only, deliberately no friendship row -------
INSERT INTO blocks (id, blocker_id, blocked_id, created_at, blocked_username_snapshot)
SELECT
    gen_random_uuid(), viewer.id, blocked.id, now(), blocked.username
FROM (SELECT id FROM load_accounts WHERE role = 'viewer') AS viewer
CROSS JOIN (SELECT id, username FROM load_accounts WHERE role = 'blocked') AS blocked
ON CONFLICT ON CONSTRAINT ux_blocks_ordered_pair DO NOTHING;

-- ---- availability_series (AvailabilitySeriesConfiguration.cs) ----------------------------------
-- overlap.js's omitted-friendIds call resolves readable owners to the CALLER's accepted friends
-- (FindOverlapWindowsUseCase.cs:55-62 -- ReadOwnersWindowAsync never reads the caller's own row),
-- so it is the 'friend' pool (shared by every viewer-N account) that needs availability data, not
-- the viewers themselves. One open-ended weekly series per friend owner -- effective_from in the
-- past, effective_until NULL -- is always "current" for AvailabilityReadRepository's A-076-padded
-- window predicate (AvailabilityReadRepository.cs:44-57), so ReadOwnersWindowAsync returns a series
-- row for every one of the :friend_count owners on every run, regardless of when it is executed.
INSERT INTO availability_series (
    id, owner_id, day_of_week, start_time, duration_minutes, zone_id, effective_from, effective_until
)
SELECT
    uuid_generate_v5(ns, 'availability-friend-' || friend.idx),
    friend.id,
    ((friend.idx - 1) % 7) + 1, -- NodaTime IsoDayOfWeek: Monday=1 .. Sunday=7 (WeeklyPattern.cs:27)
    '18:00:00'::time,
    120,
    'Etc/UTC',
    current_date - 30,
    NULL
FROM load_namespace, (SELECT id, idx FROM load_accounts WHERE role = 'friend') AS friend
ON CONFLICT (id) DO NOTHING;

-- ---- sessions (SessionConfiguration.cs) ---------------------------------------------------------
-- rsvp-burst target: far enough in the future that it never crosses end-time during a run (so
-- every PUT .../my-participation contends the lock on a live, non-terminal session, not a
-- 422 session_responses_closed from a session the sweeper has already resolved). Unbounded
-- capacity (max_capacity NULL) so a burst of concurrent "Going" responses never trips waitlist
-- logic -- rsvp-burst.js is testing lock contention, not capacity edge cases.
INSERT INTO sessions (
    id, organizer_id, title, start_time, duration_minutes, platform, notes,
    min_attendance, max_capacity, is_private,
    origin_poll_id, status, created_at, game_name, game_steam_app_id
)
SELECT
    uuid_generate_v5(ns, 'rsvp-target-session'),
    organizer.id,
    'Load test: rsvp-burst target',
    now() + interval '7 days',
    60, 3, 'Seeded for S10 load testing -- rsvp-burst.js target session.',
    1, NULL, false,
    NULL, 1, now(), NULL, NULL
FROM load_namespace, (SELECT id FROM load_accounts WHERE role = 'organizer') AS organizer
ON CONFLICT (id) DO NOTHING;

INSERT INTO session_participations (
    id, session_id, user_id, guest_identity_id, status, origin,
    reminder_choice, created_at, responded_at, is_waitlisted
)
SELECT
    gen_random_uuid(),
    (SELECT id FROM sessions WHERE title = 'Load test: rsvp-burst target'),
    rsvp.id, NULL, 0, 0, NULL, now(), NULL, false
FROM (SELECT id FROM load_accounts WHERE role = 'rsvp') AS rsvp
-- ux_session_participations_session_user is a PARTIAL unique index (filter: user_id IS NOT NULL),
-- not a table constraint -- ON CONFLICT ON CONSTRAINT rejects it. Index inference (the column list
-- + matching WHERE clause) targets it correctly instead.
ON CONFLICT (session_id, user_id) WHERE user_id IS NOT NULL DO NOTHING;

-- due-batch: :due_batch_count sessions timed to cross end-time :due_offset_minutes minutes after
-- COMMIT (see the header). duration_minutes = 15 (the ck_sessions_duration floor) keeps the math
-- simple: start_time + 15m = now() + :due_offset_minutes.
INSERT INTO sessions (
    id, organizer_id, title, start_time, duration_minutes, platform, notes,
    min_attendance, max_capacity, is_private,
    origin_poll_id, status, created_at, game_name, game_steam_app_id
)
SELECT
    uuid_generate_v5(ns, 'due-batch-' || n),
    organizer.id,
    'Load test: due-batch ' || n,
    now() - interval '15 minutes' + (:due_offset_minutes || ' minutes')::interval,
    15, 3, 'Seeded for S10 load testing -- sweeper-load.js due-batch session.',
    1, NULL, false,
    NULL, 1, now(), NULL, NULL
FROM load_namespace, (SELECT id FROM load_accounts WHERE role = 'organizer') AS organizer,
     generate_series(1, :due_batch_count) AS n
ON CONFLICT (id) DO NOTHING;

-- Every sweeper account is invited to every due-batch session (a deliberate full cross product,
-- not a spread) so sweeper-load.js's round-robin VU/session pairing always lands on an existing
-- invite regardless of which VU picks which session on a given iteration.
INSERT INTO session_participations (
    id, session_id, user_id, guest_identity_id, status, origin,
    reminder_choice, created_at, responded_at, is_waitlisted
)
SELECT
    gen_random_uuid(), due.id, sweeper.id, NULL, 0, 0, NULL, now(), NULL, false
FROM (SELECT id FROM sessions WHERE title LIKE 'Load test: due-batch %') AS due
CROSS JOIN (SELECT id FROM load_accounts WHERE role = 'sweeper') AS sweeper
-- ux_session_participations_session_user is a PARTIAL unique index (filter: user_id IS NOT NULL),
-- not a table constraint -- ON CONFLICT ON CONSTRAINT rejects it. Index inference (the column list
-- + matching WHERE clause) targets it correctly instead.
ON CONFLICT (session_id, user_id) WHERE user_id IS NOT NULL DO NOTHING;

COMMIT;

-- ---- Owner's next steps -------------------------------------------------------------------------
-- 1. Export credentials for the k6 scripts (README.md's "Credentials files" section has the
--    exact `psql -Atc` invocations) -- one JSON array of {"email","password"} per role.
-- 2. Grab the session ids the scripts need:
--      SELECT id FROM sessions WHERE title = 'Load test: rsvp-burst target';        -- SESSION_ID
--      SELECT id FROM sessions WHERE title LIKE 'Load test: due-batch %' ORDER BY 1; -- SESSION_IDS
-- 3. Start the matching k6 script within :due_offset_minutes of this COMMIT for sweeper-load.js's
--    due-batch to still be "mid-run" when the sweeper's tick fires.
