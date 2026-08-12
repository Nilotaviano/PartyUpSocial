// PHASE8_DESIGN.md §3 S10 -- overlap: read-path load against GET /api/v1/availability/overlaps
// in the OMITTED friendIds form (D-110/A-078: no ?friendIds= query string at all means "all my
// accepted friends", SelectionSource.AllFriends -- src/PartyUp.Api/Endpoints/AvailabilityEndpoints.cs
// :35-70/:105) for one viewer seeded with a large accepted-friend roster.
//
// Route confirmed at AvailabilityEndpoints.cs:23-26 (operation id FindOverlapWindows, "overlap-search"
// rate-limit dimension -- RateLimitPolicies.cs:309-314, 30 requests/minute/user). The endpoint group
// requires RequireOnboarded (AvailabilityEndpoints.cs:20-21), so the viewer account seed.sql creates
// must have completed onboarding (lifecycle = 1 Active).
//
// D-065 (blocking deletes the friendship outright, it does not just hide it) means blocks cannot be
// shaped per-request here -- there is no "block then call overlaps" sequence to script, because by
// the time a block exists the friendship row is already gone. Blocked relationships are baked into
// the seed graph instead (seed.sql inserts `blocks` rows for a subset of the viewer's candidate
// pool and simply never creates a friendship for them), so this script's only job is to keep
// calling the omitted-friendIds form and confirm the two-query budget holds under sustained load.
//
// A-079's budget (FindOverlapWindowsUseCase.cs:34) is exactly one OverlapEngine.FindWindows call
// (two backing queries total: friend-availability fetch + windows) regardless of friend-list size --
// a regression that turns this into an N-friend fan-out query per request is exactly what a growing
// p95 under sustained concurrency here would surface; that is the point of this script, not a defect
// in the script.

import http from 'k6/http';
import { check, sleep } from 'k6';
import { SharedArray } from 'k6/data';
import { login } from './lib/auth.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const CREDENTIALS_FILE = __ENV.CREDENTIALS_FILE || './credentials/overlap-viewer.json';
const VUS = Number(__ENV.VUS || 10);
const DURATION = __ENV.DURATION || '30s';

// One seeded viewer (many-friends account) shared by every VU -- overlap.js load-tests the read
// path's query cost under concurrency, not per-user isolation, so re-using the same credentials
// across VUs is deliberate. seed.sql's viewer account is the only row this file needs.
const credentials = new SharedArray('overlap-viewer', () => JSON.parse(open(CREDENTIALS_FILE)));

export const options = {
  scenarios: {
    overlap_read: {
      executor: 'constant-vus',
      vus: VUS,
      duration: DURATION,
    },
  },
  thresholds: {
    // PROPOSED -- OWNER RATIFIES (Q5).
    http_req_failed: ['rate<0.01'],
    'http_req_duration{name:overlaps}': ['p(95)<500'],
    checks: ['rate>0.99'],
  },
};

export default function () {
  const vuCredentials = credentials[0];
  // login() also returns a usable CSRF token, but this script only issues GETs so it is
  // discarded -- kept as a bare call for symmetry with the other scripts' auth step.
  login(BASE_URL, vuCredentials);

  // No friendIds query string: the omitted-selection form under audit (D-110/A-078).
  const res = http.get(`${BASE_URL}/api/v1/availability/overlaps`, {
    tags: { name: 'overlaps' },
  });

  check(res, {
    'overlaps: 200': (r) => r.status === 200,
    'overlaps: selectionSource is allFriends': (r) => {
      if (r.status !== 200) return false;
      const body = r.json();
      return body.selectionSource === 'allFriends';
    },
  });

  sleep(1);
}
