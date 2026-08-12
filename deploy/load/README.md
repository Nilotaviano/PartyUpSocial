# Phase 8 k6 load scripts (S10 -- authoring only)

PHASE8_DESIGN.md §3 S10: this directory is **authored, not executed**. No script here has been
run against any environment, and per this repo's cardinal rules no agent runs `seed.sql` or
touches infrastructure — the owner runs both, on the owner's own schedule, per §2 Q5's open
questions (target environment, rate-limit override, run window, threshold ratification).

## Contents

| File | What it load-tests |
| --- | --- |
| `rsvp-burst.js` | N VUs contending one session's `PUT .../my-participation` -- the A-069 session parent lock. |
| `overlap.js` | Sustained reads of `GET /api/v1/availability/overlaps` (omitted-`friendIds` form) for a many-friends viewer -- the A-079 two-query budget. |
| `sweeper-load.js` | Sustained RSVP + read traffic while a seeded session batch crosses end time, so `SessionDueSweepJob`'s minutely tick contends the same locks live traffic does. |
| `seed.sql` | Owner-run Postgres seed: load-test accounts, friendships, blocks, sessions, invitees. |
| `lib/auth.js` | Shared cookie + CSRF login helper every scenario imports. |

## Auth flow (cookie + CSRF, no bearer tokens)

Confirmed against `src/PartyUp.Api/Program.cs:206-215,336`, `src/PartyUp.Api/Auth/AuthCookieSetup.cs:28`,
and `src/PartyUp.Api/Endpoints/AuthEndpoints.cs:47-69`:

1. `GET /api/v1/auth/antiforgery` → `{ "token": "..." }`, also sets the `pu.csrf` cookie
   (`HttpOnly`, `SameSite=Lax`).
2. `POST /api/v1/auth/login` with `{ "email", "password" }` and header `X-CSRF-TOKEN: <token>` →
   sets the `pu.auth` session cookie (`HttpOnly`, 30-day sliding expiration).
3. Every subsequent write (`PUT`/`POST`/`DELETE`) needs `X-CSRF-TOKEN: <token>` again -- the token
   from step 1 stays valid for the rest of the VU's session, it does not need refetching per
   request.

