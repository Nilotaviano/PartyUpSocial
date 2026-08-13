# Phase 8 S9 — authorization exceptions ledger

Every route or path that sits outside the visibility engine, outside the `/api/v1` group, or outside
limiter scope, with the reason it is allowed to and the evidence it still fails closed. Rows are the
verified survivors of the S9 authz and OWASP sweeps (`.foreman/audit-s9-authz.md`,
`.foreman/audit-s9-owasp.md`); the two Medium existence oracles those sweeps found are fixed, not
listed here.

| # | Exception | Why it is outside | Evidence it fails closed |
|---|---|---|---|
| 1 | A-085 Steam catalog search, `GET /api/v1/steam-catalog` | First-party catalog data; no user-scoped resource for the engine to resolve | `SteamCatalogEndpoints.cs:14-17` — single route, `RequireOnboarded`, `steam-catalog-search` dimension (60/min/User); returns appId/name pairs only |
| 2 | A-084 avatar proxy, `GET /a/{token}` | Capability-token transport outside `/api/v1` and outside the OpenAPI document | `AvatarProxyEndpoints.cs:16-19` GET-only + `ExcludeFromDescription()`; unknown token, deleted source and empty bytes all return the same `NotFound()` (`:30-34`); 256-bit CSPRNG slug (`AvatarSource.cs:17,96`); nosniff + `default-src 'none'; sandbox` + `Cache-Control: private` (`:36-39`); `avatar-proxy` dimension 600/min/IP |
| 3 | A-084 revocation — ban does **not** revoke avatar tokens | Deliberate: D-180 preserves lifecycle, content and history on a banned live account (`User.cs:392`), and the avatar bytes are content | `User.Ban` (`User.cs:393-410`) never touches `_avatarSources`; the proxy has no user-state check. Ruled a non-revocation by design, recorded here because S9 names ban in the revocation contract. Deletion still revokes (`DeletionResultApplier.cs:83`), as does unlink (`User.cs:322-327`) |
| 4 | A-084 revocation — capture refreshes a source **in place**, keeping its token | Deliberate: `AvatarRefreshJob` polls the same live source daily and must not churn the token (and `Avatar.Token` is denormalized onto the `users` row) | `User.CaptureAvatarSource` (`User.cs:344-349`) refreshes; pinned by `UserProfileTests.CaptureAvatarSource_UpsertsTheSingleRowPerKind_D190`. The unlink→re-link path already rotates: both unlink paths delete the source row (`User.cs:322-327`, `UnlinkAuthMethodUseCase.cs:96-104`), so the re-link goes through the create branch and mints a fresh token — proved by `UserProfileTests.UnlinkThenRelink_MintsAFreshAvatarProxyToken_D190`. A harvested token therefore dies at unlink; it survives only re-capture on a still-linked account |
| 5 | A-064 guest exchange, `GET /g/{token}` | Bearer-token transport outside `/api/v1` and outside the OpenAPI document | `GuestExchangeEndpoints.cs:19-21` GET-only + `ExcludeFromDescription()`; `no-store` + `no-referrer` set first (`:35-36`); unresolvable token, missing session and domain rejection all redirect to the same `/guest/invalid` (`:41,:46-48,:56-60`); `guest-link-exchange` 20/min/IP |
| 6 | Guest session/poll routes under `/api/v1/guest/{guestLinkId:guid}` | §5.4 ratified exception — a bearer token is its own proof (`VisibilityEngine.cs:13-16`) | Substitute gate verified: per-link cookie `pu.g.{guestLinkId:N}` unprotected and required to match the route id and be unexpired (`GuestSessionEndpoints.cs:287-308`); SessionId comes from the credential, never the route (`:68-69`); every rejection is 404 `session_not_found` (`:63-65,:71-72,:83-88`). Inside `/api/v1`, so CSRF covers the unsafe verbs; `guest-write` 20/min per GuestLink |
| 7 | `/health`, `/health/live` — anonymous, unlimited | Probe endpoints; the limiter is deliberately scoped to `/api/v1`, `/g`, `/a` | `Program.cs:333-335`; `/health/live` uses `Predicate = _ => false`; limiter scope at `Program.cs:323-327` |
| 8 | `/openapi` — anonymous, unlimited | The ratified future mobile contract | `Program.cs:336`, unconditional; exposes route shapes only, and `/g` + `/a` are excluded from the document |
| 9 | The four `/_slice2/*` test routes | Registered only inside `if (app.Environment.IsEnvironment("Testing"))` (`Program.cs:371-395`) | Now enforced by test, not by reading: `Slice2ProductionGatingTests.NoSlice2RouteExistsOutsideTheTestingEnvironment` asserts all four 404 under `UseEnvironment("Production")` |
| 10 | Organizer-only writes gate on `OrganizerId` identity, not the engine | Strictly narrower than the engine: the organizer is unconditionally Direct (`VisibilityEngine.cs:32`) and the rule-4 block check cannot fire against oneself | Uniform miss code, identical to the absent-row branch: `EditSessionUseCase.cs:82,195`; `SessionInvitationUseCases.cs:64,87,158,196,236`; `SessionJoinRequestUseCases.cs:188,210,241,295,319`; `CancelPollUseCase.cs:40-42`; `FinalizePollUseCase.cs:60-62`; `PollInvitationUseCases.cs:52-54,75-77`; `PollGuestManagementUseCases.cs:57-59,103-105,150-152`; `PollFinalizationPreviewUseCase.cs:45` |
| 11 | List/discover reads resolve visibility in SQL, not in the in-memory engine | Set-returning reads cannot round-trip every candidate through the engine | `ListDiscoverableSessionsUseCase.cs:6-11` (A-077(c) exclusion set + inheritedOnly fragment), `GetActionableInboxUseCase.cs:8-11`; the fragment annotates itself as the SQL projection of `VisibilityEngine.cs:32` (`PollVisibilitySql.cs:14`). Both carry list dimensions and `RequireOnboarded` |
| 12 | Availability resolves through `AvailabilityVisibility`, not `VisibilityEngine` | Availability is friend-scoped, not session-scoped | `FindOverlapWindowsUseCase.cs:62` + `AvailabilityVisibility.cs:22,34-47`; `RequireRead` throws NotFound (`:50-54`); writes owner-scoped with a 404 miss (`AvailabilityBlockUseCases.cs:51-57`); A-078 no-echo intact (`FindOverlapWindowsUseCase.cs:81`) |

