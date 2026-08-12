// Shared cookie + CSRF auth flow for every k6 scenario in this directory.
//
// PartyUp is cookie + CSRF only (no bearer tokens): src/PartyUp.Api/Program.cs:206-215 registers
// antiforgery with header name "X-CSRF-TOKEN" and cookie name "pu.csrf"; AuthCookieSetup.cs:28
// names the session cookie "pu.auth". k6's default per-VU http.CookieJar carries both cookies
// automatically once the server sets them via Set-Cookie -- callers never touch cookie bytes
// directly, they only need the CSRF token value for the X-CSRF-TOKEN write header.
//
// Flow (matches web/src/api/client.ts's own antiforgery+login sequence):
//   1. GET  {base}/api/v1/auth/antiforgery  -> { token }  (also sets the pu.csrf cookie)
//   2. POST {base}/api/v1/auth/login        body {email,password}, header X-CSRF-TOKEN: token
//      (also sets the pu.auth cookie on success)
//   3. Reuse the same antiforgery token for subsequent writes in this VU's session -- the token
//      stays valid as long as the pu.csrf cookie it was minted against is unchanged (ASP.NET
//      Core's antiforgery service does not rotate the cookie's masking secret per request), so
//      there is no need to refetch it before every write.
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
 * Logs the VU in with pre-seeded credentials (see seed.sql) and returns the CSRF token to reuse
 * for every write this VU issues afterward. Login itself is a write (POST) and needs the header
 * too -- AuthEndpoints.cs:66 maps POST /auth/login behind the same antiforgery filter as every
 * other /api/v1 write (api.AddEndpointFilter<ProblemAntiforgeryFilter> in Program.cs:336).
 *
 * @param {string} baseUrl
 * @param {{email: string, password: string}} credentials
 * @returns {string} csrfToken to pass as the X-CSRF-TOKEN header on subsequent writes
 */
export function login(baseUrl, credentials) {
  const csrfToken = fetchCsrfToken(baseUrl);

  const res = http.post(
    `${baseUrl}/api/v1/auth/login`,
    JSON.stringify({ email: credentials.email, password: credentials.password }),
    {
      headers: {
        'Content-Type': 'application/json',
        'X-CSRF-TOKEN': csrfToken,
      },
      tags: { name: 'auth/login' },
    },
  );

  check(res, { 'login: 200': (r) => r.status === 200 });
  if (res.status !== 200) {
    fail(`POST /api/v1/auth/login failed for ${credentials.email}: ${res.status} ${res.body}`);
  }

  return csrfToken;
}

/** Standard write headers once a VU is authenticated and holds a CSRF token. */
export function writeHeaders(csrfToken) {
  return {
    'Content-Type': 'application/json',
    'X-CSRF-TOKEN': csrfToken,
  };
}
