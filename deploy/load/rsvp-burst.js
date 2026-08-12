// PHASE8_DESIGN.md §3 S10 -- rsvp-burst: N VUs hammer the SAME session's
// PUT /api/v1/sessions/{sessionId}/my-participation, all contending the A-069 session parent
// lock (SELECT 1 FROM sessions ... FOR NO KEY UPDATE -- SessionRepository.cs:31-38, taken inside
// SessionParticipationUseCases.RespondToInvitationAsync before occupancy/capacity is read).
//
// Route confirmed at src/PartyUp.Api/Endpoints/SessionEndpoints.cs:86-87 (operation id
// RespondToSessionInvitation, "session-write" rate-limit dimension -- RateLimitPolicies.cs:236-252,
// 30 requests/minute/user). Every VU is a DIFFERENT seeded user (see seed.sql) already invited to
// the target session, so 30/min/user does not throttle the burst itself, but the shared "default"
// IP dimension (300/min/IP, RateLimitPolicies.cs:412-418) WILL throttle every VU sharing one load
// generator's IP once VUs * iterations/min crosses 300 -- see README.md's rate-limit note before
// picking VU counts.
//
// Valid RSVP response values are the capitalized §8 vocabulary in
// src/PartyUp.Api/Contracts/Sessions/SessionContracts.cs:62-68 ("Going" | "Maybe" | "Declined") --
// NOT the lowercase wire-response vocabulary the GET side returns.
//
// Thresholds below are the numbers from README.md's threshold table, RATIFIED 2026-08-12 (owner,
// PHASE8_DESIGN.md §2 Q5).

import http from 'k6/http';
import { check, sleep } from 'k6';
import { SharedArray } from 'k6/data';
import { login, writeHeaders } from './lib/auth.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const SESSION_ID = __ENV.SESSION_ID; // uuid of the seeded contended session (see seed.sql)
const CREDENTIALS_FILE = __ENV.CREDENTIALS_FILE || './credentials/rsvp-burst.json';
const VUS = Number(__ENV.VUS || 15);
const DURATION = __ENV.DURATION || '30s';

const credentials = new SharedArray('rsvp-burst-users', () => JSON.parse(open(CREDENTIALS_FILE)));

const RESPONSES = ['Going', 'Maybe', 'Declined'];

// checks below declare 422 as an expected status alongside 200; without this override k6's
// default http_req_failed classification counts any 4xx as a request failure, contradicting the
// "429/422 are excluded" comment on the threshold below.
http.setResponseCallback(http.expectedStatuses({ min: 200, max: 399 }, 422, 429));

// Login ONCE per VU, not once per iteration: auth-ip is 20 requests/15min/IP and login-email is
// 10 requests/15min/email (RateLimitPolicies.cs, README.md's rate-limit note) -- a VU that
// re-logs-in on every iteration exhausts its own login-email budget by iteration 11 and starts
// 429ing on login itself, well before a single RSVP write. Module-level state here is per-VU (k6
// gives each VU its own JS runtime instance), so this persists across that VU's iterations.
let cachedCsrfToken = null;

export const options = {
  scenarios: {
    rsvp_burst: {
      executor: 'constant-vus',
      vus: VUS,
      duration: DURATION,
    },
  },
  thresholds: {
    // RATIFIED 2026-08-12 (owner) -- README.md's threshold table (Q5). No 5xx at all; 429 from the
    // rate limiter is expected and excluded on purpose (setResponseCallback above), not a server error.
    http_req_failed: ['rate<0.01'],
    'http_req_duration{name:my-participation}': ['p(95)<800'],
    checks: ['rate>0.99'],
  },
};

export function setup() {
  if (!SESSION_ID) {
    throw new Error('SESSION_ID env var is required -- the seeded contended session uuid.');
  }
  return { sessionId: SESSION_ID };
}

export default function (data) {
  if (cachedCsrfToken === null) {
    const vuCredentials = credentials[(__VU - 1) % credentials.length];
    cachedCsrfToken = login(BASE_URL, vuCredentials);
  }
  const csrfToken = cachedCsrfToken;

  const response = RESPONSES[__ITER % RESPONSES.length];
  const res = http.put(
    `${BASE_URL}/api/v1/sessions/${data.sessionId}/my-participation`,
    JSON.stringify({ response }),
    {
      headers: writeHeaders(csrfToken),
      tags: { name: 'my-participation' },
    },
  );

  check(res, {
    'my-participation: not 5xx': (r) => r.status < 500,
    // 200 = accepted; 422 session_responses_closed is a legitimate terminal-state/window-closed
    // rejection (DomainError.WindowClosed/TerminalState/InvalidTransition -- SessionEndpoints.cs
    // ~692-694), not a load-test failure, once the target session's window has passed.
    'my-participation: expected status': (r) => r.status === 200 || r.status === 422,
  });

  sleep(1);
}