## Headers (Q7)

App-wide hardening headers shipped as middleware (`Program.cs`, registered beside the existing
Referrer-Policy block): `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
`Permissions-Policy: camera=(), microphone=(), geolocation=(), payment=()`, and a
`Content-Security-Policy` of `default-src 'self'; script-src 'self'[ 'sha256-<hash>' ...]; style-src
'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri
'self'; form-action 'self'`. `style-src 'unsafe-inline'` stays because Radix portal positioning and
the tour arrow set inline `style` attributes, which hashes cannot cover.

**Hash mechanism:** `CspInlineScriptHasher` (`src/PartyUp.Api/Security/CspInlineScriptHasher.cs`)
SHA-256-hashes each inline `<script>` body (scripts with a `src` attribute are skipped) from the
built `wwwroot/index.html`, read once at startup — never a hardcoded hash, which would rot on the
next Vite build. `wwwroot` exists only in the container image; `dotnet run` and the integration test
host have none, so the hash tokens are simply omitted there and the header still ships without them.

**Route-local override:** the avatar proxy (`AvatarProxyEndpoints.cs:37-40`) keeps its own stricter
`default-src 'none'; sandbox` CSP — the global middleware checks `ContainsKey("Content-Security-Policy")`
in its `OnStarting` callback and does not overwrite an already-set header.

**Tests:** `tests/PartyUp.Api.IntegrationTests/Security/SecurityHeadersTests.cs` —
`ApiResponse_CarriesTheAppWideHardeningHeaders` (an API JSON response), `GuestExchangeRedirectLeg_CarriesTheAppWideHardeningHeaders`
(`/g` redirect leg), `AvatarProxyMiss_StillCarriesTheAppWideHardeningHeaders` (`/a` 404 miss, and
confirms no `sha256` token in the integration host's wwwroot-less CSP); the avatar proxy's route-local
CSP itself stays pinned by the existing `AvatarProxyEndpointTests.ResponseHeadersMatchSpecExactly`.
Unit coverage for the hash computation (known inline script → correct token; no `wwwroot` → no hash
token) is `tests/PartyUp.Api.IntegrationTests/Security/CspInlineScriptHasherTests.cs`.

**Open/deferred:** HSTS in Caddy stays the owner's duty (Q7). The SPA-document header assertion in the
CI browser lane's *built* topology (Dockerfile-copied `wwwroot`, hash tokens present) is deferred to
that lane per PHASE8_DESIGN.md §5 — out of this ticket's scope; the dev topology (Vite serving the
SPA) is unaffected since the API's CSP does not apply to Vite's own responses.

## Non-exceptions confirmed complete

- **CSRF: zero exemptions.** `ProblemAntiforgeryFilter` is attached to the whole `/api/v1` group before
  any endpoint is mapped (`Program.cs:338-339`); it bypasses only safe methods and
  `IAntiforgeryMetadata{RequiresValidation:false}` (`FriendGraphProblems.cs:112-127`), and no endpoint
  in `src/` opts out (`grep -rn "DisableAntiforgery" src/` → zero hits).
- **Rate limiting: no unlimited route within limiter scope.** The `default` dimension declares
  `CoversEveryApiOperation: true` and matches any operation id including null; `avatar-proxy` is the
  only `ExcludesDefault` dimension (`RateLimitPolicies.cs`).
- **`VisibilityEngine.Require` doc comment corrected.** It described itself as "the gate the
  application layer puts in front of every read and write" while having zero production call sites —
  every gate uses the equivalent `Resolve(...).IsVisible` form. The overstatement is how the two S9
  existence oracles went unnoticed, so the comment now says so explicitly
  (`src/PartyUp.Domain/Visibility/VisibilityEngine.cs`).