k6's default per-VU `http` module maintains its own cookie jar automatically, so `pu.auth` and
`pu.csrf` ride along on every request within a VU once set -- `lib/auth.js` never reads or writes
cookie bytes directly, it only tracks the CSRF token string returned in the antiforgery response
body (mirrors `web/src/api/client.ts`'s own `refreshAntiforgeryToken`/login sequence).

## Prerequisites

- [k6](https://k6.io) installed locally or on whatever host will drive the load (**not** installed
  in this repo -- it is external tooling per the ticket, and this environment does not have it).
- `seed.sql` run by the owner against the target Postgres (see below).
- A credentials directory (default `./credentials/`, override with `CREDENTIALS_FILE` per script)
  holding one JSON array per role, e.g.:

  ```json
  [
    { "email": "load-rsvp-1@partyup.load", "password": "LoadTest!2026#Rsvp" },
    { "email": "load-rsvp-2@partyup.load", "password": "LoadTest!2026#Rsvp" }
  ]
  ```

  Every seeded account shares the one password `LoadTest!2026#Rsvp` (see `seed.sql`'s header for
  the hash provenance -- generated offline for this seed only, never a reused real credential).
  Build the three files straight from Postgres once `seed.sql` has run, from the owner's own
  session:

  ```sh
  psql "$DATABASE_URL" -Atc "
    SELECT json_agg(json_build_object('email', email, 'password', 'LoadTest!2026#Rsvp'))
    FROM users u JOIN auth_users a ON a.id = u.id
    WHERE a.email LIKE 'load-rsvp-%'"      > credentials/rsvp-burst.json
  psql "$DATABASE_URL" -Atc "
    SELECT json_agg(json_build_object('email', email, 'password', 'LoadTest!2026#Rsvp'))
    FROM users u JOIN auth_users a ON a.id = u.id
    WHERE a.email = 'load-viewer@partyup.load'"      > credentials/overlap-viewer.json
  psql "$DATABASE_URL" -Atc "
    SELECT json_agg(json_build_object('email', email, 'password', 'LoadTest!2026#Rsvp'))
    FROM users u JOIN auth_users a ON a.id = u.id
    WHERE a.email LIKE 'load-sweeper-%'"      > credentials/sweeper-load.json
  ```

  (These `psql` invocations are the owner's to run -- authoring this README does not run them.)

## Running the seed

```sh
psql "$DATABASE_URL" -v friend_count=60 -v blocked_count=5 -v rsvp_burst_count=25 \
     -v sweeper_count=30 -v due_batch_count=40 -v due_offset_minutes=3 \
     -f deploy/load/seed.sql
```

Adjust the `-v` overrides (or edit the `\set` block at the top of `seed.sql`) to size the run.
`due_offset_minutes` controls how soon the due-batch sessions cross end time after commit --
start `sweeper-load.js` within that window (`seed.sql`'s footer prints the exact `SELECT`s to
recover the session ids you'll need for `SESSION_ID` / `SESSION_IDS` below).

## Running each script

```sh
# rsvp-burst.js -- one contended session, N concurrent RSVP writers
k6 run deploy/load/rsvp-burst.js \
  -e BASE_URL=https://<target> \
  -e SESSION_ID=<rsvp-burst target session uuid> \
  -e CREDENTIALS_FILE=./credentials/rsvp-burst.json \
  -e VUS=20 -e DURATION=30s

# overlap.js -- sustained reads against the omitted-friendIds overlap endpoint
k6 run deploy/load/overlap.js \
  -e BASE_URL=https://<target> \
  -e CREDENTIALS_FILE=./credentials/overlap-viewer.json \
  -e VUS=10 -e DURATION=30s

# sweeper-load.js -- sustained RSVP+read traffic spanning a due-batch crossing
k6 run deploy/load/sweeper-load.js \
  -e BASE_URL=https://<target> \
  -e SESSION_IDS=<uuid1>,<uuid2>,...,<uuidN> \
  -e CREDENTIALS_FILE=./credentials/sweeper-load.json \
  -e VUS=30 -e DURATION=3m
```

All three read `BASE_URL` (default `http://localhost:5000`), so pointing at a WSL-compose
rehearsal vs. a clone vs. prod is a single env-var swap -- the environment choice itself is Q5's
call, not this script's.

## Threshold proposal -- PROPOSED, OWNER RATIFIES (PHASE8_DESIGN.md §2 Q5)

Spec (§11) sets no numeric SLOs, so these are starting proposals baked into each script's
`options.thresholds`, not settled numbers. The owner adjusts and ratifies before any real run:

| Metric | Script(s) | Proposed threshold | Why this number |
| --- | --- | --- | --- |
| `http_req_failed` (5xx rate) | all three | `< 1%` | Any 5xx under contention is a real defect signal; 429s from the rate limiter are excluded (see below), they are not server errors. |
| `my-participation` p95 latency | rsvp-burst, sweeper-load | `< 800ms` (`< 1000ms` under sweeper-load's combined traffic) | One `FOR NO KEY UPDATE` row lock serializes writers to the same session; a growing p95 as VUs increase is expected, the threshold is a "still acceptable" ceiling, not a "no contention" claim. |
| `overlaps` p95 latency | overlap.js, sweeper-load | `< 500ms` (`< 700ms` under sweeper-load) | A-079's two-query budget should stay flat regardless of friend-list size; a regression that turns it into an N-friend fan-out is exactly what a growing p95 here would catch. |
| Sweeper tick duration | sweeper-load (observed out-of-band) | `< 55s` | `[DisableConcurrentExecution(timeoutInSeconds: 55)]` on `SessionDueSweepJob.ExecuteAsync` (`SessionDueSweepJob.cs:26`) -- a tick that runs longer than 55s risks colliding with the next minute's tick and throwing. k6 cannot observe Hangfire tick duration directly; check the Hangfire dashboard/log for the tick's own timing, not a k6 metric. |
| Overlapping-tick throw | sweeper-load (observed out-of-band) | Expected, not a failure | An A-066 signal per `SessionDueSweepJob.cs`'s header comment: two ticks genuinely overlapped under load and the second one backed off and threw. The idempotent transition makes the retry safe. Do not raise the timeout or disable the attribute to make this "pass" -- report it as a Q5 finding (does 500 candidates/tick still fit inside one minute at the seeded session volume?). |

`checks` (business-logic assertions like expected status codes) are set to `> 99%` pass rate on
`rsvp-burst.js` as a sanity floor; `overlap.js` and `sweeper-load.js` rely on `http_req_failed`
and per-route p95 alone since their checks are read-only shape assertions, not contention signals.

## Rate limits will throttle a single-IP burst

`src/PartyUp.Api/Security/RateLimitPolicies.cs` partitions every route into a dimension with its
own permit window. The two that matter here:

- **`default`** (`RateLimitPolicies.cs:412-418`): **300 requests/minute/IP**, covers every
  operation not claimed by a more specific dimension.
- **`session-write`** (`RateLimitPolicies.cs:236-252`): **30 requests/minute/user**, covers
  `RespondToSessionInvitation` (the `my-participation` route `rsvp-burst.js`/`sweeper-load.js`
  hit) -- partitioned per authenticated user, so this does not throttle the burst across VUs, only
  a single VU hammering faster than 30/min.
- **`overlap-search`** (`RateLimitPolicies.cs:309-314`): **30 requests/minute/user**, covers
  `FindOverlapWindows`.
- **`auth-ip`** (`RateLimitPolicies.cs:122-131`): **20 requests/15 min/IP**, covers `Login` --
  every VU's login call shares this budget if they all run from one load-generator IP, so VU
  counts much above ~20 per 15-minute window from a single IP will start 429ing on login itself,
  before a single RSVP is even attempted.
- **`register-ip`** (`RateLimitPolicies.cs:138-143`): **5/hour/IP** -- not exercised by these
  scripts (accounts are pre-seeded, never registered live), noted here only because it is the
  tightest dimension in the file and the most likely one to bite if a future script variant adds
  live registration.

**Any of these three IP-partitioned dimensions (`default`, `auth-ip`, and by extension every
route sharing an IP with the load generator) will throttle a genuinely concurrent single-IP
burst well before the RSVP lock-contention scenario itself becomes the bottleneck.** Running from
multiple source IPs (a k6 Cloud/distributed run, or multiple load-generator hosts) sidesteps this
without touching prod config. Running enough VUs from one IP to actually saturate the lock instead
requires the owner to approve a **temporary** rate-limit override for the run window (e.g. via
`RateLimits:RegisterPerIpPerHour`-style configuration the `auth-ip`/`default` dimensions do not
currently expose an env override for -- extending that override surface, if the owner wants one,
is out of this ticket's scope) and revert it immediately after -- this is explicitly called out as
owner-only in PHASE8_DESIGN.md §4 item 4 ("k6: Q5 environment + seed execution + run window +
threshold ratification; revert any limit override"). These scripts never weaken or bypass the
limiter themselves; they only document that the override path exists and is owner-gated.
