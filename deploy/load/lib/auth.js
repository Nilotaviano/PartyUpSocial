// Shared cookie + CSRF auth flow for every k6 scenario in this directory.
//
// PartyUp is cookie + CSRF only (no bearer tokens): src/PartyUp.Api/Program.cs:206-215 registers
// antiforgery with header name "X-CSRF-TOKEN" and cookie name "pu.csrf"; AuthCookieSetup.cs:28
// names the session cookie "pu.auth". k6's default per-VU http.CookieJar carries both cookies
// automatically once the server sets them via Set-Cookie -- callers never touch cookie bytes
// directly, they only need the CSRF token value for the X-CSRF-TOKEN write header.
//
// Flow (matches web/src/api/client.ts's own antiforgery+login sequence):
//   1. GET  {base}/api/v1/auth/antiforgery  -> { token }  (also sets the pu.csrf cookie) --
//      minted while anonymous, only good enough to authorize the login POST itself.
//   2. POST {base}/api/v1/auth/login        body {email,password}, header X-CSRF-TOKEN: token
//      (also sets the pu.auth cookie on success)
//   3. GET  {base}/api/v1/auth/antiforgery again, now that pu.auth is set -- ASP.NET Core's
//      antiforgery service binds the token to the caller's identity (claims principal), so the
//      anonymous token from step 1 is rejected by every authenticated write (400 via
//      ProblemAntiforgeryFilter). Mirrors tests/PartyUp.Api.IntegrationTests/AntiforgeryTestClient
//      .cs:31,50-59, which always re-fetches the pair with the authentication cookie attached.
//      This second token is what callers must use for subsequent writes in this VU's session.
//
// Route paths verified against src/PartyUp.Api/Endpoints/AuthEndpoints.cs:47-69 and
// src/PartyUp.Api/ApiRoutes.cs:5 ("/api/v1").

import http from 'k6/http';
import { check, fail } from 'k6';

/**
 * @param {string} baseUrl
 * @returns {string} the antiforgery request token
 */
export function fetchCsrfToken(baseUrl) {
  const res = http.get(`${baseUrl}/api/v1/auth/antiforgery`, {
    tags: { name: 'auth/antiforgery' },
  });
  check(res, { 'antiforgery: 200': (r) => r.status === 200 });
  if (res.status !== 200) {
    fail(`GET /api/v1/auth/antiforgery failed: ${res.status} ${res.body}`);
  }
  const body = res.json();
  return body.token;
}

/**
 * Logs the VU in with pre-seeded credentials (see seed.sql) and returns a CSRF token bound to the
 * now-authenticated identity, to reuse for every write this VU issues afterward. Login itself is
 * a write (POST) and needs the header too -- AuthEndpoints.cs:66 maps POST /auth/login behind the
 * same antiforgery filter as every other /api/v1 write (api.AddEndpointFilter
 * <ProblemAntiforgeryFilter> in Program.cs:336) -- but the anonymous token minted for that POST is
 * NOT reusable afterward: ASP.NET Core's antiforgery service binds the token to the caller's
 * identity, so a token minted before login 400s on every write issued after login. The token is
 * re-fetched below once pu.auth is set, mirroring AntiforgeryTestClient.cs:31,50-59.
 *
 * @param {string} baseUrl
 * @param {{email: string, password: string}} credentials
 * @returns {string} csrfToken, bound to this identity, to pass as X-CSRF-TOKEN on subsequent writes
 */
export function login(baseUrl, credentials) {
  const anonymousCsrfToken = fetchCsrfToken(baseUrl);

  const res = http.post(
    `${baseUrl}/api/v1/auth/login`,
    JSON.stringify({ email: credentials.email, password: credentials.password }),
    {
      headers: {
        'Content-Type': 'application/json',
        'X-CSRF-TOKEN': anonymousCsrfToken,
      },
      tags: { name: 'auth/login' },
    },
  );

  check(res, { 'login: 200': (r) => r.status === 200 });
  if (res.status !== 200) {
    fail(`POST /api/v1/auth/login failed for ${credentials.email}: ${res.status} ${res.body}`);
  }

  // Re-fetch now that pu.auth is set -- the identity-bound token, valid for authenticated writes.
  return fetchCsrfToken(baseUrl);
}

/** Standard write headers once a VU is authenticated and holds a CSRF token. */
export function writeHeaders(csrfToken) {
  return {
    'Content-Type': 'application/json',
    'X-CSRF-TOKEN': csrfToken,
  };
}
