// PHASE8_DESIGN.md §3 S10 -- sweeper-load: sustained RSVP writes + roster reads while a large batch
// of seeded sessions crosses its end time, so the minutely Hangfire SessionDueSweepJob tick
// (src/PartyUp.Infrastructure/Jobs/SessionDueSweepJob.cs) contends the SAME A-069 session parent
// lock (SessionRepository.cs:31-38 FOR NO KEY UPDATE) that live RSVP traffic takes.
//
// Job shape, cited so the threshold below is not a guess:
//   - Cron.Minutely (src/PartyUp.Api/Hosting/RecurringJobRegistrationService.cs:26-29).
//   - Up to 500 due-session candidates per tick, single ORDER BY start_time, id LIMIT 500 discovery
//     query (SessionDueCandidateStore.cs:20,30-46, BatchSize = 500), each transitioned through the
//     SAME SessionTransitionUseCase every request path uses, in its OWN transaction per candidate --
//     so the sweep never holds one lock across the whole batch, only one session at a time.
//   - [DisableConcurrentExecution(timeoutInSeconds: 55)] on ExecuteAsync
//     (SessionDueSweepJob.cs:26): if a tick is still running when the next minute's tick starts,
//     Hangfire blocks the new tick up to 55s waiting for the lock, then THROWS.
//
// *** An overlapping-tick throw during this run is the A-066 signal the design calls for -- it
// *** means two ticks genuinely overlapped under load, not that the sweeper is broken. Do not
// *** "fix" it by raising the timeout or disabling the attribute; report it as a finding for Q5's
// *** threshold ratification (does 500 candidates/tick still fit inside one Hangfire minute at the
// *** seeded session volume, or does the batch need to shrink / the schedule need to loosen).
//
// This script itself only ever calls the same two authenticated HTTP routes as rsvp-burst.js and
// overlap.js (RespondToSessionInvitation, FindOverlapWindows) -- it does not, and cannot, invoke
// the Hangfire job directly. The sweep firing is entirely a function of wall-clock time versus
// seed.sql's schedule offset; see seed.sql's header for how "falls due mid-run" is derived from
// the run's start time, and README.md for the run-order this depends on (seed, THEN start k6
// within the seeded offset window).

import http from 'k6/http';
import { check, sleep } from 'k6';
import { SharedArray } from 'k6/data';
import { login, writeHeaders } from './lib/auth.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const CREDENTIALS_FILE = __ENV.CREDENTIALS_FILE || './credentials/sweeper-load.json';
// Comma-separated uuids of the seeded sessions scheduled to fall due mid-run (seed.sql's
// due-batch). RSVP traffic is spread across all of them so the sweeper's 500-candidate tick has
// real contention to walk into, not a single hot row.
const SESSION_IDS = (__ENV.SESSION_IDS || '').split(',').filter(Boolean);
const VUS = Number(__ENV.VUS || 30);
const DURATION = __ENV.DURATION || '3m'; // must span at least one due-batch crossing + one tick

const credentials = new SharedArray('sweeper-load-users', () => JSON.parse(open(CREDENTIALS_FILE)));

const RESPONSES = ['Going', 'Maybe', 'Declined'];

// checks below declare 422 as an expected status alongside 200; without this override k6's
// default http_req_failed classification counts any 4xx as a request failure.
http.setResponseCallback(http.expectedStatuses({ min: 200, max: 399 }, 422, 429));

// Login ONCE per VU per scenario, not once per iteration -- see rsvp-burst.js's comment on the
// same pattern. sustained_rsvp and sustained_reads run as separate k6 scenarios (separate VU
// pools, each with its own module instance), so each exec function caches its own token.
let cachedRsvpCsrfToken = null;
let cachedReadCsrfToken = null;

export const options = {
  scenarios: {
    sustained_rsvp: {
      executor: 'constant-vus',
      vus: VUS,
      duration: DURATION,
      exec: 'rsvpTraffic',
    },
    sustained_reads: {
      executor: 'constant-vus',
      vus: Math.max(1, Math.floor(VUS / 3)),
      duration: DURATION,
      exec: 'readTraffic',
    },
  },
  thresholds: {
    // RATIFIED 2026-08-12 (owner) -- README.md's threshold table (Q5). 5xx off the RSVP/read routes still fails the run; a
    // Hangfire job throw is invisible to k6 (it never calls the job), so it does NOT show up
    // here -- check the Hangfire dashboard/log for the DisableConcurrentExecution throw signal
    // separately, per this file's header.
    http_req_failed: ['rate<0.01'],
    'http_req_duration{name:my-participation}': ['p(95)<1000'],
    'http_req_duration{name:overlaps}': ['p(95)<700'],
  },
};

export function setup() {
  if (SESSION_IDS.length === 0) {
    throw new Error('SESSION_IDS env var is required -- comma-separated uuids of the seeded due-batch.');
  }
  return { sessionIds: SESSION_IDS };
}

export function rsvpTraffic(data) {
  if (cachedRsvpCsrfToken === null) {
    const vuCredentials = credentials[(__VU - 1) % credentials.length];
    cachedRsvpCsrfToken = login(BASE_URL, vuCredentials);
  }
  const csrfToken = cachedRsvpCsrfToken;
  const sessionId = data.sessionIds[__ITER % data.sessionIds.length];
  const response = RESPONSES[__ITER % RESPONSES.length];

  const res = http.put(
    `${BASE_URL}/api/v1/sessions/${sessionId}/my-participation`,
    JSON.stringify({ response }),
    {
      headers: writeHeaders(csrfToken),
      tags: { name: 'my-participation' },
    },
  );

  check(res, {
    'my-participation: not 5xx': (r) => r.status < 500,
    // 422 session_responses_closed is expected once a candidate crosses its due time and the
    // sweeper (or a request-path transition) materializes Completed/Expired underneath the RSVP.
    'my-participation: expected status': (r) => r.status === 200 || r.status === 422,
  });

  sleep(1);
}

export function readTraffic() {
  if (cachedReadCsrfToken === null) {
    const vuCredentials = credentials[0];
    cachedReadCsrfToken = login(BASE_URL, vuCredentials);
  }

  const res = http.get(`${BASE_URL}/api/v1/availability/overlaps`, {
    tags: { name: 'overlaps' },
  });

  check(res, { 'overlaps: 200': (r) => r.status === 200 });

  sleep(2);
}
