# PartyUp — Phase 2 Technical Design (Accounts, Auth, Onboarding)

**Status:** round-8 draft (round-1 through round-7 review findings resolved — round 7 was the first **cross-family** review, Codex gpt-5.6-sol, verdict PASS_WITH_NOTES with 6 findings, all accepted; see §18 Review log)
**Basis:** PRODUCT_SPEC.md (D-001..D-180), ARCHITECTURE.md (A-001..A-020), DOMAIN_MODEL.md (ratified §1–§13), IMPLEMENTATION_PLAN.md "Phase 2 — Accounts, auth, onboarding".
**Audience:** implementers who have not read the design conversation. Everything needed to build Phase 2 is here or cited by exact path.

New architectural decisions in this document are numbered **A-021 .. A-053** and are meant to be appended to ARCHITECTURE.md §9 when this design is ratified. (Round 2 added A-043..A-048, round 3's first pass added A-049, round 3's second pass added A-050..A-052, and round 4 added A-053 — all of them consequences of the adversarial reviews; see §18. Rounds 5, 6 and 7 added no new decision: every finding in all three was resolved inside an existing one — round 7's six inside A-024/A-025, A-029, A-045 and A-050.)

---

## 1. Scope and non-scope

### 1.1 In scope (from the plan)

1. ASP.NET Identity, cookie-based BFF auth: email+password, passwordless magic link, Google OAuth, Steam OpenID 2.0 (D-067). Apple deferred to the mobile phase.
2. Antiforgery for the SPA.
3. Transactional email behind a port, dev implementation that needs no provider; sending domain `mail.partyup.social`.
4. Onboarding per D-127: age gate → auth → username → display name → avatar → time zone → optional platform linking → friends (friends step is a stub screen; Phase 3 owns it).
5. Username rules D-128/D-145 wired to the **existing** `UsernameRegistry` domain service; stable friend-code generation.
6. Auth-method linking and the last-method unlink error (D-137); email recovery (D-067, D-078).
7. Profile constraints + profanity filter (D-096/D-097), normalized wordlists en/pt-BR/es, timeboxed.
8. Account deletion **skeleton**: request → 7-day cancelable grace → sign-out-everywhere → login-cancels (D-148/D-158/D-165) → executor run at grace expiry → deletion journal (A-017). Public deletion-request route (D-072) — API **and** both public SPA pages (round-5, §7.4: the confirm page is the only consumer of the Steam leg's fragment token, so the banned/email-less path is otherwise unreachable).
9. Admin foundations: seeded admin role (D-177), `BannedAt` on User, ban/unban reusing the security-stamp sign-out, lookup exclusion (D-180). A minimal admin user-list read endpoint (D-179 data half); no admin UI.
10. First React slices: onboarding, profile, settings; generated API client; i18n including server-rendered email templates in 3 locales (D-060/D-146).
11. Migrations forward-only from this phase on.

### 1.2 Explicitly not in scope

- Per-entity deletion effects for friendships/sessions/polls/availability (D-092). The `DeletionExecutor` already exists in Domain and is called; in Phase 2 it runs against an empty `DeletionScope` because those tables do not exist yet (§9.4). Phases 3–6 add loaders; Phase 8 verifies completeness.
- Notification records (no notification tables exist until their phase). Phase 2 side effects are emails only.
- Admin dashboard (D-178) and admin UI (D-179 presentation) — Phase 8.
- Hangfire (A-013). See A-034 for how grace expiry executes without it.
- Friend requests, friend code lookup surfaces, Steam friend import (Phase 3). Phase 2 only *generates and stores* the friend code and exposes it on `GET /me`.
- Sign in with Apple (D-067, mobile phase).

### 1.3 Ambiguities resolved by this document

The recon reports flagged nine open points. Resolutions, each expanded where cited:

| # | Ambiguity | Resolution |
|---|---|---|
| 1 | D-092 split across phases | Phase 2 tests cover request/grace/cancel/sign-out/tombstone/journal only. TRACEABILITY gets a `2 (skeleton)` phase marker for D-092; the row stays open until Phase 8. §14.2 |
| 2 | Ban content-removal reuses the deletion executor | Ordering constraint made explicit: slice 8 (deletion) lands before slice 9 (admin). §15 |
| 3 | Apple deferral / provider extensibility | Auth methods are rows in `auth_user_logins` with a free-form provider string; adding Apple is a config + adapter change with no migration. A-025 |
| 4 | D-137 error wording | The error is explicit and explains why: code `last_auth_method`, message key `errors.auth.lastMethod`. Not pre-disabling the button is a design *preference*, not a D-087 requirement. §6.5 |
| 5 | Steam-as-auth vs Steam-as-platform-link | Two stored concepts, one user flow. A-030 |
| 6 | Profanity vs username reservation | The text policy runs **before** any registry claim, so a rejected name never creates or consumes a registry row. A-036 |
| 7 | Banned users in lookup predicates | `LookupVisibility.IsListed` and `User.CanBeFriended` exclude banned users; `ProfileVisibility` hides a banned subject from everyone except self and accepted friends; `IdentityMasking` does **not** mask them (content and attribution untouched, D-180). A-032/**A-043** |
| 8 | Guest age-gate exemption (D-124) | No Phase 2 code: guests do not exist until Phase 4. The age gate lives only on the registered-account paths. |
| 9 | Email locale (D-060/D-146 extension to email) | Confirmed intentional: `IEmailSender` requires an explicit recipient locale argument; there is no default-locale overload. A-039 |

---

## 2. Current state the design builds on (verified against the code)

- `src/PartyUp.Domain/Users/User.cs` — the `User` aggregate. Lifecycle `Onboarding → Active → PendingDeletion → Deleted`. **No email, no credential, no auth field of any kind.** Constructed only via `User.BeginOnboarding(UserId, FriendCode, AgeGateSource, IClock)`. `CompleteOnboarding(Username, DisplayName, Avatar, DateTimeZone, Locale, IClock)` is the only door into `Active` and requires all five fields at once. `MarkDeleted(IClock)` nulls username/display name/avatar/friend code and clears platform links. **`IsProfileComplete` already exists** (`User.cs:90-91`, `Username.HasValue && DisplayName.HasValue && Avatar.HasValue && TimeZone is not null && Locale.HasValue`) — §4.1 consumes it, it does not add it (round-4). `IsDiscoverable`/`CanBeFriended` (`:85`, `:88`) currently carry no ban term; §4.2 adds one to `CanBeFriended` only.
- `src/PartyUp.Domain/Users/UsernameRegistry.cs` + `UsernameRegistryEntry.cs` — the single username constraint domain; `Claim`, `Change`, `Release`, `Canonicalize` (trim + `ToLowerInvariant`), 30-day `ReservationPeriod`.
- `src/PartyUp.Domain/Users/DeletionExecution/DeletionExecutor.cs` — `Execute(User, DeletionScope, IClock)`, seven ordered steps, idempotent, returns `DeletionExecutionResult` including `Journal` (`DeletionJournalEntry(UserId, Instant ExecutedAt)`).
- `src/PartyUp.Domain/Visibility/LookupVisibility.cs` — `IsListed`, `CanSurface`, `CanReceiveFriendRequest`, `CanMatchSteamImport`.
- `src/PartyUp.Infrastructure/Persistence/AppDbContext.cs` — **empty**: no `DbSet`, no `OnModelCreating`. The single migration `20260730151405_InitialCreate` has empty `Up`/`Down`. **Phase 2 creates the first real tables.**
- `src/PartyUp.Api/Program.cs` — Serilog, `AddInfrastructure`, OpenAPI, health checks, Data Protection when `/data/dataprotection` exists, `api = app.MapGroup(ApiRoutes.V1)` with an `api.MapFallback(NotFound)` guard, SPA fallback. **No auth middleware, no endpoints.**
- `web/src/` — `App.tsx` placeholder, `api/client.ts` (`openapi-fetch` `createClient<paths>({ baseUrl: '/' })`), `api/schema.d.ts` (empty, generated), `i18n/index.ts` (i18next, `en`/`pt-BR`/`es`, single `common` namespace), `main.tsx` (QueryClientProvider → BrowserRouter). **No auth-aware code.**
- Tests: `tests/PartyUp.Domain.Tests` (xunit, `Method_Behavior_D###` naming, `NodaTime.Testing.FakeClock`, `TestUsers` fixture helpers), `tests/PartyUp.Application.Tests` (empty), `tests/PartyUp.Api.IntegrationTests` (`HealthEndpointTests`, `WebApplicationFactory<Program>`).

---

## 3. Identity integration shape

### A-021 — Identity is a separate persistence-layer aggregate keyed 1:1 to the domain `UserId`; no custom `IUserStore`

`PartyUp.Infrastructure.Identity.AuthUser : IdentityUser<Guid>` with `Id` **equal to** the domain `User.Id.Value`. The default `UserStore<AuthUser, AuthRole, AppDbContext, Guid>` from `Microsoft.AspNetCore.Identity.EntityFrameworkCore` is used unmodified.

Rationale: writing a custom `IUserStore` over the pure `User` aggregate would drag email, password hash, lockout counters, `DateTimeOffset` and normalized-name semantics into `PartyUp.Domain`, which references NodaTime and nothing else (ARCHITECTURE §2) and whose tombstone invariant is "no PII survives" (DOMAIN_MODEL §3). A sibling table keeps Identity's BCL/mutable-bag model where it belongs — in Infrastructure — and costs one extra row per user. There is no scenario in this product where the two can diverge: creation and deletion are both single transactions that write both.

Consequences:
- `AppDbContext` derives from `IdentityDbContext<AuthUser, AuthRole, Guid>` (not plain `DbContext`) and adds the domain sets. Snake-case naming and NodaTime mapping continue to apply.
- The FK direction is `auth_users.id → users.id` (`ON DELETE RESTRICT`). The domain row is the parent; a tombstone keeps the auth row (stripped) so the FK and the security stamp still exist for a hostile-login attempt to fail against.
- No `UserManager`/`SignInManager` type ever appears in `PartyUp.Application` or `PartyUp.Domain`. Application talks to ports (§8.2).

### A-022 — Email, password, lockout and external-login rows live **only** on the Identity side

`users` (domain) never gains an email column. `auth_users` holds `Email`/`NormalizedEmail`/`EmailConfirmed`/`PasswordHash`/`SecurityStamp`/`ConcurrencyStamp`/lockout fields; `auth_user_logins` holds `(provider, provider_key, user_id)` for Google and Steam. This keeps `User.MarkDeleted` correct as written (it nulls everything PII it owns) and confines the tombstone's new obligation to one extra Infrastructure step (§9.5).

### A-023 — `AuthUser.UserName` is the user id string, never the product username

`UserName` = `NormalizedUserName` = the `Guid` in `"D"` format. The product username lives exclusively in `users.username` + `username_registry.name_canonical` (already the single constraint domain per DOMAIN_MODEL §3).

Rationale: Identity's `NormalizedUserName` unique index is a second, competing case-insensitive name space with different normalization (`ToUpperInvariant` by default) and no reservation concept. Two constraint domains for one product rule is exactly the bug D-145 invites (a reserved name would be free in one table and taken in the other). Making `UserName` an opaque stable id removes the conflict entirely and also means a username change never touches Identity. `Login` by username is not a product feature (D-067 lists email, passwordless email, Steam, Google), so nothing is lost.

`IdentityOptions.User.RequireUniqueEmail = false` at the Identity level (social-only accounts have **no** email, D-078); uniqueness is enforced by a **partial unique index** on `auth_users(normalized_email) WHERE normalized_email IS NOT NULL` (§5.3).

### A-024 — The claims principal carries only id, roles and the security stamp; product state is fetched per request

Cookie principal claims:

| Claim | Value |
|---|---|
| `ClaimTypes.NameIdentifier` (`sub` in OpenAPI docs) | domain `UserId` |
| `ClaimTypes.Role` | `admin` when applicable (D-177) |
| `AspNet.Identity.SecurityStamp` | Identity's stamp claim |

**That claim set requires a custom `IUserClaimsPrincipalFactory<AuthUser>`; the default emits four more groups (round-7 cross-family finding 6).** ASP.NET Core's `UserClaimsPrincipalFactory<TUser, TRole>` — the implementation `AddIdentityCore().AddRoles<AuthRole>()` registers — writes `options.ClaimsIdentity.UserNameClaimType` (the user name), `EmailClaimType` (whenever `SupportsUserEmail` and the row has an address), every row of `auth_user_claims` via `UserManager.GetClaimsAsync`, and the role claims, on top of the user id and the security stamp. Left at the default, the cookie this design describes as "id + roles + stamp" would in fact carry the user's **email address** in a client-held (if encrypted) ticket that survives an A-048 address change and a §9.5 tombstone purge until the cookie expires, and would grow with any future `auth_user_claims` row. The table above is the whole contract, so it is enforced by a factory rather than by prose:

> **`Infrastructure/Identity/PartyUpUserClaimsPrincipalFactory.cs`** implements `IUserClaimsPrincipalFactory<AuthUser>` **from scratch** (it does not derive from `UserClaimsPrincipalFactory<TUser, TRole>`, for the same reason A-025's validator does not derive from `SecurityStampValidator<TUser>`: overriding a base whose job is to add claims is how the extra claims come back). It builds one `ClaimsIdentity(IdentityConstants.ApplicationScheme, options.ClaimsIdentity.UserNameClaimType, options.ClaimsIdentity.RoleClaimType)` carrying exactly three groups — `options.ClaimsIdentity.UserIdClaimType` (= `ClaimTypes.NameIdentifier`) with the `AuthUser.Id` string, one `RoleClaimType` claim per `UserManager.GetRolesAsync`, and `options.ClaimsIdentity.SecurityStampClaimType` with `AuthUser.SecurityStamp` — and nothing else. No user-name claim, no email claim, no `auth_user_claims` read.
>
> **Layer: Infrastructure**, per A-025's own rule. `IUserClaimsPrincipalFactory<TUser>`, `UserManager<TUser>` and `IdentityOptions` are `Microsoft.Extensions.Identity.Core` types with no `HttpContext` in any signature, so they are exactly the half A-025 keeps out of Api; only `SignInManager`/`AddIdentityCookies`/the stamp validator are shared-framework and stay in `PartyUp.Api`.
>
> **Registration is a `Replace`, not an `Add`** (slice 2, §15): `services.Replace(ServiceDescriptor.Scoped<IUserClaimsPrincipalFactory<AuthUser>, PartyUpUserClaimsPrincipalFactory>())` **after** `AddIdentityCore<AuthUser>().AddRoles<AuthRole>()`. A plain `AddScoped` would depend on whether the framework registered its default with `TryAdd` or `Add` in the pinned version — last-registration-wins holds for a single resolve, but an `IEnumerable<IUserClaimsPrincipalFactory<AuthUser>>` resolution or a future framework change makes that silent, and the failure mode is invisible (a working sign-in with too many claims). `Replace` is deterministic and fails loudly if the descriptor is not there.
>
> The security-stamp claim is **not** optional: A-025's validator compares the principal's `AspNet.Identity.SecurityStamp` claim against the projection, so a factory that dropped it would make every authenticated request a mismatch.

Test: `AuthCookieTests.Login_IssuesAPrincipalWithIdRolesAndStampOnly` (§14.3, slice 2) — sign in, take the `pu.auth` value, unprotect it with the test host's own `IDataProtectionProvider` (`TicketDataFormat`, purpose `"Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationMiddleware"`, `IdentityConstants.ApplicationScheme`, `"v2"`), and assert the ticket principal's claim types are exactly `{NameIdentifier, SecurityStamp}` (plus `Role` for a seeded admin) — in particular **no email claim**, for an account that has a confirmed address on file.

No email, display name, username, lifecycle or ban flag is embedded. Everything mutable is read per request as a projection `(user_id, lifecycle, banned_at, grace_ends_at, is_admin, is_profile_complete, locale, security_stamp)` — **one** query, `users u JOIN auth_users a ON a.id = u.id` on the same PK, cached for the request scope. The `security_stamp` column is in the projection so that the stamp validator (A-025) and the accessor genuinely share one query; see A-025 for why that had to be stated explicitly.

**`is_admin` has a third relation, stated (round-5).** Role membership lives in `auth_user_roles` and the role name in `auth_roles` (§5.2); `auth_users` carries no admin column, so the two-table statement above could not produce the column the record returns. An implementer taking it literally would either hard-code `false` — making `SessionResponse.isAdmin` permanently false and every projection-based admin check unsatisfiable — or add a second round trip and silently break A-025's cost claim. It is an **`EXISTS` sub-select inside the same statement**, not a second query:

```sql
SELECT u.id, u.lifecycle, u.banned_at, u.grace_ends_at,
       (u.username IS NOT NULL)                       AS is_profile_complete,   -- A-026
       u.locale, a.security_stamp,
       EXISTS (SELECT 1 FROM auth_user_roles ur
                 JOIN auth_roles r ON r.id = ur.role_id
                WHERE ur.user_id = u.id AND r.normalized_name = 'ADMIN')        AS is_admin
  FROM users u JOIN auth_users a ON a.id = u.id
 WHERE u.id = @id;
```

The cost claim throughout this document is therefore "**one indexed round trip** per authenticated request" — the join and the `EXISTS` are all in it — not "a two-table read".

### A-052 — The projection **read** is a port implemented in Infrastructure; Api keeps only claims parsing, the request-scoped cache and the validator wiring

Round-3 review: §8.2's rule is "ports (interfaces in Application, implementations in Infrastructure)", but round-2 placed `CurrentUserAccessor` in `Api/Auth/` *and* specified the two-table SQL there — making `PartyUp.Api`, which ARCHITECTURE §2 defines as "composition root. Minimal API endpoints, OpenAPI, health, static SPA hosting", the owner of a hot-path persistence read, and the only port implementation in Phase 2 living outside Infrastructure, in the one project no test project can reach in isolation. A-025's layering argument (the `HttpContext`-bound half genuinely must be in Api, because `SecurityStampValidator`/`CookieValidatePrincipalContext` are shared-framework types Infrastructure must not reference) is right about the *validator*; it does not extend to the *data read*. The split:

```csharp
// PartyUp.Application/Abstractions/ICurrentUserProjectionReader.cs
public interface ICurrentUserProjectionReader {
    Task<CurrentUserProjection?> ReadAsync(UserId id, CancellationToken ct);
}
public sealed record CurrentUserProjection(UserId Id, UserLifecycle Lifecycle, bool IsBanned,
                                           Instant? GraceEndsAt, bool IsAdmin, bool IsProfileComplete,
                                           Locale? Locale, string SecurityStamp);
```

- **Application** owns `ICurrentUserProjectionReader`, `CurrentUserProjection`, `ICurrentUserAccessor` and `CurrentUser`.
- **`Infrastructure/Persistence/CurrentUserProjectionReader.cs`** owns the query — the single-PK `users u JOIN auth_users a ON a.id = u.id` statement **with A-024's `is_admin` `EXISTS` sub-select over `auth_user_roles`/`auth_roles`** (round-5: `auth_users` has no admin column, so the two-table wording could not fill `CurrentUserProjection.IsAdmin`) — like every other persistence read in the codebase. One round trip.
- **`Api/Auth/CurrentUserAccessor.cs`** keeps only: parse `NameIdentifier` from `HttpContext.User`, call the reader, cache the result for the request scope, project to `CurrentUser`. No SQL, no `DbContext`.
- **`Api/Auth/PartyUpSecurityStampValidator.cs`** keeps only the shared-framework wiring and reads `SecurityStamp` off the same request-scoped projection. It is a from-scratch `ISecurityStampValidator` that rejects on mismatch and does nothing on match — **it must not derive from `SecurityStampValidator<TUser>`**, whose `ValidatePrincipalAsync` re-issues the principal via `UserManager.GetUserAsync` + `SignInWithClaimsAsync` and would add an Identity user load and a role read per request, contradicting A-025's cost statement (round-4).

§8.2's rule and this design now agree, and the query becomes reachable from `PartyUp.Api.IntegrationTests` against real migrations like everything else in Infrastructure.

Rationale: lifecycle, ban and onboarding-completeness all change out of band and must take effect immediately (D-165: "immediately signs the account out on every active device"; D-180: same for ban). Embedding them in a cookie makes correctness depend on cookie refresh timing. One indexed PK read per authenticated request is trivially affordable at the sizing ARCHITECTURE §1 commits to (single-digit-thousands MAU on one VM), and it is the same read the security-stamp validator needs anyway.

### A-025 — Security-stamp validation runs on **every** authenticated request (`ValidationInterval = TimeSpan.Zero`)

`SecurityStampValidatorOptions.ValidationInterval = TimeSpan.Zero`. **The option is inert under this design's own validator, and is set deliberately anyway (round-6 minor):** the cookie handler invokes `ISecurityStampValidator.ValidateAsync` on *every* `ValidatePrincipal`, and the interval is consulted only inside the framework's `SecurityStampValidator<TUser>` — which `PartyUpSecurityStampValidator` replaces from scratch (below), so "every request" is a property of the implementation, not of the option. It stays configured so that a future revert to the framework validator cannot silently reintroduce an interval, and so the intent is readable at the wiring site. Rotating the stamp is therefore the single mechanism that implements: deletion-request sign-out (D-165), ban sign-out (D-180), password reset/change sign-out, and unlinking a compromised provider.

Rationale: the spec words for both D-165 and D-180 are "immediately". A non-zero interval means a banned user keeps a working session for up to the interval.

**Cost, stated accurately** (round-1 review corrected an earlier claim that the validator and the accessor "share one read"): the security stamp lives on `auth_users`, the A-024 projection reads `users`, and `is_admin` lives in `auth_user_roles`/`auth_roles`. To keep it to **one round trip** per request, A-024's projection is a single statement — `users u JOIN auth_users a ON a.id = u.id` on the same PK, plus an `EXISTS` sub-select for `is_admin` (round-5; the round-4 "two-table read" wording named no source for that column at all) — returning `(user_id, lifecycle, banned_at, grace_ends_at, is_admin, is_profile_complete, locale, security_stamp)`. The custom validator reads the stamp from that scoped, request-cached projection.

**The validator is reject-only; it does not re-issue the principal (round-4 correction, and the two halves of the round-3 paragraph could not both be true).** Round-4 review found A-025 asserting simultaneously that the read stays at one query per request (A-052) and that "the principal is re-issued and a `Set-Cookie` is written on **every** authenticated response". Re-issuing is done by `SecurityStampValidator<TUser>.ValidatePrincipalAsync`, which calls `SignInManager.ValidateSecurityStampAsync` → `UserManager.GetUserAsync(principal)` and then `SignInManager.SignInWithClaimsAsync` — it loads the `AuthUser` and re-runs the claims-principal factory, roles included, from the database. Deriving from it and overriding only the comparison does not remove those reads, so the re-issuing design costs the A-024 projection **plus** an Identity user load **plus** a role read on every authenticated request, and the cost sentence would be wrong. This design picks the other implementation and states its consequence:

> `PartyUpSecurityStampValidator` implements `ISecurityStampValidator` **from scratch**. It reads `SecurityStamp` off the request-scoped `CurrentUserProjection` (A-052), compares it to the principal's `AspNet.Identity.SecurityStamp` claim, and on mismatch calls `context.RejectPrincipal()` + `SignOutAsync(IdentityConstants.ApplicationScheme)`. **A `null` projection — no row for the principal's id, which is what a hard-deleted or never-existing subject looks like — is a mismatch and rejects too (round-5; "reject-only" must not mean "fail open on absence").** On a match it does **nothing** — no `SignInWithClaimsAsync`, no re-issue, **no `Set-Cookie` of its own**. Cost is genuinely one indexed PK round trip per authenticated request, shared with the accessor. **Precisely: the *validator* never writes a cookie header (round-6 minor).** The claim is not "no `Set-Cookie` on a valid session" — A-028 sets `SlidingExpiration = true`, so the cookie handler itself renews the ticket, and emits one `Set-Cookie`, once the request falls past the halfway point of `ExpireTimeSpan` (15 days). That renewal is the framework's, carries the *existing* claims, costs no extra query, and is the behavior A-028 wants; what A-025 rules out is a per-response re-issue driven by stamp validation, which would reload the `AuthUser` and re-run the claims factory on **every** authenticated request. `ICurrentUserAccessor.GetAsync` returns `null` on the same input, so every `Require*` policy fails closed. Test: `SecurityStampTests.Response_ForAPrincipalWithNoProjectionRow_IsRejected` (§14.3).

The consequence — a cookie's `ClaimTypes.Role` is frozen for the life of the cookie — is closed rather than accepted, because a frozen admin claim would make A-039's seeding take effect only at next sign-in: **every role grant and revoke rotates the security stamp**, exactly as ban and deletion do. `AdminSeeder` (A-039) and any future role change therefore call `IAccountSessionService.SignOutEverywhereAsync` in the same transaction as `AddToRoleAsync`; the stale principal is rejected on its very next request and the next sign-in carries the new role. So "role changes take effect immediately" remains true, through the one mechanism A-025 already owns, and nothing in the design depends on per-response re-issue. Tests: `SecurityStampTests.Response_ForAValidSession_CarriesNoValidatorIssuedSetCookie` and `AdminSeederTests.Seed_RotatesTheStampSoTheRoleTakesEffect_D177` (§14.3).

**Layering** (round-1 review): `SecurityStampValidator<TUser>`, `SignInManager<TUser>` and `AddIdentityCookies()` live in `Microsoft.AspNetCore.Identity` — the ASP.NET Core *shared framework*, with `HttpContext`/`CookieValidatePrincipalContext` in their signatures. Infrastructure must not take that dependency (§16). Therefore: `UserManager` + `EntityFrameworkStores` (from `Microsoft.Extensions.Identity.*`) stay in Infrastructure; `AddAuthentication().AddIdentityCookies()`, `AddSignInManager`, `SecurityStampValidatorOptions` and `PartyUpSecurityStampValidator` all live in **`PartyUp.Api`** (`Api/Auth/`). See slice 2 (§15).

Helper placement is unchanged: `IAccountSessionService` is a port; its implementation only calls `UserManager.UpdateSecurityStampAsync`, which is `Microsoft.Extensions.Identity.Core`, so it may stay in Infrastructure.

`IAccountSessionService.SignOutEverywhereAsync(UserId)` = `UserManager.UpdateSecurityStampAsync`. All four callers use it; there is no second sign-out mechanism.

### A-026 — Onboarding-incomplete is an authorization policy, not a separate authentication state

A user in `UserLifecycle.Onboarding` gets a **fully valid auth cookie**. Endpoint groups declare one of three policies:

| Policy | Meaning | Failure |
|---|---|---|
| `RequireAuthenticated` | any signed-in, non-banned, non-elapsed-grace user | 401 (anonymous) / 403 `account_banned` |
| `RequireOnboarded` | the above **and** `lifecycle ∈ {Active, PendingDeletion}` **and** `IsProfileComplete` | 403 `onboarding_required` |
| `RequireAdmin` | `RequireOnboarded` + `CurrentUser.IsAdmin` | 404 (admin surface is not advertised) |

**`RequireAdmin` is authorized by the fresh projection, not by the cookie's role claim (round-5).** A-025's validator is reject-only and never re-issues the principal, so `ClaimTypes.Role` is frozen for the life of a cookie; A-039 closes the *grant* direction by rotating the stamp, but the *revoke* direction is a deliberate SQL/ops action (A-039: "removing an entry from config does not revoke"), and a runbook `DELETE FROM auth_user_roles` rotates nothing — a revoked admin would keep every admin endpoint until their cookie expired. The policy therefore reads `ICurrentUserAccessor`'s `IsAdmin`, which comes from the per-request `EXISTS` in A-024's projection and is correct on the very next request either way. `ClaimTypes.Role` stays in the principal (A-024) for framework-level diagnostics and for the OpenAPI-documented scheme, but nothing in Phase 2 authorizes on it. The revocation runbook step (`RUNBOOK-admin.md`, closing slice) additionally instructs the operator to rotate the user's security stamp so any open session is signed out, matching the grant path. Test: `AdminAuthorizationTests.Admin_AfterAnOutOfBandRoleRevocation_Returns404_D177` (§14.3).

The complete `RequireAuthenticated` list, reconciled with §7.2/§7.3 (round-6 minor — the round-5 enumeration named three entries while §7.3 marked five more as `auth`): `/api/v1/auth/*`, `/api/v1/onboarding/*`, `GET /api/v1/me`, **`GET`/`POST /api/v1/me/platform-links`, `PATCH`/`DELETE /api/v1/me/platform-links/{id}`** (D-127 step 7 links platforms mid-onboarding, and `User.LinkPlatform` is legal in `Onboarding` — §7.2) and **`POST /api/v1/me/deletion`** (A-047: an account abandoned mid-onboarding must be deletable). Everything else in this and later phases uses `RequireOnboarded`.

**`IsProfileComplete`, not the lifecycle enum, is the `RequireOnboarded` predicate (round-2 correction).** A-047 makes `Onboarding → PendingDeletion` reachable, so `lifecycle ∈ {Active, PendingDeletion}` alone would admit an account that requested deletion mid-onboarding — profile-incomplete, but holding every onboarded-only endpoint. The projection in A-024 therefore carries `is_profile_complete` (computed as `users.username IS NOT NULL`, which the domain sets only inside `CompleteOnboarding` together with the other four fields) and `CurrentUser` carries it as a property. Test: `OnboardingGuardTests.ProtectedEndpoint_ForAnIncompleteProfileInPendingDeletion_Returns403_D127`.

Rationale: D-127 creates the account before the profile, and DOMAIN_MODEL §12-22 keeps such a user invisible; a half-onboarded principal must be able to talk to exactly the endpoints that finish onboarding and nothing else. 403 rather than 404 because the caller is asking about *their own* account — the fail-closed-to-404 rule (ARCHITECTURE §3) protects other people's resources, not the caller's own state.

### A-027 — Ban is a domain field with an infrastructure enforcement point

`User.BannedAt : Instant?` is added to the domain aggregate with `Ban(IClock)` / `Unban()` methods (§4.1), and `LookupVisibility.IsListed` is extended to exclude banned users (A-032). Enforcement of "login blocked" is at the auth boundary: the sign-in use case and `ICurrentUserAccessor` both reject `BannedAt is not null`.

---

## 4. Domain deltas (`PartyUp.Domain`)

Small and deliberate — Phase 2 is mostly an Infrastructure/Application/Api phase. Every change below must be reflected in DOMAIN_MODEL.md in the same commit (CLAUDE.md rule).

### 4.1 `User` (`src/PartyUp.Domain/Users/User.cs`)

```csharp
public Instant? BannedAt { get; private set; }

public bool IsBanned => BannedAt is not null;

/// <summary>D-180: orthogonal to the lifecycle. A banned user keeps their content and history.</summary>
public void Ban(IClock clock);      // no-op if already banned; forbidden on a tombstone (InvalidTransition)
public void Unban();                // no-op if not banned; forbidden on a tombstone
```

- **`IsDiscoverable` is left exactly as it is** (`Lifecycle is Active or PendingDeletion`). Round-1 review established that it has consumers beyond the lookup predicates — `User.CanBeFriended` (`src/PartyUp.Domain/Users/User.cs:88`) and, transitively through `LookupVisibility.IsListed`, `ProfileVisibility.For` (`src/PartyUp.Domain/Visibility/ProfileVisibility.cs:42`) — so overloading it with ban would have made a banned user's profile `Hidden` (→404) even to already-accepted friends. See A-043 for the placement that was chosen instead.
- `CanBeFriended` becomes `IsDiscoverable && !IsBanned` (an explicit, separately stated rule): a ban must stop *new* relationships forming, which is the discovery-side effect D-180 names.
- `MarkDeleted` does **not** clear `BannedAt`: a banned user who deletes their account stays a banned tombstone; `Unban` on a tombstone throws `InvalidTransition`.
- **`RequestDeletion` accepts `Onboarding` as well as `Active`** (A-047). The guard becomes `Lifecycle is Onboarding or Active`; `MarkDeleted` only ever requires `PendingDeletion` (`src/PartyUp.Domain/Users/User.cs:335`), so the tombstone path is unchanged. DOMAIN_MODEL §3's lifecycle diagram gains the `Onboarding → PendingDeletion` edge **and** the `PendingDeletion → Onboarding` return edge in the same commit.
- **`CancelDeletion` returns the user to the lifecycle they came from** (A-047, round-2 correction). Verified at `src/PartyUp.Domain/Users/User.cs:297-319`: it unconditionally sets `Lifecycle = UserLifecycle.Active`. Combined with A-047's new `Onboarding → PendingDeletion` edge and §6.6 step 4 (every successful login during grace cancels), "begin onboarding → `POST /me/deletion` → log in" produced an **`Active` user with null username, display name, avatar, time zone and locale** — breaking the one invariant the whole design rests on (A-033: "an `Active` user always has a complete profile"; `CompleteOnboarding` is documented as the only door into `Active`). The guard becomes:

  ```csharp
  GraceEndsAt = null;
  Lifecycle = IsProfileComplete ? UserLifecycle.Active : UserLifecycle.Onboarding;
  ```

  `IsProfileComplete` is **not a new member — it already exists** (round-4 correction; the round-2/round-3 drafts said "added to `User`", which would have produced a duplicate member and, under `TreatWarningsAsErrors`, a build error). Verified at `src/PartyUp.Domain/Users/User.cs:90-91`: `public bool IsProfileComplete => Username.HasValue && DisplayName.HasValue && Avatar.HasValue && TimeZone is not null && Locale.HasValue;` — exactly the derived predicate this design needs, over the fields `CompleteOnboarding` sets together, so it cannot drift. Phase 2 adds no member here; it only changes `CancelDeletion` to consult it (and §2 now records the predicate as pre-existing state). `CompleteOnboarding` stays the only door into `Active` from a *fresh* account; the cancel path can only ever restore a state the user already held. Tests: `UserDeletionTests.CancelDeletion_ReturnsAnOnboardingUserToOnboarding_D158`, `UserDeletionTests.CancelDeletion_ReturnsAnActiveUserToActive_D158`, `UserDeletionTests.CancelDeletion_NeverProducesAnActiveUserWithAPartialProfile_D127`.
- New domain events: `UserBanned(UserId, Instant BannedAt)`, `UserUnbanned(UserId)` in `UserEvents.cs` (ids/instants only, per DOMAIN_MODEL §9).

### 4.2 Visibility placement of the ban (A-032, revised)

### A-043 — Ban excludes from *lookup*, not from *direct profile access by existing relationships*

Three separate one-line edits, each with its own test, instead of one overloaded predicate:

| Predicate | Change | Effect |
|---|---|---|
| `LookupVisibility.IsListed` | `candidate.IsDiscoverable && !candidate.IsBanned` | search, friend-code lookup, `CanSurface`, `CanReceiveFriendRequest`, `CanMatchSteamImport` all exclude banned users |
| `User.CanBeFriended` | `IsDiscoverable && !IsBanned` | `Friendship.Create` (`src/PartyUp.Domain/Friendships/Friendship.cs:73`) refuses a banned party |
| `ProfileVisibility.For` | **a total ban branch**, placed after the self branch and *before* the existing `IsListed` gate (see below) | a banned user's profile resolves **only** for self and for an already-accepted friend (`Full`); every other viewer gets `Hidden` |

**Round-2 correction — the guard swap was wrong.** The round-2 draft said "gate on `subject.IsDiscoverable` instead of `LookupVisibility.IsListed(subject)`" and described the outcome as "`Full` for friends, `UsernameOnly` otherwise". Verified against `src/PartyUp.Domain/Visibility/ProfileVisibility.cs:42` — the function's tail is `viewer.IsFriendOf(subject.Id) ? Full : UsernameOnly` and there is **no** "already has a relationship" filter to lean on, so that swap yields `UsernameOnly` for *any* stranger. That keeps a banned user resolvable by profile link — and `LookupVisibility`'s own doc comment enumerates the lookup surfaces as "profile link, friend code, QR resolution, username search", so a stranger resolving `/u/{username}` and getting D-068's pre-friendship disclosure is precisely the discovery surface D-180 (Confirmed: "hidden from lookup and discovery") closes. It also contradicted §14.1's own `ProfileVisibilityTests.For_HidesABannedUserFromANonFriend_D180`. The resolvable version of the intent is a relationship gate, not a guard swap:

**Round-5 correction — the ban branch must be *total*, not a pre-filter.** The round-4 draft specified `if (subject.IsBanned && !viewer.IsFriendOf(subject.Id)) return Hidden;` followed by the unchanged `if (!LookupVisibility.IsListed(subject)) return Hidden;`, with an inline comment claiming the earlier branch "has already handled" friends. It has not: for a banned subject *with* an accepted friend as viewer, the first branch is skipped, control falls into the `IsListed` gate — which the same decision has just made false for banned users (row 1 of the table above) — and the function returns `Hidden` for exactly the case A-043 exists to preserve. Verified against `src/PartyUp.Domain/Visibility/ProfileVisibility.cs:27-48` (blocked → self → `IsListed` → `viewer.IsFriendOf(subject.Id) ? Full : UsernameOnly`) and `LookupVisibility.cs:18-23` (`return candidate.IsDiscoverable;`). The two edits of A-043 interact, and a pre-filter cannot survive the interaction. The branch therefore answers the banned case completely and never falls through:

```csharp
if (subject.Id == viewer.Id)               // unchanged
{
    return ProfileDisclosure.Full;
}
if (subject.IsBanned)                      // D-180: banned users leave every lookup surface,
{                                          // profile-link resolution included — but an already-
    return viewer.IsFriendOf(subject.Id)   // accepted friend is not doing discovery. Total: the
        ? ProfileDisclosure.Full           // banned case never reaches the (now ban-excluding)
        : ProfileDisclosure.Hidden;        // IsListed gate below.
}
if (!LookupVisibility.IsListed(subject))   // unchanged
{
    return ProfileDisclosure.Hidden;
}
```

The self branch above it (`subject.Id == viewer.Id → Full`) is untouched, so a banned user still sees their own profile, and the blocked-counterpart branch above *that* still wins over everything (a blocked friend gets `Hidden` regardless of ban state). `/u/{username}` resolution is gated by exactly this function and by nothing else: the endpoint loads the subject, calls `ProfileVisibility.For`, and returns **404** on `Hidden` (ARCHITECTURE §3 fail-closed), so a stranger cannot distinguish a banned user from a nonexistent one.

Rationale: D-180 is "hidden from lookup and discovery … content and history are otherwise untouched". A friend who already knows the account is not doing discovery, and making *their* existing profile link 404 goes past what the decision grants — that is the case the round-1 finding raised and it stays fixed. A stranger following a link **is** doing discovery, so they get nothing. Both §14.1 tests (`For_KeepsABannedUsersProfileVisibleToAnExistingFriend_D180` → `Full`, `For_HidesABannedUserFromANonFriend_D180` → `Hidden`) now agree with the specified implementation and with the A-043 row in §17.

**`IdentityMasking` is deliberately untouched**: rosters and poll grids keep showing a banned user's real display name and avatar (D-180 "content and history are otherwise untouched"). This is the nuance the recon flagged (#7) and it is a design requirement, not an accident.

### A-047 — Every account state has a reachable deletion path

`src/PartyUp.Domain/Users/User.cs:275` previously refused deletion outside `Active`, which left an account abandoned mid-onboarding — one that already holds an email, a password hash, a Steam id, a persona and an avatar URL — permanently undeletable. PRODUCT_SPEC §8.6 requires both "In-app account deletion" and "A public web route for requesting account and associated-data deletion" for *the account*, not for a subset of lifecycles, and D-072 is Policy-required. Resolution:

1. Domain: `RequestDeletion` accepts `Onboarding` (§4.1). An onboarding user has no username to retire, so `DeletionScope` is empty and `DeletionExecutor` tombstones straight through — `MarkDeleted` already nulls a null username idempotently.
2. Domain, the other half of the edge (round-2): `CancelDeletion` restores `IsProfileComplete ? Active : Onboarding` (§4.1) rather than unconditionally `Active`. Adding the entry edge without the matching exit edge would have let an incomplete account walk into `Active` through the login-cancels path, defeating D-127's ordering and A-033's invariant.
3. API: `POST /me/deletion` moves from `RequireOnboarded` to `RequireAuthenticated` (§7.3).
4. API, second half (round-2): **`RequireOnboarded` is a profile-completeness policy, not a lifecycle enum test** — see A-026. Without that, an `Onboarding` user who merely *requested* deletion satisfied `lifecycle ∈ {Active, PendingDeletion}` and gained every onboarded-only endpoint while still profile-incomplete.
5. Public route: reachable for email-less accounts via the Steam-assertion variant (§7.4, two-step).
6. Tests: `RequestAccountDeletionUseCaseTests.Execute_DeletesAnAccountAbandonedDuringOnboarding_D072`, `DeletionSweepTests.Sweep_TombstonesAnOnboardingAccount_D148`, the three `CancelDeletion` domain tests in §4.1, and `OnboardingGuardTests.ProtectedEndpoint_ForAnIncompleteProfileInPendingDeletion_Returns403_D127`.

### 4.3 `FriendCode` generation

Add `src/PartyUp.Domain/Users/FriendCodeFormat.cs` — a pure format/validation helper only:

```csharp
public static class FriendCodeFormat
{
    public const int Length = 10;
    public const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"; // Crockford-ish: no 0/O/1/I
    public static bool IsWellFormed(string candidate);
}
```

Generation itself is an Application port (`IFriendCodeGenerator`, §8.2) because it needs a CSPRNG and a uniqueness retry loop against the database. The code is an *identifier for discovery*, not an access grant, so A-015 does not force ≥256 bits; 10 chars over a 32-symbol alphabet is 50 bits, and its only power is "find this profile" (D-068 exposes only the username pre-acceptance). Rotation is out of MVP (§8.3 of the spec).

### 4.4 `DomainError`

Append-only enum. Phase 2 needs no new member: `Duplicate` covers taken usernames/emails, `NameReserved` covers reservations, `CooldownActive` covers D-128, `ValidationFailed` covers profanity and shape, `Forbidden` covers ban/last-method, `WindowClosed` covers expired tokens and elapsed grace. **Do not add values.**

---

## 5. Database schema and migrations

Postgres 17, snake_case (`EFCore.NamingConventions`), `timestamptz ↔ Instant` (`Npgsql...NodaTime`). All tables below are created for the first time in this phase.

### 5.1 Domain tables

**`users`**

| Column | Type | Notes |
|---|---|---|
| `id` | uuid PK | domain `UserId` (Guid v7) |
| `lifecycle` | int NOT NULL | `UserLifecycle` |
| `username` | text NULL | display form (original casing) |
| `display_name` | text NULL | |
| `avatar_kind` | int NULL | owned type `Avatar` |
| `avatar_emoji` | text NULL | |
| `avatar_source_platform_link_id` | uuid NULL | FK → `platform_links.id`, `ON DELETE SET NULL` |
| `time_zone_id` | text NULL | `DateTimeZone.Id`, converted via `DateTimeZoneProviders.Tzdb` |
| `locale` | text NULL | BCP-47 |
| `friend_code` | text NULL | |
| `age_gate_passed` | bool NOT NULL | |
| `age_gate_source` | int NULL | |
| `reminder_default` | int NOT NULL | |
| `created_at` | timestamptz NOT NULL | |
| `username_changed_at` | timestamptz NULL | |
| `grace_ends_at` | timestamptz NULL | |
| `deleted_at` | timestamptz NULL | |
| `banned_at` | timestamptz NULL | D-180 |
| `xmin` | system | optimistic concurrency (`UseXminAsConcurrencyToken`) |

Indexes: `ux_users_friend_code` UNIQUE on `upper(friend_code)` `WHERE friend_code IS NOT NULL`; `ix_users_grace_ends_at` `WHERE lifecycle = 2` (the sweeper's only query); `ix_users_lifecycle`.

Value objects map as EF **value converters** on scalar columns (`Username`, `DisplayName`, `Locale`, `FriendCode`, `UserId`, `DateTimeZone`) and one **owned type** for `Avatar` (three columns). Establish the converter pattern in `src/PartyUp.Infrastructure/Persistence/Converters/` — later phases reuse it (`ITextValue<T>` and `ITypedId<T>` make both converters generic; write `TextValueConverter<T>` and `TypedIdConverter<T>` once).

**`platform_links`** — `id uuid PK`, `user_id uuid NOT NULL FK→users.id ON DELETE CASCADE`, `platform int NOT NULL`, `handle text NOT NULL`. `UNIQUE (user_id, platform)`.

**`username_registry`** — `name_canonical text PK`, `state int NOT NULL`, `owner_id uuid NULL FK→users.id ON DELETE SET NULL`, `reserved_until timestamptz NULL`. Additional `UNIQUE (owner_id) WHERE state = 0` — a user owns at most one active name. The PK **is** the case-insensitive uniqueness constraint because the domain already stores the canonical (lower-cased, trimmed) form (`UsernameRegistry.Canonicalize`); no `citext`, no functional index, no Identity normalization involved (A-023).

### 5.2 Identity tables

Standard `IdentityDbContext<AuthUser, AuthRole, Guid>` schema, renamed for readability:

| Entity | Table |
|---|---|
| `AuthUser` | `auth_users` |
| `AuthRole` | `auth_roles` |
| `IdentityUserRole<Guid>` | `auth_user_roles` |
| `IdentityUserLogin<Guid>` | `auth_user_logins` |
| `IdentityUserClaim<Guid>` | `auth_user_claims` |
| `IdentityRoleClaim<Guid>` | `auth_role_claims` |
| `IdentityUserToken<Guid>` | `auth_user_tokens` |

`auth_users.id` FK → `users.id ON DELETE RESTRICT`. Default Identity indexes kept, except: the `NormalizedUserName` unique index stays (it indexes the id string — harmless), and the `NormalizedEmail` index is replaced by a **partial unique** index (§5.3).

`auth_user_tokens` is kept because Identity's store requires it, but Phase 2 writes nothing to it (A-028).

**Identity's BCL time columns are pinned explicitly (round-3 addition).** A-034 confines BCL time to "inside the gateway implementations", but A-021 puts Identity's own entities into the EF model, and `AuthUser : IdentityUser<Guid>` carries `LockoutEnd : DateTimeOffset?` — while the same `AppDbContext` is configured with `npgsql.UseNodaTime()` (verified at `src/PartyUp.Infrastructure/DependencyInjection.cs:13`), which changes the provider type mappings for `timestamp`/`timestamptz`. "Standard `IdentityDbContext` schema, renamed" does not decide what `LockoutEnd` maps to, and this lands in slice 2 with migration `AddIdentitySchema` — before anything can be tested — where `TreatWarningsAsErrors` plus a startup-applied migration turns a wrong guess into a crash-loop. The decision:

> `AuthUserConfiguration` maps `LockoutEnd` with an explicit value converter **`DateTimeOffset? ↔ Instant?`** (`Instant.FromDateTimeOffset` / `Instant.ToDateTimeOffset`) onto a `timestamptz` column, so the NodaTime plugin sees the type it maps natively and no `DateTimeOffset` provider mapping is relied on. This is the one place Identity's BCL model touches the NodaTime-only persistence stack, and the converter is the constraint-explaining comment A-034 permits. `ConcurrencyStamp`/`SecurityStamp` and every other Identity column are strings and need nothing.

Test: `MigrationTests.Migrate_RoundTripsAnIdentityLockoutEnd` — write a lockout through `UserManager`, read it back, assert the instant survives (integration, §14.3).

### 5.3 New Phase-2 tables

**`admin_seed_refusals`** (A-039 closing correction) — `user_id uuid PK FK→auth_users.id ON DELETE CASCADE`, `refused_at timestamptz NOT NULL`. A row durably records that the `Admin:SeedEmails` fallback once matched this account while its address was unconfirmed. The refusal binds **only** that email fallback; `Admin:SeedUserIds` deliberately bypasses it as the operator recovery path.

**`auth_tokens`** — the A-015 security-token store (magic link, password reset, email verification, deletion-request confirmation, Steam/OAuth linking is *not* here).

**Closing correction (slice 13):** `request_ip_hash` is schema-only in Phase 2 and remains `NULL`. The issue port preserves the nullable field for later abuse-forensics work, but no API endpoint currently computes or supplies the hash. §9.7's retention rationale therefore describes the preserved forensic column, not data Phase 2 emits.

| Column | Type | Notes |
|---|---|---|
| `id` | uuid PK | Guid v7, not the token |
| `purpose` | int NOT NULL | `AuthTokenPurpose` enum, persisted append-only |
| `token_hash` | bytea NOT NULL | SHA-256 of the raw token |
| `user_id` | uuid NULL | FK→`users.id ON DELETE CASCADE`; null for pre-account magic links |
| `email_normalized` | text NULL | for pre-account / recovery flows |
| `created_at` | timestamptz NOT NULL | |
| `expires_at` | timestamptz NOT NULL | |
| `consumed_at` | timestamptz NULL | single-use marker |
| `request_ip_hash` | bytea NULL | SHA-256(ip + server pepper); abuse forensics without storing IPs |
| `pending_locale` | text NULL | A-046 |
| `pending_age_gate` | bool NOT NULL DEFAULT false | A-046 / D-083 — the attestation is captured at request time and replayed at redemption. For `ExternalStartIntent` this is the attestation the antiforgery-protected POST carried (A-050), and it is the **only** source `CompleteExternalSignInUseCase` accepts |
| `payload` | jsonb NULL | purpose-scoped, **non-credential** data. Used by `ExternalStartIntent` only in Phase 2: `{"mode":"login\|link\|reauth","returnTo":"/app","provider":"google\|steam","ageGateAttested":true,"binderHash":"<base64url SHA-256 of the pu.intent binder secret>"}` — the serialized `ExternalIntentPayload` (A-029), `returnTo` already sanitized by `ReturnToValidator` at mint time and `binderHash` the A-050 browser binding (round-5 — a hash, never the binder itself; round-7 — **base64url text**, written at issuance by `IssueTokenRequest.Intent` and compared as text by the redeeming statement, because `payload->>'binderHash'` is `text` and a `byte[]` parameter binds as `bytea`). Same prohibition as the rest of the row: nothing that grants access may be stored here (A-046) |

**There is deliberately no `pending_password_hash` column** (A-046 round-2 correction). A token row may carry preferences and an attestation, never a credential: the password is chosen by whoever redeems the link and hashed at redemption. A reviewer who finds a credential column on this table has found a regression of the trojan-identifier pre-hijack.

Indexes: `ux_auth_tokens_token_hash` UNIQUE; `ix_auth_tokens_expires_at` (consumed by the §9.7 retention purge — round-4: the round-3 draft created this index for a purge that did not exist); `ix_auth_tokens_email_normalized` (the tombstone purge in §9.5 and the re-resolution in §6.2 both key on it).

**Retention (round-4 correction, §9.7).** `email_normalized` holds a **raw email address**, and A-046 makes `POST /auth/register` write such a row for *any* address anyone submits without ever creating a user; `/auth/magic-link`, `/auth/password/forgot` and `POST /account-deletion-requests` do the same for unknown addresses. The §9.5 purge is keyed to a user being tombstoned, so it structurally cannot reach rows for addresses that never registered — leaving an append-only table of third-party personal data with no retention basis (the same LGPD/GDPR framing A-017/A-038 apply to the deletion journal) and an unbounded growth surface any anonymous caller can pump. §9.7 therefore defines a time-based purge on the existing sweep tick. Hashing the address instead was considered and rejected: it breaks §6.2's in-transaction re-resolution and §9.5's `email_normalized` match.

`AuthTokenPurpose` = `{ MagicLink, PasswordReset, EmailVerification, EmailChange, DeletionRequestConfirmation, ExternalStartIntent, Reauthentication }`, persisted append-only. (`Reauthentication` is A-049's 5-minute proof; `DeletionRequestConfirmation` is minted by both the email and the Steam legs of §7.4; `ExternalStartIntent` is A-050's one-time start value for **every** external mode — round-3 replaces round-2's `ExternalLinkIntent`, which covered `mode=link` only. The intent row's payload carries `mode`, the sanitized `returnTo` and `ageGateAttested`; a `login` intent can never satisfy a `link` or `reauth` start.)

**`deletion_journal`** (A-017) — `user_id uuid PK` (one execution per user; the executor is idempotent), `executed_at timestamptz NOT NULL`, `shipped_at timestamptz NULL` (offsite-export bookkeeping, §9.6). No FK: the journal must survive even a hypothetical hard delete. Insert-only by convention and by code path; a migration grants no UPDATE/DELETE to the app role only if the operator later splits roles (out of scope, noted in the runbook).

**`steam_identities`** (A-030) — `user_id uuid PK FK→users.id ON DELETE CASCADE`, `steam_id64 text NOT NULL UNIQUE`, `persona_name text NOT NULL`, `avatar_url text NULL`, `fetched_at timestamptz NOT NULL`. Holds the Steam-supplied avatar URL and persona, which the domain deliberately does not model (`PlatformLink` carries only a handle).

**`google_identities`** is *not* created — Google contributes nothing beyond the `auth_user_logins` row and a verified email.

### 5.4 Migration list (forward-only, one per slice)

| # | Name | Contents |
|---|---|---|
| 1 | `AddUserAggregate` | `users` **including `banned_at`** + partial index `ix_users_banned_at WHERE banned_at IS NOT NULL`, `platform_links`, `username_registry` + converters |
| 2 | `AddIdentitySchema` | `auth_*` tables, partial unique email index, FK to `users` |
| 3 | `AddAuthTokens` | `auth_tokens` (incl. `pending_locale`/`pending_age_gate`/`payload`; **no credential column** — A-046) |
| 4 | `AddSteamIdentities` | `steam_identities` |
| 5 | `AddDeletionJournal` | `deletion_journal` |
| 6 | `AddAdminSeedRefusals` | `admin_seed_refusals` (A-039 durable email-fallback refusal) |

**Round-1 correction — `banned_at` moves to migration 1.** It was scheduled last, but A-024's per-request projection includes `banned_at`, `CurrentUser` carries `IsBanned`, A-026's `RequireAuthenticated` rejects banned users, and §6.6 step 2 reads `user.IsBanned` — all of which land in slices 2 and 4. With the column arriving in slice 9, slices 2–8 could not compile or run the design as written, and would have had to ship a throwaway projection and be rewritten. The **column, the `User.BannedAt` property, `Ban`/`Unban`, and the A-043 visibility edits all land in slice 1**; only the *use cases and endpoints* that drive them (`BanUserUseCase`, `AdminEndpoints`, `AdminSeeder`) stay in slice 9. The D-180 domain tests land in slice 1 with the domain code, not in slice 9 (§15).

Author with `dotnet dotnet-ef migrations add <Name> --project src/PartyUp.Infrastructure --startup-project src/PartyUp.Api`. **Never execute anything against a database** (CLAUDE.md cardinal rule 1) — authoring migration source and letting CI/Testcontainers/startup apply them is the only path. If a migration needs hand-editing (partial indexes are not expressible in the fluent API on every provider version), edit the generated `Up`/`Down` with `migrationBuilder.Sql(...)` and keep the snapshot consistent by re-running the tool.

---

## 6. Authentication flows

Cookie/BFF, same-origin (A-008 — the SPA is served by the API container in prod, and by the Vite proxy in dev, so there is no CORS and no cross-site cookie problem).

### A-028 — Auth cookie configuration

| Setting | Value | Why |
|---|---|---|
| Name | `pu.auth` | short, non-default (no framework fingerprint) |
| `HttpOnly` | true | XSS cannot read it |
| `SecurePolicy` | `Always` in non-Development, `SameAsRequest` in Development | plain-HTTP `localhost` dev |
| `SameSite` | `Lax` | the Google/Steam return legs are top-level GET redirects, which `Lax` allows; `Strict` would break them |
| `ExpireTimeSpan` | 30 days | |
| `SlidingExpiration` | true | |
| Path | `/` | SPA and API share an origin |
| Events | 401/403 JSON, **never** a login redirect | API-only surface; `LoginPath`/`AccessDeniedPath` must be neutralised or the SPA gets 302s to HTML |

Data Protection keys already persist to `/data/dataprotection` when the volume exists (Program.cs) — that is what makes the cookie survive a container replacement, and it is now load-bearing.

### A-029 — Own token pipeline for magic link / reset / verification; Identity's token providers are bypassed

Identity's default `DataProtectorTokenProvider` tokens are stateless: they cannot be revoked, cannot be marked single-use, and are validated by decryption rather than by a stored hash. That directly contradicts A-015 ("stored hashed so a database leak doesn't leak live bearer credentials" — the inverse problem here: a *key* leak forges tokens, and there is no server-side record to invalidate).

Phase 2 therefore implements `SecurityTokenService`:

```csharp
// PartyUp.Application/Auth/ISecurityTokenService.cs
public interface ISecurityTokenService
{
    Task<IssuedToken> IssueAsync(IssueTokenRequest request, CancellationToken ct);
    /// One conditional, atomic redemption. Every condition the caller needs — purpose, the A-050
    /// binder and the D-083 pre-account age gate — is a PREDICATE inside the single redeeming
    /// statement, never a post-read comparison (round-6) and never a peek-then-redeem pair
    /// (round-7). A condition that fails leaves the row live and unconsumed.
    Task<TokenRedemption> RedeemAsync(RedeemTokenRequest request, CancellationToken ct);
}

/// Round-7 cross-family review: the round-6 positional signature could express the binder but not
/// §6.2's "409 without consuming the token", because redemption was the only operation that
/// exposed the row's metadata. One request record carries every condition; the statement below
/// evaluates all of them.
public sealed record RedeemTokenRequest(
    AuthTokenPurpose Purpose,
    string RawToken,
    /// A-050's browser binding, base64url(SHA-256(binder)) — see the encoding rule below.
    /// Non-null only for ExternalStartIntent; null makes the binder arm inert.
    string? BinderHash = null,
    /// §6.2/D-083. Non-null only on the two redemptions that can CREATE an account
    /// (MagicLink, EmailVerification); null makes the age-gate arm inert, so an unattested
    /// ExternalStartIntent for a returning user is unaffected. `false` means "the caller has no
    /// attestation to offer", which is what makes a pre-account row answer RequiresAgeGate.
    bool? AgeGateAttested = null);

/// Round-2 review: the round-2 draft's four-positional-argument IssueAsync could not carry the
/// A-046 pending-registration payload or request_ip_hash, so A-046 and A-029 were mutually
/// unimplementable. One request record carries everything the row holds.
public sealed record IssueTokenRequest(
    AuthTokenPurpose Purpose,
    UserId? UserId,
    string? EmailNormalized,
    Duration Ttl,
    PendingRegistration? Pending = null,   // EmailVerification / MagicLink pre-account only
    ExternalIntentPayload? Intent = null,  // ExternalStartIntent only (A-050) → auth_tokens.payload
    byte[]? RequestIpHash = null);         // §5.3 request_ip_hash; SHA256(ip + pepper), computed in Api

/// A-050. Mode, provider and the already-sanitized returnTo, plus the D-083 attestation the
/// antiforgery-protected POST carried, plus the browser binder's hash. No credential and no
/// secret: BinderHash is base64url(SHA-256(binder)), never the binder itself.
/// Serialized verbatim into auth_tokens.payload — this record IS the issuance data path for
/// payload.binderHash (round-7 cross-family finding 4; the round-6 draft named the predicate
/// but gave no type any way to write the value).
public sealed record ExternalIntentPayload(string Provider, ExternalStartMode Mode,
                                           string ReturnTo, bool AgeGateAttested,
                                           string BinderHash);
public enum ExternalStartMode { Login, Link, Reauth }

/// No credential here, deliberately (A-046 round-2 correction): a preference and an attestation,
/// nothing that grants access. The password is chosen by the redeemer and hashed by
/// IUserAccountGateway.CreateAccountAsync at redemption.
public sealed record PendingRegistration(string Locale, bool AgeGateAttested);

public sealed record IssuedToken(Guid TokenId, string RawToken, Instant ExpiresAt);

public sealed record TokenRedemption(
    bool Succeeded,
    // NotFound | Expired | AlreadyConsumed | RequiresAgeGate (round-7 — the only failure that
    // reports a LIVE row: nothing was consumed and the same raw token may be presented again
    // with an attestation).
    TokenRedemptionFailure? Failure,
    AuthTokenPurpose Purpose,
    UserId? UserId,
    string? EmailNormalized,
    PendingRegistration? Pending,
    ExternalIntentPayload? Intent,
    Instant ConsumedAt);
```

- Raw token: 32 bytes from `RandomNumberGenerator.GetBytes(32)` → `Base64Url` (43 chars, 256 bits) (A-015).
- Stored: `SHA256(rawToken)` in `auth_tokens.token_hash`. The raw value exists only in the email body and the redemption request.
- Single use **and purpose-scoped**: redemption is

  ```sql
  WITH candidate AS (
      SELECT t.id,
             (t.user_id IS NULL
              AND NOT EXISTS (SELECT 1 FROM auth_users a
                               WHERE a.normalized_email = t.email_normalized)) AS is_pre_account
        FROM auth_tokens t
       WHERE t.token_hash = @h AND t.purpose = @purpose
         AND t.consumed_at IS NULL AND t.expires_at > @now
         AND (@binderHash IS NULL OR t.payload->>'binderHash' = @binderHash)
  ),
  redeemed AS (
      UPDATE auth_tokens t SET consumed_at = @now
        FROM candidate c
       WHERE t.id = c.id
         AND t.consumed_at IS NULL                      -- re-checked here, not only in candidate
         AND (@ageGateAttested IS NULL OR @ageGateAttested OR t.pending_age_gate
              OR NOT c.is_pre_account)
      RETURNING t.id, t.purpose, t.user_id, t.email_normalized,
                t.pending_locale, t.pending_age_gate, t.payload
  )
  SELECT c.is_pre_account, r.*
    FROM candidate c LEFT JOIN redeemed r ON r.id = c.id;
  ```

  — **one statement**, so two concurrent redemptions cannot both win, and **a refused condition consumes nothing**. Three outcomes, distinguished without a second round trip:

  | Result | Meaning | `TokenRedemption` |
  |---|---|---|
  | no rows | no live row matches hash + purpose + expiry + binder | `Failure = NotFound` (never a distinct code for *which* of those failed) |
  | one row, `r.id IS NULL` | the row is live but the age-gate arm refused it | `Failure = RequiresAgeGate`, `Purpose`/`EmailNormalized` populated so the endpoint can answer **409 `age_gate_required`**; `consumed_at` is still null |
  | one row, `r.id IS NOT NULL` | redeemed | `Succeeded = true`, the row's data returned |

  **Single-use and non-consumption are properties of the same statement (round-7 cross-family finding 3).** The round-6 contract exposed the row's metadata *only* through a consuming `RedeemAsync`, so §6.2's "the 409 path must **not** consume the token — check the age-gate precondition *before* the consuming UPDATE" had no implementable shape: an implementer either consumed and then checked (burning the victim's magic link on a UI prompt the SPA is specified to retry) or added a peek-then-redeem pair, reintroducing the double-redemption race the single-statement design exists to prevent. The CTE keeps one snapshot and one mutation. `AND t.consumed_at IS NULL` is repeated in the `UPDATE`'s own `WHERE` **deliberately**: under READ COMMITTED, Postgres re-evaluates the `UPDATE`'s qualification against the updated row after a concurrent write (EvalPlanQual), so the guard must live where that re-check happens — in `candidate` alone it would be evaluated on the pre-update snapshot and both racers could win.

  **The age-gate arm is scoped, not global.** It is inert whenever `@ageGateAttested IS NULL`, which is every call site except the two redemptions that can create an account (`POST /auth/magic-link/consume` and `POST /auth/email/verify`). Without that scoping every `mode=login` `ExternalStartIntent` — `user_id IS NULL`, `email_normalized IS NULL`, therefore `is_pre_account = true` — would be refused at `/start` for a returning user who legitimately attested nothing, breaking A-050's own flow. The row's own `pending_age_gate` also satisfies the arm, so a registration whose attestation was captured at request time (A-046) redeems without the caller resupplying it.

  **`AND (@binderHash IS NULL OR payload->>'binderHash' = @binderHash)` is a predicate, not a post-read check (round-6 correction).** A-050 requires `/start` to compare `SHA256(binder-from-pu.intent)` against the row's `payload.binderHash` and to answer 400 `intent_required` on mismatch, and §14.3's `ExternalIntentTests.Start_WithAnIntentMintedInAnotherBrowser_IsRejected` / `..._Start_WithAnInjectedIntentCookieValue_IsRejected` both assert that a refused attempt **consumes nothing**. With the round-5 contract — a `RedeemAsync` with no binder parameter and no non-consuming read anywhere on the port — the only reachable implementation was redeem-then-compare, which consumes the row on *every* refused attempt: both tests were unwritable as named, and anyone who learned an intent value could burn it. That is a cheap denial of every external sign-in/link/reauth flow for the holder, on the one token this design deliberately hands to the client in a response body and then puts in a URL (browser history, `window.name`, extension surface, client-side error reporting) — the identical weapon round 2 removed from `MagicLink`/`PasswordReset`. The alternative repair, a peek-then-redeem pair, reintroduces the double-redemption race the single-statement design exists to prevent. Carrying the binder into the same `WHERE` makes a mismatch return `TokenRedemptionFailure.NotFound`, consume nothing, and stay indistinguishable from a missing or unknown intent. **`ExternalStartIntent` is the only purpose that supplies a non-null `BinderHash`**; every other call site leaves it null and the predicate is inert.

  **The binder hash is a `string`, its encoding is defined once, and issuance is what writes it (round-7 cross-family finding 4).** The round-6 draft compared a jsonb text extraction (`payload->>'binderHash'`, which is `text`) against a `byte[]` parameter — Npgsql binds `byte[]` as `bytea`, so the predicate is either a type error or a silent never-match, and neither `IssueTokenRequest` nor any payload type carried the value at all, leaving `payload.binderHash` with no producer. Both halves are closed by one rule:

  > **`BinderHash = Base64Url(SHA-256(binder))`**, computed by a single shared helper (`IntentBinder.Hash(ReadOnlySpan<byte> binder) → string`, `Api/Auth/`), used at **both** ends: `POST /auth/external/intent` mints the 32-byte binder, puts `IntentBinder.Hash(binder)` on `ExternalIntentPayload.BinderHash` (serialized into `auth_tokens.payload`) and the binder itself into the data-protected `pu.intent` cookie; `GET /auth/external/{provider}/start` recomputes `IntentBinder.Hash(binder)` for each binder in the cookie and passes the resulting **string** as `RedeemTokenRequest.BinderHash`, which the statement compares as text against `payload->>'binderHash'`. Base64url (not base64) so the value is URL-, JSON- and log-safe and can never acquire `+`/`/`/`=` escaping differences between the two ends; the raw binder never leaves the cookie in any encoding.

  Test: `SecurityTokenServiceTests.Redeem_WithAMismatchedBinder_ConsumesNothing_D067` (§14.3).

  **`AND purpose = @purpose` is in the `WHERE` clause, not a post-read check (round-4 correction).** Round-4 review found the round-3 statement carried `purpose` in the `RedeemAsync` signature but not in the SQL, while A-029 and A-050 both claim guarantees that only a purpose check can provide ("a change token can no longer be redeemed to confirm the old address"; "a `login` intent can never satisfy a `link` or `reauth` start"). With seven purposes sharing one table and one hash index, an unfiltered redeem makes **any** live raw token redeemable at **any** redemption endpoint — `/auth/password/reset`, `/auth/magic-link/consume`, `/auth/email/change/verify`, `/account-deletion-requests/confirm` and the two `/start?intent=` handlers all take a caller-supplied string and act on the row's `user_id`. A `MagicLink` value replayed at `/auth/password/reset` would set a password; an `ExternalStartIntent` value — the one purpose deliberately handed to the client in a response body and then carried in a URL — replayed at `/account-deletion-requests/confirm` would start a grace. Filtering *after* the read would reintroduce the double-redemption race the single-statement design exists to prevent, so the predicate must be in the statement. A purpose mismatch returns `TokenRedemptionFailure.NotFound` (never a distinct code), so the mismatch is not observable and no row is consumed. Test: `SecurityTokenServiceTests.Redeem_RefusesATokenOfAnotherPurpose_D067` (§14.3).
- **Never logged, and never in any URL the server or the proxy can see** — see A-044, which replaces this bullet's round-1 wording.
- TTLs: magic link 15 min; password reset 60 min; email verification 24 h; email-*change* verification 24 h; deletion-request confirmation 60 min when emailed, **10 min when minted by the Steam callback** (§7.4 — it travels in a redirect fragment and is consumed on the very next screen); re-authentication 5 min.
- **Concurrent-token rule (round-2 correction).** The round-2 draft said "issuing a new token of a purpose for the same subject consumes any outstanding ones". Round-2 review showed that is a weapon, not a hardening: on any *anonymously-initiated* purpose, an attacker who names the victim's address consumes the victim's own live link, so the only redeemable link in the victim's mailbox is the attacker's — and on `PasswordReset`/`MagicLink` it is a one-request denial of recovery. The rule is therefore split by who initiated the issue:
  - **Anonymously-initiated purposes** (`MagicLink`, `PasswordReset`, `EmailVerification`, **`DeletionRequestConfirmation`**): outstanding tokens are **left alive**. Each is independently redeemable until its TTL or its own single-use consumption. The count is bounded by the §7.6 `auth-email`/`auth-ip`/`deletion-request`/`deletion-steam-ip` rate limits, not by consumption, and every one of them is harmless on its own because redemption grants nothing an attacker can steer (A-046: the credential comes from the redeemer; §7.4: the confirmation token only reaches the mailbox owner or the browser that proved the SteamID).
  - **Authenticated-subject purposes** (`EmailChange`, `Reauthentication`): issuing consumes the subject's outstanding tokens of that purpose, because on **these** purposes only the already-authenticated owner can trigger an issue, so there is no third party to deny.
  - **`DeletionRequestConfirmation` moved buckets in round 5.** The round-4 draft filed it under "authenticated-subject" on the rationale that "only the already-authenticated owner can trigger an issue". That rationale is false for this purpose: §7.4 mints it from two **anonymous** routes — `POST /account-deletion-requests {email}`, where any caller names any address, and the Steam leg `GET /account-deletion-requests/steam/{start,callback}`, which A-051/§7.4 deliberately exempt from antiforgery and from the A-050 intent on the grounds that it mutates nothing (minting a token row *is* a mutation, and the consumption rule made it a destructive one). Under the old bucket an attacker who named the victim's address, or who forced a top-level navigation to `/account-deletion-requests/steam/start` in a browser holding a `steamcommunity.com` session, silently consumed the victim's live confirmation link — the identical weapon round 2 removed for `MagicLink`/`PasswordReset`, reinstated on the one route PRODUCT_SPEC §8.6 / D-072 makes Policy-required, and on which the D-078 address-less class gets no notification at all (§9.1) and so no signal that its link died. The `deletion-request` limit does not substitute: it is shared with the victim, so exhausting it is itself the denial. Test: `SecurityTokenServiceTests.Issue_DoesNotConsumeAnotherPartysOutstandingDeletionConfirmation_D072` (§14.3).
  - **`ExternalStartIntent` is exempt from both rules**: issuing never consumes an outstanding one (a `mode=login` intent has no subject to key on, and the user may legitimately have two tabs open — see the multi-intent `pu.intent` cookie in A-050), each is independently single-use with a 10-minute TTL, and the count is bounded by the `intent-ip` policy in every mode plus `reauth` in `link`/`reauth` mode (§7.6).
  - Test: `SecurityTokenServiceTests.Issue_DoesNotConsumeAnotherPartysOutstandingRecoveryToken_D067` (§14.3 — the service is Infrastructure; round-4).
- **`AuthTokenPurpose` has separate members for `EmailVerification` (confirm the address already on the row) and `EmailChange` (confirm the address stored in the token's own `email_normalized`)** — round-1 review: with one shared purpose, a "resend verification" would silently consume a pending change, and a change token could be redeemed to confirm the *old* address. The full enum is in §5.3.

### A-044 — Raw tokens are delivered in the URL **fragment**, and query strings are scrubbed from request logs globally

Round-1 review found a literal A-015 violation in the round-1 design: emailed links pointed at SPA *document* routes (`https://partyup.social/auth/magic?token=<raw>`, §12.1). Clicking one issues a real GET to the origin, served by `MapFallbackToFile` — which sits **after** `app.UseSerilogRequestLogging()` (`src/PartyUp.Api/Program.cs:54`, fallback at `:71`) — so the full query string, token included, lands in the application request log and in Caddy's access log before a single line of SPA code runs. `history.replaceState` is far too late, and `Referer` on any subresource of that document leaks the token to third parties. Two independent fixes, both required:

1. **Fragment delivery.** Every emailed link carries the token after `#`, which browsers never transmit: `https://partyup.social/auth/magic#token=<raw>`, `/auth/reset#token=…`, `/auth/verify-email#token=…`, `/auth/email-change#token=…`, `/account-deletion/confirm#token=…`. The SPA reads `window.location.hash`, immediately calls `history.replaceState(null,'',location.pathname)` to clear it, holds the value in memory only, and POSTs it in the **request body** to the redemption endpoint. No token ever reaches the server in a URL, so there is nothing to redact on the document leg and nothing for `Referer` to carry. `PublicBaseUrl` + fragment is the only link shape `IEmailTemplateRenderer` is allowed to produce; a test (`EmailTemplateRendererTests.Render_PutsTheTokenInTheFragment_D067`) asserts no template output contains `?token=`.
2. **Global query scrubbing in the request log.** `UseSerilogRequestLogging(o => o.EnrichDiagnosticContext = …)` overrides `RequestPath` with the path only (query string dropped entirely) for **every** request, not per endpoint — this also covers the Steam callback's `openid.*` parameters (§6.4) and any future accidental leak. Caddy's access log is configured the same way in `deploy/` (log the path, not the raw URI); that is an owner action, §13.3 item 9.

Consequence for §12.1: the SPA routes `/auth/magic`, `/auth/reset`, `/auth/verify-email`, `/auth/email-change` and `/account-deletion/confirm` read `location.hash`, never `useSearchParams`.

Identity's `PasswordHasher<AuthUser>` (Argon2-less but PBKDF2-SHA512 with 100k+ iterations by default in .NET 10) **is** used, as are lockout counters and `SecurityStamp`. Only the token providers are bypassed. `IdentityOptions.Tokens` is left at defaults but no code path calls `GenerateUserTokenAsync`.

### 6.1 Email + password

### A-046 — Password registration is mailbox-confirmed: it always answers `202`, never issues a session, never creates an account the mailbox owner has not proven, and **never carries a credential the mailbox owner did not choose**

Round-1 review showed the round-1 rule ("email already in use → still 201 with the same shape") was not implementable: the 201 shape is a `SessionResponse` with a real `userId` and a `pu.auth` cookie, so a duplicate either had to sign the caller into somebody else's account (total takeover by knowing an address) or return a fabricated body the SPA detects on its very next call. It also created the precondition for the account pre-hijack in A-045 — an unverified `auth_users` row on an arbitrary address.

**Round-2 correction — the password is *not* carried on the token.** The round-2 draft had `POST /auth/register` hash the submitted password at request time and park it on the `auth_tokens` row (`pending_password_hash`), with the account created at redemption using that hash. Round-2 review showed that is a *trojan-identifier pre-hijack*: the requester and the redeemer are never proven to be the same party, so an attacker POSTs `{email: victim@x, password: attackerPw}`, the victim receives a genuine, legitimate-looking "finish creating your PartyUp account" mail from `partyup.social`, and clicking it creates the victim's account with the **attacker's** password and `EmailConfirmed = true` — which also re-arms A-045's auto-link precondition and A-039's `SeedEmails` grant. The address was proven; the *credential*, which is what actually grants access, was not.

Resolution (option (a) of the finding — the cheaper one, and it removes a column):

- `POST /auth/register` **takes no password at all**. Body is `{email, ageGateAttested, locale}`.
- The password is collected on the redemption page (`/auth/verify-email`, §12.1) and set **inside the redemption transaction**, by the party who holds the mailbox. `pending_password_hash` is deleted from `auth_tokens` (§5.3); `pending_locale` and `pending_age_gate` remain, because they are preferences/attestations, not credentials — the worst an attacker can pre-seed is a locale the user changes in settings.
- No password hashing happens at request time, so the two register branches are trivially equal in cost (issue a token, send one mail) and the round-2 timing argument for hashing-at-register disappears with it. Password hashing happens exactly once, at redemption, inside `IUserAccountGateway.CreateAccountAsync` (Identity's `PasswordHasher`) — the layer that owns hashing.

**Register** `POST /api/v1/auth/register`
1. Validate body: `email`, `ageGateAttested == true`, `locale`. **No password field**; a request that carries one is rejected 400 (the SPA must not train users to type a password into the wrong leg).
2. Reject `ageGateAttested != true` with 400 `age_gate_required` (D-083/D-093 — the gate is the first onboarding step and the server refuses to create an account without it).
3. **Nothing is written to `users` or `auth_users`.** Issue an `EmailVerification` token with `user_id = NULL` and `email_normalized = <email>`, carrying the *pending registration* (`pending_locale text NULL`, `pending_age_gate bool NOT NULL DEFAULT false` — §5.3), and email the verification link (A-044 fragment form).
4. If the address **already has an account**: do exactly the same amount of work (write nothing), and instead send the `ExistingAccountNotice` template ("someone tried to register with your address; sign in instead").
5. **Always `202 Accepted`** with body `{ "status": "verification_sent" }`. No `userId`, no cookie, no `SessionResponse`. The two branches are byte-identical to the caller and equal in cost, so there is nothing to enumerate. RL: `register-ip` + `register-email` (§7.6 is the only place the numbers live).

**Redemption** `POST /auth/email/verify {token, password}` — inside one transaction: redeem single-use; validate `password` (≥12 chars, no other composition rules — length beats classes); **re-resolve the address inside the transaction** (same rule as §6.2 — an account may have appeared during the token's 24 h); if one now exists, do nothing but return 200 (the notice email already told them to sign in); otherwise create `User.BeginOnboarding(...)` + `AuthUser { EmailConfirmed = true }` and set the password **from this request**, run the post-authentication gate (§6.6), issue the cookie, return `200 SessionResponse`. This is the *only* path that turns a password registration into an account: it proves mailbox possession before the account exists **and** binds the credential to the party that proved it.

The same request shape serves the "resend verification for an existing unconfirmed address" case, where `password` is absent and ignored (204, §7.1).

Test: `VerifyEmailUseCaseTests.Execute_TakesThePasswordFromTheRedeemerNotTheRequester_D016` — a token issued by one caller and redeemed by another yields an account whose password is the *redeemer's*, and no field of the issuing request can influence the credential.

SPA consequence (§12.1): `/register` collects only the address, submits, and shows "check your inbox"; the password field lives on `/auth/verify-email`. It does not transition to `/onboarding` until the link is followed. This costs one round trip and buys the removal of two entire classes of pre-hijack.

**Login** `POST /api/v1/auth/login` — `UserManager.FindByEmailAsync` → `CheckPasswordSignInAsync` with lockout (5 failures / 15 min). On success, run the **post-authentication gate** (§6.6) and issue the cookie.

**Timing equalization (required, not optional) — and the rule is *exactly one* hash-cost verification per request, not "always also do the dummy" (round-7 cross-family finding 2).** The ~100 ms PBKDF2 asymmetry (A-029) is a trivially measurable oracle that defeats the enumeration protection built into register, forgot-password and magic link, and the §7.6 `auth-ip`/`login-email` limits do not stop a patient attacker. The round-6 wording ("the use case **must still** call the dummy" on the unknown / password-less paths, plus a lockout branch that "**still calls `VerifyDummyPasswordAsync`**") equalized the cheap branches against each other but left the *wrong-password* branch paying **two** hashes — Identity's real `VerifyHashedPassword` plus, on the reading that the dummy is unconditional, the dummy — while unknown, password-less and locked-out paid one. That inverts the oracle rather than removing it: an existing account with a password becomes the measurably *slowest* answer, which is precisely the account-existence signal the equalization exists to erase, and `LoginEnumerationTests` as named would have passed anyway (it compares the three cheap branches). The rule is therefore stated as an invariant over the whole request:

> **Every `POST /auth/login` request performs exactly one PBKDF2-cost verification, whatever the outcome.** The dummy verify runs **only** on the branches where no real password verification occurred, and never alongside one:
>
> | Branch | Real verify? | Dummy verify? |
> |---|---|---|
> | Unknown email (no `auth_users` row) | no — nothing to verify against | **yes** |
> | Password-less account (`PasswordHash IS NULL`, D-078 social-only) | no — `UserManager.VerifyPasswordAsync` returns `Failed` on a null stored hash without hashing | **yes** |
> | Locked out | no — `CheckPasswordSignInAsync` evaluates lockout **before** verification and short-circuits | **yes** |
> | Wrong password | **yes** — the real hash was compared | **no** |
> | Correct password | **yes** | **no** |
>
> The branch is not inferred by the Application layer from Identity's internals: `IUserAccountGateway.CheckPasswordAsync` returns a `PasswordCheckResult` carrying **`HashVerified`**, set true by the gateway only when a stored hash actually existed and was compared (§8.2). The use case's rule is one line and covers every path including success — `if (!result.HashVerified) await gateway.VerifyDummyPasswordAsync(ct);` — so a future Identity change to the short-circuit order cannot silently reintroduce an asymmetry, and no branch can double up.

All **four** negative outcomes — unknown email, wrong password, password-less social account, **locked out** — return the identical `401 { code: "invalid_credentials" }` with no distinguishing field. `IUserAccountGateway` gains `Task VerifyDummyPasswordAsync(CancellationToken ct)` so the Application layer can require it without knowing Identity types; it verifies a fixed, compiled-in dummy PBKDF2 hash of the same cost as the configured hasher.

**Lockout is the fourth outcome and it is equalized too (round-3 correction).** The round-2 text enumerated three outcomes, but login is `CheckPasswordSignInAsync` **with lockout** (5 failures / 15 min, §13.1), whose `IsLockedOut` result the design never dispositioned. An unknown address can never lock out — there is no row to count against — so six bad guesses against a candidate address make an existing account answer with lockout semantics while a nonexistent one keeps answering `invalid_credentials`: a reliable account oracle that defeats the anti-enumeration work A-046, §6.2 and §7.4 all pay for. Whatever form it took (a distinct `code`, a 423, a 429 with a different `Retry-After` source than the rate limiter) it would leak, and the sharpest form is timing: a locked-out account short-circuits before PBKDF2 runs, so it is measurably *faster* than both the dummy-hash path and the wrong-password path. Therefore:

> The `IsLockedOut` branch returns the **byte-identical** `401 { code: "invalid_credentials" }`, sets **no** `Retry-After` and no other distinguishing header, and — because the short-circuit means no real verification ran (`HashVerified == false`) — **calls `VerifyDummyPasswordAsync` before returning**, which is the same one-hash rule the table above states, not an extra hash on top of a real one. Lockout remains fully effective — it still refuses the correct password for the window — it is simply not *observable* from the response. The generic `default`/`auth-ip` rate limiter is the only thing on this route allowed to emit a 429 with `Retry-After`, and it partitions on IP/email hashes, not on account existence.

Test: `SignInWithPasswordUseCaseTests.Execute_ReturnsTheSameFailureForUnknownWrongPasswordlessAndLockedOutAccounts_D067` — four cases, asserting the identical failure result and `code`, and that **exactly one** hash-cost verification happened in each: the fake gateway counts real and dummy verifications, and the assertion is `real + dummy == 1` on all four (dummy on unknown/password-less/locked-out, real on wrong-password), plus the same for the success case (round-7).

**Anti-enumeration equalization also covers `/auth/password/forgot` and `POST /account-deletion-requests` (round-4 correction).** §6.1 makes register byte- *and* cost-identical, and equalizes all four login outcomes because "the sharpest form is timing" — but the round-3 draft applied neither treatment to the two other anonymous email-keyed endpoints, whose branches did observably different work. A token can only be issued against an existing account, so the unknown-address branch wrote nothing and sent nothing while the known-address branch wrote an `auth_tokens` row and performed a mail send; with `Email:Provider="smtp"` (A-040 — MailKit, STARTTLS, 3 retries with backoff) that is an SMTP round trip on one branch and not the other. A far larger and more reliable oracle than the ~100 ms PBKDF2 asymmetry §6.1 refuses to tolerate, and it defeats the enumeration protection register, login and magic link all pay for. `/auth/magic-link` was never affected, because both of *its* branches issue a token and send mail (§6.2) — that is the shape the other two must match. Both halves of the fix ship:

- **A no-account counterpart template on each route** (the `ExistingAccountNotice` pattern inverted, §11): `PasswordResetNoAccount` ("someone asked to reset a password for this address, but there is no PartyUp account on it") and `DeletionRequestNoAccount`. Every branch of both routes now performs exactly one template render and one send, so the branches are equal in work and in outbound traffic, and the mail is genuinely useful to a real recipient — it tells a user who forgot *which* address they signed up with.
- **Both branches also write exactly one `auth_tokens` row (round-6 minor).** With mail off the request path, the residual asymmetry on all three anti-enumeration routes was one INSERT: the register *unknown-address* branch, the forgot *known-address* branch and the deletion-request *known-address* branch each wrote a token row while their counterparts wrote none. Rather than argue the difference is unmeasurable, it is removed: the counterpart branch issues a row of the same purpose with the same TTL, which is **never emailed** and which nothing can act on — a `PasswordReset`/`DeletionRequestConfirmation` row with `user_id IS NULL` redeems to no subject, and the register decoy is redeemed by the rule §6.1 already states (the redemption re-resolves the address inside the transaction and returns 200 doing nothing when an account exists). Nobody ever holds the raw value, and §9.7's retention purge removes the rows. Every branch of all three routes is then one INSERT, one render and one enqueue. The three enumeration tests (`RegistrationEnumerationTests…`, `ForgotPasswordEnumerationTests…`, `PublicDeletionRouteTests.Request_ForAKnownAndAnUnknownAddress_AreIndistinguishable_D072`) each additionally assert **equal `auth_tokens` row counts** on the two branches.
- **Transactional mail is dispatched off the request path**, and §8.3 says so explicitly: post-commit, the use case hands the `EmailMessage` to an `IEmailDispatcher` that returns immediately and sends on a background channel (a bounded `System.Threading.Channels` queue drained by one hosted service — no queue table, no package, A-013-neutral). A provider outage or a slow SMTP handshake therefore cannot be measured from any response, and it cannot roll back or delay an account creation either (which §8.3 already required and the round-3 design achieved only by accident of ordering).

Tests: `ForgotPasswordEnumerationTests.Forgot_ForAKnownAndAnUnknownAddress_AreIndistinguishable_D067` and `PublicDeletionRouteTests.Request_ForAKnownAndAnUnknownAddress_AreIndistinguishable_D072` (§14.3) — identical status, body and header set, one captured send on both branches, and no response-observable difference in the number of round trips made before responding.

**Password reset** — `POST /auth/password/forgot` (202 always, no enumeration; a known address gets `PasswordReset`, an unknown one gets `PasswordResetNoAccount` — same work either way) issues a `PasswordReset` token; `POST /auth/password/reset` redeems it, sets the hash, **rotates the security stamp** (kills every other session), then **runs the post-authentication gate (§6.6)** and only then signs the caller in. The gate is not optional here: without it a banned user obtains a live session through forgot-password (D-180 "blocks login") and a reset-driven sign-in during the grace would not cancel the deletion (D-158). See §6.6: `AuthenticateAndSignIn` is the *only* door that issues `pu.auth`, on every path without exception. Tests: `PostAuthenticationGateUseCaseTests.Execute_RefusesABannedAccountBeforeAnyCookie_D180` and `..._CancelsAPendingDeletionOnASuccessfulCredentialCheck_D158` (the gate owns the behavior for **every** credential path, reset included — round-5, names deduplicated in the round-6 sweep), plus `ResetPasswordUseCaseTests.Execute_SignsInOnlyThroughTheGate_D165` for the reset path's own obligation to route through it.

`POST /auth/password/change` (authenticated) requires the current password and also rotates the stamp, re-issuing the caller's cookie.

**Setting a password later** — a social-only account calls `POST /auth/password/set` (authenticated, **requires a verified email on file**; 409 `email_required` otherwise, with the SPA linking to `/auth/email/change`). This is *not* a D-078 conflict: D-078 says social-only accounts "do not require a separate verified recovery email", i.e. the account must be fully usable with none — and it is, since Steam/Google sign-in never touches the email column. A password with no email would be an unusable credential, because login is email-keyed (`FindByEmailAsync`). The round-1 test that asserted the opposite is rewritten in §14.2.

### 6.2 Magic link (passwordless email)

`POST /auth/magic-link { email, locale? }` → **202 always**. If an account exists, issue a `MagicLink` token and email `https://partyup.social/auth/magic#token=<raw>` (fragment form, A-044). If not, and self-service registration is open (D-016), also issue one with `user_id = NULL, email_normalized = <email>` so the redemption creates the account.

`POST /auth/magic-link/consume { token, ageGateAttested? }` — one transaction:
- Redeem (single-use, 15 min), passing the caller's attestation state as `RedeemTokenRequest.AgeGateAttested` — non-null here and at `POST /auth/email/verify`, null everywhere else (A-029). This is an **account-creating** redemption, which is why the age gate is a condition of the redeeming statement rather than a check around it.
- If the token has a `user_id`: post-auth gate (§6.6) → cookie → 200. Also sets `EmailConfirmed = true` (possession of the mailbox is proven).
- If it has only an email: **re-resolve `email_normalized` inside the same transaction** before deciding. Round-1 review: a registration (or another magic link) for the same address can land during the token's 15-minute life, and the round-1 text went straight to "create the account", which would hit the partial unique index on `normalized_email` — a 500 on a sign-in path, or a second orphan domain user for one mailbox if an implementer "fixed" it by ignoring the conflict.
  - **An account now exists** → adopt it: set `EmailConfirmed = true` (mailbox possession *is* the proof, which is exactly the precondition A-045 requires), post-auth gate, cookie, 200. No age-gate prompt — the existing account already passed one.
  - **Still no account** → the redemption requires `ageGateAttested == true` (or a row whose own `pending_age_gate` is true), else **409 `age_gate_required`**. The 409 path must **not** consume the token — the SPA shows the gate and retries with the *same* token — and that is a property of the redeeming statement, not of an ordering convention: `RedeemAsync` answers `TokenRedemptionFailure.RequiresAgeGate` with `consumed_at` still null (A-029, round-7 cross-family finding 3). The round-6 instruction "check the age-gate precondition *before* the consuming UPDATE" is withdrawn: `RedeemAsync` was the only operation exposing the row's metadata, so the only way to obey it was a peek-then-redeem pair, which reintroduces the double-redemption race. When the arm passes, create the account as in A-046's redemption (minus the password) and sign in.
- Test: `ConsumeMagicLinkUseCaseTests.Execute_AdoptsAnAccountCreatedAfterTheTokenWasIssued_D067`.

Rate limits: 5 requests / hour per normalized email, 20 / hour per IP (§7.6).

### 6.3 Google (OAuth 2.0 / OIDC)

Package: `Microsoft.AspNetCore.Authentication.Google`. Handler registered on scheme `Google`, sign-in scheme `IdentityConstants.ExternalScheme` (the standard Identity external cookie), `CallbackPath = /api/v1/auth/external/google/callback`, scopes `openid email profile`.

**`email_verified` must be mapped explicitly — the handler does not map it (round-7 cross-family finding 1).** `GoogleOptions`' default `ClaimActions` map `sub`, `name`, `given_name`, `family_name`, `picture`, `profile` and **`email`** — and nothing else. Google's userinfo/ID-token response *does* carry `email_verified`, but no default action projects it, so `principal.FindFirst("email_verified")` is null on every assertion. A-045 step 1 and step 6 both turn on that exact claim ("auto-link **only if** the assertion carries `email_verified == true`"; "Google, assertion carries `email_verified == true` → write `EmailConfirmed = true`"), so under the default mappings the verified branch is **unreachable**: every Google sign-in falls into the unverified branch and answers `?error=link_required` — Google login never works at all — or, if an implementer "fixes" the dead branch by treating the presence of `email` as proof, the design silently ships the account pre-hijack A-045 step 1 exists to close. The mapping is therefore part of `GoogleAuthSetup.cs` (slice 6):

```csharp
options.ClaimActions.MapJsonKey("email_verified", "email_verified");   // Google OIDC; not a default
```

(`OnCreatingTicket` reading `context.User.GetProperty("email_verified")` and adding the claim by hand is the equivalent, and is the fallback if a future provider's payload nests the flag. Whichever form is used, the claim type is the literal string `"email_verified"` — the same key both providers' adapters and the use case agree on.)

**Consumption is in the use-case input mapping, and it fails closed.** `ExternalAuthEndpoints` builds `CompleteExternalSignInUseCase`'s input from the external principal, and that mapping is the one place the claim is read:

```csharp
EmailVerified: string.Equals(principal.FindFirstValue("email_verified"), "true",
                             StringComparison.OrdinalIgnoreCase)   // absent → false, "0"/"" → false
```

so **absent, empty, malformed or `false` are all "unverified"**, indistinguishable from each other and from a provider that asserts nothing. `CompleteExternalSignInUseCase` never re-reads the principal and never infers verification from the presence of an address — a provider adapter that cannot prove verification passes `EmailVerified: false` and inherits A-045's refusal branches (step 1: no auto-link, `?error=link_required`; step 6: no account created, nothing written). Steam passes `Email: null, EmailVerified: false` and is exempt by construction (A-045 step 2). Tests: `CompleteExternalSignInUseCaseTests.Execute_TreatsAnAbsentEmailVerifiedClaimAsUnverified_D067` (§14.2) and `GoogleClaimMappingTests.Callback_ProjectsEmailVerifiedOntoThePrincipal_D067` (§14.3).

- `GET /api/v1/auth/external/google/start?intent=<value>` → redeem the intent (A-050 — single-use; **no mode, returnTo or attestation is read from the query string**), then `Results.Challenge` with `AuthenticationProperties` carrying `mode`, the sanitized `returnTo`, `ageGateAttested` and (for `link`/`reauth`) the `UserId` the intent was bound to. Correlation and state are the framework's own correlation cookie, configured `CorrelationCookie.SameSite = SameSiteMode.Lax`, `SecurePolicy = Always` outside Development (`SameAsRequest` in Development, where there is no TLS). **`Lax`, not `None` (round-4 correction).** The round-3 draft set `None` "because the correlation cookie must survive the Google hop" — but the hop back is a **top-level GET redirect**, which `Lax` allows; that is the identical reasoning A-028 uses for `pu.auth` and §6.4 uses for `pu.ext`, both of which cross the same leg on `Lax` today. `None` would gratuitously expose the correlation nonce in every cross-site subresource context and leave the three sibling cookies on three different policies. If a future provider ever needs a `form_post` response mode, the exception is named for that provider with its reason, not applied to the default.
- Google's redirect hits the handler's `CallbackPath` (framework-owned; not a route of ours), which signs into the **external** cookie and redirects to `ReturnUri` = `GET /api/v1/auth/external/complete`.
- `GET /api/v1/auth/external/complete` (our route) reads the external principal, then branches per A-045 below.
- Always: `SignOutAsync(IdentityConstants.ExternalScheme)`, then `302` to **`/auth/callback`** — never straight to the SPA destination — carrying `?status=<code>` and, when the flow had one, `&next=<sanitized returnTo>` (A-045 step 5, round-6). `/auth/callback` is the one SPA route that clears the URL fragment before it renders anything, which is what the `mode=reauth` transport (A-049) requires; it then navigates to `next` (or to the lifecycle default when there is none).

**No auth data is ever put in the redirect URL** — the result is conveyed by the `pu.auth` cookie plus a short `?status=` code the SPA reads and strips.

### A-045 — External identity binding: verified-on-both-sides auto-link, intent-bound link mode, and positively-validated redirects

This decision lives in `CompleteExternalSignInUseCase`, **not** in the Google adapter, because A-025's provider model is pluggable (Apple arrives in the mobile phase and inherits these rules for free).

**1. Auto-link requires the IdP *and* the local account to have proven the mailbox.** The round-1 rule ("the verified Google email matches an existing account → auto-link", called "the standard safe auto-link") was an account pre-hijack path. Two independent gaps: the design never checked Google's `email_verified` claim (the `email` claim alone is not proof — hosted/Workspace domains can assert unverified addresses), and the *local* side was created unverified by round-1 register, so an attacker could register `victim@gmail.com`, wait, and have the victim's Google identity bound into the attacker's password-holding account. The rule is now:

> Auto-link **only if** the assertion carries `email_verified == true` **and** the matched `auth_users.EmailConfirmed == true`. Otherwise: no binding, no account creation on that address. Redirect `?error=link_required`, and the SPA tells the user to sign in with their existing method first and then link from `/settings/account`.

A-046 removes the other half of the precondition by never creating an unverified `auth_users` row at all, but both checks are stated and tested independently — neither may be dropped because the other exists.

**"Verified on the IdP side" is a claim this design must go and get; it is not a default (round-7 cross-family finding 1).** The mechanism is specified in §6.3: `ClaimActions.MapJsonKey("email_verified", "email_verified")` in `GoogleAuthSetup` (the Google handler maps `email` but **not** `email_verified`), and one input mapping in `ExternalAuthEndpoints` that turns the claim into `EmailVerified: bool` with **fail-closed** semantics — absent, empty, malformed or `false` all mean *unverified*, and the use case has no other source. Everywhere this decision says `email_verified == true` it means "the mapped claim resolved to true", and every other case takes the refusal branch: step 1 refuses the auto-link (`?error=link_required`), step 6 refuses the create and writes nothing. Tests: `CompleteExternalSignInUseCaseTests.Execute_RefusesToAutoLinkAnUnverifiedProviderEmail_D067`, `..._RefusesToAutoLinkAnUnconfirmedLocalAccount_D067`, `..._TreatsAnAbsentEmailVerifiedClaimAsUnverified_D067` (round-7 — the absent case is not the `false` case, and it is the one the default mappings actually produce), and the route-level `GoogleClaimMappingTests.Callback_ProjectsEmailVerifiedOntoThePrincipal_D067` (§14.3), which fails if the `ClaimActions` line is dropped.

**2. Steam is exempt and it says so.** Steam OpenID asserts no email, so no email-match branch runs for it: an unknown SteamID is always "create", never "link". §6.4 step 6's "same login/link/create branching" means the *login/link/create* shape only — the email-match auto-link branch does not exist on the Steam path.

### A-050 — **Every** external `/start`, in every mode, is initiated by an antiforgery-protected POST that also carries the D-083 attestation; a bare GET `/start` does not exist

**3 (round-3 rewrite).** The round-2 rule protected `mode=link` only, on the reasoning that "`mode=login` mutates nothing that belongs to the victim". Round-3 review showed that reasoning is wrong twice over, and both halves are worse than the link case it did cover:

- `GET /auth/external/steam/start?mode=login&returnTo=/app&ageGateAttested=true` is a bare top-level navigation an attacker page can force (link, `window.open`, meta refresh, redirect chain). Steam's `checkid_setup` is **silent** for a browser that already holds a `steamcommunity.com` session — no consent screen — so the victim's browser completes the assertion, reaches `/complete`, and `AuthenticateAndSignIn` issues `pu.auth`. That is exactly the login-CSRF A-031 calls "a real attack" and covers for `POST /auth/login`, left uncovered on the far more silent provider leg. Google behaves the same once the client has been consented to once.
- Worse than session fixation: per §6.4 step 6 and step 2 above, an unknown SteamID on `mode=login` is **always** "create". So the forced navigation creates a brand-new PartyUp account bound to the victim's Steam identity, and the D-083/D-093 age-gate attestation that authorizes that creation is a **query-string flag on the attacker's URL**. §14.2's own `..._RejectsAnUnattestedAgeGateOnProviderAccountCreation_D083` only rejects `ageGateAttested=false` — which the attacker simply does not send. D-083/D-093 are Confirmed and require *the user* to attest before an account exists.
- Secondary: the same forced flow reaches §6.6 step 4, so it could silently cancel a victim's pending deletion (D-158) from a cross-site navigation.

The rule is therefore uniform across `login`, `link` and `reauth`:

| Method | Route | Auth | Notes |
|---|---|---|---|
| POST | `/auth/external/intent` | anon (`mode=login`) / auth (`mode=link`, `mode=reauth`) | **antiforgery-enforced** (A-031, no `.DisableAntiforgery()`). Body `{provider, mode, returnTo?, ageGateAttested?}`. Mints a 32-byte one-time `intent` — an `auth_tokens` row, purpose `ExternalStartIntent`, TTL 10 min — whose data-protected/stored payload carries `mode`, the sanitized `returnTo` (A-045 step 5, validated **here**, at mint time) and `ageGateAttested`. Bound to the caller's `UserId` for `link`/`reauth`; `user_id IS NULL` for `login`. **Also sets the `pu.intent` browser-binding cookie** (see below). Returns `{intent}` |

`GET /auth/external/{provider}/start` **requires** `?intent=<value>` **and the matching `pu.intent` cookie** in every mode, redeems the intent inside the start handler (single-use, so the same intent cannot start two flows), and refuses with **400 `intent_required`** otherwise. `mode`, `returnTo` and `ageGateAttested` are read **from the redeemed intent only** — query-string copies of them are ignored, so nothing an attacker can put in a URL reaches the flow. `AuthTokenPurpose` gains `ExternalStartIntent` (and no longer has `ExternalLinkIntent`; one purpose serves all three modes, and `mode` inside the payload is what distinguishes them — a `login` intent can never satisfy a `link` start, and vice versa).

**Second, independent control on the creation leg.** `CompleteExternalSignInUseCase` may only create a `users`/`auth_users` pair when the *redeemed intent* carried `ageGateAttested == true`; it never reads an attestation from anywhere else. An assertion that arrives with no proven attestation redirects `?status=age_gate_required` and writes nothing, and the SPA sends the user to `/age-gate` and restarts the flow through `POST /auth/external/intent`. Identity is proved by the provider; **consent is proved by the antiforgery-protected POST** — the same "identity proved, consent still needed" split §7.4 already adopted for the Steam deletion route.

**The intent is bound to the browser that minted it (round-4 correction — without this, A-050 does not stop the attack it was written for).** Round-4 review found the round-3 intent was a bare value in an `auth_tokens` row with `user_id IS NULL` for `mode=login`, tied to nothing: no cookie, no session, no other link between mint and redeem. The antiforgery check then proves only that *some* browser posted with a valid token pair, not that it is the browser that performs the `/start` navigation — so the round-3 forced-navigation attack survives verbatim. The attacker visits partyup.social normally, legitimately mints `{provider:"steam", mode:"login", ageGateAttested:true}` in their own browser, and then forces the victim's browser to `https://partyup.social/api/v1/auth/external/steam/start?intent=<that value>` by link, `window.open`, meta refresh or redirect chain. Steam's `checkid_setup` is silent for a browser holding a `steamcommunity.com` session; the state cookie is minted in the *victim's* browser by that same navigation, so A-051 authenticates the flow and not the intent's origin (§7.4 says so in as many words); and `/complete` reads `ageGateAttested = true` from an intent whose attestation the **attacker** ticked. All three A-050 consequences follow unchanged — silent account creation on the victim's Steam identity with an attestation the victim never made (A-045 step 2 makes an unknown SteamID always "create"), forced sign-in with `pu.auth` issued for a known SteamID, and the §6.6 step 4 silent cancellation of a pending deletion (D-158). The 10-minute TTL and single use do not help: the attacker mints on demand, one per victim.

Therefore, at mint time `POST /auth/external/intent` also sets:

| Cookie | Value | Attributes |
|---|---|---|
| `pu.intent` | a **data-protected list of binder secrets**, at most 5 entries, each a 32-byte CSPRNG value minted with its intent and never returned to the client in any other form. Protector purpose `PartyUp.IntentBinder`; the intent row stores only `IntentBinder.Hash(binder)` = **base64url(SHA-256(binder))** on `ExternalIntentPayload.BinderHash`, serialized into `payload` (`{"binderHash":"…"}`) — one helper, used at mint and at redeem (A-029, round-7) | `HttpOnly`, `Secure` outside Development, **`SameSite=Strict`**, `Path=/api/v1/auth/external`, 10-minute expiry |

`GET /auth/external/{provider}/start` refuses with **400 `intent_required`** — the same code and shape as a missing `intent`, so nothing is learnable — unless `pu.intent` is present, unprotects, and contains a binder whose hash equals the `binderHash` on the row the presented `intent` value redeems. **The comparison is performed by the redeeming statement itself, not after it** (round-6): `/start` computes `IntentBinder.Hash(binder)` for each binder in the cookie and calls `ISecurityTokenService.RedeemAsync(new RedeemTokenRequest(ExternalStartIntent, intent, BinderHash: hash), ct)` — one call per candidate binder until one succeeds, each of which consumes nothing when it fails — whose `WHERE` carries the binder predicate (A-029, round-7: the hash is base64url **text**, matching `payload->>'binderHash'`). A mismatch therefore answers `NotFound` and **consumes nothing**, which is what makes the two round-4/round-5 tests writable at all and what stops a leaked intent value from being burnable by anyone who learns it. The `/start` handler rewrites the cookie in the same response **with that one binder removed** (deleting the cookie entirely when the list empties), so each binding is single-use on both halves.

**Two round-5 corrections to the round-4 shape, both in this table.**

- *Multi-tab.* A single-valued `pu.intent` at one name and path meant the second of two concurrently minted intents overwrote the first, so the first tab's `/start` failed `intent_required` — breaking exactly the case A-029 cites as the reason `ExternalStartIntent` is consumption-exempt ("the user may legitimately have two tabs open"), and doing it with an error whose whole purpose is to be indistinguishable from an attack. A short capped list makes concurrent flows work while keeping the cookie bounded; the cap is 5 because a legitimate user does not have six provider hops in flight in ten minutes, and the oldest entry is evicted rather than the newest rejected.

  **The list is a read-modify-write, and its concurrency rule is stated (round-6 minor).** Each mint builds the new cookie from the value that arrived **on that request** plus its own binder, so two mints genuinely in flight at the same instant are last-write-wins and one binder can be lost — the very case this list was added to fix. It is left as last-write-wins deliberately (the alternatives — a server-side per-browser list, or one cookie per intent — buy a rarer race for a stored-state or cookie-count cost), and the loss is made non-fatal instead: a `/start` whose binder is missing answers the ordinary 400 `intent_required`, and **§12.2's client rule is that a provider button re-mints and retries exactly once on `intent_required` before surfacing an error**, which converts the lost race into one extra round trip. `/start`'s own rewrite (removing the used binder) is not exposed to this: it is a single response on a navigation the user is already committed to.
- *Not a cookie-value double submit.* The round-4 cookie carried the intent row's `id`, which the minting caller already knows — so the check was A-031's explicitly rejected cookie-value double-submit pattern with `SameSite=Strict` carrying the entire load, and one cookie-injection primitive (a sibling subdomain, per A-031's own reasoning) defeated it. The binder is a server-minted secret compared against a **stored hash**, so a value the attacker can write into the victim's cookie jar does not match any row, and forging a match requires the secret itself, which never leaves the protected cookie. `SameSite=Strict` remains — it stops the cross-site-initiated top-level GET before any of this runs — but it is now the second factor, not the only one.

`SameSite=Strict` is the load-bearing attribute: a cross-site-initiated top-level GET does not carry a `Strict` cookie, so the forced navigation arrives cookie-less and is refused before any provider hop, whichever of the four forcing techniques was used. (`Lax` would *not* suffice here — `Lax` is sent on cross-site top-level GET navigations, which is exactly the attack shape. This is the one cookie in Phase 2 that must be `Strict`, and it can be, because unlike `pu.auth` it is never needed on a provider *return* leg — the return legs carry `pu.ext`/the correlation cookie instead, both minted same-site by our own redirect.)

Same-site navigation from our own SPA — the only legitimate initiator, per §12.1's POST-then-navigate provider buttons — carries it. `Path=/api/v1/auth/external` keeps it off every other route.

Tests: `ExternalIntentTests.Start_WithoutAnIntent_IsRejected_InEveryMode`, `ExternalIntentTests.Complete_WithAMismatchedIntentMode_LinksNothing_D087` (round-6 — restated at the layer that can enforce it, see below), `ExternalIntentTests.Start_ConsumesTheIntentOnce`, `ExternalIntentTests.Start_WithAnIntentMintedInAnotherBrowser_IsRejected` (round-4 — no cookie jar sharing between the minting and redeeming client; asserts 400 `intent_required`, that the row is **not** consumed by the refused attempt, and that no redirect to the provider is emitted), `ExternalIntentTests.Start_RemovesOnlyTheUsedBinderFromTheIntentCookie` (round-5 — the consumed binder is gone, a sibling binder minted in another tab still works, and the cookie is deleted once the list empties), `ExternalIntentTests.Start_WithTwoConcurrentlyMintedIntents_BothSucceed` (round-5 — the multi-tab case A-029's exemption promises), `ExternalIntentTests.Start_WithAnInjectedIntentCookieValue_IsRejected` (round-5 — a caller-chosen cookie value matches no stored `binderHash`), `CompleteExternalSignInUseCaseTests.Execute_RejectsAnUnattestedAgeGateOnProviderAccountCreation_D083` (rewritten to assert the attestation is read from the intent, and that a query-string `ageGateAttested=true` cannot substitute for it).

**`/start` has no `mode` to reject, and §7.1 no longer implies it has one (round-6).** The round-5 draft carried a separate §7.1 row `GET /auth/external/{provider}/start?mode=reauth | auth` and a required test `ExternalIntentTests.Start_WithALoginIntentInLinkMode_IsRejected`, both of which contradict this decision's own "reads nothing from its own query string" and its sibling test `Start_IgnoresQueryStringModeReturnToAndAttestation`. Under A-050 there is no such thing as a link-mode *start*: the mode is a property of the redeemed intent row, so `/start` has no requested mode to compare it against. An implementer reconciling the contradiction by re-reading `?mode=` would put an attacker-controllable parameter back on the exact route this decision hardened, and would invite a "mode mismatch" error code distinguishable from `intent_required` — A-050 requires one indistinguishable answer. The `?mode=` row is deleted (folded into the two provider rows) and the mode check is restated where it can be enforced: **`/complete`**, per A-045 step 4's per-mode table. A `login` intent must never reach the link branch, and a `link` or `reauth` intent with no bound `UserId` must fail with `?error=` and link nothing.

*(A-050 ends here; steps 4–6 below continue A-045.)*

**4. Identity checks per mode — `reauth` binds the assertion to the caller, and `link` and `reauth` both bind the intent to the caller (round-4 rewrite).** Round-4 review found step 4 stated the identity check for `link` only, while A-049 item 1 makes the external re-assertion the *sole* proof branch for the D-078 address-less class and §6.4 step 6 says every mode runs "the same login/link/create branching" — so a `reauth` completion was free to mint the `Reauthentication` token on the strength of **any** valid assertion. The attack that opens: a thief holding a stolen `pu.auth` (30-day sliding, A-028 — the exact threat A-048/A-049 exist for) posts `/auth/external/intent {provider:"google", mode:"reauth"}` with the victim's cookie, completes the Google leg with **their own** Google account, receives the `Reauthentication` token, and then satisfies every A-049 endpoint — link their own identity, unlink the victim's, set a password, take the first address (which for a Steam-only account notifies nobody, A-048), and request deletion. Every re-authentication control in the design would collapse to "holds the cookie", the one thing A-049 states it must not. The rules, stated per mode and each independently testable:

| Mode | `/complete` must require |
|---|---|
| `login` | nothing beyond a valid assertion and a redeemed intent (A-050); the intent has `user_id IS NULL` |
| `link` | an authenticated `pu.auth` whose `UserId` **equals the intent's bound `UserId`**; add the login row; `?error=provider_already_linked` if that provider key belongs to someone else |
| `reauth` | **both** of: (a) an authenticated `pu.auth` whose `UserId` equals the intent's bound `UserId` — same rule as `link`; **and** (b) `IUserAccountGateway.FindUserIdByLoginAsync(provider, providerKey)` returning **exactly that same `UserId`**, i.e. the asserted identity is *already linked to the caller* |

**The branch `/complete` takes is selected by the *intent's* mode and by nothing else (round-6)** — there is no request-supplied mode anywhere in the flow (A-050). An intent whose mode does not match the branch's preconditions fails with `?error=` and links, mints and creates nothing: a `login` intent (`user_id IS NULL`) arriving under an authenticated cookie must not fall into the link branch, and a `link` or `reauth` intent with no bound `UserId` is refused rather than treated as a login. Test: `ExternalIntentTests.Complete_WithAMismatchedIntentMode_LinksNothing_D087` (§14.3).

A `reauth` completion that fails either check redirects `?error=reauth_identity_mismatch`, **mints nothing and links nothing** — in particular it must never fall through to the `link` or `create` branch, because an unlinked identity asserted in `reauth` mode is precisely the attack above. Mismatch on (a) covers the symmetric case the round-3 draft also left undispositioned: an intent minted by user A and completed in a browser that now holds user B's cookie.

The binding is re-checked at **replay** time as well: when a `Reauthentication` token is presented as `reauthToken` on any A-049 endpoint, the redeemed row's `user_id` must equal the calling principal's `UserId`, or the proof is rejected as `409 reauth_required` (never a distinct code). A token is a proof *about a subject*, not a bearer capability.

Tests: `ReauthenticationTests.ExternalReauth_WithAnUnlinkedProviderIdentity_MintsNoToken_D067`, `ReauthenticationTests.ExternalReauth_CompletedUnderAnotherUsersCookie_MintsNoToken_D067`, `ReauthenticationTests.ReauthToken_ReplayedByAnotherUser_IsRejected_D067` (all §14.3).

**5. `returnTo` is validated positively, at every redirect that consumes it.** The round-1 rule ("relative path beginning `/` and not `//`") accepts `/\evil.com` and `/\/evil.com`, which browsers normalize to protocol-relative URLs and follow off-origin, and it accepts CR/LF/TAB and `/%09/evil.com` (stripped or decoded before parsing). On a route that has just issued an authenticated cookie, that is a live phishing and `Referer`-leak vector. Replacement:

```
reject unless: the raw value contains no character < 0x20 and no 0x7F,
               and it matches ^/(?![/\\])[A-Za-z0-9._~!$&'()*+,;=:@%/\-]*$
prefer:        an allow-list — /app, /onboarding, /settings/profile, /settings/account —
               with anything else silently replaced by the lifecycle default
```

The allow-list is what ships in Phase 2 (the SPA only ever needs those four); the regex is the documented fallback for when later phases need deep links. **The same validation runs at the Steam `/callback` redirect (§6.4 step 7)**: the value is tamper-proof inside `pu.ext`, but it was attacker-supplied at `POST /auth/external/intent` and round-1 never re-validated it. A shared `ReturnToValidator.Sanitize(string?) → string` is the only way any handler may build a redirect **target**, and it is applied twice — once when the intent is minted (A-050) and once at every redirect that consumes it. Test: `ExternalReturnToTests.Complete_WithABackslashRelativeReturnTo_RedirectsToTheDefault`.

**A sanitized `returnTo` is a *destination*, not the redirect target of an external completion (round-6 correction).** The round-5 draft said both that every external completion redirects to the sanitized `returnTo` (§6.3, §6.4 step 7) and that `/auth/callback` is "the landing for external redirects" (§12.1) which carries the A-049 `#reauth=` token — two statements that cannot both hold, because `/auth/callback` is **not** in the allow-list above. An implementer following the stated rule emits `Location: /app#reauth=<raw>&status=reauth_ok`; `/app` is a protected placeholder with no hash handling, so a live 5-minute single-use access-granting token (A-015 class — it satisfies every A-049 endpoint) would sit in the address bar and in browser history, survive a copy-pasted URL, and be readable by anything else on that page. That is exactly the failure A-044 exists to close, on the one token this design says grants something. The rule:

> **Every external *authentication* completion — Google `/complete`, the Steam auth callback (§6.4 step 7), in every mode — redirects to `/auth/callback`, and to nothing else.** The sanitized destination travels as `?next=<value>`; `/auth/callback` clears `location.hash` with `history.replaceState` **before any render**, and only then navigates to `next` (or to the lifecycle default when `next` is absent). `ReturnToValidator.Sanitize` still governs `next` — the allow-list is unchanged and `/auth/callback` is deliberately *not* added to it, because it is never a user-requested destination, only the fixed landing the server chooses.
>
> The one external leg that is **not** an authentication completion is §7.4's public-deletion Steam callback: it issues no session, and its output is a `DeletionRequestConfirmation` token whose only consumer is `/account-deletion/confirm` (§12.1). It therefore lands there, and that route carries the identical obligation — `replaceState`-clear the fragment before any render, hold the token in memory only. The invariant the design actually needs is the general one, and it is stated that way: **every `Location` an external leg emits must name a route with a hash-clearing entry point**, which in Phase 2 is exactly `/auth/callback` (auth) and `/account-deletion/confirm` (public deletion). `ExternalRedirectTests` asserts both halves: no raw token in any query (unchanged), and a `Location` path drawn from that two-route set, with `/auth/callback` for every completion that issued or refused a session.

**6. What a provider CREATE writes to the email columns (round-3 addition).** Step 1 fully specifies the auto-*link* branch and step 2 the Steam exemption, but the round-2 draft never said what the *create* branch does with the address the provider asserted. That silence decided five things by accident (whether a Google-created account has D-067 recovery at all; whether it is a D-078 social-only account; whether `/auth/password/set`'s `email_required` precondition fires for it; whether the partial unique index on `auth_users(normalized_email)` can be violated on a sign-in route — the 500 round-1 finding 12 fixed for magic link; and whether A-039's confirmed-only `SeedEmails` fallback can ever match). The rule:

| Provider | On CREATE |
|---|---|
| Google, assertion carries `email_verified == true` | Write `Email` / `NormalizedEmail` / `EmailConfirmed = true` on the new `auth_users` row. The account therefore has D-067 email recovery from birth, is **not** a D-078 social-only account, satisfies `/auth/password/set` immediately, and is a legitimate `SeedEmails` match. |
| Google, assertion carries `email_verified != true` | **No account is created.** An unverified IdP address must never become a confirmed local address — that is A-045 step 1's precondition seen from the other side — and creating the account *without* an address is the alternative this design refuses (see the box below). Redirect `?error=link_required`: the SPA tells the user to sign in with an existing method (or register by email) and then link Google from `/settings/account`. Nothing is written. |
| Steam | No address (Steam asserts none). Always a D-078 social-only account — and the **only** address-less account class Phase 2 creates. |

**A provider may only create an address-less account if that provider also has a public deletion leg (round-6 blocking finding).** The round-5 draft had both the unverified-`email_verified` case and the address-taken race create the account *address-less*, which manufactured a Google-only, address-less account class. Once such an account is banned, every deletion path is closed: `POST /account-deletion-requests {email}` cannot match (there is no address), §7.4's assertion leg resolves `auth_user_logins('Steam', …)` and there is no Steam row, and `POST /me/deletion` is `RequireAuthenticated` — which A-026 fails with 403 `account_banned`, while §6.6 step 2 blocks login, so the account can never obtain a session to add a first address through `POST /auth/email/change`. That contradicts `PRODUCT_SPEC.md:457` ("A banned user can still request account deletion through the public route"), reinstating round-1 finding 14 on a class this design's own step 6 created; §7.4's "Ops fallback" is an operator action, not the public route the Confirmed decision names. The rule, stated as an invariant rather than as three cases:

> **`CompleteExternalSignInUseCase` never creates an account with no address on file unless the provider has an assertion-based public deletion leg (§7.4).** In Phase 2 that is Steam and only Steam. Google's two address-less branches therefore become refusals: no user row, no `auth_users` row, no login row, an `?error=` redirect the SPA can act on, and the user reaches an account through a route that does leave a deletion path (email registration, magic link, or Steam). Adding a future provider means shipping either a verified address on every create or a `/account-deletion-requests/{provider}/…` leg alongside it — the A-051 per-flow cookie/purpose/`flow`/`return_to` machinery generalizes for exactly that. Test: `CompleteExternalSignInUseCaseTests.Execute_NeverCreatesAnAddresslessAccountForAProviderWithNoPublicDeletionLeg_D072` (§14.2) — a table-driven case over the provider set, which fails the moment a provider is added without one of the two.

The address write happens **inside the same transaction** as the `users`/`auth_users` insert, and it is re-resolved there exactly as §6.2 does: if the normalized address has been taken since the assertion began, the create branch **does not create the account at all** — it redirects `?error=email_unavailable` and writes nothing — so the partial unique index can never surface as a 500 on a sign-in path *and* no address-less Google account is produced by the race. Tests: `CompleteExternalSignInUseCaseTests.Execute_WritesTheVerifiedGoogleAddressOnCreate_D067`, `..._RefusesToCreateAnAccountFromAnUnverifiedGoogleEmail_D067`, `..._RefusesToCreateAnAccountWhenTheAddressWasTakenMeanwhile_D067`, `..._CreatesAnAddresslessAccountFromASteamAssertion_D078` (the D-078 class is now Steam's alone), `..._NeverCreatesAnAddresslessAccountForAProviderWithNoPublicDeletionLeg_D072`.

### A-030 — Steam is one flow producing two stored facts: an auth login and a platform link

Signing in with Steam creates (a) `auth_user_logins('Steam', steamid64)` — an authentication method, and (b) `platform_links(user_id, Platform.Steam, handle = persona name)` + `steam_identities(steam_id64, persona_name, avatar_url)` — the D-032 gaming-platform link that D-050 can use as an avatar source and Phase 3 uses for friend import. Unlinking Steam as an auth method (when it is not the last one, D-137) also removes the platform link, which the domain's `UnlinkPlatform` already handles by falling the avatar back to emoji (D-050).

Rationale: the spec lists them separately (auth methods in §8.3, "linked gaming platforms and handles" in the profile minimum), but requiring the user to connect Steam twice would be absurd UX. One flow, two rows, one unlink.

**Both unlink directions are defined, and they are the same operation (round-2 correction).** The round-2 draft defined only the auth-method direction, leaving `DELETE /me/platform-links/{id}` (§7.3) silent on whether it deletes the `steam_identities` row and whether the `auth_user_logins('Steam', …)` row survives. Round-2 review named both failure modes: leaving `steam_identities` behind keeps third-party personal data (`persona_name`, `avatar_url`) after the user has revoked the link, which PRODUCT_SPEC §8.6 ("A way to unlink platform accounts and **revoke their data access**") forbids; and having the platform route silently drop the login row would bypass the D-137 last-method check that lives in §6.5. The rule:

> **Unlinking Steam by either route removes all three facts atomically** — the `platform_links` row, the `steam_identities` row, and the `auth_user_logins('Steam', …)` row — and is subject to the **same** D-137 guard and the same A-049 re-authentication. `DELETE /me/platform-links/{id}` for a Steam link and `DELETE /auth/methods/steam` both delegate to one `UnlinkSteamUseCase`, which takes the `FOR UPDATE` lock on `users`, counts remaining auth methods, returns 409 `last_auth_method` if the count would reach 0 (**including when the caller came in through the platform-link route** — the SPA surfaces the same `errors.auth.lastMethod` message there), then removes all three rows and falls the avatar back per D-050.

**The proof is carried by the contract, not only by this decision (round-5).** The round-4 draft asserted the A-049 obligation here while §7.3's row declared no request body and no `409 reauth_required`, and §7.6 — "the only place limits are stated", each route carrying "the complete set of policies that apply to it" — omitted the route from the `reauth` policy. Both now name it: §7.3's row takes `{currentPassword?/reauthToken?}` and can answer 409 `reauth_required`, A-049 item 1 lists the route, and §7.6's `reauth` row covers it. Tests: `UnlinkSteamUseCaseTests.Execute_ForASteamLinkRequestedAsAPlatformUnlink_RequiresReauthentication_D137` (§14.2 — over the use case's `UnlinkOrigin` argument, which is all an Application test can see) and `PlatformLinkRouteTests.Delete_ForASteamLink_Returns409WithoutProofAndRemovesAllThreeRowsWithIt_D137` (§14.3 — the route half; round-6).

Non-Steam platform links (D-032 handles for other platforms) are never auth methods, so `DELETE /me/platform-links/{id}` on those removes exactly one row with no auth interaction, requires no proof, and carries no `reauth` policy.

Tests: `UnlinkSteamUseCaseTests.Execute_ByEitherRoute_RemovesTheLoginTheLinkAndTheSteamIdentity_D032`, `UnlinkSteamUseCaseTests.Execute_ForASteamLinkRequestedAsAPlatformUnlink_StillRefusesTheLastAuthMethod_D137`, `UnlinkSteamUseCaseTests.Execute_ForASteamLinkRequestedAsAPlatformUnlink_RequiresReauthentication_D137` (round-5, renamed round-6), `PlatformLinkRouteTests.Delete_ForASteamLink_Returns409WithoutProofAndRemovesAllThreeRowsWithIt_D137` (§14.3, round-6).

### 6.4 Steam OpenID 2.0 (hand-rolled, per A-019)

No library (A-019: "the protocol is trivially hand-rollable"). Two endpoints, both ours:

`GET /api/v1/auth/external/steam/start?intent=<value>` (A-050 — the intent is redeemed first and is the sole source of `mode`, `returnTo`, `ageGateAttested` and the bound `userId`)

### A-051 — Each external OpenID flow owns its own state cookie (name **and** path) and stamps a flow discriminator inside the protected payload

There are two Steam OpenID legs in Phase 2 — the auth leg (§6.4) and the public-deletion leg (§7.4) — and they live under different route prefixes. Round-3 review found that §7.4's "verifies the assertion exactly as §6.4 steps 1–5" was **unimplementable as written**: a cookie set from `/api/v1/account-deletion-requests/steam/start` with `Path=/api/v1/auth/external/steam` is never sent back to `/api/v1/account-deletion-requests/steam/callback`, so step 1 fails `state_mismatch` on every legitimate attempt; and the `return_to` step 3 pins is a different absolute URL on that leg. The three ways an implementer would "fix" it are all worse — widen `pu.ext` to `Path=/` (scoping lost), share one cookie name across both flows (a deletion-flow assertion satisfying the auth callback, or vice versa: **cross-flow state confusion**), or drop the state check on the deletion leg (restoring the unconfirmed-GET hazard round-2 finding 2 closed). Therefore, per flow:

| Flow | Cookie | Path | Protector purpose | `flow` value in the payload | `openid.return_to` pinned to |
|---|---|---|---|---|---|
| Auth (§6.4) | `pu.ext` | `/api/v1/auth/external/steam` | `PartyUp.SteamState.Auth` | `"auth"` | `<PublicBaseUrl>/api/v1/auth/external/steam/callback?n=<nonce>` |
| Public deletion (§7.4) | `pu.extdel` | `/api/v1/account-deletion-requests/steam` | `PartyUp.SteamState.Deletion` | `"deletion"` | `<PublicBaseUrl>/api/v1/account-deletion-requests/steam/callback?n=<nonce>` |

Each callback (a) reads **only** its own cookie name, (b) unprotects with **only** its own protector purpose — so a payload minted for the other flow fails to unprotect at all — and (c) additionally asserts `payload.flow` equals its own constant before anything else, and (d) pins its own absolute `return_to`. Any mismatch → 400 `state_mismatch`. The three checks are deliberately redundant: purpose-scoped data protection alone would be sufficient, but a future refactor that shares a protector must still fail on the discriminator. Tests: `ExternalStateTests.AuthStateCookie_IsRejectedByTheDeletionCallback`, `ExternalStateTests.DeletionStateCookie_IsRejectedByTheAuthCallback`, `ExternalStateTests.Callback_WithoutTheStateCookie_IsRejected`.

*(A-051 ends here; the §6.4 `/steam/start` steps follow.)*

1. Build state `{ flow = "auth", nonce = 32B base64url, mode, returnTo, userId?, ageGateAttested }` (all four taken from the redeemed A-050 intent), protect it with `IDataProtector("PartyUp.SteamState.Auth")`, write it to cookie `pu.ext` (`HttpOnly`, `Secure` outside Development, `SameSite=Lax`, 10-minute expiry, `Path=/api/v1/auth/external/steam`).
2. 302 to `https://steamcommunity.com/openid/login?` with exactly:
   `openid.ns=http://specs.openid.net/auth/2.0`, `openid.mode=checkid_setup`,
   `openid.identity=openid.claimed_id=http://specs.openid.net/auth/2.0/identifier_select`,
   `openid.return_to=<absolute callback>?n=<nonce>`, `openid.realm=<origin>`.

`GET /api/v1/auth/external/steam/callback?openid.*&n=`
1. Read + unprotect `pu.ext` with the `PartyUp.SteamState.Auth` purpose; assert `payload.flow == "auth"` (A-051); delete the cookie immediately (single use). Missing cookie, failed unprotect, wrong `flow`, or missing/!matching `n` → 400 `state_mismatch`.
2. **Pin the OP endpoint**: verification POSTs to the constant `https://steamcommunity.com/openid/login`, never to a value taken from the callback (`openid.op_endpoint` is echoed by the attacker otherwise).
3. **Validate `openid.return_to`** equals the exact URL we sent for **this flow** (scheme, host, path and `n`) — the per-flow absolute URL in A-051's table, never a shared constant.
4. **Validate the assertion envelope *before* trusting any parameter** — this is the step round-1 was missing and it is the difference between a working check and a bypass:
   - `openid.ns == "http://specs.openid.net/auth/2.0"` and `openid.mode == "id_res"`;
   - parse `openid.signed` (comma-separated field names) and require it to contain **at least** `claimed_id, identity, return_to, response_nonce, assoc_handle, op_endpoint`;
   - only then POST all received `openid.*` parameters back with `openid.mode=check_authentication` and require a body line `is_valid:true`. Anything else → 401 `steam_assertion_invalid`.

   Why the `signed` check is load-bearing: the OP validates the signature over **exactly** the fields listed in `openid.signed`, and nothing else. An attacker can take a genuinely-signed assertion for their own Steam account, replace `openid.claimed_id`/`openid.identity` with the victim's `steamcommunity.com/openid/id/<victim64>`, and delete those two names from `openid.signed`. Steam then re-checks only the remaining fields and answers `is_valid:true` — after which round-1's step 5 would have read the victim's steamid64 out of an *unsigned* parameter and signed the attacker in as the victim (or, under A-030, linked the victim's SteamID to the attacker's account). `is_valid:true` alone means "these particular fields are authentic", never "this identity is authentic".
5. Extract steamid64 with `^https?://steamcommunity\.com/openid/id/(\d{17})$` against `openid.claimed_id`, and require `openid.identity == openid.claimed_id`.
6. Same login/link/create branching as §6.3 — **except the email-match auto-link branch, which does not exist here**: Steam asserts no email, so an unknown SteamID is always "create", never "link" (A-045 step 2), and a Steam create is always address-less (A-045 step 6). Every mode — `login` included — reached this callback through a redeemed intent (A-050), and creation additionally requires that intent's `ageGateAttested == true`. **`mode=reauth` is not part of that branching at all**: per A-045 step 4 it neither links nor creates, and it mints the `Reauthentication` token only when the asserted SteamID already resolves to the caller's own `UserId`; otherwise `?error=reauth_identity_mismatch`. Plus: call `ISteamUser/GetPlayerSummaries` (key optional — if `Steam:ApiKey` is absent, skip and use a placeholder persona `Steam <last4>` which the user can edit) and upsert `steam_identities` + the platform link (A-030).
7. 302 to **`/auth/callback?status=…&next=<sanitized returnTo>`** (round-6 — the same landing as the Google leg, §6.3), the `next` value produced by `ReturnToValidator.Sanitize` (A-045 step 5); the `returnTo` inside `pu.ext` is tamper-proof but was attacker-supplied at `/start`.

Steam's nonces are single-use on their side too (a refresh of the callback fails `check_authentication`) — that is a second line of defence behind our own single-use state cookie, not the primary one.

**Logging**: `Steam:ApiKey` travels in query strings (A-019). Wrap all Steam Web API calls in a helper that scrubs the key from URLs before logging and from exception messages before rethrowing (`SteamApiClient.Sanitize`). The callback's own query string is covered by the **global** request-log scrubbing in A-044 — not by a per-endpoint redaction list, which round-1 review showed is the mistake that leaves the SPA fallback route uncovered.

### 6.5 Auth-method linking and unlinking (D-087, D-137)

`GET /api/v1/auth/methods` → the caller's methods:

```json
{ "methods": [
  { "kind": "password",  "linked": true,  "removable": true,  "detail": "a***@example.com" },
  { "kind": "magicLink", "linked": true,  "removable": false, "detail": "a***@example.com" },
  { "kind": "google",    "linked": true,  "removable": true,  "detail": "a***@gmail.com" },
  { "kind": "steam",     "linked": false, "removable": false, "detail": null } ] }
```

**`magicLink` is a first-class method, and it counts for D-137 (round-4 correction).** D-067 lists **passwordless email** as an authentication method in its own right (PRODUCT_SPEC:559), but the round-3 `kind` enum was `password|google|steam` and D-137's count treated `password` as present "only when a hash exists". A magic-link-only account (§6.2 creates them: confirmed email, no hash, no login row) therefore reported **zero** auth methods while being fully signable-in, and the last-method invariant D-137 protects was computed over a set that omitted the one method the account actually uses. The count was also wrong in the other direction for any email-confirmed account: `UnlinkAuthMethodUseCase` would refuse to remove the "last" method while magic link would still have worked. Therefore:

- `kind ∈ password|magicLink|google|steam`. `magicLink` is `linked: true` **iff** `auth_users.EmailConfirmed = true` and an address is on file — it is a *derived* method, not a stored row, because that is exactly the precondition `POST /auth/magic-link` checks.
- It is included in the D-137 remaining-methods count on every path that computes one (`UnlinkAuthMethodUseCase`, `UnlinkSteamUseCase`).
- It is **not independently removable**: `DELETE /auth/methods/magicLink` returns **400 `method_not_removable`**, message key `errors.auth.methodNotRemovable`, because the way to stop having it is to stop having a confirmed address, and `POST /auth/email/change` can only replace an address, never clear one. The `removable` flag on each row is what the SPA renders, so the UI never offers a button that cannot work.
- Interaction with the last-method rule, stated so it is not rediscovered: an account with a confirmed address always has at least `magicLink`, so **the last-method 409 is reachable only for address-less accounts** — precisely the D-078 class. That is the intended reading of D-137, not a weakening of it: an account that can still receive a sign-in link has not lost its way in.

Tests: `ListAuthMethodsUseCaseTests.Execute_ReportsMagicLinkForAConfirmedAddress_D067`, `UnlinkAuthMethodUseCaseTests.Execute_CountsMagicLinkTowardTheLastMethodRule_D137`, `UnlinkAuthMethodUseCaseTests.Execute_RefusesToRemoveMagicLinkDirectly_D137` (§14.2).

`DELETE /api/v1/auth/methods/{kind}` where `kind ∈ password|google|steam` (`magicLink` is accepted by the route and answered 400, above):
- **Re-authenticate first (A-049).** No proof → 409 `reauth_required`, before the lock and before any count.
- **Lock second.** `UnlinkAuthMethodUseCase` opens its transaction with `IUserRepository.FindForUpdateAsync(id)` (`SELECT … FOR UPDATE` on the parent `users` row) **before** counting. Round-1 review: without the lock this is a read-then-write race — concurrent `DELETE /auth/methods/password` and `DELETE /auth/methods/google` each see two methods, each concludes one would remain, and both succeed, leaving an account with zero sign-in methods and no recovery (`/auth/password/set` itself needs a session). ARCHITECTURE §3 already mandates locking the parent row for invariant-touching commands; this is one. Test: `UnlinkAuthMethodUseCaseTests.Execute_SerializesConcurrentUnlinks_D137` (integration, two overlapping transactions).
- Count remaining methods after the hypothetical removal. `password` counts only when a hash exists; `google`/`steam` count when an `auth_user_logins` row exists; **`magicLink` counts when `EmailConfirmed = true` and an address is on file** (round-4). If the count would be 0 → **409** `last_auth_method`, message key `errors.auth.lastMethod` ("You must keep at least one way to sign in. Add another method first."). This is D-137's "explicit error".
- **UI note, corrected**: D-087 reads "Users can link more authentication methods later; **no warning is required** when only one remains" (PRODUCT_SPEC:579) — a permission, not a prohibition. Round-1 review was right that the round-1 text ("D-087 forbids a proactive warning") invented a constraint. Not pre-disabling the button is this design's **preference** (a disabled control with no explanation is worse than an explicit error), stated as such; it is not a spec requirement and §14.4 must not assert it as one.
- Removing `steam` also removes the platform link and `steam_identities` row (A-030).
- Removing `password` clears the hash and rotates the stamp.

### 6.6 The post-authentication gate (shared by every successful credential check)

**The gate is split in two (round-2 correction).** The round-2 draft put the whole thing — loading the domain `User`, the ban/tombstone checks, `user.CancelDeletion(clock)`, "inside one transaction" — in `src/PartyUp.Api/Auth/AuthenticateAndSignIn.cs`. That placed an aggregate mutation and an explicit DB transaction in the composition root, against §8.3 ("one transaction per use case, opened by `IUnitOfWork.ExecuteAsync`") and ARCHITECTURE §2 (Api = composition + endpoints); it would have been the only place in Phase 2 where domain state changes outside a use case. Worse, §14.2 assigns the very assertions it owns (`SignInWithPasswordUseCaseTests.Execute_CancelsAPendingDeletion_D158`, `..._RefusesLoginAfterTheGraceElapsed_D148`, `..._RefusesABannedAccount_D180`, and the two `ResetPasswordUseCaseTests`) to `tests/PartyUp.Application.Tests`, whose csproj references only `src/PartyUp.Application` — verified — so those tests were unwritable at the layer they were filed under. Therefore:

- **`PartyUp.Application/Accounts/PostAuthenticationGateUseCase.cs`** owns steps 1–5 below: it opens the transaction via `IUnitOfWork.ExecuteAsync`, makes every domain call, and returns a `PostAuthenticationResult` (`Admitted(UserId, bool DeletionWasCanceled)` or `Refused(code)`). Every §14.2 D-158/D-148/D-180 gate assertion lands here. **Round-5 correction to the filing**: the round-2 pass left five of those assertions named `SignInWithPasswordUseCaseTests.*` / `ResetPasswordUseCaseTests.*` in §14.2 while this paragraph gave the logic to the gate and to a single caller — so the tests named classes that structurally cannot execute the behavior, and §14.5's D-148/D-158/D-180 cells would have cited them. They are renamed to `PostAuthenticationGateUseCaseTests.*`; the two credential use cases keep one assertion each (`…_SignsInOnlyThroughTheGate_*`) that they emit an admit-through-the-gate result and never a session of their own, which is the part of the one-door rule those classes *can* verify.
- **`src/PartyUp.Api/Auth/AuthenticateAndSignIn.cs`** is a thin adapter: call the use case, and on `Admitted` call `SignInAsync` and trigger the post-commit email. It contains no domain call and no transaction.

`AuthenticateAndSignIn` remains the **only** function in the codebase that issues `pu.auth`. No exceptions, and the list is exhaustive and testable: password login, magic-link consume, registration-verification redemption (A-046), **password reset** (§6.1 — round-1 omitted it), Google complete, Steam callback. The code-review rule survives unchanged: `SignInAsync(IdentityConstants.ApplicationScheme, …)` appears in exactly one file, `Api/Auth/AuthenticateAndSignIn.cs`. The complementary rule is that `PostAuthenticationGateUseCase` is called from exactly that one file too, so no path can reach a cookie without the gate.

After credential success and **before** issuing a cookie, inside one transaction (steps 1–5 in the use case, step 6 in the adapter):

1. Load the domain `User`.
2. `IsBanned` → abort with 403 `account_banned` (D-180 "blocks login"). No cookie.
3. `IsTombstoned` → abort 401 `invalid_credentials` (the account is gone; do not distinguish).
4. `IsPendingDeletion` and grace **not** elapsed → `user.CancelDeletion(clock)` and record it (**D-158/D-165**). This is the only reachable cancel path (§12-33) and is why there is no `DELETE /me/deletion` endpoint. Per §4.1 the user lands back in `Active` **or `Onboarding`**, whichever they left; a returning half-onboarded user is redirected to `/onboarding`, not `/app`.
5. `IsPendingDeletion` and grace **elapsed** → abort 403 `account_pending_removal`, and enqueue an immediate targeted sweep of that user (§9.4). Rationale: D-158 grants cancellation "during the seven-day grace period"; once it has elapsed the user's data is contractually gone even if the sweeper has not run yet, and letting a late login resurrect it would make the guarantee timing-dependent.
6. Commit, then `SignInAsync` (cookie), then post-commit email if the deletion was canceled ("your deletion request was canceled because you signed in").

---

## 7. HTTP API — full inventory

All routes under `/api/v1` (`ApiRoutes.V1`), minimal APIs in feature files under `src/PartyUp.Api/Endpoints/` (ARCHITECTURE §3): `AuthEndpoints.cs`, `ExternalAuthEndpoints.cs`, `OnboardingEndpoints.cs`, `MeEndpoints.cs`, `AccountDeletionEndpoints.cs`, `AdminEndpoints.cs`. Each exposes `public static RouteGroupBuilder MapXxx(this RouteGroupBuilder api)`.

**Error shape** (all 4xx/5xx): RFC 9457 `ProblemDetails` with an added `code` member:

```json
{ "type":"about:blank", "title":"Conflict", "status":409,
  "code":"last_auth_method", "detail":"…", "traceId":"…",
  "errors": { "username": ["errors.username.profanity"] } }
```

`code` is a stable machine string (snake_case); `errors` values are **i18n keys**, never English prose, so the SPA localizes (D-060). A single `DomainException`→ProblemDetails mapper lives in `src/PartyUp.Api/ErrorHandling/DomainExceptionMapper.cs`: `ValidationFailed`→400, `NotFound`→404, `Forbidden`→403, `Duplicate`/`Conflict`/`CooldownActive`/`NameReserved`→409, `WindowClosed`/`InvalidTransition`→409.

### 7.1 Auth

| Method | Route | Auth | Body → Response | Notes |
|---|---|---|---|---|
| GET | `/auth/antiforgery` | anon | – → 200 `{token}` | sets the HttpOnly antiforgery cookie and **returns the request token in the body**; see A-031 |
| GET | `/auth/session` | anon | – → `SessionResponse` | never 401; anonymous ⇒ `{authenticated:false, providers:[…]}` (A-042) |
| POST | `/auth/register` | anon | `{email,ageGateAttested,locale}` → **202** `{status:"verification_sent"}` | A-046: **no password field**; identical response whether or not the address exists; no cookie, no `userId`. RL `register-ip` + `register-email` |
| POST | `/auth/login` | anon | `{email,password}` → 200 `SessionResponse` | RL `auth-ip` + `login-email` |
| POST | `/auth/logout` | auth | – → 204 | clears cookie only (not stamp) |
| POST | `/auth/magic-link` | anon | `{email,locale?}` → 202 | RL `auth-email` + `magic-link-ip` |
| POST | `/auth/magic-link/consume` | anon | `{token,ageGateAttested?}` → 200 `SessionResponse` / **409 `age_gate_required`** | the 409 is `TokenRedemptionFailure.RequiresAgeGate` and **consumes nothing**, so the SPA retries the same token after the gate (A-029/§6.2, round-7). RL `auth-ip` |
| POST | `/auth/password/forgot` | anon | `{email}` → 202 | RL `auth-email` |
| POST | `/auth/password/reset` | anon | `{token,newPassword}` → 200 `SessionResponse` | rotates stamp. RL `auth-ip` |
| POST | `/auth/password/change` | auth | `{currentPassword,newPassword}` → 204 | rotates stamp, re-signs caller. RL `reauth` |
| POST | `/auth/password/set` | auth | `{newPassword, currentPassword?/reauthToken?}` → 204 | social-only accounts; 409 `email_required` without a verified email (§6.1); **409 `reauth_required` without proof, rotates the stamp, notifies (A-049)** |
| POST | `/auth/email/change` | auth | `{email, currentPassword?/reauthToken?}` → 202 | see A-048; re-auth per A-049 |
| POST | `/auth/email/verify/request` | auth | – → 202 | RL `email-send` |
| POST | `/auth/email/verify` | anon | `{token, password?}` → 200 `SessionResponse` (pending registration, A-046 — `password` required and set here) or 204 (resend confirm, `password` absent) | RL `auth-ip` |
| POST | `/auth/email/change/verify` | anon | `{token}` → 204 | purpose `EmailChange`; confirms the address **stored on the token**, not the row's current one |
| POST | `/auth/external/intent` | anon (`mode=login`) / auth (`mode=link`,`mode=reauth`) | `{provider, mode, returnTo?, ageGateAttested?, currentPassword?/reauthToken?}` → 200 `{intent}` | **A-050 — the only way to start any external flow**; antiforgery-enforced; `mode=link` additionally **409 `reauth_required` without proof (A-049)**. RL `intent-ip` in **every** mode (round-5 — `mode=login` is anonymous, so a `UserId`-partitioned policy cannot reach it), plus `reauth` for `mode=link` and `mode=reauth` |
| POST | `/auth/reauth` | auth | `{currentPassword}` → 200 `{reauthToken}` | A-049 proof branch 1; 5-min single-use. RL `reauth` |
| POST | `/auth/reauth/email` | auth | – → 202 | **A-049 proof branch 3 (round-4)** — emails a 5-min single-use `Reauthentication` token to the account's confirmed address; 409 `email_required` without one. The only proof a magic-link-only account can produce. RL `reauth` + `email-send` |
| GET | `/auth/methods` | auth | – → `AuthMethodsResponse` | D-087 |
| DELETE | `/auth/methods/{kind}` | auth | `{currentPassword?/reauthToken?}` → 204 / 409 `last_auth_method` / 409 `reauth_required` | D-137, A-049 |
| GET | `/auth/external/google/start` | anon/auth | `?intent=` → 302 | A-050 — `intent` is the **only** query parameter read, and the redeemed intent is the sole source of `mode` (`login`/`link`/`reauth`), `returnTo`, `ageGateAttested` and the bound `userId`; 400 `intent_required` without it or without a matching `pu.intent` binder. **Round-6: the separate `…/start?mode=reauth` row is deleted** — it presented `mode` as a route parameter and implied a per-mode auth requirement, both of which A-050 denies. A `mode=reauth` completion still mints the `Reauthentication` token and links nothing (A-045 step 4); its raw token rides the `/auth/callback#reauth=` fragment (A-049) |
| GET | `/auth/external/google/callback` | – | framework-owned handler path | |
| GET | `/auth/external/steam/start` | anon/auth | `?intent=` → 302 | same rule |
| GET | `/auth/external/steam/callback` | – | `openid.*`,`n` → 302 | §6.4 |
| GET | `/auth/external/complete` | – | → 302 to `/auth/callback?status=…[&next=…]` | shared tail of both providers; the landing is fixed and the sanitized destination rides as `next` (A-045 step 5, round-6) |

`SessionResponse`:

```jsonc
{ "authenticated": true,
  "userId": "0199…",
  // Deployment capability list — the methods THIS INSTANCE can offer, identical for every caller
  // including anonymous ones. NOT what this account has linked; that is GET /auth/methods (§6.5).
  "providers": ["password", "magicLink", "google", "steam"],
  "state": "onboarding|active|pending_deletion",
  "isAdmin": false,
  "onboarding": { "completedSteps": ["ageGate","auth"], "nextStep": "username" },
  "pendingDeletion": { "graceEndsAt": "2026-08-07T12:00:00Z" },
  "profile": { "username": "nilo", "displayName": "Nilo", "avatar": {...}, "timeZone": "America/Sao_Paulo", "locale": "pt-BR", "friendCode": "K7N2QX9ABC" } }
```

`profile` is null while onboarding. `SessionResponse` is the SPA's single source of truth and the only endpoint the app calls on boot. It is returned **only** by endpoints that actually issued or confirmed a session — never as a decorative body on an anti-enumeration path (A-046).

**`providers` (round-4 addition).** A-042 already promised this field ("`Google:ClientId` empty → … `GET /auth/session` reports `providers: ["password","magicLink"]` so the SPA hides the button") but the round-3 contract did not declare it, so the mechanism A-042 depends on had nowhere to live and §7.7's `.Produces<SessionResponse>()` would have generated a TypeScript type without it. It is a **deployment capability list, not a per-user one**: the sign-in methods this instance can offer, computed once at startup from configuration (`password` and `magicLink` always; `google` iff `Google:ClientId` is non-empty; `steam` iff **`Steam:Enabled`** is true — A-042/§13.1). It is present and identical on the anonymous `{authenticated:false}` response, because the sign-in page needs it before there is a session, and it reveals nothing a probe of `/auth/external/{provider}/start` would not. What a *particular account* has linked stays `GET /auth/methods` (§6.5), which is authenticated and is the only place `linked`/`removable` appear.

**`steam` is gated on an explicit flag, not on "the Steam handler" (round-6 correction).** The round-5 predicate was "`steam` iff the Steam handler is registered", which has no referent: A-019/§6.4 make the Steam leg hand-rolled endpoints on `ExternalAuthEndpoints`/`AuthEndpoints` that are always mapped — there is no handler to register — and the one key an implementer would reach for, `Steam:ApiKey`, is precisely the key A-042 bullet 2 forbids gating on (with it empty, Steam login **still works**; only persona/avatar enrichment is skipped). The likely outcome was a deployment with keyless Steam login fully functional while `/login` never rendered the Steam button (§12.1 renders a provider button only when `providers` contains it), silently removing one of D-067's four authentication methods, and `SessionEndpointTests` asserted only the `google`-omitted case. **`Steam:Enabled` (§13.1, default `true`)** now gates *both* the `providers` entry and the mapping of the two **sign-in** routes `/auth/external/steam/{start,callback}` — one flag, so the button and the sign-in routes can never disagree.

**The flag deliberately does *not* unmap `/account-deletion-requests/steam/{start,callback}` (consistency sweep).** Gating all four routes together would have put this decision in direct conflict with A-045 step 6's round-6 invariant: Steam is permitted to create address-less accounts *because* it has an assertion-based public deletion leg, so an operator flipping `Steam:Enabled=false` after any Steam-only account exists would close both that account's login (D-180 aside, the button is gone) and its only public deletion route — reinstating the blocking finding this round closed, by configuration rather than by code. The deletion leg mints nothing beyond a `DeletionRequestConfirmation` token bound to an **already-resolved** `auth_user_logins('Steam', …)` row, issues no cookie and creates no account (§7.4), so leaving it mapped when sign-in is off offers an attacker nothing and keeps `PRODUCT_SPEC.md:457` reachable for every account the deployment has ever created. Turning Steam off therefore means "no new Steam sign-ins", never "existing Steam-only accounts lose their deletion path". Test: `SessionEndpointTests.Session_ListsSteamWithNoSteamApiKeyConfigured_D067` (§14.3).

### A-048 — Changing the email is a re-authenticated, both-sides-notified, stamp-rotating operation

Round-1's `POST /auth/email/change {email}` was authenticated-only: no password check, no notice to the old address, no stamp rotation, no distinct token purpose. That converts a stolen `pu.auth` cookie (30-day sliding, A-028) into permanent ownership — change email → verify from the attacker's mailbox → `/auth/password/reset` → victim locked out with no signal anywhere. The flow becomes:

1. **Request** (`POST /auth/email/change`): if a `PasswordHash` exists, `currentPassword` is **required** and verified (same rule as `/auth/password/change`); an account with no hash instead supplies a `reauthToken` obtained from a fresh external re-assertion — an intent minted `mode=reauth` at `POST /auth/external/intent` and redeemed at `GET /auth/external/{provider}/start?intent=…` (A-049/A-050; **the mode lives on the intent, never on the start URL** — round-6). **Both branches ship in Phase 2** — round-3 correction: the round-2 sentence "Phase 2 ships the password branch and returns 409 `reauth_required` for the hash-less case" contradicted A-049, which makes re-authentication *mandatory* on five endpoints. Under the stale reading a Steam-only or Google-only account (a class D-078 explicitly permits and A-023's `RequireUniqueEmail = false` exists to support) could never produce a proof, so it would permanently receive 409 `reauth_required` on `link-intent` (killing D-087), on `/auth/email/change` and transitively `/auth/password/set` (killing D-067 recovery, since a first address can only arrive through the change route) and on `DELETE /auth/methods/{kind}` (killing D-137's unlink). Three Confirmed decisions unreachable for a whole account class is not a scheduling trade-off. **A-049's external branch is authoritative and in scope**; §15 slices 6 and 7 ship the `mode=reauth` completion, both before slice 8.

   **First-address case (round-3 addition).** `POST /auth/email/change` is also how an address-less account (Steam-only — the sole address-less class A-045 step 6 permits, round-6) *gains* its first address; there is no separate `/auth/email/set`. When `auth_users.Email IS NULL`: re-authentication is still required (necessarily the `reauthToken` branch, since no hash exists); step 3's "notify the old address" is a **no-op** — there is no old address and nothing is sent; steps 2, 4 and 5 are unchanged, so the address only lands after the new mailbox is proven, `EmailConfirmed = true`, and the stamp rotates. Tests: `ChangeEmailUseCaseTests.Execute_AddsAFirstAddressToAnAddresslessAccount_D067`, `..._SendsNoPreviousAddressNoticeWhenThereIsNone_D078`.
2. Issue an `EmailChange` token (own purpose, §A-029) carrying the **target** address in `email_normalized`. Nothing on `auth_users` changes yet.
3. **Notify the old address immediately** (`EmailChangeRequested` template, 7th template — "if this wasn't you, change your password now"), and send the verification link to the new address.
4. **Verify** (`POST /auth/email/change/verify`): inside one transaction, apply the address **from the token**, set `EmailConfirmed = true`, and **rotate the security stamp** (every other device signs out). If the target address has meanwhile been taken, return 409 `email_unavailable` — deliberately *not* an enumeration concern, because the caller is authenticated and already knows they own the session; the round-1 design would have surfaced the partial unique index as a 500 here.
5. Because `EmailChange` is a distinct purpose, a "resend verification" (`EmailVerification`) can no longer silently consume a pending change, and a change token can no longer be redeemed to re-confirm the old address.

Tests: `ChangeEmailUseCaseTests.Execute_RequiresTheCurrentPassword_D067`, `..._NotifiesThePreviousAddress_D067`, `..._RotatesTheSecurityStampOnVerify_D165`, `..._ConfirmsTheTokensAddressNotTheRowsAddress_D067`.

### A-049 — Every change to *how you sign in* is re-authenticated, stamp-rotating and notified — not only the email change

A-048 reasons correctly that a stolen 30-day `pu.auth` cookie must not convert into permanent ownership. Round-2 review showed the reasoning was applied to exactly one of the four endpoints that perform that conversion; the other three needed nothing but the cookie, and none of them told the user anything:

- `POST /auth/external/intent` (`mode=link`) + `GET /start` (§7.1, A-050) — antiforgery stops *cross-site* initiation, but a cookie thief is not cross-site. The attacker binds their own Google/Steam identity to the victim's account.
- `DELETE /auth/methods/{kind}` (§6.5) — the attacker then removes `password` and/or the victim's own provider. D-137's last-method rule still passes, because the attacker's freshly linked method counts. Removing `password` rotates the stamp, which signs the **victim** out while the attacker re-authenticates through their own provider.
- `POST /auth/password/set` (§7.1) — on a social-only account with an email on file, sets a password with no current-credential check and (round-2 draft) no stamp rotation. A durable email+password credential, silently.

The rule, uniform across all of them and the email change:

1. **Re-authentication is required** on `POST /auth/external/intent` (`mode=link`), `DELETE /auth/methods/{kind}`, **`DELETE /me/platform-links/{id}` when the link is a Steam link (round-5)**, `POST /auth/password/set`, `POST /auth/password/change`, `POST /auth/email/change` **and `POST /me/deletion`**. The platform-link route is on the list because A-030 routes it into the very same `UnlinkSteamUseCase` under the same D-137 guard: with the proof named in A-030 but in neither the §7.3 contract nor §7.6, an implementer honoring A-030 would ship a route that 409s `reauth_required` forever (killing PRODUCT_SPEC §8.6's "unlink platform accounts and revoke their data access" through §12.1's `/settings/profile`), and one honoring §7.3 would let a stolen `pu.auth` strip the victim's Steam auth method with no proof and no per-user throttle — the exact conversion A-049 exists to close. Missing or stale proof → **409 `reauth_required`**, which the SPA renders as "confirm it's you". `AuthTokenPurpose` gains `Reauthentication`. **All three proof branches ship in Phase 2** (A-048 step 1; round-4 adds the third) — an account class that cannot produce a proof is permanently locked out of D-087, D-137, D-067 and PRODUCT_SPEC §8.6's in-app deletion:

   | # | Proof | Available to |
   |---|---|---|
   | 1 | `currentPassword` in the body, verified exactly as `/auth/password/change` does | any account with a `PasswordHash` |
   | 2 | a fresh external assertion: `POST /auth/external/intent {mode:"reauth"}` then `GET /auth/external/{provider}/start?intent=…` (round-6 — `mode` is a property of the intent row, never a query parameter on `/start`), whose completion mints a 5-minute single-use `Reauthentication` token the SPA replays as `reauthToken`. Reuses the A-050 intent machinery, changes no auth methods, and is bound to the caller per **A-045 step 4** | any account with an `auth_user_logins` row (Google, Steam) |
   | 3 | **`POST /auth/reauth/email` (round-4 addition)** — emails a `Reauthentication`-purpose token (same 5-minute TTL, same single use, same `reauthToken` replay shape) to the account's **confirmed** address; 409 `email_required` when there is none. Reuses the whole A-029/A-044 pipeline, so it adds one endpoint and one template and no new machinery | any account with `EmailConfirmed = true` |

   **Why branch 3 exists (round-4 blocking finding).** The round-3 list admitted only branches 1 and 2, and §6.2 creates accounts that have **neither**: a magic-link redemption with `user_id = NULL` produces a `users` + `auth_users` pair with a confirmed email, **no `PasswordHash` and no `auth_user_logins` row**. Such an account signs in forever by magic link but would receive a permanent 409 on `POST /auth/password/set` (it passes §6.1's `email_required` gate — it has a verified address — and then fails A-049), on `POST /auth/email/change`, on `POST /auth/external/intent {mode:"link"}` (so D-087 "Users can link more authentication methods later" is unreachable for the class) and on `POST /me/deletion` (so PRODUCT_SPEC §8.6's in-app deletion is unreachable and only the D-072 public route remains). That is the identical failure shape round-3 finding 7 fixed for the D-078 social-only class, on a different class the fix did not consider — and there is no slice-ordering escape, because the missing branch did not exist in any slice. Branch 3 is strictly no weaker than magic-link sign-in itself, which is the credential such an account already has: it proves control of the same mailbox, and unlike a magic link it grants no session.

   RL: `reauth` plus `email-send` on `POST /auth/reauth/email` (§7.6 holds the numbers). The emailed link lands on the SPA route `/auth/confirm-identity` (§12.1), which reads `location.hash` (`#token=…`, A-044), `replaceState`-clears it before any render, holds the value **in memory only** — never `sessionStorage` — and renders the pending settings action in that same tab so the proof is used where it was received. Template `ReauthenticationRequested` (§11). Test: `ReauthenticationTests.EmailedProof_LetsAMagicLinkOnlyAccountSetAPassword_D087` (§14.3).

   **`POST /me/deletion` is in the list (round-3 addition).** The round-2 draft exempted it, which left the largest cookie-to-permanent-harm conversion on the whole surface behind the weakest proof: the cookie alone starts a 7-day grace, rotates the stamp, and — per §9.2 and §12-33, deliberately — there is **no cancel endpoint**, so the only recovery is a successful login inside seven days. For an account with an address on file that is survivable (the `DeletionRequested` mail plus a login cancels it). For the D-078 address-less class the design goes out of its way to support, §11 sends nothing, so the victim's only signal is an unexplained sign-out — which A-049's own item 3 argues is "indistinguishable from routine expiry" — and seven days later `DeletionExecutor` tombstones the account irreversibly. That is strictly worse than any of the five endpoints already covered, obtained with strictly less proof. `POST /me/deletion` therefore requires `currentPassword` or `reauthToken` and returns 409 `reauth_required` without one. (`POST /account-deletion-requests/confirm` is *not* in the list: it is anonymous and its proof is the redeemed token itself.)

   **How the `Reauthentication` token reaches the SPA (round-3 addition).** The round-2 draft never said, and every obvious answer contradicted a rule already ratified here: a query parameter violates A-044 (SPA document routes are served by `MapFallbackToFile`, **after** `UseSerilogRequestLogging` — verified at `src/PartyUp.Api/Program.cs:54` and `:71`) and §6.3's "no auth data is ever put in the redirect URL"; and §12.1's `/auth/callback` is specified to read `?status=` only. `Reauthentication` is a hashed 256-bit `auth_tokens` row precisely *because* it grants something (A-015), so leaving its transport undefined is how the round-1 A-015 violation happened. It is therefore specified: `GET /auth/external/complete`, on a `mode=reauth` flow, redirects to **`/auth/callback?status=reauth_ok#reauth=<raw>`** — and A-045 step 5's round-6 rule makes `/auth/callback` the landing for *every* external completion, so this transport is not a special case the allow-list can strand (the round-5 draft's `returnTo` allow-list did not contain `/auth/callback`, which would have put the raw token on `/app`, a route with no hash handling at all). The SPA reads `location.hash`, calls `history.replaceState(null,'',location.pathname)` **before any render**, holds the value in memory only, and replays it in the body as `reauthToken` — exactly the handling §12.1 already requires for `/auth/magic`. Route-level test: `ExternalRedirectTests.NoRedirectLocationFromAnExternalLegContainsARawTokenInItsQuery` asserts every `Location` produced by `/start`, the provider callbacks and `/complete` has an empty or token-free query string, the sibling assertion to `EmailTemplateRendererTests.Render_PutsTheTokenInTheFragment_D067`.

   **The proof endpoints are rate-limited per user, and share the login lockout counters (round-3 addition).** A-049's whole value is that a cookie thief cannot convert a session into durable ownership without also knowing the password — which collapses if the proof endpoints are a free password oracle. §7.6's round-2 table covered none of them, leaving only `default` at 300/min/IP: ~300 guesses a minute against a known account, with no lockout (the re-auth path is `IUserAccountGateway.CheckPasswordAsync`, whose lockout behavior was unstated) and no notification, and a password recovered at `/auth/reauth` is then usable at `/auth/login`, sidestepping the login lockout entirely. Therefore: §7.6 gains a **`reauth` policy, 5 / 15 min / user**, applied to `POST /auth/reauth`, `POST /auth/password/change`, `POST /auth/password/set`, `POST /auth/external/intent`, `DELETE /auth/methods/{kind}`, `POST /auth/email/change` and `POST /me/deletion`; and **`CheckPasswordAsync` increments and honors the *same* Identity lockout counters as `CheckPasswordSignInAsync`** (implemented as `UserManager.CheckPasswordAsync` + `AccessFailedAsync`/`ResetAccessFailedCountAsync`, with `IsLockedOutAsync` checked first), so the two paths cannot be played against each other. A locked-out account gets the same `409 reauth_required` as a wrong password — the §6.1 equalization rule applies here too. Test: `ReauthenticationTests.Reauth_SharesTheLoginLockoutCounters_D067`.
2. **The security stamp rotates** on every one of them, including `POST /auth/password/set` (round-2 draft rotated only on `change` and on removing the password). Setting a first password is the creation of a credential and must invalidate every other session, same as resetting one.
3. **The address on file is notified**, post-commit, via two new templates: `AuthMethodAdded` ("a new way to sign in was added to your account — if this wasn't you…") and `AuthMethodRemoved`. They go to the account's current verified address; an account with no address on file gets no mail (D-078 permits it) and relies on the sign-out plus §6.5's `GET /auth/methods` list. Silence was the whole problem: the round-2 draft left the sign-out as the victim's only signal, and a sign-out is indistinguishable from routine expiry.

Tests: `UnlinkAuthMethodUseCaseTests.Execute_RequiresReauthentication_D137`, `SetPasswordUseCaseTests.Execute_RotatesTheSecurityStamp_D165`, `SetPasswordUseCaseTests.Execute_RequiresReauthentication_D067`, `ExternalIntentUseCaseTests.Execute_RequiresReauthenticationInLinkMode_D087`, `CompleteExternalSignInUseCaseTests.Execute_NotifiesTheAccountAddressWhenAMethodIsLinked_D087` (round-4 — the intent use case links nothing, so the obligation cannot live there), `SetPasswordUseCaseTests.Execute_NotifiesTheAccountAddress_D087`, `UnlinkAuthMethodUseCaseTests.Execute_NotifiesTheAccountAddress_D137`, `UnlinkSteamUseCaseTests.Execute_NotifiesTheAccountAddress_D137`, `RequestAccountDeletionUseCaseTests.Execute_RequiresReauthentication_D165`, `ReauthenticationTests.Reauth_SharesTheLoginLockoutCounters_D067`, `ExternalRedirectTests.NoRedirectLocationFromAnExternalLegContainsARawTokenInItsQuery`.

### 7.2 Onboarding

### A-033 — Onboarding steps are validated individually and committed atomically

The domain's `CompleteOnboarding` requires all five profile fields at once, and the `Change*` methods require `Active`. Rather than add a server-side draft table (or weaken the aggregate), the SPA accumulates the draft locally and the server exposes **validation-only** step endpoints plus one atomic completion. D-127's ordering is enforced by the client wizard; the server enforces the *invariant* (an `Active` user always has a complete profile), which is the thing that must not be violated.

| Method | Route | Auth | Body → Response |
|---|---|---|---|
| GET | `/onboarding/state` | auth | – → `SessionResponse.onboarding` |
| GET | `/onboarding/username/availability?username=` | auth | – → `{available, reason?}` — `reason ∈ taken|reserved|profanity|invalid` (RL 30/min/user) |
| POST | `/onboarding/validate` | auth | `{username?,displayName?,avatar?,timeZone?,locale?}` → 200 `{errors:{field:[key]}}` |
| POST | `/onboarding/complete` | auth | `{username,displayName,avatar,timeZone,locale}` → 200 `SessionResponse` |

Platform linking during onboarding (D-127 step 7) uses the same `/me/platform-links` endpoints, which are `RequireAuthenticated` (not `RequireOnboarded`) because `User.LinkPlatform` is legal in `Onboarding`.

`avatar` request shape: `{ "kind":"emoji", "emoji":"🎮" }` or `{ "kind":"platform", "platformLinkId":"019…" }`.

`POST /onboarding/complete` transaction: text policy (A-036) → `UsernameRegistry.Claim` (loading the candidate row `FOR UPDATE`) → `user.CompleteOnboarding(...)` → commit. Unique-violation on `username_registry` PK maps to 409 `username_taken` (ARCHITECTURE §3: DB constraints, not app checks, are the authority).

### 7.3 Profile and settings (`/me`)

| Method | Route | Auth | Notes |
|---|---|---|---|
| GET | `/me` | auth | full profile incl. `friendCode`, platform links, auth-method summary |
| PUT | `/me/username` | onboarded | D-128/D-145; 409 `username_cooldown` with `retryAt`, 409 `username_taken`, 409 `username_reserved` |
| PUT | `/me/display-name` | onboarded | text policy |
| PUT | `/me/avatar` | onboarded | D-050 |
| PUT | `/me/time-zone` | onboarded | tzdb id validated against `DateTimeZoneProviders.Tzdb` |
| PUT | `/me/locale` | onboarded | D-146 account override |
| PUT | `/me/reminder-default` | onboarded | D-082 |
| GET | `/me/platform-links` | auth | |
| POST | `/me/platform-links` | auth | `{platform,handle}`; **text policy on `handle` (A-036, `TextField.GamingHandle`, ≤64 — D-096/D-097), run before `User.LinkPlatform`**; 409 `platform_already_linked`; 409 `steam_external_flow_required` for Steam (use the external auth flow) |
| PATCH | `/me/platform-links/{id}` | auth | `{handle}`; **same text policy, run before `User.ChangePlatformHandle`** |
| DELETE | `/me/platform-links/{id}` | auth | body `{currentPassword?/reauthToken?}` — **required for a Steam link** (round-5); falls avatar back (D-050); for a **Steam** link this is the shared `UnlinkSteamUseCase` — removes the login row and `steam_identities` too, and can return 409 `last_auth_method` (A-030, D-137) or **409 `reauth_required` without proof (A-049)**. Non-Steam links are not auth methods: no proof is required and the body may be absent. RL `reauth` (Steam links) |
| POST | `/me/deletion` | **auth** | `RequireAuthenticated`, *not* `RequireOnboarded` — an account abandoned mid-onboarding must be deletable (A-047). Body `{currentPassword?/reauthToken?}`: **re-authentication is required (A-049 item 1, round-3)**, 409 `reauth_required` without proof; RL `reauth`. → 202 `{graceEndsAt}`; rotates the stamp (D-165) so this response is the last thing the session does |

There is **no** `DELETE /me/deletion`. §12-33: the account is signed out everywhere, so the only reachable cancel is a successful login (D-158). The 202 body and the confirmation email must state this (spec §8.6 requires the disclosure).

**Closing correction (slice 13):** Steam links are created and removed through the external authentication flow so the login, platform-link and `steam_identities` facts stay atomic (A-030). `GET /auth/methods` is the client-facing inventory that reports whether the Steam authentication method is linked; the settings UI uses that row to offer the external link/unlink flow instead of treating Steam as an arbitrary handle.

### 7.4 Public deletion request (D-072)

Unauthenticated, for the public web route. **Phase 2 ships the API, the identity verification DOMAIN_MODEL §11 requires, and both SPA pages (round-5).**

The round-4 draft said "Phase 8 builds the page; Phase 2 ships the API" while §12.1 listed `/account-deletion` and `/account-deletion/confirm` as Phase 2 routes and slice 12 was titled "public deletion page hook" — three statements that could not all hold. The resolution is Phase 2, and it is forced rather than chosen: `/account-deletion/confirm` is the *only* consumer of the Steam callback's `#token=<raw>` fragment (A-044 forbids any other transport), and `POST /account-deletion-requests/confirm` is the single place the public route calls `RequestDeletion`. Without that page the round-1 blocking gap — a banned, email-less D-078 account with no reachable deletion path (PRODUCT_SPEC:457, §8.6, D-072 Policy-required) — is closed only on paper, and the §14.3 `PublicDeletionRouteTests` prove an API no user can reach. `/account-deletion` (the request form) ships with it, because a confirm page with no way to originate a request is not a route anyone lands on. §12.1, §14.5 and slice 12 all say Phase 2; IMPLEMENTATION_PLAN.md:125, which files D-072's public page under Phase 8, is amended in the closing slice. Phase 8 keeps the *marketing/legal* surfacing of the route (linking it from the footer and the store listing), not the functional page.

| Method | Route | Body → Response |
|---|---|---|
| POST | `/account-deletion-requests` | `{email}` → **202 always** (no enumeration). A known address gets a `DeletionRequestConfirmation` token, 60-minute TTL; an unknown one gets the `DeletionRequestNoAccount` template, so both branches do one render and one send and are indistinguishable in cost (§6.1 round-4). RL `deletion-request`. The known-account email uses template `DeletionRequestConfirmation`; `DeletionRequested` is reserved for the tokenless, localized grace notice sent only after confirmation starts the grace. |
| POST | `/account-deletion-requests/confirm` | `{token}` → 202 `{graceEndsAt}`. Redeems the token, calls `RequestDeletion`, rotates the stamp |
| GET | `/account-deletion-requests/steam/start` | → 302 to the Steam OP. Identity proof for **email-less accounts**; see below. **Non-destructive.** Mints state `{ flow = "deletion", nonce }` into cookie **`pu.extdel`**, `Path=/api/v1/account-deletion-requests/steam`, protector purpose `PartyUp.SteamState.Deletion` (A-051), and pins `openid.return_to` to **this** leg's absolute callback URL. It needs no A-050 intent: it is anonymous, its only durable output is a token delivered into the initiating browser's own fragment, which a cross-site initiator cannot read, and (round-5) minting that token no longer consumes any outstanding one — A-029's concurrent-token rule now files `DeletionRequestConfirmation` under *anonymously initiated*. RL `deletion-steam-ip` |
| GET | `/account-deletion-requests/steam/callback` | `openid.*`,`n` → 302 to `/account-deletion/confirm#token=<raw>`. Verifies the assertion by §6.4 steps 1–5 **with this flow's cookie name, protector purpose, `flow` discriminator and `return_to`** (A-051 — §6.4's are scoped to the auth leg and are never valid here), resolves `auth_user_logins('Steam', steamid64)`, **mints a `DeletionRequestConfirmation` token bound to that `UserId` (10-minute TTL)** and redirects with it in the fragment (A-044). It performs **no** state change and **issues no cookie**. RL `deletion-steam-ip` **Closing verification (slice 13):** this callback landed in slice 8 and completes A-051's pair; `ExternalStateTests.AuthStateCookie_IsRejectedByTheDeletionCallback` exercises this real route, the reverse-direction test rejects the deletion cookie at the auth callback, and `ExternalStateTests.AuthPurposeCookie_ForTheDeletionFlow_IsRejectedByTheAuthCallback` additionally pins the `flow` discriminator. |

**Both public variants are two-step (round-2 correction).** The round-2 draft had the Steam callback call `RequestDeletion` + stamp rotation directly, while the email variant was deliberately request → emailed token → `/confirm`. Round-2 review showed the asymmetry is a live hazard: both Steam legs are GETs that A-031 exempts from antiforgery, so an unintended top-level navigation — an attacker page, a mistyped link, a link preview or prefetch of `/steam/start` — landing in a browser with a live `steamcommunity.com` session ended with the victim in `PendingDeletion`, signed out of every device (D-165), with only the `DeletionRequested` mail (which an email-less account never receives) as a signal. The `pu.ext` state cookie does not help: it is minted by the same attacker-initiated `/start`, so it authenticates the *flow*, not the user's intent. A Steam assertion proves "this browser controls this SteamID" — identity, never consent.

So the Steam path now has the identical shape to the email path and reuses the machinery that already exists: assertion → `DeletionRequestConfirmation` token → `/account-deletion/confirm` page → **antiforgery-protected `POST /account-deletion-requests/confirm {token}`**, which is the single place `RequestDeletion` + stamp rotation happen for the public route. `ConfirmSteamDeletionUseCase` is deleted from §8.1; `ConfirmPublicDeletionUseCase` serves both, since both arrive holding the same purpose of token.

A banned user can use this route (spec §10 line 457: "A banned user can still request account deletion through the public route") — the ban check is not applied here.

**Email-less accounts (round-1 blocking finding).** D-078 permits Steam-only accounts with no email on file (A-023 sets `RequireUniqueEmail = false` for exactly that reason), and ban blocks login (§6.6 step 2). An email-keyed public route therefore left a whole class of accounts — banned, Steam-only — with *neither* the in-app route nor the public one, contradicting the §10 guarantee above and PRODUCT_SPEC §8.6. The Steam variant closes it: a valid, fully-envelope-checked OpenID assertion (§6.4 step 4, `openid.signed` included) is the same identity proof the emailed token provides, and it is the only credential such an account has. It deliberately reaches `RequestAccountDeletionUseCase` **without** going through `AuthenticateAndSignIn` — no session is issued, so the ban block in §6.6 is neither bypassed nor triggered.

**Why two variants suffice, stated as a closed enumeration rather than as a claim about Google (round-6).** The round-5 text justified covering only the email and Steam variants with "Google-only accounts always carry a verified email and use the email route" — a sentence its own A-045 step 6 falsified, because two Google branches created accounts with `Email = NULL`. A-045 step 6 now carries the invariant that makes the enumeration true by construction: **no provider may create an address-less account unless it also has an assertion-based public deletion leg**, and in Phase 2 the only address-less-creating provider is Steam. So every account Phase 2 can produce holds either a confirmed address (password registration, magic link, verified-Google create, or a later `POST /auth/email/change`) or a Steam login row — and each of those is exactly one of the two public variants. A new provider is a design change that must ship one of the two, and `CompleteExternalSignInUseCaseTests.Execute_NeverCreatesAnAddresslessAccountForAProviderWithNoPublicDeletionLeg_D072` fails if it does not.

**Closing correction (slice 13):** the ops fallback remains a backstop for legacy/imported data, never a route a Phase 2 account class depends on. The earlier sentence claimed the admin deletion-sweep endpoint accepted a `userId`; shipped code proves it does not. `RUNBOOK-deletion.md` records that the targeted fallback is not operational and forbids direct database improvisation. Tests: `PublicDeletionRouteTests.SteamAssertion_ForAnEmaillessBannedAccount_MintsAConfirmationTokenWithoutStartingTheGrace_D072`, `PublicDeletionRouteTests.SteamAssertion_ThenConfirm_StartsTheGrace_D072`, `PublicDeletionRouteTests.SteamAssertion_IssuesNoCookie_D180`, plus the A-045 step 6 invariant test above.

### 7.5 Admin (`/api/v1/admin`, `RequireAdmin`, 404 on failure)

| Method | Route | Notes |
|---|---|---|
| GET | `/admin/users?query=&page=&pageSize=` | D-179 data half: id, username, display name, email (masked), created, lifecycle, `bannedAt`. Per-user activity stats are Phase 8 (their tables don't exist) |
| GET | `/admin/users/{id}` | same shape, single |
| POST | `/admin/users/{id}/ban` | `{removeContent:false}` → 200 (**no `reasonNote`** — round-6 minor: the paragraph below drops it, and the round-5 body still declared it). Sets `BannedAt`, rotates stamp. `removeContent:true` is **accepted but rejected with 501 `not_implemented_in_phase`** until the effect steps have entities to act on (see §10.2) |
| POST | `/admin/users/{id}/unban` | → 200. Clears `BannedAt`. Does not restore removed content (D-180) |
| POST | `/admin/maintenance/deletion-sweep` | → 200 `{executed, failed, tokensPurged}`. Ops trigger for the executor (A-037) **and for §9.7's `PurgeExpiredAuthTokensUseCase`** (round-5 — the purge must be reachable when `Deletion:SweepEnabled` is false, which is every integration run) |
| GET | `/admin/deletion-journal?since=<instant>&limit=` | A-017 restore-replay feed |

`reasonNote` is stored in… nothing in Phase 2. Decision: **drop it from the request** — it is absent from the row above and from the OpenAPI schema, not accepted-and-ignored — rather than add an unmodelled audit table; ban/unban are recorded by `banned_at` and by the structured log line only. Phase 8's admin work owns an audit trail if one is wanted.

### A-031 — Antiforgery: framework tokens, header-carried, enforced by one endpoint filter on the whole `/api/v1` group

**Round-1 correction.** The round-1 text described a mechanism ASP.NET Core does not have: "the request token rides in the non-HttpOnly `pu.csrf` cookie … while the framework's own `HttpOnly` companion cookie holds the cryptographic half". There is no companion cookie. `AntiforgeryOptions.Cookie` configures the **single** antiforgery cookie, which holds the *cookie token* (the cryptographic half); `GetAndStoreTokens(ctx)` sets that cookie and separately returns `RequestToken`, a different value, which is what must arrive in `X-CSRF-TOKEN`. As written, round 1 would have (a) exposed the cookie token to any script on the origin, (b) returned 204 with no body so the SPA had no way to obtain the request token at all, and (c) failed `ValidateRequestAsync` on every unsafe request — making `POST /auth/login`, `/auth/register` and every mutation unreachable, and invalidating `AntiforgeryTests` as designed. The corrected configuration:

- `builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.Name = "pu.csrf"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = Lax; o.Cookie.SecurePolicy = <as A-028>; })`. **`HttpOnly` stays `true`** — the cookie token is the half that must remain unguessable to script.
- `GET /api/v1/auth/antiforgery` calls `IAntiforgery.GetAndStoreTokens(HttpContext)` and returns **200 `{ "token": tokens.RequestToken }`**. The cookie token is set by the same call as an `HttpOnly` cookie the SPA never reads. This is the genuine cookie/request-token pair, not a cookie-value double submit (which cookie injection from a sibling subdomain defeats).
- **The token is bound to the authenticated identity.** The SPA must re-fetch it after **login, logout, registration-verification redemption and any external-provider return** — not only on a 400. §12.2 is updated accordingly.
- An endpoint filter `AntiforgeryFilter` is attached to the `/api/v1` group and validates every request whose method is not GET/HEAD/OPTIONS/TRACE, **except** an explicit allow-list carrying `.DisableAntiforgery()`: none. Every unsafe endpoint, including `POST /auth/login` and `POST /auth/register`, requires the token — login CSRF is a real attack (an attacker logs the victim into the attacker's account) and there is no cost to covering it because the SPA fetches the token on boot.
- The two external-provider callbacks are GET and therefore exempt; their CSRF defence is the correlation/state cookie (§6.3/§6.4/A-051), which is the correct mechanism for that leg. **`/auth/external/{provider}/start` is *not* covered by that argument — in any mode** (round-3): `mode=link` begins a mutation of the caller's own account, and `mode=login` silently creates an account and issues `pu.auth` for a browser that already holds a provider session. Both therefore require the one-time `intent` minted by the antiforgery-protected `POST /auth/external/intent` (A-050). No GET in this codebase both starts a provider flow and reads its parameters from the URL.
- Cookie-less clients (future mobile) will use a different auth scheme entirely; the filter checks `User.Identity.IsAuthenticated` only via the cookie scheme, so the mobile phase can bypass it by scheme without touching this code.

### 7.6 Rate limiting

Built-in `Microsoft.AspNetCore.RateLimiting` (framework, no package). All ceilings are fixed-window with a 0-length queue, keyed by a **hashed** partition key (`SHA256(ip+pepper)` or `SHA256(normalizedEmail+pepper)` or `SHA256(userId+pepper)`) so no raw identifier lands in memory dumps or logs.

**Limiter architecture: one chained global limiter, not per-endpoint named policies (round-7 cross-family finding 5).** This is the wiring for every ceiling in the table below, including A-049's `reauth`; it mints no new decision because it changes no ceiling, no partition key and no route assignment — it is the only mechanism that implements the ones already decided.

**Named endpoint policies do not compose.** ASP.NET Core resolves *at most one* `EnableRateLimitingAttribute` per endpoint: a second `RequireRateLimiting` **replaces** the first rather than intersecting with it (dotnet/aspnetcore#42691), and the pipeline evaluates the global limiter plus that single endpoint limiter. The table below assigns two and sometimes three ceilings to one route — `POST /auth/login` under `auth-ip` **and** `login-email`, `POST /auth/external/intent` under `intent-ip` **and** `reauth`, `POST /auth/reauth/email` under `reauth` **and** `email-send`, `POST /auth/magic-link` under `auth-email` **and** `magic-link-ip` — and §7.6's own rule is "a route under two policies must satisfy both". Wired as thirteen named policies with two `RequireRateLimiting` calls on a route, the *first* ceiling silently disappears: `/auth/login` would enforce `login-email` (10/15 min/email) and **not** `auth-ip`, and `RateLimitTests.EveryPolicyNamedInTheDesignIsRegistered` would still pass, because the policies would all be registered — just not all reachable. That is a security control that reads as present and is absent, on the routes the anti-enumeration work depends on.

The mechanism this design ships is the one that *does* intersect:

```csharp
options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
    Dimension.AuthIp, Dimension.LoginEmail, Dimension.RegisterIp, /* … */ Dimension.Default);
```

- **`PartitionedRateLimiter.CreateChained<HttpContext>(…)`** (System.Threading.RateLimiting) is a real API and it is the composing one: a request must be permitted by **every** member; if any member refuses, the leases already acquired from the earlier members are released, so a rejected request does not burn budget on the ceilings that would have allowed it.
- **Each "policy" in the table below is one chain member**, a `PartitionedRateLimiter.Create<HttpContext, string>(ctx => …)` whose partition function inspects the matched endpoint and method (and, for the per-user dimensions, the `NameIdentifier` claim on `HttpContext.User` — the claim only, never `ICurrentUserAccessor`, because a limiter must not perform a database read) and returns either `RateLimitPartition.GetNoLimiter("")` — the request is outside this dimension's route set, so the member is inert for it — or `RateLimitPartition.GetFixedWindowLimiter(<hashed key>, …)` with that row's window and permit count. Two ceilings on one route are two members returning a real partition for the same request, which is exactly the conjunction the table means.
- **No endpoint calls `RequireRateLimiting`, and no named policy is registered.** `RateLimitPolicies.cs` exposes the route-set predicates and the chain; endpoints declare nothing. This is deliberate: a route's ceilings are then decided in one file that can be reviewed against this table, and an endpoint author cannot accidentally replace a ceiling by adding one.
- **The route sets are path predicates, and they are the pattern-matched route templates, not raw string prefixes** — `ctx.GetEndpoint()` is available to the global limiter (it runs after routing when `UseRateLimiter` is placed after `UseRouting`, which slice 3 requires), so a member matches on the endpoint's `.WithName(...)` operation id. That keeps the mapping stable under any future path change and makes `RateLimitTests.EveryDimensionCoversItsDocumentedRoutes` a mechanical comparison against this table.
- **A partition function may not read the request body**, so the per-user dimensions key on the authenticated principal only: `reauth`, `email-send` and `lookup` return a real partition when `HttpContext.User` is authenticated and `GetNoLimiter` otherwise. The one place that matters is `POST /auth/external/intent`, whose `mode` lives in the body: the `reauth` member therefore counts **every authenticated mint** on that route, whatever the mode, and never counts the anonymous `mode=login` mint (which `intent-ip` bounds, and which is why `intent-ip` exists). That is stricter than A-049's list, never looser, and it needs no body buffering.
- **Order in the chain is irrelevant to correctness** (conjunction is commutative) but is kept table-order for readability, with `default` last so its `Retry-After` is the least specific one a caller can see.
- `OnRejected` writes the 429 `ProblemDetails` with `code: rate_limited` and `Retry-After` from the rejecting member's `MetadataName.RetryAfter`; the §6.1 exception below is unchanged, because the members that can reject on `/auth/login` partition on IP and email hashes, never on account existence.

The numeric ceilings, the partition keys and the route assignments below are **unchanged** by this decision — only the wiring is.

**This table is the only place limits are stated (round-4).** The round-3 draft restated numbers in §6.1, §6.2, A-046 step 5 and four §7.1/§7.4 rows, and three of them disagreed with it — `/auth/login` was "10/15 min/IP+email" in §7.1 and "20/15 min/IP" in §6.1, `/auth/register` was "5/h/IP, 3/h/email" in A-046 but had no matching policy at all, and `/auth/magic-link` was "5/h/email, 20/h/IP" against an `auth-ip` policy of 20/**15 min**. An implementer wiring `RateLimitPolicies.cs` from the table would have shipped limits looser than the anti-enumeration prose assumes, and no test would have caught it. The table below absorbs every number that was only in prose; **every other section now cites a policy name and never a number**, and each route carries the complete set of policies that apply to it (a route under two policies must satisfy both).

| Dimension (chain member) | Limit | Route set the partition function matches (elsewhere: `GetNoLimiter`) |
|---|---|---|
| `auth-ip` | 20 / 15 min / IP | `POST /auth/login`, `POST /auth/magic-link/consume`, `POST /auth/password/reset`, `POST /auth/email/verify` |
| `login-email` | 10 / 15 min / email | `POST /auth/login` (in addition to `auth-ip` — this is §7.1's round-3 "10/15min/IP+email" made real as two chain members, both of which must permit the request; round-7) |
| `register-ip` | 5 / hour / IP | `POST /auth/register` (A-046 step 5) |
| `register-email` | 3 / hour / email | `POST /auth/register` (A-046 step 5) |
| `auth-email` | 5 / hour / email | `POST /auth/magic-link`, `POST /auth/password/forgot` |
| `magic-link-ip` | 20 / hour / IP | `POST /auth/magic-link` (in addition to `auth-email`) |
| `deletion-request` | 3 / hour / email **and** 10 / hour / IP | `POST /account-deletion-requests`. **Two chain members under one name** (`deletion-request-email`, `deletion-request-ip`), because one member has one partition key; the route must satisfy both, which is what the round-4 wording always meant and what the chain now makes literal (round-7). Every other row is a single member |
| `deletion-steam-ip` | **10 / hour / IP** (round-5) | `GET /account-deletion-requests/steam/start`, `GET /account-deletion-requests/steam/callback` — the two anonymous, antiforgery-exempt routes that mint a `DeletionRequestConfirmation` row; every other anonymous token-minting route already carried a policy and these did not |
| `intent-ip` | **30 / hour / IP** (round-5) | `POST /auth/external/intent` in **every** mode — the anonymous `mode=login` case is not covered by `reauth`, which partitions on an authenticated `UserId` that does not exist there, so the route was bounded only by `default` (300/min/IP) while every call inserts an `auth_tokens` row retained past its expiry (§9.7) |
| `email-send` | 3 / hour / user | `POST /auth/email/verify/request`, `POST /auth/reauth/email` |
| `reauth` | **5 / 15 min / user** | `POST /auth/reauth`, `POST /auth/reauth/email` (also `email-send`), `POST /auth/password/change`, `POST /auth/password/set`, `POST /auth/external/intent` (`mode=link` **and `mode=reauth`** — round-5, matching A-029's exemption rationale and A-049's proof-endpoint list; all three modes additionally carry `intent-ip`), `DELETE /auth/methods/{kind}`, **`DELETE /me/platform-links/{id}`** (round-5 — A-030 routes a Steam link into the same `UnlinkSteamUseCase` under the same A-049 proof), `POST /auth/email/change`, `POST /me/deletion` |
| `lookup` | 30 / min / user | username availability |
| `default` | 300 / min / IP | the whole `/api/v1` group — the one member whose partition function never returns `GetNoLimiter` |

**The `reauth` policy is not optional (round-3 addition).** The round-2 table covered none of the A-049 proof endpoints, so `default` (300/min/IP) was their only ceiling — a free ~300-guesses-per-minute password oracle against a known account, held by exactly the cookie thief A-049 exists to stop, with no lockout and no notification, and a password recovered there is then usable at `/auth/login`. Partition key is the **authenticated `UserId`** (hashed per the rule above), not the IP, because the attacker already holds the session and can change IPs freely. This is in addition to, not instead of, the shared Identity lockout counters A-049 item 1 requires `CheckPasswordAsync` to increment.

Rejections return 429 with `code: rate_limited` and `Retry-After`. **Exception, §6.1:** `POST /auth/login` never emits a lockout-derived `Retry-After` or any other header that distinguishes an existing account from an unknown one; only the IP/email-hash-partitioned limiter above may 429 there.

**`ForwardedHeaders` is part of this design, not a note (round-4 correction).** The round-3 draft said only "call it out in the compose/Caddy notes" and then assigned it to no slice and no owner action, which would have shipped the whole IP half of this section dead: behind Caddy every `auth-ip`, `register-ip`, `magic-link-ip`, `deletion-request` and `default` partition collapses to the single proxy container address (one attacker exhausts everyone's budget, and no attacker is ever limited relative to anyone else), and `auth_tokens.request_ip_hash` records the proxy on every row, making the "abuse forensics" justification for that column false. Concretely, in **slice 3**, next to `RateLimitPolicies.cs`:

- `app.UseForwardedHeaders(...)` runs **before** authentication, rate limiting and `UseSerilogRequestLogging`, with `ForwardedHeaders = XForwardedFor | XForwardedProto`.
- `KnownProxies`/`KnownNetworks` are **cleared and then set explicitly** to the compose network (the defaults trust only loopback, which is not where Caddy is); the network is configured, not hard-coded — `Network:TrustedProxies` (§13.1), empty in Development where there is no proxy.
- `ForwardLimit = 1`. Caddy sets exactly one hop, so anything further left in `X-Forwarded-For` is caller-supplied and must not be honoured — otherwise the header itself becomes the rate-limit bypass.
- Test: `RateLimitTests.PartitionKey_UsesTheForwardedClientIpNotTheProxy` (§14.3), which is also what stops a later middleware reorder from silently reverting this.

### 7.7 OpenAPI

- Cookie scheme documented via a document transformer adding `securitySchemes: { cookieAuth: { type: apiKey, in: cookie, name: pu.auth } }` and the antiforgery header as a documented parameter on unsafe operations.
- Every endpoint sets `.WithName(...)` (stable operationId), `.Produces<T>(...)` and `.ProducesProblem(...)` so the generated TypeScript is useful.
- `npm run gen:api` must be re-run at the end of each slice that adds endpoints; a stale `schema.d.ts` fails the web build.

---

## 8. Application layer

### 8.1 Use cases (`src/PartyUp.Application/`)

Plain classes, DI-registered, one public `ExecuteAsync` (A-003 — no MediatR). Folder per feature.

```
Application/
  Accounts/       RegisterWithPasswordUseCase, SignInWithPasswordUseCase, RequestMagicLinkUseCase,
                  ConsumeMagicLinkUseCase, RequestPasswordResetUseCase, ResetPasswordUseCase,
                  ChangePasswordUseCase, SetPasswordUseCase, RequestEmailVerificationUseCase,
                  VerifyEmailUseCase, ChangeEmailUseCase,
                  CompleteExternalSignInUseCase (Google + Steam share it),
                  PostAuthenticationGateUseCase (§6.6 steps 1–5 — the transaction and every domain
                                                 call; Api/Auth/AuthenticateAndSignIn.cs only wraps it),
                  ReauthenticateUseCase (A-049), IssueExternalIntentUseCase (A-050 — all three modes;
                                                 replaces round-2's IssueLinkIntentUseCase),
                  ListAuthMethodsUseCase, UnlinkAuthMethodUseCase, UnlinkSteamUseCase (A-030)
  Onboarding/     GetOnboardingStateUseCase, CheckUsernameAvailabilityUseCase,
                  ValidateProfileDraftUseCase, CompleteOnboardingUseCase
  Profile/        GetMyProfileUseCase, ChangeUsernameUseCase, ChangeDisplayNameUseCase,
                  ChangeAvatarUseCase, ChangeTimeZoneUseCase, ChangeLocaleUseCase,
                  SetReminderDefaultUseCase, LinkPlatformUseCase, ChangePlatformHandleUseCase,
                  UnlinkPlatformUseCase
  Deletion/       RequestAccountDeletionUseCase, RequestPublicDeletionUseCase,
                  IssueSteamDeletionConfirmationUseCase (§7.4 — mints the token, changes nothing),
                  ConfirmPublicDeletionUseCase (the only public path that calls RequestDeletion),
                  ExecuteDueDeletionsUseCase, ExecuteDeletionForUserUseCase,
                  PurgeExpiredAuthTokensUseCase (§9.7 — invoked by the sweep tick, by the admin
                                                 maintenance endpoint and directly by its test;
                                                 gated by Auth:TokenRetention, not by SweepEnabled)
  Admin/          ListUsersUseCase, BanUserUseCase, UnbanUserUseCase, ReadDeletionJournalUseCase
  Auth/           ISecurityTokenService, AuthTokenPurpose, IssueTokenRequest, RedeemTokenRequest,
                  PendingRegistration, ExternalIntentPayload, ExternalStartMode, IssuedToken,
                  TokenRedemption, TokenRedemptionFailure
  Text/           ITextPolicy, TextPolicyResult, TextField, ProfanityFilter (impl lives here — pure
                  code, no I/O)
  Email/          IEmailSender, IEmailDispatcher, EmailMessage, EmailTemplate,
                  IEmailTemplateRenderer, EmbeddedEmailTemplateRenderer (A-041 — pure, so it lives
                  in Application, not Infrastructure)
  Abstractions/   IUserRepository, IUsernameRegistryRepository, IUnitOfWork, IUserAccountGateway,
                  IAccountSessionService, IFriendCodeGenerator, IDeletionJournal, IDeletionScopeProvider,
                  ICurrentUserAccessor, CurrentUser, ICurrentUserProjectionReader (A-052),
                  CurrentUserProjection, ISteamProfileClient
```

### 8.2 Ports (interfaces in Application, implementations in Infrastructure)

```csharp
public interface IUserRepository {
    Task<User?> FindAsync(UserId id, CancellationToken ct);
    Task<User?> FindForUpdateAsync(UserId id, CancellationToken ct);       // SELECT … FOR UPDATE
    // Ids only, no tracked aggregates and no row lock — see A-037 (round-6). The lock is taken
    // per user by FindForUpdateAsync inside that user's own transaction (§9.4 step 1).
    Task<IReadOnlyList<UserId>> FindDueForDeletionAsync(Instant now, int max, CancellationToken ct);
    void Add(User user);
}

public interface IUsernameRegistryRepository {
    Task<UsernameRegistryEntry?> FindAsync(string canonical, CancellationToken ct);         // FOR UPDATE
    Task<UsernameRegistryEntry?> FindActiveForOwnerAsync(UserId owner, CancellationToken ct);
    void Add(UsernameRegistryEntry entry);
}

public interface IUnitOfWork {           // one explicit DATABASE transaction per use case — see A-053
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct);
}

/// Everything Identity-shaped, expressed without Identity types leaking into Application.
public interface IUserAccountGateway {
    Task<UserId?> FindUserIdByEmailAsync(string email, CancellationToken ct);
    Task<UserId?> FindUserIdByLoginAsync(string provider, string providerKey, CancellationToken ct);
    Task CreateAccountAsync(UserId id, string? email, bool emailConfirmed, string? password, CancellationToken ct);
    // HashVerified is true only when a stored hash existed AND was compared — false for a
    // password-less account and for the lockout short-circuit. §6.1's one-hash-per-request rule
    // is `if (!result.HashVerified) await VerifyDummyPasswordAsync(ct);` and nothing else.
    Task<PasswordCheckResult> CheckPasswordAsync(UserId id, string password, CancellationToken ct);
    Task VerifyDummyPasswordAsync(CancellationToken ct);   // timing equalization, §6.1 — called on
                                                           // exactly the branches where no real
                                                           // verification ran, never alongside one
    Task SetPasswordAsync(UserId id, string password, CancellationToken ct);
    Task RemovePasswordAsync(UserId id, CancellationToken ct);
    Task SetEmailAsync(UserId id, string email, bool confirmed, CancellationToken ct);
    Task ConfirmEmailAsync(UserId id, CancellationToken ct);
    Task<AccountEmailInfo?> GetEmailAsync(UserId id, CancellationToken ct);
    Task AddLoginAsync(UserId id, string provider, string providerKey, CancellationToken ct);
    Task RemoveLoginAsync(UserId id, string provider, CancellationToken ct);
    Task<IReadOnlyList<string>> ListLoginProvidersAsync(UserId id, CancellationToken ct);
    Task PurgeCredentialsAsync(UserId id, CancellationToken ct);      // tombstone step, §9.5
    Task EnsureRoleExistsAsync(string role, CancellationToken ct);    // A-039 (round-4): creates the auth_roles row if absent, idempotent
    Task<bool> AddToRoleAsync(UserId id, string role, CancellationToken ct);   // true iff the grant changed something (A-025 stamp rotation)
}

public interface IAccountSessionService {                 // security-stamp machinery (A-025)
    Task SignOutEverywhereAsync(UserId id, CancellationToken ct);
}

public interface IEmailSender {
    Task SendAsync(EmailMessage message, CancellationToken ct);
}
public sealed record EmailMessage(string ToAddress, Locale Locale, EmailTemplate Template,
                                  IReadOnlyDictionary<string, string> Model);

public interface IFriendCodeGenerator { Task<FriendCode> NextAsync(CancellationToken ct); }

public interface IDeletionJournal {
    void Append(DeletionJournalEntry entry);                                   // same tx (A-016)
    Task<IReadOnlyList<DeletionJournalEntry>> ReadSinceAsync(Instant since, int limit, CancellationToken ct);
}

public interface IDeletionScopeProvider {                 // Phase 2 impl returns username entry only
    Task<DeletionScope> LoadAsync(UserId id, CancellationToken ct);
}

public interface ICurrentUserAccessor {                   // scoped, request-cached (A-024)
    Task<CurrentUser?> GetAsync(CancellationToken ct);    // impl: Api/Auth (HttpContext-bound, A-025)
}

/// A-052: the read itself is a normal persistence port, implemented in
/// Infrastructure/Persistence/CurrentUserProjectionReader.cs — not in the composition root.
public interface ICurrentUserProjectionReader {
    Task<CurrentUserProjection?> ReadAsync(UserId id, CancellationToken ct);
}
public sealed record CurrentUserProjection(UserId Id, UserLifecycle Lifecycle, bool IsBanned,
                                           Instant? GraceEndsAt, bool IsAdmin, bool IsProfileComplete,
                                           Locale? Locale, string SecurityStamp);
public sealed record CurrentUser(UserId Id, UserLifecycle Lifecycle, bool IsBanned,
                                 Instant? GraceEndsAt, bool IsAdmin, bool IsProfileComplete,
                                 Locale? Locale);   // IsProfileComplete drives RequireOnboarded (A-026)

public interface ISteamProfileClient {                    // A-019; null when no API key configured
    Task<SteamProfile?> GetProfileAsync(string steamId64, CancellationToken ct);
}
```

`IClock` is NodaTime's, already injected everywhere in Domain (ARCHITECTURE §3). Register `SystemClock.Instance` in the composition root only.

### A-034 — NodaTime/BCL boundary: conversion happens inside the gateway implementations, never above them

`PartyUp.Application` and `PartyUp.Domain` see only `Instant`/`Duration`. Identity's `DateTimeOffset` (`LockoutEnd`) and `DateTime` never cross `IUserAccountGateway`, and its *column* mapping under the NodaTime plugin is pinned explicitly in §5.2 (an `Instant?` converter) rather than left to provider defaults. In Infrastructure, convert with `Instant.ToDateTimeOffset()` / `Instant.FromDateTimeOffset()`. The `auth_tokens` table maps `Instant` natively via Npgsql.NodaTime, so the custom token pipeline never touches BCL time at all. An analyzer-friendly guard: keep a `// BCL time is confined to this file` comment only where a real constraint exists (Identity's own model) — no changelog comments (CLAUDE.md rule 4).

### 8.3 Transaction boundaries and A-016

One transaction per use case, opened by `IUnitOfWork.ExecuteAsync`. What must be inside it:

| Use case | Inside the transaction | After commit |
|---|---|---|
| Register request (A-046) | the `auth_tokens` pending-registration row only — **no** `users` or `auth_users` write | send verification (or `ExistingAccountNotice`) email |
| Registration verification / magic-link create | token redemption, address re-resolution, domain `User` insert, `auth_users` insert, friend-code uniqueness retry, post-auth gate | cookie issue |
| Complete onboarding | `username_registry` claim (row-locked), `User.CompleteOnboarding` | – |
| Change username | old row `FOR UPDATE`, new row claim, `User.ChangeUsername` | – |
| Sign-in (any) | post-auth gate incl. `CancelDeletion` | cookie issue, "deletion canceled" email |
| Request deletion | `User.RequestDeletion`, security-stamp rotation | confirmation email (D-148 disclosure text) |
| Execute deletion | `DeletionExecutor.Execute`, tombstone, registry reservation, credential purge, **journal row** | offsite journal append (§9.6) |
| Ban / unban | `User.Ban`/`Unban`, stamp rotation | – |

**Mail leaves the request path (round-4).** Every "After commit" cell above that says *send / email / notify* means "hand the `EmailMessage` to `IEmailDispatcher`, which enqueues it on a bounded `System.Threading.Channels` queue and returns"; a single `EmailDispatchHostedService` drains the queue and calls `IEmailSender`. Two reasons, both load-bearing: §6.1's anti-enumeration equalization is only real if a known-address branch does not pay an SMTP round trip a no-account branch skips (A-040's MailKit adapter retries 3× with backoff, which is seconds of observable difference); and this table's own promise that a mail-provider outage must never roll back or stall an account creation was previously achieved only by ordering, not by design. The queue is deliberately lossy on process exit — the same recovery §8.3 already states applies (the user re-requests), and A-016 already says no email is crash-critical.

**A-016 in Phase 2:** the only crash-critical side effect that exists is the **deletion journal row**, and it commits in the same transaction as the tombstone. There are no notification records yet (their tables arrive with their phase), so nothing else qualifies. Emails are explicitly *not* transactional: they are a lossy, retriable transport, and a mail-provider outage must never roll back an account creation. Where email loss is user-visible (verification, magic link) the user can re-request; that is the designed recovery.

Security-stamp rotation lives inside the transaction because it *is* the D-165 effect; if the transaction rolls back, the user must not have been signed out.

### A-053 — `IUnitOfWork.ExecuteAsync` is a real database transaction on the same `DbContext` Identity writes through, and every `IdentityResult` failure aborts it

Round-4 review found the whole table above rests on an unstated guarantee. §8.2 defined `ExecuteAsync` with the comment "one explicit transaction per use case" and nothing else, while A-021 mandates the **default** `UserStore<AuthUser, AuthRole, AppDbContext, Guid>` used unmodified — and that store runs with `AutoSaveChanges = true`. Every `UserManager` call the gateway makes (`UpdateSecurityStampAsync`, `SetEmailAsync`, `RemoveLoginAsync`, `AddToRoleAsync`, the credential purge) therefore issues its **own** `SaveChangesAsync` mid-use-case. If `ExecuteAsync` were merely a deferred `SaveChangesAsync`, the stamp rotation and `PurgeCredentialsAsync` would commit independently of the journal insert and the domain tombstone — exactly the crash window A-016 exists to close — and would flush partially-mutated tracked aggregates early. Three rules, all testable:

1. **`UnitOfWork.ExecuteAsync` begins and commits an `IDbContextTransaction`** (`AppDbContext.Database.BeginTransactionAsync`), not just a `SaveChangesAsync`. It calls `SaveChangesAsync` once before commit for whatever the domain repositories tracked, and rolls back on any exception. Nesting is a no-op join: if a transaction is already open on the context, the inner `ExecuteAsync` enlists rather than opening a second one, so a use case that calls another cannot split the boundary.
2. **Every gateway implementation resolves the same scoped `AppDbContext`.** `UserManager`, `UserStore` and every repository are registered `Scoped` against one context instance per request, so Identity's auto-saves land inside the open transaction rather than in an ambient autocommit. Registration is stated in slice 2 (§15) and asserted by `UnitOfWorkTests.Rollback_UndoesAnIdentityAutoSave_D165` (§14.3): rotate a stamp and insert a journal row inside one `ExecuteAsync`, throw, and assert **both** are absent afterwards.
3. **`IdentityResult` failures become `DomainException`s.** Every `UserManager` call in `UserAccountGateway` checks `result.Succeeded` and throws on failure (mapping to `DomainError.Conflict` unless a more specific code applies) rather than returning silently. Without this, `PurgeCredentialsAsync` can no-op — e.g. on a concurrency-stamp mismatch, which `UserManager` reports as a failed `IdentityResult`, not an exception — while the tombstone and the journal row commit, leaving **live credentials on a "deleted" account**: the inverse of the §9.5 defect round 1 fixed. Test: `DeletionPurgeTests.Sweep_WithAFailingCredentialPurge_LeavesNoTombstone_D092` (§14.3).

### A-035 — Friend-code generation is a bounded retry against a unique index

`FriendCodeGenerator` draws 10 symbols from `RandomNumberGenerator`, checks `ux_users_friend_code`, and retries on unique violation up to 5 times before failing the request with 500. At 50 bits and friend-group scale a collision is a curiosity, and the DB index — not an app-side check — is the authority (ARCHITECTURE §3).

### A-036 — Text policy (profanity + shape) is an Application concern that runs before any domain call

`ITextPolicy.Check(string value, TextField field, Locale? locale) → TextPolicyResult` where `TextField ∈ {Username, DisplayName, GamingHandle}` (Phase 4/6 add `SessionTitle`, `SessionNotes`, `FreeTextGame`).

Pipeline:
1. **Shape** — Unicode letters/numbers plus a per-field punctuation allow-list (username: `._-`; display name: space `'`-`.`), no leading/trailing punctuation, no runs >2 of the same punctuation, no bidi/zero-width/format characters (`Cf`), NFC-normalized before storage. Lengths come from the domain value objects (Username 3–30, DisplayName 1–50, GamingHandle ≤64 — D-096).
2. **Normalize for matching** — NFKD → strip combining marks → `ToLowerInvariant` → map confusables/leet (`0→o, 1→i, 3→e, 4→a, 5→s, 7→t, @→a, $→s, ! → i`) → collapse repeated characters (`fuuuck→fuck`) → strip all non-alphanumerics.
3. **Match** — against three embedded wordlists `Application/Text/Wordlists/{en,pt-BR,es}.txt` plus `all.txt`, each line a normalized term, with a per-term mode prefix: `=term` (exact token match only) and bare `term` (substring). Substring mode is what catches `sh1tter`; exact mode is what stops the Scunthorpe problem for short/embeddable terms. **All locales are always checked** — a Portuguese slur in an English UI is still a slur (recipient locale is irrelevant here; this is not rendering).
4. Result: `Ok` or `Rejected(field, "errors.text.profanity")`.

**Timebox (the plan says so):** ~150–250 terms per language, sourced by hand, no external dependency, no admin editing UI, no ML. It exists chiefly for store review (D-096/D-097) and is documented as best-effort in the code's class doc.

**Ordering (recon #6):** the policy runs *before* `UsernameRegistry.Claim`, so a rejected name never creates a registry row and never consumes a reservation.

**Call sites are enumerated, not implied (round-2 correction).** The round-2 draft defined `TextField.GamingHandle` but annotated only the username and display-name endpoints, so the handle endpoints — which PRODUCT_SPEC §8.6 names as user-authored text and which D-096/D-097 exist for (store review) — had no stated obligation to call the policy, and §14.2's `TextPolicyTests` exercised the policy in isolation. The complete Phase 2 call-site list is: `POST /onboarding/validate`, `POST /onboarding/complete`, `PUT /me/username`, `PUT /me/display-name`, `POST /me/platform-links`, `PATCH /me/platform-links/{id}` — each invoking it **before** the corresponding domain call. Test: `TextPolicyTests.Check_RunsBeforeAGamingHandleReachesTheDomain_D096` asserts a rejected handle produces no `User.LinkPlatform` / `User.ChangePlatformHandle` call and no `platform_links` row, mirroring `..._RunsBeforeTheUsernameIsClaimed_D096`.

---

## 9. Account deletion skeleton

### 9.1 Request paths

1. **In-app** — `POST /api/v1/me/deletion` (authenticated, **and re-authenticated per A-049 item 1**). `User.RequestDeletion(clock)` → `PendingDeletion`, `GraceEndsAt = now + 7d`; rotate the security stamp (D-165 — every device signs out, including the caller's); commit; send the disclosure email (D-148/D-158: "signing in before <date> cancels this").

   **The address-less notification gap, stated explicitly (round-3).** An account with no address on file — the Steam-only D-078 class A-045 step 6 and A-023 exist to support — receives **no** `DeletionRequested` mail, because there is nowhere to send it (§11 has no recipient). Its only in-band signal is the sign-out, which is indistinguishable from routine expiry, and its only recovery is a login inside the seven days. This is why re-authentication on `POST /me/deletion` is load-bearing rather than a nicety: for this account class the proof at request time is the *only* control between a stolen cookie and irreversible tombstoning. It is stated here so that nobody later relaxes the re-auth on the grounds that "the confirmation email covers it" — for a whole account class, it does not.
2. **Public route** (D-072) — `POST /api/v1/account-deletion-requests` + `/confirm`, the token being the identity verification DOMAIN_MODEL §11 requires. Same domain call, same stamp rotation, same email.

### 9.2 Cancellation

Only via a successful login (§6.6 step 4). There is no cancel endpoint by design (§12-33). The confirmation email and the 202 response both state the mechanism.

### 9.3 During the grace period

Nothing happens (DOMAIN_MODEL §12-34). The user is `PendingDeletion`, still discoverable (`IsDiscoverable` includes it), simply signed out. No Phase 2 code has to do anything for this — it is the absence of behavior.

### A-037 — Grace expiry executes on an in-process `PeriodicTimer` hosted service, plus an admin-triggered endpoint; Hangfire stays deferred

`DeletionSweepHostedService : BackgroundService` in `PartyUp.Api`:

```
enabled by  Deletion:SweepEnabled     (default true; false in integration tests that drive it manually)
interval    Deletion:SweepInterval    (default 00:15:00)
batch       Deletion:SweepBatchSize   (default 50)
```

It resolves a scope per tick and calls `ExecuteDueDeletionsUseCase`, which reads the **ids** of users with `lifecycle = PendingDeletion AND grace_ends_at <= now` (the partial index) and then runs `ExecuteDeletionForUserUseCase` once per id, each in its own DI scope and its own transaction, logging and continuing on individual failure.

**How the batch and A-053 fit together, stated because they did not (round-6).** A-053 added two rules the sweep was never re-checked against: `IUnitOfWork.ExecuteAsync` opens a real `IDbContextTransaction`, inner calls *join* an open one rather than nesting, and it calls `SaveChangesAsync` once before commit "for whatever the domain repositories tracked". Against a batch that returns tracked `User` aggregates under a `FOR UPDATE SKIP LOCKED`, that produced three unresolved consequences — the batch lock is released at statement end if the read is outside a transaction (so the multi-instance claim was false), the N per-user transactions collapse into one if it is inside a transaction (so a single failure aborts the whole batch, no savepoints being specified, and "log and continue" silently loses every remaining user *and* rolls back journal rows already written), and, worst and independent of the choice, a rolled-back user's in-memory mutations (`MarkDeleted`, registry release) stay dirty on the **shared scoped** `AppDbContext` A-053 rule 2 mandates and are flushed by the *next* user's `SaveChangesAsync` — committing a tombstone whose `deletion_journal` row was rolled back, the exact A-016/A-017 pairing this design protects everywhere else. Therefore, all three stated as rules:

1. **`ExecuteDueDeletionsUseCase` opens no transaction of its own.** It does not call `IUnitOfWork.ExecuteAsync`; it is a loop.
2. **The batch query returns ids only** — no tracked aggregates, no `FOR UPDATE`, no `SKIP LOCKED` (§8.2's signature is `Task<IReadOnlyList<UserId>>`). The only row lock in the whole sweep is §9.4 step 1's `FindForUpdateAsync` inside the per-user transaction, which is also the only lock that could serialize two instances; the batch-level lock was redundant with it and, held or not held, misleading.
3. **Each iteration runs on a fresh DI scope**, hence a fresh `AppDbContext` and a fresh change tracker (`IServiceScopeFactory.CreateAsyncScope()` per id, resolving `ExecuteDeletionForUserUseCase` from it). A failed user therefore cannot leak tracked state into the next user's transaction, and rule 1's "join an open transaction" cannot reach across users because no two users share a context.

Test: `DeletionSweepTests.Sweep_WhereOneUserFails_CompletesTheOthersAndLeavesNoTombstoneWithoutItsJournalRow_D092` (§14.3) — a multi-user batch (the existing `DeletionSweepTests`/`DeletionPurgeTests` are all single-user and structurally cannot catch this) in which user N's credential purge throws; it asserts users N+1.. are tombstoned and journalled, user N is untouched, and that no `users` row is `Deleted` without a matching `deletion_journal` row.

Rationale, against A-013 and against D-148/D-158/D-165:
- A-013 forbids adding the **Hangfire packages** before their phase (Phase 4/6, when the session/poll sweepers ship). It does not forbid time-based execution. A 40-line `BackgroundService` adds zero dependencies and is deleted in Phase 4 when the recurring job replaces it (`RecurringJob.AddOrUpdate("deletion-sweep", …)` calling the very same use case — the use case is the durable artifact, the trigger is not).
- D-148 requires destructive effects **after** seven days; a deferred-until-next-login design would leave `PendingDeletion` accounts un-executed indefinitely, which is a compliance failure under Google Play's account-deletion requirements (spec §8.6) and would make the deletion journal useless. So "deferred sweep" is rejected.
- Idempotency makes the timer safe: `DeletionExecutor.Execute` over a tombstone is a documented no-op, and the journal row is keyed by `user_id`.
- Single instance (ARCHITECTURE §1/§7), so there is no multi-node contention; a future second instance is made harmless by the **per-user** `SELECT … FOR UPDATE` in §9.4 step 1 plus that step's idempotent "skip if not `PendingDeletion` or grace not elapsed" guard, not by anything the batch query does. (Round-6 correction: the round-5 text claimed `FindDueForDeletionAsync` uses `FOR UPDATE SKIP LOCKED`. It cannot usefully — see the box below.)
- `POST /api/v1/admin/maintenance/deletion-sweep` gives the operator an on-demand trigger for the restore-replay runbook and for the "grace already elapsed at login" path (§6.6 step 5), which enqueues a targeted `ExecuteDeletionForUserUseCase`.

### 9.4 Execution (`ExecuteDeletionForUserUseCase`)

One transaction per user:

1. `IUserRepository.FindForUpdateAsync(id)` — **the only row lock in the sweep** (A-037 round-6: the batch read takes none); skip if not `PendingDeletion` or grace not elapsed (idempotent).
2. `IDeletionScopeProvider.LoadAsync(id)` → **Phase 2 implementation** returns `new DeletionScope([], [], [], [], usernameEntry)`. Phases 3–6 replace the body as their tables land; the interface does not change. Document this in the implementation's class doc as a *current-state constraint* (allowed comment per CLAUDE.md rule 4), not as a changelog.
3. `DeletionExecutor.Execute(user, scope, clock)` — retires the username into the ownerless 30-day reservation (D-145/§12-36) and tombstones the user (PII nulled, friend code retired).
4. `IUserAccountGateway.PurgeCredentialsAsync(id)` (§9.5).
5. `IDeletionJournal.Append(result.Journal)` — **same transaction** (A-016).
6. Commit. Post-commit: offsite journal append (§9.6). No email — the account is gone and the address has been purged.

### 9.5 Identity purge at tombstone

The tombstone invariant is "no PII survives" (DOMAIN_MODEL §3). Identity holds the email, so `PurgeCredentialsAsync` must, in the same transaction, **in this order**:

- **First, while the address is still known:**
  `DELETE FROM auth_tokens WHERE user_id = @id OR email_normalized = @currentNormalizedEmail`.
  **Rows keyed to a *former* address are reached by the `user_id` arm, and that is a rule, not an accident (round-6 minor).** The address history is not stored, so a purge keyed on the *current* address alone would leave a raw former address in `auth_tokens.email_normalized` after an A-048 email change. The rule that closes it: **every token issued against a resolved account carries `user_id`, on anonymous routes too** — `RequestPasswordResetUseCase`, `RequestMagicLinkUseCase` (existing-account branch), `RequestEmailVerificationUseCase` and `RequestPublicDeletionUseCase` all resolve the account before issuing, so they set it; `EmailChange` sets it by construction. A `user_id IS NULL` row therefore means "this address never belonged to any account", which is exactly the set §9.7's retention purge owns and which no tombstone should have to reach. `DeletionPurgeTests.Sweep_LeavesNoAuthTokenRowForTheDeletedUser_D092` covers the former-address case by changing the address before requesting deletion.
  Round-1 review found the round-1 claim ("outstanding `auth_tokens` rows cascade-delete with the user FK; rows keyed only by email are deleted by `email_normalized` match") was wrong twice over: tombstoning is an **UPDATE** (`User.MarkDeleted`, `src/PartyUp.Domain/Users/User.cs:335` — the row and `Id` stay FK-stable *by design*), so `ON DELETE CASCADE` on `auth_tokens.user_id` never fires; and once the email has been nulled the `email_normalized` rows can no longer be matched, because the address is gone. The result was that `auth_tokens.email_normalized` — a **raw email address** — plus live and expired token rows survived every tombstone, breaking the invariant that D-092/§11 rests on. The ordering above is load-bearing and the §9.5 cascade claim is struck.
- Then `Email = NormalizedEmail = null`, `EmailConfirmed = false`
- `PasswordHash = null`
- delete every `auth_user_logins` row (Google/Steam)
- delete every `auth_user_tokens` row
- delete `steam_identities` row
- new `SecurityStamp` (any surviving cookie dies)
- **keep** the `auth_users` row and its `id`, and keep role rows (a banned admin tombstone should not silently become role-less) — the row is what makes a login attempt on the old address fail cleanly instead of 500-ing on a missing FK.

Tests: `DeletionPurgeTests.Sweep_LeavesNoAuthTokenRowForTheDeletedUser_D092` and `DeletionPurgeTests.Sweep_PurgesTheIdentityCredentials_D092` — both **§14.3**, not §14.2 (the §14 preamble moved them in round 2: the assertion is that no row in `auth_tokens` references the tombstoned user's id or former address, which needs real rows).

### 9.6 Offsite journal shipping

See A-038 below.

### 9.7 `auth_tokens` retention purge (round-4 addition)

§9.5 purges by *user*, which reaches nothing for the addresses that never became accounts (§5.3). A second, time-based purge therefore runs on the **existing** `DeletionSweepHostedService` tick (A-037, every 15 minutes — no new machinery, no new hosted service):

```sql
DELETE FROM auth_tokens
 WHERE expires_at < @now - @retention
    OR (consumed_at IS NOT NULL AND consumed_at < @now - @retention);
```

- Config: `Auth:TokenRetention` (default `30.00:00:00`), listed in §13.1. A value of zero disables the purge (documented, for a forensic investigation only).
- It runs in its own transaction, before the deletion batch, and its row count is logged. It is bounded by `ix_auth_tokens_expires_at`.
- **The purge is a use case, not hosted-service code, and it is not gated by `Deletion:SweepEnabled` (round-5).** `PurgeExpiredAuthTokensUseCase` (`Application/Deletion/`) is invoked from three places: the sweep tick, `POST /admin/maintenance/deletion-sweep`, and directly by `AuthTokenRetentionTests`. This matters because §14.3's `PartyUpApiFactory` sets `Deletion:SweepEnabled=false` for every integration test, so a purge living inside the gated tick would never run under test — a privacy-motivated retention control verified by a test that cannot execute it. Enablement is `Auth:TokenRetention > 0` alone; the flag decides only whether the *deletion batch* runs, and the hosted service still ticks when either is enabled.
- It deliberately keys on `expires_at`/`consumed_at`, not `created_at`: a live token is never deleted, so no in-flight recovery, verification or intent flow can be broken by the purge.
- The retention window exists so that abuse forensics (`request_ip_hash`) has something to look at at all; the privacy policy (§13.3 item 8) states it.
- Test: `AuthTokenRetentionTests.Purge_RemovesExpiredAndConsumedRowsIncludingPreAccountAddresses_D067` (§14.3) — asserts that a *pre-account* row (`user_id IS NULL`, an address that never registered) is gone after the window and that an unexpired unconsumed row survives.

### A-038 — The deletion journal is a Postgres table (source of truth) mirrored to an append-only NDJSON file shipped with the backups

A-017 requires the journal to be shipped **offsite with the backups** so that, after restoring a snapshot, deletions executed *after* the snapshot can be replayed. A table alone cannot do that: it lives inside the very dump being restored, so restoring wipes exactly the entries the replay needs. A file alone cannot do it either: it is not transactional with the tombstone.

Therefore, both:

1. `deletion_journal` table — written in the tombstone transaction (A-016), authoritative, `shipped_at NULL` until mirrored.
2. `FileDeletionJournalShipper` — post-commit, appends one JSON line per entry to `/data/deletion-journal/<yyyy-MM>.ndjson` (`{"userId":"…","executedAt":"…"}`), `fsync`s, then sets `shipped_at`. On every sweep tick it first re-ships any rows with `shipped_at IS NULL` (crash recovery). Appending an entry whose `userId` is already the last-seen for that month is skipped, so replay of a partial write is harmless.
3. The `/data/deletion-journal` directory joins the backup sidecar's offsite set (`deploy/` compose + `RUNBOOK-backup.md`), and `RUNBOOK-restore.md` gains the step: *after restore, `GET /api/v1/admin/deletion-journal?since=<snapshot instant>` on the restored instance returns nothing; read the NDJSON files instead and re-request+execute deletion for each `userId` newer than the snapshot* — via the admin endpoint plus the targeted executor. A-017's "personal data under LGPD/GDPR" note applies: the directory is mode 600 and covered by the privacy policy.

Config: `Deletion:JournalPath` (default `/data/deletion-journal`). If the path's parent does not exist (local dev), the shipper logs once at startup and no-ops — dev must work without the volume.

---

## 10. Admin foundations

### A-039 — The admin role is seeded from configuration at startup, idempotently

Round-1 review found the round-1 rule was a role-grant race: an email match against *any* `auth_users` row, re-checked on every startup, combined with a register endpoint that created unverified accounts on arbitrary addresses, meant whoever registered the owner's (publicly guessable) address first became `admin` at the next container restart — inheriting the user list, ban, deletion sweep and deletion journal. The indefinite re-check is what made the window unbounded. Corrected:

**Primary mechanism — `Admin:SeedUserIds`** (a string array of domain `UserId` GUIDs). A `UserId` can only be obtained by an operator reading the database or the admin user list, so it is unguessable and unclaimable. This is the configuration form the runbook and §13.3 item 5 instruct the owner to use.

**Fallback — `Admin:SeedEmails`**, retained for first-boot convenience, with hard preconditions:
- the matched `auth_users` row must have **`EmailConfirmed = true`**; and
- since A-046 no `auth_users` row exists at all until the mailbox is proven, an attacker cannot manufacture the match by registering.
- A match on an **unconfirmed** row is logged at **Error** (`"Admin seed email {EmailHash} matched an unconfirmed account; refusing to grant the admin role"`) and the grant is **refused**, permanently for that row — not deferred, not retried into a grant.
- An email with no account at all is logged at Warning and re-checked next startup (harmless: there is nothing to grant to).

On startup, an `AdminSeeder` hosted service (running after migrations) ensures the `admin` role row exists, then applies `SeedUserIds` and the surviving `SeedEmails` matches idempotently. **It does that through `IUserAccountGateway.EnsureRoleExistsAsync` (round-4).** The round-3 draft named the obligation with no port behind it: the gateway exposed only `AddToRoleAsync`, nothing could create an `auth_roles` row, and `RoleManager<AuthRole>` is an Identity type Application must never see (§8.2). Two registration consequences follow and are pinned in slice 2: Identity is registered as `AddIdentityCore<AuthUser>().AddRoles<AuthRole>().AddEntityFrameworkStores<AppDbContext>()` — without `.AddRoles<AuthRole>()`, `AddIdentityCore` registers no `IRoleStore`/`RoleManager<AuthRole>`, so `EnsureRoleExistsAsync` and `AddToRoleAsync` would have nothing to call and the seeder could not create or grant the role at all. **That, and only that, is why `.AddRoles` is mandatory (round-6 minor).** The round-4 rationale also said the missing role-aware claims factory would leave `ClaimTypes.Role` unemitted and "silently make `RequireAdmin` unsatisfiable for everyone" — stale since A-026's round-5 change: `RequireAdmin` authorizes on the per-request projection's `is_admin`, never on the role claim, so that half is now false and, left standing, points an implementer back at exactly the claim-based admin check A-026 removed. `ClaimTypes.Role` remains in the principal for diagnostics and for the documented OpenAPI scheme (A-024); nothing authorizes on it. **A grant that actually changes anything also rotates that user's security stamp, in the same transaction** (round-4, A-025): the validator is reject-only and never re-issues the principal, so `ClaimTypes.Role` is frozen for the life of a cookie; rotating the stamp rejects the stale principal on its next request and the next sign-in carries the role. An idempotent no-op grant rotates nothing, so restarts do not sign the owner out. No in-app role management (D-177). Removing an entry from config does **not** revoke — revocation is a deliberate SQL/ops action documented in the runbook, so a config typo cannot lock the owner out mid-incident. **Revocation takes effect on the next request regardless (round-5)**, because `RequireAdmin` authorizes on the per-request projection's `is_admin` (A-026), not on the frozen `ClaimTypes.Role`; the runbook step still rotates the revoked user's security stamp so any open session is signed out, mirroring the grant path. Tests: `AdminSeederTests.Seed_AssignsTheConfiguredAdminRole_D177`, `AdminSeederTests.Seed_RefusesAnUnconfirmedEmailMatch_D177`, `AdminSeederTests.Seed_PrefersSeedUserIds_D177`, `AdminSeederTests.Seed_RotatesTheStampSoTheRoleTakesEffect_D177`, `AdminSeederTests.Seed_CreatesTheAdminRoleRowWhenAbsent_D177` (round-4).

### 10.1 Ban / unban (D-180)

`BanUserUseCase`: load user `FOR UPDATE` → `user.Ban(clock)` → `IAccountSessionService.SignOutEverywhereAsync` (**the same D-165 machinery**, A-025) → commit. Effects, all already designed above:
- signed out everywhere — stamp rotation;
- login blocked — §6.6 step 2;
- hidden from lookup/discovery — **the three A-043 predicates, and `IsDiscoverable` is deliberately *not* one of them** (round-3 correction: this bullet was stale round-1 text). The change lands in `LookupVisibility.IsListed` (`candidate.IsDiscoverable && !candidate.IsBanned`), `User.CanBeFriended` (`IsDiscoverable && !IsBanned`) and an explicit **total** ban branch in `ProfileVisibility.For` that answers the banned case for every viewer and never falls through to the `IsListed` gate (round-5) — §4.1/A-043. **`User.IsDiscoverable` itself is left exactly as it is** (`Lifecycle is Active or PendingDeletion`, `src/PartyUp.Domain/Users/User.cs:85`): adding `&& !IsBanned` there would propagate through `CanBeFriended` (`:88`) and `ProfileVisibility.For`'s `IsListed` gate and 404 a banned user's profile to their own already-accepted friends — the round-1 finding A-043 exists to fix — and would break `ProfileVisibilityTests.For_KeepsABannedUsersProfileVisibleToAnExistingFriend_D180` (§14.1);
- content untouched — `IdentityMasking` deliberately unchanged;
- unban restores — `Unban()` clears `BannedAt`, nothing else to undo.

### 10.2 Optional content removal

D-180's optional "remove the user's content" is the D-092 effect steps without the tombstone. In Phase 2 there is no content: sessions, polls, friendships and participations have no tables. The endpoint therefore accepts the flag and returns **501 `not_implemented_in_phase`** when it is `true`, so the API contract is stable and the gap is loud. The phase that lands each entity extends `DeletionScope` loading; the executor already separates "effect steps" (1–5) from "tombstone" (6), so the content-removal path is `DeletionExecutor` steps 1–5 with the tombstone skipped — a small refactor (`DeletionExecutor.ExecuteEffectsOnly`) that Phase 8 completes. **Do not build it in Phase 2**; do write the 501 and the TRACEABILITY note.

---

## 11. Email

### A-040 — `IEmailSender` with a dev file/console sender and one SMTP adapter; no vendor SDK

The provider is the owner's later choice (Resend, Postmark, SES, Brevo — all speak SMTP). Binding the code to a vendor SDK now buys nothing and costs a rewrite if the choice changes. Implementations:

| Registration | When | Behavior |
|---|---|---|
| `LoggingEmailSender` | `Email:Provider = "log"` (default in Development) | renders the template, logs subject + body at Information, writes `.eml` to `Email:DropPath` (default `<content root>/mail-drop`) if writable. **Magic-link URLs are logged in Development only** — the sender asserts `IHostEnvironment.IsDevelopment()` and refuses to run otherwise (A-015: tokens are never logged in production). |
| `SmtpEmailSender` | `Email:Provider = "smtp"` | MailKit `SmtpClient`, STARTTLS, credentials from config; From `PartyUp <no-reply@mail.partyup.social>`, envelope sender on the same domain (SPF alignment), `List-Unsubscribe` omitted (all Phase 2 mail is transactional). Retries 3× with jittered backoff on 4xx/timeouts; a permanent 5xx logs at Error and gives up — no queue table in Phase 2. |

`Email:Provider = "smtp"` with no credentials must fail **at startup** (validated options), not at first send.

### A-041 — Templates are embedded resources rendered by a placeholder substitutor; recipient locale is a required argument

`Application/Email/Templates/{template}.{locale}.{part}` embedded as resources — e.g. `MagicLink.pt-BR.subject.txt`, `MagicLink.pt-BR.html`, `MagicLink.pt-BR.text`. `IEmailTemplateRenderer` substitutes `{{Key}}` tokens from `EmailMessage.Model` with HTML-encoding on the HTML part. Missing locale falls back to `en` (D-146). No Razor, no templating package: fourteen templates × three locales × three parts is data, not a rendering engine.

**The renderer lives in `PartyUp.Application`, not Infrastructure (round-2 correction).** `EmbeddedEmailTemplateRenderer` was filed under `Infrastructure/Email/` while its three D-numbered tests (`Render_UsesTheRecipientLocale_D060`, `Render_FallsBackToEnglish_D146`, `Render_PutsTheTokenInTheFragment_D067`) were filed under `tests/PartyUp.Application.Tests/`, which references only `src/PartyUp.Application` — so as designed they could not compile, and the repo has no `PartyUp.Infrastructure.Tests` project (ARCHITECTURE §2 defines exactly three test projects). The renderer is pure, I/O-free code reading embedded resources **from the Application assembly the templates already live in**, so Application is where it belongs; it is `Application/Email/EmbeddedEmailTemplateRenderer.cs`. Nothing else moves and no fourth test project is needed. The genuinely infrastructural half — `LoggingEmailSender`, `SmtpEmailSender` — stays in `Infrastructure/Email/` and is covered by the integration project's capturing sender. Every link a template emits carries the token in the **fragment** (A-044); `EmailTemplateRendererTests.Render_PutsTheTokenInTheFragment_D067` asserts no rendered output ever contains `?token=`.

Templates (D-060/D-146 — every one exists in en, pt-BR, es):

| Template | Trigger |
|---|---|
| `MagicLink` | magic-link request |
| `EmailVerification` | register (A-046 — the link that actually creates the account), resend |
| `EmailChangeRequested` | notice to the **previous** address when an email change is requested (A-048) |
| `EmailChangeVerification` | verification link to the **new** address (A-048) |
| `PasswordReset` | forgot password |
| `DeletionRequested` | grace notice sent only after deletion actually enters `PendingDeletion` (in-app request or confirmed public request); tokenless; contains the D-148/D-158 disclosure and `GraceEndsAt` formatted in the recipient's time zone and locale |
| `DeletionRequestConfirmation` | known-account leg of `POST /account-deletion-requests`; contains the public-request confirmation `Token` and starts no grace period by itself (§7.4) |
| `DeletionCanceled` | login canceled a pending deletion |
| `ExistingAccountNotice` | registration attempt against an existing address (anti-enumeration, §6.1) |
| `AuthMethodAdded` | a sign-in method was linked, or a first password was set (A-049) |
| `AuthMethodRemoved` | a sign-in method was unlinked (A-049) |
| `PasswordResetNoAccount` | `POST /auth/password/forgot` for an address with no account — the anti-enumeration counterpart that equalizes the two branches (§6.1 round-4) |
| `DeletionRequestNoAccount` | `POST /account-deletion-requests` for an address with no account — same reason (§7.4) |
| `ReauthenticationRequested` | `POST /auth/reauth/email` — the emailed A-049 proof branch 3, for accounts with neither a password nor a provider login |

**Closing correction (slice 13):** slice 8 split the former overloaded deletion mail into the two rows above. `DeletionRequestConfirmation` owns the public-request token; `DeletionRequested` is the tokenless grace notice and its `GraceEndsAt` model value is localized before rendering. The A-041 inventory is therefore fourteen templates, not thirteen.

Locale resolution order: the account's `Locale` → the request's `locale` field → `Accept-Language` (matched against `en`, `pt-BR`, `es`) → `en`. Dates in email bodies are formatted with NodaTime patterns in the recipient's `TimeZone` and locale culture.

DNS (owner action, §13): `mail.partyup.social` with SPF, DKIM and DMARC, inbox-tested — magic-link auth is dead if mail lands in spam.

---

## 12. Frontend (`web/`)

### 12.1 Routes (React Router v8, library mode)

```
/                       → redirect: authenticated+active → /app, onboarding → /onboarding, else /welcome
/welcome                Public landing + "Sign in / Create account"
/age-gate               D-127 step 1. Checkbox "I am at least 13 years old" (D-083/D-093). Gate result held
                        in sessionStorage and posted with register / external start. No birth date is collected.
/login                  email+password, "email me a link", Google, Steam. The provider buttons are
                        NOT links: each POSTs /auth/external/intent (antiforgery, with the age-gate
                        attestation from sessionStorage) and only then navigates to the returned
                        /start?intent=… — A-050 removes the bare-GET start entirely. A provider
                        button renders only when GET /auth/session's `providers` list contains it
                        (A-042, round-4), so an unconfigured provider is never a 404 the user finds
/register               email address only, no password field (A-046 round-2 — the credential is
                        chosen by whoever proves the mailbox); requires the gate → 202, shows
                        "check your inbox"; it does NOT sign in or advance to /onboarding
/auth/magic             reads location.hash (#token=…), replaceState-clears it before any render,
                        POSTs the token in the body (A-044 — never a query string)
/auth/callback          the ONLY landing for external AUTHENTICATION completions (A-045 step 5,
                        round-6 — §7.4's public-deletion callback is not one; it lands on
                        /account-deletion/confirm, the other hash-clearing route). It clears
                        location.hash with replaceState BEFORE any render, then reads ?status=,
                        refetches the session and the antiforgery token (A-031), and finally
                        navigates to ?next= (the server-sanitized destination) or to the lifecycle
                        default. On a mode=reauth return the cleared fragment held #reauth=<raw>,
                        which is kept in memory only (A-044/A-049) — never a query string, never
                        sessionStorage. No other route may be the target of an external
                        authentication completion
/auth/reset             reads #token=; new-password form
/auth/confirm-identity  A-049 proof branch 3 (round-4). Reads #token= from the ReauthenticationRequested
                        mail, replaceState-clears it before any render, holds it IN MEMORY ONLY (never
                        sessionStorage — it is an access-granting token, A-015), and renders the pending
                        /settings/account action in that same tab so the proof is spent where it arrived
/auth/verify-email      reads #token=; **collects the password** and completes a pending
                        registration in one POST (A-046 round-2)
/auth/email-change      reads #token=; confirms the new address (A-048)
/onboarding             wizard shell, step index derived from local draft + session state
  /onboarding/username        /onboarding/display-name        /onboarding/avatar
  /onboarding/time-zone       /onboarding/link-platforms      /onboarding/friends  (stub → Phase 3)
/app                    protected placeholder home (Phase 4 fills it)
/settings/profile       username (with cooldown state), display name, avatar, platform links
/settings/account       email, password, linked auth methods (D-087/D-137), language, time zone,
                        delete account. Every method change AND the delete-account action are preceded
                        by the A-049 re-auth prompt, which offers whichever of the three proof branches
                        the account can actually produce (current password; "sign in with Google/Steam
                        again"; or "email me a confirmation link" for a magic-link-only account) —
                        all three ship, A-048 step 1 / A-049 item 1
/account-deletion       public deletion-request form (D-072) — unauthenticated; offers both the email
                        route and "prove it with Steam" for email-less accounts (§7.4)
/account-deletion/confirm  reads #token= (from the email link **or** from the Steam-assertion
                        redirect, §7.4), replaceState-clears the fragment BEFORE any render and
                        holds the token in memory only (A-044 — the second hash-clearing route,
                        A-045 step 5), then requires an explicit confirming POST — the page is the
                        consent step, the assertion was only the identity step
```

Route protection: `<RequireAuth>` and `<RequireOnboarded>` wrappers read the session query and redirect (`/login?next=…`, `/onboarding`). Never render protected content while the session query is loading.

### 12.2 Data layer

- `web/src/api/client.ts` gains a middleware that (a) attaches `X-CSRF-TOKEN` from an **in-memory** request token — obtained from the `GET /auth/antiforgery` **response body** (A-031; the `pu.csrf` cookie is `HttpOnly` and unreadable by design) — on unsafe methods, fetching it on boot, **after every login / logout / verification redemption / external return** because the token is identity-bound, and again on 400 `antiforgery_failed`; (b) maps `ProblemDetails` into a typed `ApiError { status, code, errors }`; (c) on 401 invalidates the session query.
- `web/src/auth/useSession.ts` — `useQuery(['session'], …)` with `staleTime: 30_000`; mutations that change identity (`login`, `logout`, `completeOnboarding`, `requestDeletion`) call `queryClient.invalidateQueries({queryKey:['session']})`. **No global auth store** (ARCHITECTURE §5: server state in TanStack Query).
- **Provider buttons re-mint once on `intent_required` (round-6).** The A-050 flow is POST `/auth/external/intent` → navigate to `/start?intent=…`. Because `pu.intent` is a capped, last-write-wins list, two mints in flight at the same instant can lose a binder; the affected `/start` answers 400 `intent_required`, so the button re-mints and retries **exactly once** before surfacing an error. The retry is capped at one so a genuine attack (an intent minted in another browser, an injected cookie value) does not turn into a loop.
- **`/auth/callback` clears `location.hash` before it renders anything**, then reads `?status=` and navigates to `?next=` (A-045 step 5, A-049). It is the only route an external completion may land on.
- Onboarding draft: `useReducer` + `sessionStorage` (survives the external-provider round trip), submitted once at `/onboarding/complete`.
- Language: i18next `LanguageDetector` already gives D-146's browser default with `en` fallback; when authenticated, `useSession().profile.locale` overrides it via `i18n.changeLanguage`, and `/settings/account` writes back with `PUT /me/locale`.

### 12.3 Components

`web/src/features/{auth,onboarding,profile,settings}/`. Radix/shadcn primitives arrive with this phase (first real UI): `Button`, `Input`, `Label`, `Dialog`, `Select` (time zone), `Checkbox` (age gate), `Toast`. Accessibility is scope, not polish (CLAUDE.md): every field has a `<label>`, errors use `aria-describedby` + `role="alert"`, the wizard announces step changes via a polite live region, focus moves to the step heading on navigation, and the emoji-avatar picker is a keyboard-navigable `radiogroup`.

### 12.4 i18n keys

Split the single `common` namespace into: `common`, `auth`, `onboarding`, `profile`, `settings`, `errors`. `errors` keys mirror the API's `code`/`errors` values exactly (`errors.auth.lastMethod`, `errors.username.taken`, `errors.username.cooldown`, `errors.username.reserved`, `errors.text.profanity`, `errors.auth.banned`, …), so the SPA renders any server error by key lookup with a generic fallback. All three locale files must stay key-complete; add a Vitest that asserts `pt-BR` and `es` key sets equal `en`'s.

---

## 13. Configuration, secrets, and owner actions

### 13.1 Config keys (`appsettings.json` + env overrides `Section__Key`)

```jsonc
{
  "Auth": {
    "Cookie": { "Name": "pu.auth", "ExpireDays": 30 },
    "Password": { "MinimumLength": 12 },
    "Lockout": { "MaxFailedAttempts": 5, "LockoutMinutes": 15 },
    "TokenRetention": "30.00:00:00"                 // §9.7 auth_tokens purge; "00:00:00" disables it
  },
  "PublicBaseUrl": "https://partyup.social",       // used to build email links; dev: http://localhost:5173
  "Email": {
    "Provider": "log",                              // "log" | "smtp"
    "FromAddress": "no-reply@mail.partyup.social",
    "FromName": "PartyUp",
    "DropPath": "mail-drop",
    "Smtp": { "Host": "", "Port": 587, "User": "", "Password": "" }
  },
  "Google": { "ClientId": "", "ClientSecret": "" },
  "Steam": { "Enabled": true, "ApiKey": "", "Realm": "" },   // Enabled gates the two sign-in routes
                                                             // AND the providers list (§7.1); the
                                                             // public-deletion leg stays mapped;
                                                             // ApiKey is enrichment only, never a
                                                             // gate (A-042)
  "Admin": { "SeedUserIds": [], "SeedEmails": [] },   // A-039: prefer SeedUserIds; SeedEmails grants
                                                      // only against EmailConfirmed = true rows
  "Deletion": { "SweepEnabled": true, "SweepInterval": "00:15:00", "SweepBatchSize": 50,
                "JournalPath": "/data/deletion-journal" },
  "Security": { "PartitionPepper": "" },            // rate-limit/IP hashing pepper
  "Network": { "TrustedProxies": [] }               // round-4, §7.6: CIDR/addresses of the compose
                                                    // network Caddy runs on; empty in Development
}
```

### A-042 — Every external dependency is optional at startup; the app must run and be testable with none of them configured

- `Google:ClientId` empty → the Google handler is **not registered** and `/auth/external/google/start` is **not mapped** (404 — not a mapped route that 500s on a challenge against a missing scheme), and `GET /auth/session` reports `providers: ["password","magicLink"]` so the SPA hides the button. `providers` is a declared member of `SessionResponse` (§7.1, round-4 — the round-3 draft named the field here without putting it in the contract), computed at startup from configuration and returned on the anonymous response too.
- `Steam:ApiKey` empty → Steam **login still works** (A-019: OpenID verification is keyless); only the persona/avatar enrichment is skipped. Turning Steam *off* is a separate, explicit `Steam:Enabled = false`, which unmaps the two Steam **sign-in** routes and drops `steam` from `providers` together (round-6). The public-deletion leg `/account-deletion-requests/steam/{start,callback}` stays mapped regardless — it is the compliance route for accounts the deployment already created (§7.1, A-045 step 6). `Steam:Enabled` defaults to **true**, so a deployment that configures nothing still offers Steam login.
- `Email:Provider = "log"` → full magic-link/reset/verification flows work locally with the link in the console.
- `Admin:SeedUserIds` / `Admin:SeedEmails` empty → no admin exists; admin endpoints 404 for everyone.
- `Security:PartitionPepper` empty → a random per-process value is generated with a startup Warning (rate limiting still works; partitions just reset on restart).

This is what makes the whole phase implementable and CI-testable before the owner completes §13.3.

### 13.2 Secrets inventory

| Secret | Where it lives | Rotation |
|---|---|---|
| `Google__ClientSecret` | VM `.env` (mode 600), GitHub secret not needed (runtime only) | Google Cloud console; restart container |
| `Steam__ApiKey` | VM `.env` | steamcommunity.com/dev/apikey; **never log** (A-019) |
| `Email__Smtp__Password` | VM `.env` | provider console |
| `Security__PartitionPepper` | VM `.env` | any time; resets rate-limit partitions |
| Data Protection keys | `/data/dataprotection` volume | losing them signs everyone out; back the volume up |
| `ConnectionStrings__Db` | existing | – |

### 13.3 OWNER-ACTION list (only the human can do these)

| # | Action | What the code needs back | Blocking? |
|---|---|---|---|
| 1 | **OPEN —** Choose a transactional email provider and create the account (owner evaluating Resend/Postmark/SES) | SMTP host/port/user/password → `Email__Smtp__*`, and `Email__Provider=smtp` | Not for dev/CI (A-042). `Email:Provider=log` in production sends no real mail, so this blocks real magic-link/recovery mail. |
| 2 | **OPEN — follows #1:** Create `mail.partyup.social` DNS: SPF (`v=spf1 include:<provider> -all`), DKIM (provider-issued CNAME/TXT), DMARC (`v=DMARC1; p=quarantine; rua=…`), then **inbox-test** to Gmail/Outlook | nothing (DNS only) | Blocks go-live, not development |
| 3 | **OPEN —** Create a Google Cloud OAuth 2.0 Web client; authorized redirect URI `https://partyup.social/api/v1/auth/external/google/callback` (+ `http://localhost:5000/...` for dev) | `Google__ClientId`, `Google__ClientSecret` | Blocks Google login only |
| 4 | **DONE —** Obtain a Steam Web API key (needs a Steam account with ≥$5 spend) | `Steam__ApiKey` | Blocks Steam avatar/persona only — **not** Steam login |
| 5 | **OPEN —** Register the owner account, confirm its mailbox, read its `UserId` through an operator-controlled path, and configure it | `Admin__SeedUserIds__0` (preferred; `Admin__SeedEmails__0` is the confirmed-only fallback, A-039) | Admin endpoints return 404 for everyone until the production app container receives this value. |
| 6 | **DONE —** Complete the A-019 human pass: a real Steam login round trip, and confirm private-friends-list behavior | confirmation only | Recommended before slice 5 merges |
| 7 | **BLOCKED IN REPO —** Add `/data/deletion-journal` to the compose volumes and to the offsite backup set; update `RUNBOOK-backup.md`/`RUNBOOK-restore.md` | a durable app mount plus offsite copy | Blocks A-017 compliance at go-live. The closing audit found `deploy/compose.yaml` still mounts only Data Protection into the app; docs cannot truthfully mark this done. |
| 8 | **PHASE 8 —** Confirm the privacy policy covers the deletion journal as pseudonymous personal data (A-017) and the analytics posture (D-161) | nothing | Blocks go-live |
| 9 | **DONE —** Configure Caddy's access log to record the **path only**, not the raw URI with query string (A-044), and confirm no other proxy/CDN layer logs full URIs | nothing | Deployed Caddyfile scrubs query strings; done 2026-07-31. |
| 10 | **DONE —** Confirm Caddy sets `X-Forwarded-For`/`X-Forwarded-Proto` (its reverse-proxy default) and read back the compose network's subnet | `Network__TrustedProxies__0` (CIDR) for the VM `.env` — §7.6, round-4 | VM trusted-proxy configuration set; done 2026-07-31. |

**Closing status (slice 13):** items 9 and 10 are done as of 2026-07-31: the deployed Caddyfile scrubs query strings and the VM trusted-proxy configuration is set. The active Phase 2 owner inputs are the SMTP provider/DNS (#1–2), Google OAuth credentials (#3), and `Admin__SeedUserIds__0` (#5). Item 7 is separately code-blocked by missing Compose volume wiring and is called out in the backup/restore runbooks rather than being falsely marked complete.

---

## 14. Test plan

Naming convention is mandatory: `MethodUnderTest_ExpectedBehavior_D###` (`CA1707` is already suppressed for test projects).

**Test homes are a compile constraint, not a preference (round-2 correction).** ARCHITECTURE §2 defines exactly three test projects — `PartyUp.Domain.Tests`, `PartyUp.Application.Tests`, `PartyUp.Api.IntegrationTests` — and this design adds none. `tests/PartyUp.Application.Tests/PartyUp.Application.Tests.csproj` references only `src/PartyUp.Application` (verified), so it can see neither Api nor Infrastructure types, and it has no database, so it cannot assert row-level or concurrency behavior. Round-2 review found seven §14.2 entries violating that. Their dispositions, all applied below:

| Test | Round-2 problem | Disposition |
|---|---|---|
| `AdminSeederTests.Seed_*_D177` (3) | `AdminSeeder` is `Api/Hosting/AdminSeeder.cs` | Moved to **§14.3** (integration); they need a real `auth_users` row to assert the `EmailConfirmed` precondition anyway |
| `EmailTemplateRendererTests.*_D060/_D146/_D067` (3) | renderer filed under Infrastructure | **Renderer moved to `Application/Email/`** (A-041) — pure, I/O-free, and the templates are already Application resources. Tests stay in §14.2 |
| `UnlinkAuthMethodUseCaseTests.Execute_SerializesConcurrentUnlinks_D137` | annotated "(integration)" inside the in-memory section; it is the only check of §6.5's `FOR UPDATE` | Moved to **§14.3** |
| `ExecuteDueDeletionsUseCaseTests.Execute_PurgesTheIdentityCredentials_D092`, `..._LeavesNoAuthTokenRowForTheDeletedUser_D092` | §9.5 requires a real row assertion | Moved to **§14.3** |

| `SocialOnlyAccountTests.Execute_CompletesOnboardingAndReachesEveryMeSurfaceWithNoEmail_D078` | **round-3**: missed by the round-2 sweep, and it is the *sole* verification of D-078. "Reaches every `/me` surface" is an HTTP-level assertion the Application project structurally cannot make | **Split**: the onboarding half stays in §14.2 as `SocialOnlyAccountTests.Execute_CompletesOnboardingWithNoEmail_D078`; the reachability half becomes `SocialOnlyAccountReachabilityTests.EveryMeSurface_ForAnAddresslessAccount_Succeeds_D078` in **§14.3**, where a real `auth_users` row with a null email exists. §14.5's D-078 cell names both |
| `SecurityTokenServiceTests.Issue_DoesNotConsumeAnotherPartysOutstandingRecoveryToken_D067` | **round-4**: missed by both earlier sweeps. `SecurityTokenService` is `Infrastructure/Auth/SecurityTokenService.cs` (slice 4's own file list says so), so `PartyUp.Application.Tests` — one `ProjectReference`, to `src/PartyUp.Application` — cannot see the type. Consequence: the A-029 split concurrent-token rule, the round-2 fix that stops an attacker consuming a victim's live recovery link, had no writable test at the layer it was filed under | **Moved to §14.3**, together with the round-4 `Redeem_RefusesATokenOfAnotherPurpose_D067` and the single-use/TTL cases named in slice 4. They need real rows and a real `UPDATE … RETURNING` to prove single-use and the purpose predicate at all, which is the same reason the purge tests moved |
| `RegisterWithPasswordUseCaseTests.Execute_DoesNotRevealAnExistingAccount_D016`, `..._RejectsARequestCarryingAPassword_D016`, `SignInWithPasswordUseCaseTests.Execute_ReturnsTheSameFailureForUnknownWrongPasswordlessAndLockedOutAccounts_D067` | **round-4**: each asserts an HTTP-level fact — an identical **202 body** and **no `Set-Cookie`**; a **400** for a body carrying a password field (§6.1 step 1 makes that endpoint-level request validation; the use-case request record has no password property, so an Application test has nothing to reject); "the same status, the same `code`, and **the same header set**". Status codes, `Set-Cookie` and header sets are produced by `Api/Endpoints/*` and the cookie pipeline | **Split, each into a use-case half and an HTTP half.** §14.2 keeps only what the use case returns (identical result object on both register branches, and **no call to the session-issuing port** on the fake; identical failure *result* with **exactly one hash-cost verification** in each of the four login cases — round-7). The HTTP halves become `RegistrationEnumerationTests.Register_ForAKnownAndAnUnknownAddress_AreByteIdentical_D016`, `RegistrationEnumerationTests.Register_WithAPasswordField_Returns400_D016` and the existing `LoginEnumerationTests…_D067` in **§14.3** |

No fourth test project, and therefore no ARCHITECTURE §2 amendment.

**The general rule, so this stops recurring across rounds** (three sweeps have now moved eleven entries for the same cause): before filing a test in §14.2, check that every assertion in its *name* is expressible over a use-case's inputs and return value with in-memory fakes. Status codes, headers, cookies, redirect `Location`s, row-level state, concurrency and anything living in `Api/` or `Infrastructure/` belong in §14.3.

### 14.1 Domain tests (`tests/PartyUp.Domain.Tests/Users/`, `…/Visibility/`)

| Test | D |
|---|---|
| `UserBanTests.Ban_MarksTheUserBannedWithoutChangingTheLifecycle_D180` | D-180 |
| `UserBanTests.Ban_IsIdempotent_D180` | D-180 |
| `UserBanTests.Unban_RestoresLookupListing_D180` — **round-5 rename**. The old name (`…_RestoresDiscoverability_D180`) named `User.IsDiscoverable`, the one predicate A-043 and §10.1 explicitly forbid touching; what `Unban` restores is `LookupVisibility.IsListed` (and `CanBeFriended`, and the `ProfileVisibility` branch), which is what the test asserts | D-180 |
| `UserBanTests.MarkDeleted_KeepsTheBanOnTheTombstone_D180` | D-180 |
| `LookupVisibilityTests.IsListed_ExcludesABannedUser_D180` | D-180 |
| `ProfileVisibilityTests.For_KeepsABannedUsersProfileVisibleToAnExistingFriend_D180` (A-043 — pins the deliberate answer) | D-180 |
| `ProfileVisibilityTests.For_HidesABannedUserFromANonFriend_D180` (A-043 round-2 — asserts `Hidden`, which the specified branch now produces) | D-180 |
| `ProfileVisibilityTests.For_KeepsABannedUsersOwnProfileVisibleToThemselves_D180` | D-180 |
| `UserTests.CanBeFriended_IsFalseForABannedUser_D180` | D-180 |
| `IdentityMaskingTests.ForUser_DoesNotMaskABannedUser_D180` | D-180 |
| `UserDeletionTests.RequestDeletion_IsAllowedFromOnboarding_D072` (A-047) | D-072 |
| `UserDeletionTests.MarkDeleted_TombstonesAnAccountThatNeverOnboarded_D072` | D-072 |
| `UserDeletionTests.CancelDeletion_ReturnsAnOnboardingUserToOnboarding_D158` (A-047 round-2) | D-158 |
| `UserDeletionTests.CancelDeletion_ReturnsAnActiveUserToActive_D158` | D-158 |
| `UserDeletionTests.CancelDeletion_NeverProducesAnActiveUserWithAPartialProfile_D127` | D-127 |
| `FriendCodeFormatTests.IsWellFormed_RejectsAmbiguousSymbols_D144` | D-144 |

### 14.2 Application tests (`tests/PartyUp.Application.Tests/`, in-memory fakes for every port)

| Test | D |
|---|---|
| `RegisterWithPasswordUseCaseTests.Execute_AcceptsARegistrationRequestFromAnyone_D016` — **renamed (round-4)**. The round-1 name was `..._CreatesAnOnboardingAccountForAnyone_D016`, which asserts the behavior A-046 abolished: `POST /auth/register` writes **nothing** to `users` or `auth_users`, and the account is created only inside the `POST /auth/email/verify` transaction. It directly contradicted the two rows below it, and because it was the *first* D-016 entry it is the one §14.5 would have cited — training an implementer to pin register-creates-an-account, the exact precondition (an unverified `auth_users` row on an arbitrary address) that A-046 and A-045 step 1 exist to remove. It now asserts only that self-service registration is open to any address (D-016): the request is accepted and a token is issued | D-016 |
| `RegisterWithPasswordUseCaseTests.Execute_RejectsAnUnattestedAgeGate_D083` | D-083 |
| `VerifyEmailUseCaseTests.Execute_RecordsTheAgeGateSourceWithoutABirthDate_D093` — **round-5 move** (was `RegisterWithPasswordUseCaseTests…`, the same stale-after-A-046 defect class round 4 caught for D-016 and missed here). A-046 makes `POST /auth/register` write nothing to `users`/`auth_users`; the token row carries only `pending_locale` and `pending_age_gate`, and `IssueTokenRequest`/`PendingRegistration` carry no `AgeGateSource`. `AgeGateSource` is set exclusively by `User.BeginOnboarding`, which runs inside the `POST /auth/email/verify` redemption transaction — so this is the password-registration creation door, and it sits alongside `ConsumeMagicLinkUseCaseTests…_D093` and `CompleteExternalSignInUseCaseTests…_D093` as the other two | D-093 |
| `RegisterWithPasswordUseCaseTests.Execute_DoesNotRevealAnExistingAccount_D016` — **restated (round-4)** in terms the Application project can observe: the **identical result object** on both branches, **no call to the session-issuing port** on the fake, and **no `users` row and no `auth_users` row** through the fake gateway (A-046). The byte-identical-response and no-`Set-Cookie` half moves to §14.3's `RegistrationEnumerationTests` | D-016 |
| `RegisterWithPasswordUseCaseTests.Execute_CreatesNoAccountUntilTheMailboxIsProven_D016` | D-016 |
| `RegisterWithPasswordUseCaseTests.Execute_IssuesTheSameWorkOnBothBranches_D016` — **round-4**: one template render and one send on both branches (`EmailVerification` vs `ExistingAccountNotice`), so §6.1's cost-equality claim is pinned by a test rather than by prose. (The 400-for-a-password-field assertion is endpoint-level request validation and moves to §14.3 — the use-case request record has no password property to reject) | D-016 |
| `VerifyEmailUseCaseTests.Execute_CreatesTheAccountFromAPendingRegistration_D016` | D-016 |
| `VerifyEmailUseCaseTests.Execute_TakesThePasswordFromTheRedeemerNotTheRequester_D016` — a token issued in one context and redeemed in another yields the **redeemer's** credential; nothing on the token row can influence it (A-046 round-2) | D-016 |
| `SignInWithPasswordUseCaseTests.Execute_ReturnsTheSameFailureForUnknownWrongPasswordlessAndLockedOutAccounts_D067` — **four** cases (round-3), **restated round-4, corrected round-7**: the lockout branch is the fourth negative outcome and the use case must return the **identical failure result and `code`** for all four. The verification assertion is **exactly one hash-cost verification per request** (`realVerifies + dummyVerifies == 1` on the fake gateway, in all four negative cases and in the success case) — *not* "the dummy ran in all four", which pinned the wrong-password branch at two hashes and made an existing password account the slowest, measurable answer. The "same header set" half is HTTP-level and is carried by §14.3's `LoginEnumerationTests` (§6.1) | D-067 |
| *(The round-5 renames of the two `ResetPasswordUseCaseTests…` gate assertions landed on names the sign-in renames below already produce — `PostAuthenticationGateUseCaseTests.Execute_RefusesABannedAccount…_D180` and `…_CancelsAPendingDeletion…_D158`. **Consistency sweep: they are one test each, not two.** The gate is credential-path-agnostic by construction — §6.6 gives it one caller — so there is nothing a "reset" copy could assert that the entry below does not. The two surviving rows are `…_RefusesABannedAccountBeforeAnyCookie_D180` and `…_CancelsAPendingDeletionOnASuccessfulCredentialCheck_D158`; each credential use case keeps only its own `…_SignsInOnlyThroughTheGate_*` row.)* | – |
| `ResetPasswordUseCaseTests.Execute_SignsInOnlyThroughTheGate_D165` — **round-5 addition**: the reset use case emits a sign-in request that the adapter can only satisfy through `PostAuthenticationGateUseCase`, and issues nothing itself (§6.1's "the gate is not optional here", §6.6's one-door rule) | D-165 |
| `ConsumeMagicLinkUseCaseTests.Execute_SignsInWithoutAPassword_D067` | D-067 |
| `ConsumeMagicLinkUseCaseTests.Execute_RejectsAReusedToken_D067` | D-067 |
| `ConsumeMagicLinkUseCaseTests.Execute_AdoptsAnAccountCreatedAfterTheTokenWasIssued_D067` (§6.2) | D-067 |
| `ConsumeMagicLinkUseCaseTests.Execute_RejectsAnUnattestedAgeGateOnAccountCreation_D083` — the third and most subtle D-083 creation door (round-2 review): asserts **no `users` row, no `auth_users` row, and that the token is *not* consumed** — the use case passes `AgeGateAttested: false` and maps `TokenRedemptionFailure.RequiresAgeGate` to the 409, and the fake token service records that the row stayed live (round-7 restatement: the non-consumption is a property of the redeeming statement, not of an ordering convention — A-029/§6.2), so the SPA can retry with the same token | D-083 |
| `ConsumeMagicLinkUseCaseTests.Execute_RecordsTheAgeGateSourceWithoutABirthDate_D093` | D-093 |
| `ConsumeMagicLinkUseCaseTests.Execute_SkipsTheAgeGateWhenAdoptingAnExistingAccount_D083` — the adoption branch deliberately does not re-gate; pins that as intended, not an oversight | D-083 |
| `CompleteExternalSignInUseCaseTests.Execute_LinksGoogleToAMatchingVerifiedEmail_D067` (both sides verified) | D-067 |
| `CompleteExternalSignInUseCaseTests.Execute_RefusesToAutoLinkAnUnverifiedProviderEmail_D067` (A-045) | D-067 |
| `CompleteExternalSignInUseCaseTests.Execute_RefusesToAutoLinkAnUnconfirmedLocalAccount_D067` (A-045) | D-067 |
| `CompleteExternalSignInUseCaseTests.Execute_TreatsAnAbsentEmailVerifiedClaimAsUnverified_D067` — **round-7**: the input's `EmailVerified` is `false` when the claim is absent, empty or malformed, not only when it is literally `"false"` — which is the case the Google handler's default `ClaimActions` actually produce (§6.3). Neither auto-link nor create may run on it | D-067 |
| `CompleteExternalSignInUseCaseTests.Execute_RejectsAnUnattestedAgeGateOnProviderAccountCreation_D083` — **rewritten (round-3)**: asserts **no `User` row and no `auth_users` row**, *and* that the attestation is read **only** from the redeemed A-050 intent — a query-string `ageGateAttested=true` on `/start` cannot substitute for it. Round-2's version only rejected `ageGateAttested=false`, which an attacker forcing the navigation simply does not send | D-083 |
| `CompleteExternalSignInUseCaseTests.Execute_RecordsTheAgeGateSourceWithoutABirthDate_D093` (provider path) | D-093 |
| `CompleteExternalSignInUseCaseTests.Execute_CreatesAPlatformLinkAlongsideTheSteamAuthMethod_D032` | D-032 |
| `ChangeEmailUseCaseTests.Execute_RequiresTheCurrentPassword_D067` (A-048) | D-067 |
| `ChangeEmailUseCaseTests.Execute_NotifiesThePreviousAddress_D067` | D-067 |
| `ChangeEmailUseCaseTests.Execute_ConfirmsTheTokensAddressNotTheRowsAddress_D067` | D-067 |
| `ChangeEmailUseCaseTests.Execute_RotatesTheSecurityStampOnVerify_D165` | D-165 |
| `ListAuthMethodsUseCaseTests.Execute_ReportsEveryLinkedMethod_D087` | D-087 |
| `ListAuthMethodsUseCaseTests.Execute_ReportsMagicLinkForAConfirmedAddress_D067` — **round-5: added to the table**. §6.5 and §14.5 have cited it since round 4, but it appeared in no §14.2 row and no slice test line, so an implementer building from the inventory would have written none of the three magic-link-method tests. `magicLink` is `linked: true` iff `EmailConfirmed = true` and an address is on file, and `removable: false` | D-067 |
| `UnlinkAuthMethodUseCaseTests.Execute_CountsMagicLinkTowardTheLastMethodRule_D137` — **round-5: added to the table** (cited by §6.5/§14.5 since round 4). A magic-link-only account reports one method, not zero, and the count is computed over four kinds on every path that computes one | D-137 |
| `UnlinkAuthMethodUseCaseTests.Execute_RefusesToRemoveMagicLinkDirectly_D137` — **round-5: added to the table** (cited by §6.5/§14.5 since round 4): 400 `method_not_removable`, message key `errors.auth.methodNotRemovable`, nothing removed | D-137 |
| `UnlinkAuthMethodUseCaseTests.Execute_RemovesANonFinalMethod_D087` | D-087 |
| `UnlinkAuthMethodUseCaseTests.Execute_RefusesToUnlinkTheLastMethod_D137` | D-137 |
| `UnlinkAuthMethodUseCaseTests.Execute_RequiresReauthentication_D137` (A-049) | D-137 |
| `UnlinkAuthMethodUseCaseTests.Execute_NotifiesTheAccountAddress_D137` (A-049 `AuthMethodRemoved`) | D-137 |
| `UnlinkSteamUseCaseTests.Execute_ByEitherRoute_RemovesTheLoginTheLinkAndTheSteamIdentity_D032` (A-030 round-2) | D-032 |
| `UnlinkSteamUseCaseTests.Execute_ForASteamLinkRequestedAsAPlatformUnlink_StillRefusesTheLastAuthMethod_D137` — **round-6 rename** (was `…_ViaThePlatformLinkRoute_…`): an Application test cannot observe which *route* called it. The use case takes an `UnlinkOrigin` (`AuthMethod` \| `PlatformLink`) and the assertion is over that argument; the route wiring itself is `PlatformLinkRouteTests` in §14.3 | D-137 |
| `UnlinkSteamUseCaseTests.Execute_ForASteamLinkRequestedAsAPlatformUnlink_RequiresReauthentication_D137` — **round-5, renamed round-6** (A-030/A-049): same proof obligation whichever origin the request carries; the route half is `PlatformLinkRouteTests.Delete_ForASteamLink_Returns409WithoutProofAndRemovesAllThreeRowsWithIt_D137` (§14.3), which is where §7.3's contract and §7.6's `reauth` row are actually observable | D-137 |
| `ExternalIntentUseCaseTests.Execute_RequiresReauthenticationInLinkMode_D087` (A-049/A-050; renamed from `LinkIntentUseCaseTests`) | D-087 |
| `CompleteExternalSignInUseCaseTests.Execute_NotifiesTheAccountAddressWhenAMethodIsLinked_D087` — **moved off `ExternalIntentUseCaseTests` (round-4)**. `IssueExternalIntentUseCase` only mints a one-time `ExternalStartIntent` row; the login row is added later, in `CompleteExternalSignInUseCase`, and an intent is frequently minted and never redeemed (10-minute TTL, user abandons the provider hop). Pinning `AuthMethodAdded` on the intent use case would specify mail for flows that linked nothing **and** leave the path that actually links a method untested | D-087 |
| `SetPasswordUseCaseTests.Execute_NotifiesTheAccountAddress_D087` — **round-4**, the second half of A-049 item 3's "a sign-in method was linked, **or a first password was set**"; `SetPasswordUseCaseTests` previously carried only re-auth and stamp-rotation entries | D-087 |
| `UnlinkSteamUseCaseTests.Execute_NotifiesTheAccountAddress_D137` — **round-4**: `UnlinkSteamUseCase` is the shared path A-030 requires **both** unlink routes to use, including §7.3's `DELETE /me/platform-links/{id}`, and it carried no `AuthMethodRemoved` assertion at all | D-137 |
| `ExternalIntentUseCaseTests.Execute_SanitizesReturnToAtMintTime` (A-050) | – |
| `SocialOnlyAccountTests.Execute_CompletesOnboardingWithNoEmail_D078` — **split (round-3)**. It replaces the round-1 `SetPasswordUseCaseTests.Execute_AllowsASocialOnlyAccountWithoutARecoveryEmail_D078`, which asserted the opposite of §6.1's stated precondition. D-078 is "social-only accounts do not require a separate verified recovery email" (PRODUCT_SPEC:570) — the account must be fully usable with none. **The Application half asserts only what this project can see**: an address-less account completes onboarding and every use case it touches succeeds without an address. The *endpoint-reachability* half ("reaches every `/me` surface") is an HTTP assertion and moves to §14.3 (`SocialOnlyAccountReachabilityTests`), because `tests/PartyUp.Application.Tests/PartyUp.Application.Tests.csproj` references only `src/PartyUp.Application` — no Api, no Infrastructure, no database — and the assertion also needs a real `auth_users` row with a null email to be meaningful. Setting a *password* still needs an email, because login is email-keyed | D-078 |
| `ChangeEmailUseCaseTests.Execute_AddsAFirstAddressToAnAddresslessAccount_D067` (A-048 first-address case) | D-067 |
| `ChangeEmailUseCaseTests.Execute_SendsNoPreviousAddressNoticeWhenThereIsNone_D078` (A-048 first-address case) | D-078 |
| `CompleteExternalSignInUseCaseTests.Execute_WritesTheVerifiedGoogleAddressOnCreate_D067` (A-045 step 6) | D-067 |
| `CompleteExternalSignInUseCaseTests.Execute_RefusesToCreateAnAccountFromAnUnverifiedGoogleEmail_D067` — **round-6** (was `..._CreatesAnAddresslessAccountFromAnUnverifiedGoogleEmail_D078`): A-045 step 6 no longer creates an address-less Google account; the branch writes nothing and redirects `?error=link_required` | D-067 |
| `CompleteExternalSignInUseCaseTests.Execute_RefusesToCreateAnAccountWhenTheAddressWasTakenMeanwhile_D067` — **round-6** (was `..._CreatesAnAddresslessAccount…`): the race redirects `?error=email_unavailable` and writes nothing, so the partial unique index still never surfaces as a 500 *and* the race cannot manufacture an address-less Google account | D-067 |
| `CompleteExternalSignInUseCaseTests.Execute_CreatesAnAddresslessAccountFromASteamAssertion_D078` — **round-6**: the D-078 address-less class is now Steam's alone, and Steam is the provider that has a §7.4 assertion leg | D-078 |
| `CompleteExternalSignInUseCaseTests.Execute_NeverCreatesAnAddresslessAccountForAProviderWithNoPublicDeletionLeg_D072` — **round-6 blocking**: table-driven over the provider set; a create that leaves `Email IS NULL` is permitted only for a provider that has an assertion-based public deletion leg (§7.4). This is the artifact that keeps `PRODUCT_SPEC.md:457` reachable for every account class and fails the moment a provider is added without one | D-072 |
| `RequestAccountDeletionUseCaseTests.Execute_RequiresReauthentication_D165` (A-049 item 1, round-3) | D-165 |
| `SetPasswordUseCaseTests.Execute_RequiresAVerifiedEmail_D067` | D-067 |
| `SetPasswordUseCaseTests.Execute_RequiresReauthentication_D067` (A-049) | D-067 |
| `SetPasswordUseCaseTests.Execute_RotatesTheSecurityStamp_D165` (A-049) | D-165 |
| `RequestPasswordResetUseCaseTests.Execute_SendsRecoveryMailToAKnownAddress_D067` | D-067 |
| `CompleteOnboardingUseCaseTests.Execute_ActivatesTheAccountOnlyWithACompleteProfile_D127` — **renamed** (round-2): A-033 puts D-127's *ordering* on the client wizard and accepts all five fields in one atomic body, so a use-case test structurally cannot fail on order. What it can and does pin is the invariant: a missing field means no activation. The ordering half of D-127 is verified by `OnboardingWizard.test.tsx` (§14.4), and §14.5's traceability cell must name **both** | D-127 |
| `CompleteOnboardingUseCaseTests.Execute_ClaimsTheUsernameCaseInsensitively_D128` | D-128 |
| `ChangeUsernameUseCaseTests.Execute_ReservesTheFormerNameForThirtyDays_D145` | D-145 |
| `ChangeUsernameUseCaseTests.Execute_RejectsASecondChangeInsideTheCooldown_D128` | D-128 |
| `ChangeUsernameUseCaseTests.Execute_KeepsTheFriendCodeStable_D128` | D-128 |
| `CheckUsernameAvailabilityUseCaseTests.Execute_ReportsAReservedNameAsUnavailable_D145` | D-145 |
| `TextPolicyTests.Check_RejectsNormalizedProfanityInEveryLocale_D096` | D-096 |
| `TextPolicyTests.Check_RejectsPunctuationOutsideTheFieldAllowList_D096` | D-096 |
| `TextPolicyTests.Check_RunsBeforeTheUsernameIsClaimed_D096` | D-096 |
| `TextPolicyTests.Check_RunsBeforeAGamingHandleReachesTheDomain_D096` (A-036 round-2 — pins the `/me/platform-links` call sites) | D-096 |
| `ChangeLocaleUseCaseTests.Execute_OverridesTheDetectedLanguage_D146` | D-146 |
| `EmailTemplateRendererTests.Render_UsesTheRecipientLocale_D060` | D-060 |
| `EmailTemplateRendererTests.Render_FallsBackToEnglish_D146` | D-146 |
| `RequestAccountDeletionUseCaseTests.Execute_StartsASevenDayGrace_D148` | D-148 |
| `RequestAccountDeletionUseCaseTests.Execute_SignsOutEveryDevice_D165` | D-165 |
| `RequestAccountDeletionUseCaseTests.Execute_MailsTheCancellationDisclosure_D158` | D-158 |
| `PostAuthenticationGateUseCaseTests.Execute_CancelsAPendingDeletionOnASuccessfulCredentialCheck_D158` — **round-5 rename** (was `SignInWithPasswordUseCaseTests…`) | D-158 |
| `PostAuthenticationGateUseCaseTests.Execute_RefusesLoginAfterTheGraceElapsed_D148` — **round-5 rename** (was `SignInWithPasswordUseCaseTests…`); also asserts the targeted sweep is enqueued (§6.6 step 5) | D-148 |
| `PostAuthenticationGateUseCaseTests.Execute_RefusesABannedAccountBeforeAnyCookie_D180` — **round-5 rename** (was `SignInWithPasswordUseCaseTests…`) | D-180 |
| `SignInWithPasswordUseCaseTests.Execute_SignsInOnlyThroughTheGate_D180` — **round-5 addition**, the sign-in half of the same split: on a correct password the use case returns an admit-through-the-gate result and never a session of its own, which is what makes §6.6's "no path reaches a cookie without the gate" checkable at this layer | D-180 |
| `ConfirmPublicDeletionUseCaseTests.Execute_VerifiesIdentityBeforeStartingTheGrace_D072` | D-072 |
| `RequestAccountDeletionUseCaseTests.Execute_DeletesAnAccountAbandonedDuringOnboarding_D072` (A-047) | D-072 |
| `ExecuteDeletionForUserUseCaseTests.Execute_TombstonesOnlyAnElapsedGrace_D148` — **consistency sweep**: all three of these assert the *per-user* transaction (§9.4 steps 1–6), and A-037's round-6 rules make `ExecuteDueDeletionsUseCase` a bare loop over ids that opens no transaction and resolves each user from a fresh DI scope — so the loop class can observe none of this with in-memory fakes. The loop's own behavior (fresh scope per id, continue on failure) is `DeletionSweepTests.Sweep_WhereOneUserFails_…_D092` in §14.3 | D-148 |
| `ExecuteDeletionForUserUseCaseTests.Execute_AppendsTheJournalEntry_D092` — same move; the journal append is §9.4 step 5, inside the per-user transaction | D-092 (skeleton) |
| `ExecuteDeletionForUserUseCaseTests.Execute_IsIdempotent_D148` — same move; idempotency is §9.4 step 1's "skip if not `PendingDeletion` or grace not elapsed" guard plus `DeletionExecutor`'s own no-op over a tombstone | D-148 |
| `PostAuthenticationGateUseCaseTests.Execute_ReturnsAnOnboardingUserToOnboardingOnCancel_D158` (§4.1/A-047 round-2) | D-158 |
| `BanUserUseCaseTests.Execute_SignsOutEveryDevice_D180` | D-180 |
| `UnbanUserUseCaseTests.Execute_RestoresAccess_D180` | D-180 |
| `ListUsersUseCaseTests.Execute_ReportsBanState_D179` | D-179 (partial) |

### 14.3 Integration tests (`tests/PartyUp.Api.IntegrationTests/`, Testcontainers + real migrations)

All `[Trait("Category","Integration")]`. A shared `PartyUpApiFactory : WebApplicationFactory<Program>` with a `PostgreSqlContainer`, `Database:MigrateOnStartup=true`, `Deletion:SweepEnabled=false`, `Email:Provider=log` with a capturing in-memory sender, and a fake `IClock` addressable per test.

| Test | Covers |
|---|---|
| `MigrationTests.Migrate_CreatesEverySchemaObject` | migration list §5.4 |
| `UsernameUniquenessTests.Claim_RejectsACaseVariantAtTheDatabaseLevel_D128` | DB constraint, not app check |
| `AntiforgeryTests.Post_WithoutTheHeader_Fails` / `_WithTheHeader_Succeeds` | A-031 |
| `AuthCookieTests.Login_IssuesAnHttpOnlyCookie` / `Logout_ClearsIt` | A-028 |
| `RegistrationFlowTests.Register_ThenCompleteOnboarding_ActivatesTheAccount_D127` | end-to-end |
| `MagicLinkFlowTests.RequestThenConsume_SignsIn_D067` (reads the token from the capturing sender) | D-067 |
| `MagicLinkFlowTests.Consume_Twice_Fails_D067` | single-use (A-029) |
| `SecurityStampTests.RequestDeletion_InvalidatesAnExistingCookie_D165` | A-025/D-165 |
| `SecurityStampTests.Ban_InvalidatesAnExistingCookie_D180` | D-180 |
| `LoginCancelsDeletionTests.Login_DuringGrace_CancelsAndRestores_D158` | D-158 |
| `DeletionSweepTests.Sweep_AtGraceExpiry_TombstonesAndJournals_D148` | A-037 |
| `SteamCallbackTests.Callback_WithAForgedAssertion_IsRejected_D067` (stubbed OP endpoint via a test `HttpMessageHandler`) | §6.4 |
| `SteamCallbackTests.Callback_WithClaimedIdStrippedFromOpenIdSigned_IsRejected_D067` — the OP stub answers `is_valid:true` (as real Steam would), and the callback must still refuse because `openid.signed` omits `claimed_id`/`identity` | §6.4 step 4 |
| `SteamCallbackTests.Callback_WithAWrongOpenIdMode_IsRejected` | §6.4 step 4 |
| `SteamCallbackTests.Callback_WithAMismatchedReturnTo_IsRejected` | §6.4 |
| `ExternalStateTests.Callback_WithoutTheStateCookie_IsRejected` — the Steam legs only; Google's leg has no `pu.ext`, it uses the framework correlation cookie (`ExternalRedirectTests.GoogleChallenge_SetsTheCorrelationCookieSameSiteLax`) | §6.4 / A-051 |
| `ExternalStateTests.AuthStateCookie_IsRejectedByTheDeletionCallback` / `..._DeletionStateCookie_IsRejectedByTheAuthCallback` — a `pu.ext` minted by `/auth/external/steam/start` must not satisfy the deletion callback, and vice versa (cross-flow state confusion) | A-051 |
| `ExternalIntentTests.Start_WithoutAnIntent_IsRejected_InEveryMode` — `login`, `link` and `reauth`, both providers; 400 `intent_required` | A-050 |
| `ExternalIntentTests.Complete_WithAMismatchedIntentMode_LinksNothing_D087` — **round-6** (replaces `Start_WithALoginIntentInLinkMode_IsRejected`, which was unwritable: `/start` has no requested mode to compare an intent against). Two cases at `/complete`, where A-045 step 4's per-mode table lives: a `login` intent arriving under an authenticated cookie must not reach the link branch, and a `link` intent whose bound `UserId` is null must fail with `?error=` — both link nothing and create nothing | A-050 / A-045 step 4 |
| `ExternalIntentTests.Start_ConsumesTheIntentOnce` | A-050 |
| `ExternalIntentTests.Start_IgnoresQueryStringModeReturnToAndAttestation` — the attacker-controllable copies must have no effect | A-050 / D-083 |
| `ExternalRedirectTests.NoRedirectLocationFromAnExternalLegContainsARawTokenInItsQuery` — every `Location` from `/start`, the provider callbacks and `/complete` (including the `mode=reauth` completion, which must use `#reauth=`) | A-044 / A-049 |
| `ExternalRedirectTests.EveryCompletionLandsOnTheHashClearingCallbackRoute` — **round-6**: every external *authentication* completion emits `Location` path `/auth/callback` (the sanitized destination riding as `?next=`), and the one non-auth leg (§7.4's public-deletion callback) emits `/account-deletion/confirm` — so a fragment-carried token can never land on a route with no hash handling | A-044 / A-045 step 5 |
| `PlatformLinkRouteTests.Delete_ForASteamLink_Returns409WithoutProofAndRemovesAllThreeRowsWithIt_D137` — **round-6**: the HTTP half of the two renamed `UnlinkSteamUseCaseTests` cases; asserts §7.3's body contract, the 409 `reauth_required`, the `reauth` policy, and that a non-Steam platform link needs no proof | A-030 / A-049 |
| `SocialOnlyAccountReachabilityTests.EveryMeSurface_ForAnAddresslessAccount_Succeeds_D078` — **moved here from §14.2 (round-3)**; needs a real `auth_users` row with a null email and real HTTP | D-078 |
| `MigrationTests.Migrate_RoundTripsAnIdentityLockoutEnd` — the §5.2 `DateTimeOffset? ↔ Instant?` converter under `UseNodaTime()` | §5.2 |
| `ReauthenticationTests.Reauth_SharesTheLoginLockoutCounters_D067` — failures at `/auth/reauth` lock the account for `/auth/login` too, and the locked-out answer is indistinguishable | A-049 / §6.1 |
| `ReauthenticationTests.RequestDeletion_WithoutProof_Returns409_D165` (A-049 item 1, round-3) | D-165 |
| `LoginEnumerationTests.Login_AgainstALockedOutAccount_IsIndistinguishableFromAnUnknownAddress_D067` — same status, `code` and header set as the unknown-address branch | §6.1 |
| `ExternalReturnToTests.Complete_WithABackslashRelativeReturnTo_RedirectsToTheDefault` (`/\evil.com`, `/%09/evil.com`, CR/LF) | A-045 step 5 |
| `RequestLoggingTests.Log_NeverContainsAQueryString` — drives `/auth/magic?token=x` through the SPA fallback and asserts the Serilog sink recorded no `token` | A-044 |
| `AntiforgeryTests.Get_ReturnsTheRequestTokenInTheBody` / `_TheCookieIsHttpOnly` | A-031 |
| `OnboardingDeletionTests.RequestDeletion_WhileOnboarding_StartsTheGrace_D072` | A-047 |
| `RateLimitTests.Login_BeyondTheWindow_Returns429` | §7.6 |
| `AdminAuthorizationTests.Admin_ForANonAdmin_Returns404_D177` | D-177 |
| `AdminSeederTests.Seed_AssignsTheConfiguredAdminRole_D177` — **moved here from §14.2** (`AdminSeeder` is `Api/Hosting/`) | D-177 |
| `AdminSeederTests.Seed_RefusesAnUnconfirmedEmailMatch_D177` (A-039; needs a real `auth_users` row) | D-177 |
| `AdminSeederTests.Seed_PrefersSeedUserIds_D177` | D-177 |
| `UnlinkAuthMethodConcurrencyTests.Unlink_TwoOverlappingTransactions_LeaveOneMethod_D137` — **moved here from §14.2**; the only verification of §6.5's `FOR UPDATE` | D-137 |
| `DeletionPurgeTests.Sweep_LeavesNoAuthTokenRowForTheDeletedUser_D092` — **moved here from §14.2**; asserts no `auth_tokens` row references the tombstoned user's id **or former address** (§9.5 ordering) | D-092 (skeleton) |
| `DeletionPurgeTests.Sweep_PurgesTheIdentityCredentials_D092` — **moved here from §14.2** | D-092 (skeleton) |
| `PublicDeletionRouteTests.RequestThenConfirm_StartsTheGrace_D072` | D-072 |
| `PublicDeletionRouteTests.SteamAssertion_ForAnEmaillessBannedAccount_MintsAConfirmationTokenWithoutStartingTheGrace_D072` — the callback must be **non-destructive**: after it, the account is still `Active` (round-2) | D-072 / spec §10 |
| `PublicDeletionRouteTests.SteamAssertion_ThenConfirm_StartsTheGrace_D072` | D-072 |
| `PublicDeletionRouteTests.SteamAssertion_IssuesNoCookie_D180` | D-180 |
| `OnboardingGuardTests.ProtectedEndpoint_WhileOnboarding_Returns403_D127` | A-026 |
| `OnboardingGuardTests.ProtectedEndpoint_ForAnIncompleteProfileInPendingDeletion_Returns403_D127` (A-026/A-047 round-2) | A-026 |
| `LoginCancelsDeletionTests.Login_DuringGrace_ForAnOnboardingAccount_ReturnsToOnboarding_D158` (A-047 round-2) | D-158 |
| `ReauthenticationTests.Unlink_LinkIntent_AndSetPassword_AllRequireReauth_D067` (A-049) | D-067 |
| `ReauthenticationTests.ExternalReauth_WithAnUnlinkedProviderIdentity_MintsNoToken_D067` — **round-4 blocking**: a `reauth` intent bound to the victim, completed with the attacker's *own* provider account, must mint nothing, link nothing and create nothing (`?error=reauth_identity_mismatch`) | A-045 step 4 |
| `ReauthenticationTests.ExternalReauth_CompletedUnderAnotherUsersCookie_MintsNoToken_D067` — the symmetric case: intent minted by A, completed in a browser holding B's `pu.auth` | A-045 step 4 |
| `ReauthenticationTests.ReauthToken_ReplayedByAnotherUser_IsRejected_D067` — the row's `user_id` is re-checked against the calling principal at replay; 409 `reauth_required`, not a distinct code | A-045 step 4 |
| `ReauthenticationTests.EmailedProof_LetsAMagicLinkOnlyAccountSetAPassword_D087` — **round-4**: an account with a confirmed address, no hash and no login row uses `POST /auth/reauth/email` and completes `POST /auth/password/set`; without proof branch 3 this account 409s forever | A-049 / D-087 |
| `ExternalIntentTests.Start_WithAnIntentMintedInAnotherBrowser_IsRejected` — **round-4 blocking**: no cookie sharing between the minting and redeeming clients; 400 `intent_required`, the row is **not** consumed, and no provider redirect is emitted | A-050 |
| `ExternalIntentTests.Start_RemovesOnlyTheUsedBinderFromTheIntentCookie` — **round-5** (replaces `…Start_DeletesTheIntentCookie`): each binding is single-use on both halves, siblings survive, and the cookie is deleted when the list empties | A-050 |
| `ExternalIntentTests.Start_WithTwoConcurrentlyMintedIntents_BothSucceed` — **round-5**: the multi-tab case A-029's consumption exemption promises and the round-4 single-valued cookie broke | A-050 / A-029 |
| `ExternalIntentTests.Start_WithAnInjectedIntentCookieValue_IsRejected` — **round-5**: a caller-supplied `pu.intent` value (the cookie-injection primitive A-031 rejects the double-submit pattern for) matches no stored `binderHash`; 400 `intent_required`, nothing consumed | A-050 / A-031 |
| `SecurityTokenServiceTests.Redeem_RefusesATokenOfAnotherPurpose_D067` — **round-4**: a `MagicLink` value presented at the password-reset redemption, and an `ExternalStartIntent` value at the deletion confirm, both fail `NotFound` and consume nothing | A-029 |
| `SecurityTokenServiceTests.Redeem_WithAMismatchedBinder_ConsumesNothing_D067` — **round-6**: the A-050 binder is a predicate in the redeeming `UPDATE`, so a wrong or absent binder answers `NotFound`, leaves `consumed_at` null and is indistinguishable from an unknown intent; this is the port-level half of `ExternalIntentTests.Start_WithAnIntentMintedInAnotherBrowser_IsRejected`. **Round-7: the compared value is the base64url text `payload->>'binderHash'`, written at issuance from `ExternalIntentPayload.BinderHash`** — the round-6 `byte[]` parameter would have bound as `bytea` and never matched | A-029 / A-050 |
| `SecurityTokenServiceTests.Redeem_ForAPreAccountTokenWithoutAttestation_RefusesWithoutConsuming_D083` — **round-7**: a `MagicLink` row with `user_id IS NULL`, `pending_age_gate = false` and no `auth_users` row on its address, redeemed with `AgeGateAttested: false`, answers `RequiresAgeGate` and leaves `consumed_at` **null**, so the same raw token still redeems on the retry. Also asserts the arm is inert for `AgeGateAttested: null` (an `ExternalStartIntent` for `mode=login` is unaffected) | A-029 / §6.2 / D-083 |
| `SecurityTokenServiceTests.Redeem_WithAnAttestation_ConsumesThePreAccountToken_D083` — **round-7**: the same row with `AgeGateAttested: true` (and, separately, a row whose own `pending_age_gate` is true with `AgeGateAttested: false`) is consumed exactly once; a second concurrent redemption of either loses, proving the CTE kept single-use | A-029 / D-083 |
| `GoogleClaimMappingTests.Callback_ProjectsEmailVerifiedOntoThePrincipal_D067` — **round-7**: with a stubbed Google token/userinfo response carrying `email_verified: true`, the external principal reaching `/auth/external/complete` has an `email_verified` claim; the test fails if `ClaimActions.MapJsonKey("email_verified", …)` is dropped from `GoogleAuthSetup`, which is the state the framework defaults leave the app in | §6.3 / A-045 step 1 |
| `SecurityTokenServiceTests.Issue_DoesNotConsumeAnotherPartysOutstandingRecoveryToken_D067`, `..._Redeem_IsSingleUse_D067`, `..._Redeem_RefusesAnExpiredToken_D067` — **moved here from §14.2 (round-4)**; `SecurityTokenService` lives in Infrastructure and single-use is a property of a real `UPDATE … RETURNING` | A-029 |
| `AuthTokenRetentionTests.Purge_RemovesExpiredAndConsumedRowsIncludingPreAccountAddresses_D067` — **round-4**: §9.7; a pre-account row (`user_id IS NULL`) for an address that never registered is gone after the window, an unexpired unconsumed row survives | §9.7 |
| `RegistrationEnumerationTests.Register_ForAKnownAndAnUnknownAddress_AreByteIdentical_D016` — **moved here from §14.2 (round-4)**: identical status, body and header set, and no `Set-Cookie` on either branch | A-046 |
| `RegistrationEnumerationTests.Register_WithAPasswordField_Returns400_D016` — **moved here from §14.2 (round-4)**: §6.1 step 1 is endpoint-level request validation | A-046 |
| `ForgotPasswordEnumerationTests.Forgot_ForAKnownAndAnUnknownAddress_AreIndistinguishable_D067` — **round-4**: identical response and exactly one captured send on both branches (`PasswordReset` vs `PasswordResetNoAccount`) | §6.1 |
| `PublicDeletionRouteTests.Request_ForAKnownAndAnUnknownAddress_AreIndistinguishable_D072` — **round-4**: same, with `DeletionRequestConfirmation` vs `DeletionRequestNoAccount` | §7.4 |
| `UnitOfWorkTests.Rollback_UndoesAnIdentityAutoSave_D165` — **round-4**: rotate a stamp and insert a journal row inside one `ExecuteAsync`, throw, and assert both are absent; proves `ExecuteAsync` is a real `IDbContextTransaction` on the same scoped context Identity's `AutoSaveChanges` writes through | A-053 |
| `DeletionPurgeTests.Sweep_WithAFailingCredentialPurge_LeavesNoTombstone_D092` — **round-4**: a failed `IdentityResult` inside `PurgeCredentialsAsync` must abort the transaction rather than commit a tombstone with live credentials | A-053 |
| `DeletionSweepTests.Sweep_WhereOneUserFails_CompletesTheOthersAndLeavesNoTombstoneWithoutItsJournalRow_D092` — **round-6**: the only multi-user sweep test. User N's purge throws; N+1.. must still be tombstoned and journalled, N must be untouched, and no `users` row may be `Deleted` without its `deletion_journal` row — the failure a shared change tracker across per-user transactions would produce | A-037 / A-053 / A-016 |
| `SecurityStampTests.Response_ForAValidSession_CarriesNoValidatorIssuedSetCookie` — **round-4, renamed round-6**: a freshly issued cookie (well inside the `SlidingExpiration` halfway point, so the handler has no renewal of its own to write) produces no `Set-Cookie` on subsequent authenticated responses; the assertion is about the validator, not about A-028's sliding renewal | A-025 / A-028 |
| `SecurityStampTests.Response_ForAPrincipalWithNoProjectionRow_IsRejected` — **round-5**: a `null` projection is a mismatch, not a pass; the cookie is rejected and every `Require*` policy fails closed | A-025 |
| `AdminAuthorizationTests.Admin_AfterAnOutOfBandRoleRevocation_Returns404_D177` — **round-5**: delete the `auth_user_roles` row with a live cookie in hand; the next request 404s because `RequireAdmin` reads the per-request projection's `is_admin`, not the frozen `ClaimTypes.Role` | A-026 / A-039 |
| `SecurityTokenServiceTests.Issue_DoesNotConsumeAnotherPartysOutstandingDeletionConfirmation_D072` — **round-5**: a second `POST /account-deletion-requests` naming the same address, and a Steam-leg mint for the same user, both leave the first confirmation token redeemable (A-029's anonymously-initiated bucket) | A-029 / D-072 |
| `RateLimitTests.AnonymousExternalIntent_IsBoundedByIntentIp` — **round-5**: `POST /auth/external/intent {mode:"login"}` from one IP is limited by `intent-ip`, not by `default` alone, so anonymous `auth_tokens` minting is bounded | §7.6 |
| `AdminSeederTests.Seed_RotatesTheStampSoTheRoleTakesEffect_D177` — **round-4**: a grant that changes something rotates the stamp, so the stale non-admin principal is rejected on its next request; an idempotent re-run rotates nothing | A-025 / A-039 |
| `AdminSeederTests.Seed_CreatesTheAdminRoleRowWhenAbsent_D177` — **round-4, assertion corrected round-6 (n9)**: on a database with no `auth_roles` row the seeder creates it via `EnsureRoleExistsAsync` and grants it, asserted over the **rows** — one `auth_roles` row with `normalized_name = 'ADMIN'` and one `auth_user_roles` row for the seeded user — which is also what makes the caller's next request satisfy `RequireAdmin` through A-024's `is_admin` `EXISTS`. It is **not** asserted over `ClaimTypes.Role`: A-026 authorizes on the projection, and pinning the claim here would re-argue the frozen-claim revocation hole A-026 closed. It still fails if `.AddRoles<AuthRole>()` is missing from the slice-2 registration, because `EnsureRoleExistsAsync` then has no `IRoleStore` to call | A-039 / A-026 |
| `RateLimitTests.PartitionKey_UsesTheForwardedClientIpNotTheProxy` — **round-4**: two callers behind one trusted proxy get independent budgets; an `X-Forwarded-For` hop beyond `ForwardLimit = 1` is ignored, so the header cannot be used to escape a limit | §7.6 |
| `RateLimitTests.EveryDimensionCoversItsDocumentedRoutes` — **round-4, restated round-7** (was `EveryPolicyNamedInTheDesignIsRegistered`, which could pass while a ceiling was unreachable — named policies replace rather than compose). Asserts over `RateLimitPolicies`' declared chain: every §7.6 dimension is a member of the global chained limiter, each member's route-set predicate matches exactly the operation ids §7.6 lists for it, and **no endpoint in the app carries an `EnableRateLimitingAttribute`** (a `RequireRateLimiting` anywhere would silently replace a ceiling) | §7.6 |
| `RateLimitTests.Login_ExhaustsBothTheIpAndTheEmailCeiling` — **round-7**: the two-ceiling case made observable. From one IP, 10 failed logins against **one** address 429 on `login-email` while a *different* address from the same IP still succeeds; and 20 across *many* addresses 429 on `auth-ip`. Under the round-6 named-policy wiring one of the two would have been unenforced | §7.6 |
| `SessionEndpointTests.Session_ForAnAnonymousCaller_ListsTheConfiguredProviders` — **round-4**: `providers` is present on the `{authenticated:false}` body and omits `google` when `Google:ClientId` is empty | A-042 |
| `SessionEndpointTests.Session_ListsSteamWithNoSteamApiKeyConfigured_D067` — **round-6**: with `Steam:ApiKey` empty and `Steam:Enabled` at its default, `providers` still contains `steam` and `/auth/external/steam/start` is mapped; with `Steam:Enabled=false` the entry and the two sign-in routes disappear together **while `/account-deletion-requests/steam/start` stays mapped** (§7.1 — A-045 step 6's invariant must not be revocable by configuration) | A-042 / D-067 / D-072 |
| `ExternalRedirectTests.GoogleChallenge_SetsTheCorrelationCookieSameSiteLax` — **round-4**: the correlation cookie matches `pu.auth` and `pu.ext`, and no Phase 2 cookie is `SameSite=None` | §6.3 / A-028 |

### 14.4 Web tests (Vitest + RTL)

| Test | Covers |
|---|---|
| `AgeGate.test.tsx` — blocks continue until the checkbox is ticked (D-083) | D-083 |
| `OnboardingWizard.test.tsx` — steps appear in the D-127 order and cannot be skipped | D-127 |
| `UsernameStep.test.tsx` — renders `errors.username.taken` / `.reserved` / `.cooldown` from the API `code` | D-128/D-145 |
| `AuthMethods.test.tsx` — last-method unlink shows the explicit error from the API `code` (D-137). **The "button is not pre-disabled" assertion is dropped**: D-087 permits the absence of a warning, it does not forbid one, so pinning that UX choice with a test would encode a constraint the spec does not state | D-137 |
| `Register.test.tsx` — submitting shows "check your inbox" and does **not** navigate to `/onboarding` (A-046) | D-016 |
| `LanguageSwitch.test.tsx` — browser default, `en` fallback, account override (D-146) | D-146 |
| `DeleteAccount.test.tsx` — the dialog states the 7-day grace and the login-cancels rule before confirming | D-148/D-158 |
| `PublicDeletionPage.test.tsx` — **round-5** (§7.4 places both public pages in Phase 2): the request form offers the email route and "prove it with Steam"; the confirm page reads `#token=`, clears it before render, never renders or re-URLs the token, and POSTs only on explicit confirmation | D-072 |
| `i18n.keys.test.ts` — `pt-BR` and `es` key sets equal `en`'s | D-060 |

### 14.5 TRACEABILITY.md updates (same commits as the tests)

- Add missing rows: **D-177, D-178 (Phase 8), D-179, D-180**.
- **D-179 and D-180 get the same partial-completion treatment as D-092** (round-1 finding: rows backed by `ListUsersUseCaseTests.Execute_ReportsBanState_D179` and the D-180 ban tests would otherwise read as complete):
  - **D-179 → `2 (partial) / 8`**, note: "Phase 2 ships ban state and identity columns; the per-user stats D-179 requires (registered date, last activity, sessions organized and joined, friend count — PRODUCT_SPEC §10) need tables that do not exist until Phases 3–6 and are verified in Phase 8."
  - **D-180 → `2 (partial) / 8`**, note: "Phase 2 ships ban/unban, sign-out, login block and lookup exclusion; the Confirmed optional content-removal path returns 501 `not_implemented_in_phase` until Phase 8 (§10.2)."
- **Every instruction in this section is *additive* (round-6).** Most of these decisions already carry a Phase-1 row in TRACEABILITY.md with a passing D-numbered domain test — verified: `TRACEABILITY.md:119` (D-092, Domain, phase **1**, `FriendshipTests…_D092` +43), `:120` (D-093, phase **1**, `UserOnboardingTests.BeginOnboarding_CarriesTheAgeGateSignalWithoutABirthDate_D093` +1), `:171` (D-144, phase **1**, `ProfileValueObjectTests.FriendCode_CanonicalisesToUpperCaseSoLookupIsCaseInsensitive_D144`), `:175` (D-148, phase **1**, +15), `:185` (D-158, phase **1**, +5). An instruction below that names a Phase-2 artifact means **append it and keep the existing phase marker and citation**; none of them means *replace*. Deleting a Phase-1 citation would both drop a real verification and claim Phase-2 delivery for work Phase 1 already shipped — the same partial-completion defect class the D-092/D-179/D-180 bullets exist to prevent.
- Update the Phase/Verification cells for D-016, D-032, D-067, D-078, D-083, D-087, D-093, D-096, D-127, D-128, D-137, D-144, D-145, D-146, D-148, D-158, D-165 to name the new tests **alongside the ones already there**. **D-093 and D-144 were missing from this list (round-5)** although Phase 2 adds tests carrying both numbers:
  - **D-093's cell keeps its Phase-1 domain citation (`UserOnboardingTests.BeginOnboarding_CarriesTheAgeGateSignalWithoutABirthDate_D093`, `TRACEABILITY.md:120`) and appends all three Phase-2 age-gate-source doors**, each on the use case that actually calls `User.BeginOnboarding`; the phase marker becomes `1 (domain) / 2 (doors)`: `VerifyEmailUseCaseTests.Execute_RecordsTheAgeGateSourceWithoutABirthDate_D093` (round-5 — moved off `RegisterWithPasswordUseCaseTests`, which after A-046 creates nothing), `ConsumeMagicLinkUseCaseTests…_D093` and `CompleteExternalSignInUseCaseTests…_D093`.
  - **D-144's cell keeps `ProfileValueObjectTests.FriendCode_CanonicalisesToUpperCaseSoLookupIsCaseInsensitive_D144` (`TRACEABILITY.md:171`, Phase 1) and appends `FriendCodeFormatTests.IsWellFormed_RejectsAmbiguousSymbols_D144`** (§14.1). The marker becomes **`1 (format) / 3 (QR + lookup)`**, **not** `2` (round-6): D-144's actual subject is "QR codes use the stable friend code rather than a username-based profile link", and Phase 2 delivers neither QR nor the lookup surfaces — it only generates, stores and exposes the code (§4.3/A-035, `GET /me`). Marking the row `2` would assert delivery of something no phase-2 artifact provides while erasing the citation that is actually green.
- **The D-148/D-158/D-180 cells must name `PostAuthenticationGateUseCaseTests.*` — *in addition to* their existing Phase-1 domain citations, not instead of them (round-5, corrected round-6: `TRACEABILITY.md:175` and `:185` are Phase-1 rows with 15 and 5 passing domain tests; the instruction was only ever about which *Phase-2* class to cite, and "must name" read as replacement)** — and never the sign-in/reset use-case classes: §6.6 gives the ban, tombstone, cancel and elapsed-grace logic to `PostAuthenticationGateUseCase` and to one caller, so a cell citing `SignInWithPasswordUseCaseTests.Execute_RefusesABannedAccount_D180` would name a class that cannot execute the behavior. The credential use cases contribute only their `…_SignsInOnlyThroughTheGate_*` assertions.
- **D-127's cell names two artifacts, not one** (round-2): `CompleteOnboardingUseCaseTests.Execute_ActivatesTheAccountOnlyWithACompleteProfile_D127` for the server-side invariant and `OnboardingWizard.test.tsx` for the step *ordering*, which A-033 deliberately puts on the client. A cell naming only the Application test would read as verified by a test that structurally cannot fail on order.
- **D-083's cell names all three account-creation doors**: `RegisterWithPasswordUseCaseTests..._D083`, `CompleteExternalSignInUseCaseTests..._D083` and `ConsumeMagicLinkUseCaseTests..._D083` (round-2: the magic-link door was unverified).
- **D-092 must not be marked done**: mark it **`1 (domain) / 2 (skeleton) / 8 (verify)`** (round-6 — `TRACEABILITY.md:119` already carries it as Phase 1 with `FriendshipTests…_D092` +43 more, and the `DeletionExecutor` those tests cover is the artifact §2 and §9.4 of this design depend on; a bare `2 (skeleton) / 8` would erase a verified half) with the verification note "Phase 1 verified the domain executor; Phase 2 covers request→grace→tombstone→journal only; per-entity effects land with their owning phases."
- D-072: mark it `2` — **round-5**: the API half *and* both public pages (`/account-deletion`, `/account-deletion/confirm`) ship in Phase 2, because A-044 makes the confirm page the only consumer of the Steam leg's fragment token and without it the banned/email-less path PRODUCT_SPEC:457 guarantees is unreachable. The cell names `PublicDeletionRouteTests.*` (§14.3), `PublicDeletionPage.test.tsx` (§14.4) **and `CompleteExternalSignInUseCaseTests.Execute_NeverCreatesAnAddresslessAccountForAProviderWithNoPublicDeletionLeg_D072` (§14.2, round-6)** — the last is what keeps `PRODUCT_SPEC.md:457` ("a banned user can still request account deletion through the public route") true for *every* account class rather than for the two the route happens to serve, by refusing to create an account class the public route cannot reach. Phase 8 retains only the footer/store-listing surfacing of the route. IMPLEMENTATION_PLAN.md:125 is amended in the same commit.
- **D-067's cell must name the magic-link method artifacts (round-4)**: `ListAuthMethodsUseCaseTests.Execute_ReportsMagicLinkForAConfirmedAddress_D067` and `ReauthenticationTests.EmailedProof_LetsAMagicLinkOnlyAccountSetAPassword_D087`. PRODUCT_SPEC:559 lists passwordless email as an authentication method in its own right, and until round 4 no artifact verified that the design modelled it as one.
- **D-137's cell must name `UnlinkAuthMethodUseCaseTests.Execute_CountsMagicLinkTowardTheLastMethodRule_D137`** (round-4), because the last-method count is now computed over four kinds, not three, and the previous count was wrong in both directions for email-confirmed accounts.
- **D-016's cell must cite `RegisterWithPasswordUseCaseTests.Execute_AcceptsARegistrationRequestFromAnyone_D016`**, not the round-1 `..._CreatesAnOnboardingAccountForAnyone_D016`, which asserted the behavior A-046 abolished; and it must also name §14.3's `RegistrationEnumerationTests` for the HTTP-level anti-enumeration half.
- **D-078's cell names two artifacts** (round-3): `SocialOnlyAccountTests.Execute_CompletesOnboardingWithNoEmail_D078` (§14.2) **and** `SocialOnlyAccountReachabilityTests.EveryMeSurface_ForAnAddresslessAccount_Succeeds_D078` (§14.3). A cell naming only the Application test would claim endpoint reachability is verified by a project that cannot make an HTTP request.

---

## 15. Implementation slices

Each slice is independently reviewable, ends green (`dotnet build` 0 warnings, `dotnet test`, `npm run lint && build && test`), and carries its own migration where applicable. Slices 1–9 are backend-ordered by dependency; 10–12 are the React slices and may overlap 6–9.

**Slice 1 — Persistence foundation for the `User` aggregate, plus the ban domain delta**
Files: domain `User.BannedAt`/`Ban`/`Unban`/`CanBeFriended`, `RequestDeletion` accepting `Onboarding` (A-047), `UserEvents` additions, `LookupVisibility.IsListed` and `ProfileVisibility.For` edits (A-043), DOMAIN_MODEL.md updated in the same commit; `src/PartyUp.Infrastructure/Persistence/Converters/{TextValueConverter,TypedIdConverter,DateTimeZoneConverter}.cs`, `Persistence/Configurations/{UserConfiguration,PlatformLinkConfiguration,UsernameRegistryEntryConfiguration}.cs`, `AppDbContext` (add `DbSet`s + `OnModelCreating` + `UseXminAsConcurrencyToken`), migration `AddUserAggregate` (**includes `banned_at`** — §5.4).
Tests: all §14.1 domain tests (D-180, D-072/D-144), `MigrationTests`, `UsernameUniquenessTests` (…_D128).
*Why the domain ban delta is here and not in slice 9:* slices 2–8 read `banned_at` through A-024's projection and A-026's policies; see §5.4.

**Slice 2 — Identity schema + cookie pipeline**
Packages: `Microsoft.AspNetCore.Identity.EntityFrameworkCore`. Files:
- **Application (round-3 correction — these move *into* slice 2)**: `Application/Abstractions/{ICurrentUserAccessor,CurrentUser,ICurrentUserProjectionReader,CurrentUserProjection,IUserAccountGateway,IAccountSessionService}.cs` and `Application/Accounts/PostAuthenticationGateUseCase.cs` (+ `PostAuthenticationResult`), with `IUserRepository`/`IUnitOfWork` (the two ports the gate consumes). Round-2 filed all of them under slice 5's `Abstractions/*` while slice 2 shipped their implementations and consumers, so **slice 2 could not compile**, let alone "end green", and slice 3's `/auth/session` inherited the break — the identical defect round-2 finding 12 fixed one slice later. They are interfaces plus one use case over in-memory-fakeable ports, so they carry no dependency backwards. Slice 5 adds only the remaining ports it introduces (`IUsernameRegistryRepository`, `IFriendCodeGenerator`, `ITextPolicy`, …).
- **Infrastructure** (`Microsoft.Extensions.Identity.*` only — no `FrameworkReference`): `Infrastructure/Identity/{AuthUser,AuthRole}.cs` + `Persistence/Configurations/AuthUserConfiguration.cs` (the §5.2 `LockoutEnd` converter), `Identity/UserAccountGateway.cs` (incl. `VerifyDummyPasswordAsync` and the A-049 shared-lockout `CheckPasswordAsync`, §6.1), `Identity/AccountSessionService.cs`, **`Identity/PartyUpUserClaimsPrincipalFactory.cs` (round-7 — A-024's three-claim contract; `Microsoft.Extensions.Identity.Core` types only, so it belongs on this side of A-025's line)**, `Persistence/CurrentUserProjectionReader.cs` (A-052), `Persistence/Repositories/{UserRepository,UnitOfWork}.cs`, `Infrastructure/DependencyInjection.cs` (`AddIdentityCore<AuthUser>()` **`.AddRoles<AuthRole>()`** `.AddEntityFrameworkStores<AppDbContext>()` — round-4: `.AddRoles` is not optional, see A-039; no `AddSignInManager`/`AddIdentityCookies`, which stay in Api per A-025).
- **DI, stated so the container resolves at the end of the slice**: `AddInfrastructure` registers `IUserAccountGateway → UserAccountGateway`, `IAccountSessionService → AccountSessionService`, `ICurrentUserProjectionReader → CurrentUserProjectionReader`, `IUserRepository → UserRepository`, `IUnitOfWork → UnitOfWork` (all scoped), and **`services.Replace(ServiceDescriptor.Scoped<IUserClaimsPrincipalFactory<AuthUser>, PartyUpUserClaimsPrincipalFactory>())` after `AddIdentityCore(...).AddRoles<AuthRole>()`** (round-7 — a `Replace`, not an `AddScoped`, so it cannot depend on whether the framework used `TryAdd`; A-024); Api registers `ICurrentUserAccessor → CurrentUserAccessor` (scoped) and `PostAuthenticationGateUseCase`.
- **Api** (everything that touches `HttpContext`): `Api/Auth/{AuthCookieSetup,AuthorizationPolicies,CurrentUserAccessor,PartyUpSecurityStampValidator,AuthenticateAndSignIn,ReturnToValidator}.cs` — `CurrentUserAccessor` holds claims parsing and the request-scoped cache only, never SQL (A-052); `AuthenticateAndSignIn` is the thin adapter over `PostAuthenticationGateUseCase` (§6.6 round-2 split) — plus `AddAuthentication().AddIdentityCookies()`, `AddSignInManager()` and `SecurityStampValidatorOptions` in the composition root — round-1 review correctly noted these were missing from the file list *and* misplaced in Infrastructure (A-025 "Layering").
- `Program.cs`: `UseAuthentication`/`UseAuthorization` after `UseSerilogRequestLogging`, before endpoints; and the A-044 global query-string scrubbing on `UseSerilogRequestLogging`.
- migration `AddIdentitySchema`.
- **Round-4 additions to this slice**: `UnitOfWork.ExecuteAsync` opens a real `IDbContextTransaction` on the same scoped `AppDbContext` that `UserStore` auto-saves through, and joins rather than nests (A-053); `UserAccountGateway` throws `DomainException` on every failed `IdentityResult` (A-053 rule 3); `PartyUpSecurityStampValidator` is a from-scratch `ISecurityStampValidator` that rejects on mismatch and does **not** derive from `SecurityStampValidator<TUser>` (A-025 round-4).
- **Round-5 addition — the `IEmailDispatcher` dependency is deferred, explicitly.** §6.6 step 6 has the adapter send a "your deletion request was canceled" mail post-commit, but `IEmailDispatcher`, the templates and the sender all land in **slice 4**. Slice 2 therefore ships `AuthenticateAndSignIn` with no mail dependency at all, and `PostAuthenticationResult.DeletionWasCanceled` unread; slice 4 adds the dispatcher and slice 5 wires the `DeletionCanceled` send in the adapter, together with the flows that first make a cancel reachable. Stated so nobody pulls the email port backwards into slice 2 (it would drag the templates and the renderer with it) or forward-declares a stub sender that then ships.

Tests: `AuthCookieTests` (incl. the round-7 `Login_IssuesAPrincipalWithIdRolesAndStampOnly` — the ticket is unprotected with the test host's `TicketDataFormat` and must carry **no email claim**), `RequestLoggingTests.Log_NeverContainsAQueryString` (A-044), `MigrationTests.Migrate_RoundTripsAnIdentityLockoutEnd` (§5.2), `PostAuthenticationGateUseCaseTests` (Application, in-memory fakes), `UnitOfWorkTests.Rollback_UndoesAnIdentityAutoSave_D165` and `SecurityStampTests.Response_ForAValidSession_CarriesNoValidatorIssuedSetCookie` (round-4, renamed round-6), `SecurityStampTests.Response_ForAPrincipalWithNoProjectionRow_IsRejected` (round-5), `OnboardingGuardTests` (…_D127) once slice 4 lands — land a placeholder protected endpoint here.

**Slice 3 — Antiforgery, error mapping, rate limiting, session endpoint**
Files: `Api/ErrorHandling/DomainExceptionMapper.cs`, `Api/Security/AntiforgeryFilter.cs`, `Api/Security/RateLimitPolicies.cs` (**round-7: it builds one `PartitionedRateLimiter.CreateChained` global limiter from the thirteen §7.6 dimensions, which are **fourteen** chain members because `deletion-request` is an email/IP pair — round-5 added `deletion-steam-ip` and `intent-ip` — each a `PartitionedRateLimiter.Create<HttpContext,string>` whose partition function returns `GetNoLimiter` outside its route set; it registers **no** named policy, and no endpoint anywhere calls `RequireRateLimiting`, because a second named policy replaces the first instead of composing with it. `UseRateLimiter` goes after `UseRouting`/`UseAuthentication` so the partition functions can see the matched endpoint and the authenticated principal**), **`Program.cs` `UseForwardedHeaders` + `ForwardedHeadersOptions` bound to `Network:TrustedProxies` (round-4 — §7.6; without it every IP partition and every `request_ip_hash` is the Caddy container)**, `Api/Endpoints/AuthEndpoints.cs` (`/auth/antiforgery`, `/auth/session` incl. the A-042 `providers` list, `/auth/logout`), OpenAPI transformer. Also a `deploy/` note that Caddy must set `X-Forwarded-For`/`-Proto` and that the trusted-network value follows the compose network (OWNER-ACTION 10).
Tests: `AntiforgeryTests`, `RateLimitTests` (incl. `PartitionKey_UsesTheForwardedClientIpNotTheProxy`, round-4, and the round-7 `EveryDimensionCoversItsDocumentedRoutes` + `Login_ExhaustsBothTheIpAndTheEmailCeiling`), `SessionEndpointTests.Session_ForAnAnonymousCaller_ListsTheConfiguredProviders` (round-4, A-042).

**Slice 4 — Token pipeline, email port, templates** *(round-2: this slice is new, and slice 5's old contents moved into it)*
Round-2 review: A-046 made `POST /auth/register` write nothing but an `auth_tokens` row and send an `EmailVerification` mail, with the account created only at redemption — so registration now *depends on* the token table, `ISecurityTokenService`, `IEmailSender`, the templates and `VerifyEmailUseCase`, every one of which the round-2 draft assigned to the slice **after** it. Slice 4 as drafted could not end green, and three of its own D-016 tests plus `RegistrationFlowTests` were unreachable. The dependency is resolved by moving the machinery ahead of its first consumer rather than by merging slices.
Files: Application `Auth/{ISecurityTokenService,AuthTokenPurpose,IssueTokenRequest,RedeemTokenRequest,PendingRegistration,ExternalIntentPayload,ExternalStartMode,IssuedToken,TokenRedemption,TokenRedemptionFailure}.cs` (the full §8.1 `Auth/` list — sweep: the intent payload types ship with the port even though their only supplier is slice 6, because `RedeemAsync`'s request record and `IssueTokenRequest.Intent` reference them; **round-7 adds `RedeemTokenRequest` and the `RequiresAgeGate` failure member, and `SecurityTokenService` ships the CTE redemption statement in this slice — the age-gate arm and the binder arm are both inert until their consumers land, but they are properties of the one statement and cannot be retrofitted without re-opening the single-use race**), `Email/{IEmailSender,IEmailDispatcher,EmailMessage,EmailTemplate,IEmailTemplateRenderer,EmbeddedEmailTemplateRenderer}.cs` + `Email/Templates/*` (**13** templates × 3 locales × 3 parts, A-041 — round-4 adds `PasswordResetNoAccount`, `DeletionRequestNoAccount`, `ReauthenticationRequested`), Infrastructure `Auth/SecurityTokenService.cs`, `Email/{LoggingEmailSender,ChannelEmailDispatcher}.cs` + `Api/Hosting/EmailDispatchHostedService.cs` (§6.1 round-4 — transactional mail leaves the request path), `Persistence/Configurations/AuthTokenConfiguration.cs`, migration `AddAuthTokens`.
Tests: `EmailTemplateRendererTests` (D-060/D-146/D-067) in **§14.2**; `SecurityTokenServiceTests` (single-use, TTL, the A-029 concurrent-token rule, the round-4 cross-purpose refusal, the round-6 `Redeem_WithAMismatchedBinder_ConsumesNothing_D067` — the binder is a `WHERE` predicate, so the port carries it from this slice on even though its only supplier ships in slice 6 — and the round-7 `Redeem_ForAPreAccountTokenWithoutAttestation_RefusesWithoutConsuming_D083` plus `Redeem_WithAnAttestation_ConsumesThePreAccountToken_D083`) in **§14.3** — the service lives in Infrastructure and single-use is a property of the real `UPDATE … RETURNING` (§14 preamble, round-4).

**Slice 5 — Email+password registration, verification, login, onboarding, recovery**
Packages: `MailKit`. Files: Application `Accounts/{RegisterWithPasswordUseCase,VerifyEmailUseCase,SignInWithPasswordUseCase,RequestMagicLinkUseCase,ConsumeMagicLinkUseCase,RequestPasswordResetUseCase,ResetPasswordUseCase,ChangePasswordUseCase,SetPasswordUseCase,ChangeEmailUseCase,ReauthenticateUseCase}.cs` (`PostAuthenticationGateUseCase` and the four ports it needs landed in **slice 2** — round-3), `Onboarding/*`, the remaining `Abstractions/*` (`IUsernameRegistryRepository`, `IFriendCodeGenerator`, `IDeletionJournal`, `IDeletionScopeProvider`, `ISteamProfileClient`), `Text/{ITextPolicy,ProfanityFilter}` + wordlists, `Infrastructure/Persistence/Repositories/UsernameRegistryRepository.cs`, `Infrastructure/Accounts/FriendCodeGenerator.cs`, `Infrastructure/Email/SmtpEmailSender.cs`, `Api/Endpoints/{AuthEndpoints,OnboardingEndpoints}.cs` (incl. `POST /auth/reauth`, **`POST /auth/reauth/email`** — A-049 proof branch 3, round-4 — and the `reauth` rate-limit policy).
**Round-7 additions, both inside existing files**: `ConsumeMagicLinkUseCase` and `VerifyEmailUseCase` are the only two call sites that pass a non-null `RedeemTokenRequest.AgeGateAttested`, and the 409 `age_gate_required` answer is `TokenRedemptionFailure.RequiresAgeGate` (A-029/§6.2 — nothing is consumed, so the SPA retries with the same token); `SignInWithPasswordUseCase` implements §6.1's one-hash rule as `if (!result.HashVerified) await VerifyDummyPasswordAsync(ct);` and nowhere else.
Tests: the D-016/D-067/D-078/D-083/D-093/D-096/D-127/D-128 Application tests, `RegistrationFlowTests` (which now genuinely reaches `Active`, because the verification leg exists in the same slice), `MagicLinkFlowTests`, `RegistrationEnumerationTests` and `ForgotPasswordEnumerationTests` (round-4).

**Slice 6 — Google OAuth, the external-intent gate, and the `mode=reauth` completion**
Packages: `Microsoft.AspNetCore.Authentication.Google`. Files: `Api/Auth/GoogleAuthSetup.cs` (**incl. `ClaimActions.MapJsonKey("email_verified", "email_verified")` — round-7: the handler maps `email` but not `email_verified`, and A-045 steps 1 and 6 are both unreachable without it**), `Api/Auth/IntentBinder.cs` (**round-7 — the one `Base64Url(SHA-256(binder))` helper used at mint and at redeem, A-050/A-029**), `Api/Endpoints/ExternalAuthEndpoints.cs` (`POST /auth/external/intent`, `/start`, `/complete`; the external-principal → use-case input mapping that turns the `email_verified` claim into a fail-closed `bool`), Application `Accounts/{CompleteExternalSignInUseCase,IssueExternalIntentUseCase}.cs`.
**Round-3 additions, both of which must land here rather than later:** `POST /auth/external/intent` and the intent requirement on `/start` in **every** mode (A-050) — a slice that shipped a bare-GET `/start?mode=login` would ship the forced-sign-in/silent-creation hole for the length of the phase; and the `mode=reauth` completion that mints the `Reauthentication` token and returns it in the `/auth/callback#reauth=` fragment (A-049). Slice 8 ships `link-intent`, unlink and `password/set`, all of which are 409-only without it for password-less accounts, so it cannot follow them.
**Round-4 additions, also here**: the `pu.intent` browser-binding cookie on `POST /auth/external/intent` and its check on `/start` (A-050 — without it the round-3 forced-navigation attack survives intact, so it cannot trail the intent by a slice); and A-045 step 4's `reauth` identity binding, since this is the slice that first mints a `Reauthentication` token.
Tests: `ExternalIntentTests` (**round-6: `ExternalStateTests` is removed from this slice — it covers the A-051 *Steam* OpenID state cookie, which does not exist until slice 7; slice 6's Google leg uses the framework correlation cookie, asserted by `ExternalRedirectTests.GoogleChallenge_SetsTheCorrelationCookieSameSiteLax`**) incl. `Complete_WithAMismatchedIntentMode_LinksNothing_D087` (round-6, replacing `Start_WithALoginIntentInLinkMode_IsRejected`), `Start_WithAnIntentMintedInAnotherBrowser_IsRejected` and the three round-5 binder cases — `Start_RemovesOnlyTheUsedBinderFromTheIntentCookie`, `Start_WithTwoConcurrentlyMintedIntents_BothSucceed`, `Start_WithAnInjectedIntentCookieValue_IsRejected`), `RateLimitTests.AnonymousExternalIntent_IsBoundedByIntentIp` (round-5 — the `intent-ip` policy ships with the endpoint), `ExternalRedirectTests`, the three round-4 `ReauthenticationTests` identity-binding cases, `CompleteExternalSignInUseCaseTests` (…_D067, …_D083, …_D087 notification, the round-7 `Execute_TreatsAnAbsentEmailVerifiedClaimAsUnverified_D067`, and the A-045 step 6 email-policy cases including the round-6 `Execute_NeverCreatesAnAddresslessAccountForAProviderWithNoPublicDeletionLeg_D072`), **`GoogleClaimMappingTests.Callback_ProjectsEmailVerifiedOntoThePrincipal_D067`** (round-7).

**Slice 7 — Steam OpenID 2.0 + platform link (A-030)**
Files: `Infrastructure/Steam/{SteamOpenIdVerifier,SteamApiClient,SteamProfileClient}.cs`, `Persistence/Configurations/SteamIdentityConfiguration.cs`, migration `AddSteamIdentities`, endpoints `/auth/external/steam/{start,callback}` (intent-gated per A-050, per-flow state cookie per A-051), the Steam half of the `mode=reauth` completion, `Api/Endpoints/MeEndpoints.cs` platform-link routes, **and the use cases those routes call: Application `Profile/{LinkPlatformUseCase,ChangePlatformHandleUseCase,UnlinkPlatformUseCase}.cs` and `Accounts/UnlinkSteamUseCase.cs`** (round-4 correction — see below).
**Round-4 correction — the platform-link use cases move here from slice 8.** The round-3 list ended slice 7 with "`MeEndpoints.cs` platform-link routes" while every use case those routes call was scheduled for slice 8, so slice 7 could not compile the endpoints as specified — violating this section's own "each slice ends green" rule, and the same defect class as round-2 finding 12 and round-3 finding 11. The unsafe repair is worse than the compile break: the obvious way to make slice 7 green is to have `DELETE /me/platform-links/{id}` delete just the `platform_links` row inline, which is precisely what A-030's round-2 correction forbids — it drops the link without removing `auth_user_logins('Steam', …)` or `steam_identities`, bypassing the D-137 last-method guard and leaving third-party persona/avatar data behind after the user revoked the link (PRODUCT_SPEC §8.6 "revoke their data access"). A one-slice window is still a shipped route with the guard missing. The four use cases need only `IUserRepository`, `IUnitOfWork`, `ITextPolicy` and `IUserAccountGateway`, all of which land in slices 2 and 5, so they carry no dependency backwards; and slice 7 needs `UnlinkSteamUseCase` for its own Steam flow anyway, which makes pulling them forward the smaller change than pushing the routes back.
Tests: `SteamCallbackTests`, `ExternalStateTests` (the two A-051 cross-flow rejections — the deletion leg's cookie lands with slice 8, so land the auth-side half here and the pair in slice 8), `CompleteExternalSignInUseCaseTests` (…_D032), `UnlinkSteamUseCaseTests` (all four, incl. the round-4 `…_NotifiesTheAccountAddress_D137` and the two round-6 `…_ForASteamLinkRequestedAsAPlatformUnlink_*` renames), **`PlatformLinkRouteTests.Delete_ForASteamLink_Returns409WithoutProofAndRemovesAllThreeRowsWithIt_D137`** (consistency sweep — the HTTP half belongs to the slice that ships the route, and `DELETE /me/platform-links/{id}` is in *this* slice's `MeEndpoints.cs`; the `reauth` proof it asserts exists from slice 5, its external branch from slice 6), `TextPolicyTests.Check_RunsBeforeAGamingHandleReachesTheDomain_D096`.

**Slice 8 — Profile, settings, auth-method linking, deletion skeleton**
Files: the remaining Application `Profile/*` (the three platform-link use cases landed in **slice 7** — round-4), `Accounts/{ListAuthMethods,UnlinkAuthMethod}UseCase.cs` (`IssueExternalIntentUseCase` landed in slice 6, `UnlinkSteamUseCase` in slice 7), `Deletion/*` (**including `PurgeExpiredAuthTokensUseCase`** — §9.7, round-5), Infrastructure `Deletion/{DeletionScopeProvider,DeletionJournal,FileDeletionJournalShipper}.cs`, **the `GET /auth/methods` and `DELETE /auth/methods/{kind}` routes in `Api/Endpoints/AuthEndpoints.cs` (round-5 — §6.5's two routes were assigned to no slice at all, while their use cases and every §14.2 D-087/D-137 test landed here; the `magicLink` `removable` flag and the 400 `method_not_removable` answer ship with them)**, `Api/Endpoints/{MeEndpoints,AccountDeletionEndpoints}.cs`, `Api/Hosting/DeletionSweepHostedService.cs` (**including the §9.7 `auth_tokens` retention purge on the same tick** — round-4), migration `AddDeletionJournal`.
Tests: D-087/D-137/D-145/D-148/D-158/D-165/D-072 Application tests — **including the three round-4 magic-link auth-method tests that round 5 added to the §14.2 table** (`ListAuthMethodsUseCaseTests.Execute_ReportsMagicLinkForAConfirmedAddress_D067`, `UnlinkAuthMethodUseCaseTests.Execute_CountsMagicLinkTowardTheLastMethodRule_D137`, `UnlinkAuthMethodUseCaseTests.Execute_RefusesToRemoveMagicLinkDirectly_D137`) (the four `UnlinkSteamUseCaseTests` and their route half `PlatformLinkRouteTests` land in **slice 7** with the use case and the route — consistency sweep; the round-5 draft assigned them to both slices) — `SecurityStampTests`, `LoginCancelsDeletionTests`, `DeletionSweepTests`, `AuthTokenRetentionTests` (§9.7, driven through the admin maintenance endpoint because the factory disables the sweep), `PublicDeletionRouteTests.Request_ForAKnownAndAnUnknownAddress_AreIndistinguishable_D072`, `SecurityTokenServiceTests.Issue_DoesNotConsumeAnotherPartysOutstandingDeletionConfirmation_D072` (round-5).
**Must land before slice 9** (recon #2 — ban reuses these pieces).

**Slice 9 — Admin foundations**
Files: `Api/Hosting/AdminSeeder.cs` (A-039, `SeedUserIds` + confirmed-only email fallback), Application `Admin/*`, `Api/Endpoints/AdminEndpoints.cs`. The domain ban delta and its migration already landed in slice 1.
Tests: `BanUserUseCaseTests` (Application), `AdminAuthorizationTests` (incl. the round-5 `Admin_AfterAnOutOfBandRoleRevocation_Returns404_D177`, which pins that `RequireAdmin` reads the per-request projection rather than the frozen role claim) and `AdminSeederTests` (all five cases — three round-1/2, plus the round-4 stamp-rotation and role-row-creation cases) — the seeder tests are **integration** tests, not Application tests (§14 preamble).

**Slice 10 — Web foundation**
Files: `web/src/api/client.ts` middleware, `auth/{useSession,RequireAuth,RequireOnboarded}.tsx`, router in `App.tsx`, i18n namespace split, shadcn/Radix primitives, `npm run gen:api`.
Tests: `i18n.keys.test.ts`, route-guard tests.

**Slice 11 — Web auth + onboarding screens**
Files: `web/src/features/auth/*`, `features/onboarding/*` (all D-127 steps, friends step stub).
Tests: `AgeGate`, `OnboardingWizard`, `UsernameStep`.

**Slice 12 — Web profile + settings + the public deletion pages**
Files: `web/src/features/profile/*`, `features/settings/*`, **`features/deletion/{PublicDeletionRequestPage,PublicDeletionConfirmPage}.tsx`** and their routes `/account-deletion` and `/account-deletion/confirm` (round-5 — §7.4 now places both in Phase 2; the confirm page reads `location.hash`, `replaceState`-clears it before any render, holds the token in memory only, and requires an explicit antiforgery-protected confirming POST, per A-044 and §7.4's consent/identity split).
Tests: `AuthMethods`, `LanguageSwitch`, `DeleteAccount`, **`PublicDeletionPage.test.tsx`** (round-5 — the request form offers both the email route and "prove it with Steam"; the confirm page never renders the token, never navigates with it in a query string, and does not call the API until the user confirms).

**Closing slice — documentation**
Update ARCHITECTURE.md §9 with A-021..A-053 and §10 (move the Phase 2 bullet to done), DOMAIN_MODEL.md (§3 `BannedAt`, `IsDiscoverable`, friend-code format), TRACEABILITY.md (§14.5), **IMPLEMENTATION_PLAN.md (round-5 — line 125 files D-072's public page under Phase 8; Phase 2 now ships it, per §7.4, and Phase 8 keeps only the footer/store-listing surfacing)**, README dev-setup notes, `deploy/RUNBOOK-{backup,restore}.md` for the journal directory and `deploy/RUNBOOK-admin.md` for the A-039 role-revocation step (round-5).

---

## 16. Package additions (Central Package Management)

Add to `Directory.Packages.props` — versions only there, never in a csproj (A-009):

```xml
<PackageVersion Include="Microsoft.AspNetCore.Identity.EntityFrameworkCore" Version="10.0.10" />
<PackageVersion Include="Microsoft.AspNetCore.Authentication.Google" Version="10.0.10" />
<PackageVersion Include="MailKit" Version="4.14.0" />
```

Notes:
- Keep the ASP.NET/EF versions **exactly** in step with the existing `10.0.10` pins; a mismatch reintroduces the MSB3277 problem the file already documents for `Microsoft.EntityFrameworkCore.Relational`. Verify at implementation time that `Microsoft.AspNetCore.Identity.EntityFrameworkCore 10.0.10` resolves EF Core 10.0.10 and, if not, extend the transitive-pin block.
- `Microsoft.AspNetCore.Identity` core types, `SignInManager`, cookie authentication, antiforgery, rate limiting and Data Protection all come from the **ASP.NET Core shared framework** — no package.
- MailKit is the only third-party addition; pin the newest 4.x patch at implementation time and re-check for advisories (the repo already carries a security-motivated pin, so this is established practice).
- csproj references: `Microsoft.AspNetCore.Identity.EntityFrameworkCore` and `MailKit` → `PartyUp.Infrastructure`; `Microsoft.AspNetCore.Authentication.Google` → `PartyUp.Api` (it is composition, and Infrastructure must not take an ASP.NET auth-handler dependency it does not own).
- No new package for Steam (A-019: hand-rolled), none for profanity (own wordlists), none for templating (own substitutor), none for background work (A-037).

---

## 17. Consolidated decision record (append to ARCHITECTURE.md §9)

| ID | Decision | Rationale (short) |
|---|---|---|
| A-021 | Identity is a sibling `auth_users` table keyed 1:1 to `users.id`; default `UserStore`, no custom `IUserStore` | Keeps PII, BCL time and mutable Identity state out of the pure Domain; one extra row, zero divergence risk |
| A-022 | Email/password/logins live only on the Identity side | `User.MarkDeleted` stays correct as written; tombstone PII purge is one Infrastructure step |
| A-023 | `AuthUser.UserName` is the user-id string; the product username lives only in `username_registry` | One case-insensitive name space, not two; D-145 reservations stay authoritative |
| A-024 | Cookie carries id + roles + security stamp only — enforced by a **custom `IUserClaimsPrincipalFactory<AuthUser>` in Infrastructure**, `Replace`-registered over the framework default; mutable state is read per request | Ban/deletion/onboarding changes take effect immediately, not at cookie refresh; and the default factory emits the user name, the **email address** and every `auth_user_claims` row on top, so "id + roles + stamp" is only true if the factory is replaced (round-7) |
| A-025 | `SecurityStampValidator.ValidationInterval = Zero`; stamp rotation is the single sign-out-everywhere mechanism; the stamp is joined into A-024's projection and all `HttpContext`-bound Identity wiring lives in Api, not Infrastructure (the claims-principal factory is `Identity.Core`-only, so it stays in Infrastructure — round-7 applies the same line consistently) | D-165/D-180 both say "immediately"; one joined PK read per request is affordable, and Infrastructure must not take a shared-framework auth dependency |
| A-026 | Onboarding-incomplete is an authorization policy (`RequireOnboarded` → 403 `onboarding_required`), not a separate auth state | D-127 signs in before the profile exists; the caller's own state is not a 404 case |
| A-027 | `BannedAt` is a domain field; enforcement is at the auth boundary | Ban is orthogonal to the lifecycle (DOMAIN_MODEL §3) |
| A-028 | Cookie `pu.auth`: HttpOnly, Secure outside dev, `SameSite=Lax`, 30-day sliding, JSON 401/403 (no login redirect) | Lax survives the external-provider return leg; an API must not 302 to HTML |
| A-029 | Own hashed, single-use, TTL'd token table; **every** redemption condition — `purpose`, A-050's `binderHash` (base64url **text**, written at issuance from `ExternalIntentPayload.BinderHash`) and the D-083 pre-account age gate — is a predicate inside **one** conditional `WITH … UPDATE … RETURNING` statement that consumes nothing when a condition refuses (`RedeemTokenRequest` / `TokenRedemptionFailure.RequiresAgeGate`); Identity's token providers bypassed, its password hasher and lockout kept | A-015 requires hashed-at-rest, revocable, single-use; DataProtector tokens are none of those. Round-7: a condition that can only be evaluated *after* consuming is not a condition — §6.2's "409 without consuming" was unimplementable through the round-6 port, and peek-then-redeem reopens the double-redemption race the single statement exists to prevent |
| A-030 | Steam sign-in yields both an auth login row and a gaming platform link (+ `steam_identities` for persona/avatar) | Spec models them separately; users must not connect Steam twice |
| A-031 | Antiforgery on every unsafe `/api/v1` request via one group filter; header `X-CSRF-TOKEN`; the antiforgery cookie stays `HttpOnly` and the **request token is returned in the `GET /auth/antiforgery` body** | Login CSRF is real; and ASP.NET Core has one cookie (the cookie token) plus a separately-returned request token — the round-1 "companion cookie" description was not implementable |
| A-032 | `LookupVisibility` excludes banned users; `IdentityMasking` deliberately does not mask them | D-180 hides from discovery while leaving content and attribution untouched |
| A-033 | Onboarding steps validate individually; one atomic `POST /onboarding/complete` | Honors the aggregate's all-or-nothing `CompleteOnboarding` without a draft table |
| A-034 | BCL time is confined to the Identity gateway implementations | NodaTime-only Domain/Application (ARCHITECTURE §3) |
| A-035 | Friend codes: 10 symbols from a 32-char ambiguity-free alphabet, uniqueness by DB index with bounded retry | Discovery identifier, not an access grant; A-015 does not apply |
| A-036 | Profanity/shape policy is an Application service running before any domain call or registry claim | Keeps wordlists out of the Domain; a rejected name never consumes a reservation |
| A-037 | Grace expiry runs on a `PeriodicTimer` hosted service + an admin trigger; the batch read returns **ids only** and takes no lock, each user runs in its own DI scope and transaction, and §9.4 step 1's per-user `FOR UPDATE` is the only lock; Hangfire still deferred | A-013 bans the package, not scheduled behavior; D-148 forbids indefinite deferral; the use case survives the Phase 4 swap |
| A-038 | Deletion journal = transactional Postgres table mirrored to an append-only NDJSON file shipped offsite | A-017's restore-replay needs entries that outlive the snapshot; a table alone cannot provide them |
| A-039 | Admin role seeded from `Admin:SeedUserIds` (preferred) or a **confirmed-only** `Admin:SeedEmails` match at startup, idempotent, never auto-revoked | D-177 "assigned operationally"; an email-only, indefinitely-retried match let whoever registered the seed address first become admin |
| A-040 | `IEmailSender` with a dev logging/file sender and one SMTP adapter; no vendor SDK | The provider is the owner's later choice; every candidate speaks SMTP |
| A-041 | Email templates are embedded per-locale resources with placeholder substitution; recipient locale is a required argument | Principle 6 / D-060 / D-146; thirteen templates (§11) are data, not a rendering engine |
| A-042 | Every external dependency is optional at startup; the app runs fully with none configured | Development and CI cannot block on the OWNER-ACTION list |
| A-043 | Ban excludes from `LookupVisibility.IsListed` and `User.CanBeFriended`; `ProfileVisibility` gains a **total** ban branch (round-5) that returns `Full` for self/accepted friend and `Hidden` otherwise, and never falls through to the now ban-excluding `IsListed` gate | `IsDiscoverable` has consumers beyond lookup, so overloading it would 404 a banned profile to the user's own friends; but profile-link resolution *is* a lookup surface, so a stranger must get nothing (D-180) |
| A-044 | Raw tokens ride in the URL **fragment**; request-log query strings are scrubbed globally, in the app and in Caddy | SPA document routes are served after `UseSerilogRequestLogging`, so `?token=` was logged by app and proxy before any SPA code ran — a literal A-015 violation |
| A-045 | External identity: auto-link only when the IdP **and** the local account have both verified the address — the IdP half is the **explicitly mapped** `email_verified` claim (`ClaimActions.MapJsonKey`, not a Google-handler default) read fail-closed in the use-case input mapping (round-7); **every** mode requires an antiforgery-minted one-time intent (A-050); `returnTo` is validated positively at every consumer; and step 6 pins what a provider CREATE writes to the email columns (verified Google address → confirmed; Steam → address-less D-078 account; **a Google create that would be address-less is refused, round-6** — no provider may create an address-less account unless it also has a §7.4 assertion-based public deletion leg) | Pre-hijack, link-CSRF and open-redirect are the three classic takeover paths on this surface; the rules live in the shared use case so future providers inherit them |
| A-046 | Password registration is mailbox-confirmed: always 202, no row written until the link is followed, and **the password is chosen at redemption, never carried on the token** | The round-1 "still 201" rule was not implementable; and a credential parked on the token let an attacker pick the password for an account the victim's own mailbox click creates |
| A-047 | Every account state has a reachable deletion path: `RequestDeletion` accepts `Onboarding`, **`CancelDeletion` returns the user to the lifecycle they left**, `RequireOnboarded` tests profile completeness, `/me/deletion` is `RequireAuthenticated`, and email-less accounts prove identity with a Steam assertion | PRODUCT_SPEC §8.6 and D-072 are per-account, not per-lifecycle; the entry edge without the exit edge would have produced an `Active` user with no profile, breaking A-033's invariant and D-127 |
| A-048 | Email change requires re-authentication (**both** the password and the external branch ship in Phase 2), notifies the old address when there is one, uses its own token purpose carrying the target address, rotates the stamp on verify, and is also the **first-address** route for an address-less account | Otherwise a stolen 30-day cookie converts to permanent ownership with no signal to the victim |
| A-049 | The same re-authentication, stamp rotation and notification apply to **every** sign-in-method change — link-intent, unlink, setting a first password — **and to `POST /me/deletion`**; **three** proof branches ship (current password, external re-assertion bound to the caller per A-045 step 4, and an emailed `Reauthentication` token via `POST /auth/reauth/email` for magic-link-only accounts), the external branch's token is returned in the `/auth/callback#reauth=` fragment, and all proof endpoints share the login lockout counters under a 5/15-min-per-user `reauth` ceiling — which, like every other §7.6 ceiling, is a **member of one chained global limiter**, because an ASP.NET endpoint honours at most one named rate-limiting policy and a second would replace rather than compose (round-7) | A-048's reasoning covered one of four conversion paths; and unthrottled, lockout-free proof endpoints would have turned the barrier into a 300-guess-per-minute password oracle, while `/me/deletion` converted a cookie into irreversible tombstoning with no proof and, for address-less accounts, no notice |
| A-050 | **Every** external `/start` — `login`, `link` and `reauth` — requires a one-time `ExternalStartIntent` minted by the antiforgery-protected `POST /auth/external/intent`, which also carries the D-083 attestation and the `returnTo`, **and is bound to the minting browser by a `SameSite=Strict` `pu.intent` cookie carrying a capped list of data-protected binder secrets matched against a per-row `binderHash`** (round-5: a list, so concurrent tabs work; a hashed secret, so it is not the cookie-value double submit A-031 rejects; round-7: the hash is `Base64Url(SHA-256(binder))` computed by one shared helper at both mint and redeem, and it reaches the row through `ExternalIntentPayload.BinderHash`); `/start` reads nothing from its own query string | A silent provider assertion (Steam `checkid_setup`, a consented Google client) turns a forced top-level GET into login-CSRF *and* silent account creation authorized by an attacker-supplied age-gate flag; identity is proved by the provider, consent must be proved by an antiforgery-protected POST |
| A-051 | Each external OpenID flow owns its own state cookie name and path, its own data-protection purpose, its own `flow` discriminator in the payload, and its own pinned `return_to` | §7.4's deletion leg could not reuse §6.4's `pu.ext` (wrong path, wrong `return_to`); the plausible workarounds were a `Path=/` widening, cross-flow state confusion, or dropping the state check |
| A-052 | The `CurrentUser` projection **read** is `ICurrentUserProjectionReader`, implemented in `Infrastructure/Persistence/`; Api keeps only claims parsing, the request cache and the stamp-validator wiring | §8.2's own port rule; a hot-path projection query (the `users`/`auth_users` join plus the round-5 `is_admin` `EXISTS`) in the composition root is the only port implementation outside Infrastructure and is unreachable from any test project |
| A-053 | `IUnitOfWork.ExecuteAsync` is a real `IDbContextTransaction` on the same scoped `AppDbContext` Identity's `AutoSaveChanges` writes through, joining rather than nesting; every failed `IdentityResult` throws | A-021 mandates the default `UserStore`, whose `AutoSaveChanges = true` commits each `UserManager` call independently — so a SaveChanges-only unit of work would let stamp rotation and the credential purge escape A-016's tombstone transaction, and a silently-failing purge would leave live credentials on a "deleted" account |

---

## 18. Review log

<!-- Reviewers append findings and revisions below. Do not edit sections above without noting the change here. -->

### Round 1 — adversarial review (security / spec / architecture lenses)

29 findings. **All 29 accepted and fixed; none rebutted.** Every claim was re-verified against the code and the spec before acting; the verification is cited inline in each fix. One-line disposition per finding, in the order received:

| # | Lens / severity | Finding | Disposition |
|---|---|---|---|
| 1 | security / blocking | Google auto-link by email is an account pre-hijack path | **Fixed** — A-045 step 1: auto-link requires IdP `email_verified == true` **and** local `EmailConfirmed == true`, stated in `CompleteExternalSignInUseCase` so all providers inherit it; A-046 removes the unverified-local-row precondition entirely; §6.4 step 6 states the Steam exemption explicitly. Two new tests. |
| 2 | security / blocking | Admin seeding grants `admin` to whoever registers the seed email first | **Fixed** — A-039 rewritten: `Admin:SeedUserIds` is the primary mechanism; `SeedEmails` grants only against `EmailConfirmed = true`, refuses (log level **Error**, no retry-into-grant) on an unconfirmed match; A-046 also makes the row unmanufacturable. Two new tests. |
| 3 | security / blocking | A-031's antiforgery scheme does not match how ASP.NET Core works | **Fixed** — A-031 rewritten with the round-1 error named: one cookie (the cookie token, `HttpOnly = true`) plus a separately-returned `RequestToken`; `GET /auth/antiforgery` now returns 200 with `{token}`; identity-binding refetch rule added to A-031 and §12.2. |
| 4 | security / blocking | Magic-link/reset/verification tokens land in server and proxy logs via the SPA document routes | **Fixed** — A-044: fragment delivery (`#token=`) for every emailed link, verified against `src/PartyUp.Api/Program.cs:54` (`UseSerilogRequestLogging`) and `:71` (`MapFallbackToFile`); global query-string scrubbing rather than a per-endpoint redaction list; Caddy access-log configuration added as OWNER-ACTION 9; template and request-log tests added. |
| 5 | security / blocking | Steam callback does not require `openid.signed` to cover `claimed_id` | **Fixed** — §6.4 step 4 now validates `openid.ns`, `openid.mode == id_res` and the required `openid.signed` field set **before** `check_authentication`, with the bypass mechanism spelled out; step 5 additionally requires `identity == claimed_id`. New test `Callback_WithClaimedIdStrippedFromOpenIdSigned_IsRejected_D067`. |
| 6 | security / important | Register's "still 201" is not implementable and does not prevent enumeration | **Fixed** — A-046: register always answers `202 {status:"verification_sent"}`, writes nothing, issues no cookie; the account is created only at link redemption. Option (a) — signing the caller into the existing account — is explicitly ruled out in writing. §7.1 and §14.2 updated; the duplicate-address case on `/auth/email/change` is handled by A-048 step 4. |
| 7 | security / important | Login has no timing equalization | **Fixed** — §6.1 requires a dummy `VerifyHashedPassword` on both the unknown-email and password-less paths (new port method `VerifyDummyPasswordAsync`), with an identical `401 invalid_credentials` for all three negative outcomes and a test that asserts the identical `code`. |
| 8 | security / important | Email change needs no re-auth, no old-address notice, no stamp rotation, shares a token purpose | **Fixed** — new A-048 covering all four, plus a distinct `EmailChange` purpose carrying the target address in `email_normalized`; `AuthTokenPurpose` enumerated in A-029 and §5.3; two new email templates; four new tests. |
| 9 | security / important | Last-method unlink has no row lock | **Fixed** — §6.5 opens the transaction with `FindForUpdateAsync` on the parent `users` row before counting, citing ARCHITECTURE §3; concurrency test added. |
| 10 | security / important | `returnTo` validation is under-specified | **Fixed** — A-045 step 5: control-character rejection plus a positive regex, an allow-list as the Phase 2 default, a single shared `ReturnToValidator.Sanitize`, and explicit application at the Steam `/callback` redirect (§6.4 step 7). Test with `/\evil.com`, `/%09/evil.com` and CR/LF. |
| 11 | security / important | Link mode is initiated by an unauthenticated-shaped GET with no antiforgery | **Fixed** — A-045 step 3: new antiforgery-protected `POST /auth/external/link-intent` mints a one-time 10-minute value that `/start?mode=link` requires and consumes; A-031's GET-exemption bullet now names this as its explicit carve-out. Test added. |
| 12 | security / important | Pre-account magic-link tokens are not re-resolved at redemption | **Fixed** — §6.2 re-resolves `email_normalized` inside the redemption transaction and *adopts* an account that appeared meanwhile (setting `EmailConfirmed = true`, which is also the safe adoption path the finding notes); test added. |
| 13 | spec / blocking | No deletion path for an account still in Onboarding | **Fixed** — new A-047. Verified `src/PartyUp.Domain/Users/User.cs:275` (`RequestDeletion` requires `IsActive`) and `:335` (`MarkDeleted` requires only `PendingDeletion`). `RequestDeletion` now accepts `Onboarding`; `POST /me/deletion` moves to `RequireAuthenticated`; DOMAIN_MODEL §3 gains the edge in the same commit; D-072/D-148 tests added. |
| 14 | spec / blocking | Banned email-less (D-078 social-only) account cannot use the public deletion route | **Fixed** — §7.4 gains a Steam-assertion variant that reaches `RequestAccountDeletionUseCase` without issuing a cookie, satisfying PRODUCT_SPEC:457 ("A banned user can still request account deletion through the public route"); ops fallback documented; two tests added. |
| 15 | spec / important | A-031 misdescribes ASP.NET Core; the SPA gets no request token | **Fixed** — same change as finding 3. |
| 16 | spec / important | Password-reset sign-in bypasses the post-authentication gate | **Fixed** — §6.1 routes reset through `AuthenticateAndSignIn`; §6.6 now states that it is the only function that issues `pu.auth` and enumerates every caller, backed by a one-file `SignInAsync` rule; `..._D180` and `..._D158` reset tests added. |
| 17 | spec / important | Ban exclusion on `IsDiscoverable` has unaccounted consumers | **Fixed** — verified `User.cs:88` (`CanBeFriended`) and `ProfileVisibility.cs:42`. New A-043 makes the deliberate choice: ban lives in `IsListed` and `CanBeFriended`; `ProfileVisibility` gates on `IsDiscoverable` so an existing friend keeps access; the outcome is pinned by two `ProfileVisibilityTests..._D180`. |
| 18 | spec / important | Registration anti-enumeration response is underspecified | **Fixed** — same change as finding 6; the exact body, absent cookie and absent rows are now asserted by `Execute_DoesNotRevealAnExistingAccount_D016`. |
| 19 | spec / important | Design contradicts its own D-078 test on `/auth/password/set` | **Fixed** — §6.1 keeps the verified-email precondition with the reasoning stated (login is email-keyed); the contradictory test is replaced by `SocialOnlyAccountTests.Execute_CompletesOnboardingAndReachesEveryMeSurfaceWithNoEmail_D078`, which is what PRODUCT_SPEC:570 actually says. |
| 20 | spec / important | D-087 read as a prohibition on warnings | **Fixed** — §6.5 quotes PRODUCT_SPEC:579 and restates no-pre-disable as a design preference; the assertion is dropped from `AuthMethods.test.tsx`; §1.3 row 4 updated. The D-137 explicit error is untouched. |
| 21 | spec / important | D-179/D-180 need partial-completion markers | **Fixed** — §14.5 marks both `2 (partial) / 8` with D-092-style verification notes naming exactly what Phase 2 does and does not ship. |
| 22 | spec / important | No D-numbered test for the age gate on the external account-creation path | **Fixed** — §14.2 adds `CompleteExternalSignInUseCaseTests.Execute_RejectsAnUnattestedAgeGateOnProviderAccountCreation_D083` (asserting no `User` and no `auth_users` row) and `..._RecordsTheAgeGateSourceWithoutABirthDate_D093`. |
| 23 | architecture / blocking | Google auto-link enables takeover of unverified local accounts | **Fixed** — same change as finding 1; the round-1 phrase "the standard safe auto-link" is gone. |
| 24 | architecture / blocking | `auth_tokens` are not purged at tombstone | **Fixed** — verified that `MarkDeleted` (`User.cs:335`) is an UPDATE, so the FK cascade never fires. §9.5 now deletes `auth_tokens` by `user_id OR email_normalized` **before** nulling the email, in the same transaction; the round-1 cascade claim is struck; `ix_auth_tokens_email_normalized` added; test added. |
| 25 | architecture / important | A-031's mechanism does not work and de-HttpOnly's the cookie token | **Fixed** — same change as finding 3. |
| 26 | architecture / important | Slice order is not implementable: A-024/A-026 depend on `banned_at`, added last | **Fixed** — §5.4 moves `banned_at` into migration 1 and §15 moves the whole domain ban delta (property, `Ban`/`Unban`, A-043 visibility edits, D-180 domain tests) into slice 1; slice 9 keeps only the use cases and endpoints. |
| 27 | architecture / important | Stamp validator misplaced in Infrastructure; A-025's shared-read claim is wrong | **Fixed** — A-025 gains a "Layering" paragraph moving `AddIdentityCookies`, `AddSignInManager`, `SecurityStampValidatorOptions` and the validator to Api (with the missing `AddIdentityCookies` now in the slice-2 file list), and a "Cost, stated accurately" paragraph: A-024's projection joins `auth_users.security_stamp` so the read really is shared, and the per-response `Set-Cookie` is acknowledged as the accepted price of "immediately". |
| 28 | architecture / important | An account abandoned mid-onboarding can never be deleted | **Fixed** — same change as finding 13 (A-047), which also covers the email-less half via finding 14. |
| 29 | architecture / important | Raw tokens logged by `UseSerilogRequestLogging` via the SPA fallback | **Fixed** — same change as finding 4; the scrubbing is global (`EnrichDiagnosticContext` overriding `RequestPath`), not per-endpoint, and A-029's "never logged" bullet now defers to A-044 rather than to a redaction list. |

Net additions in round 2: **A-043 .. A-048**; one new table column group (`auth_tokens.pending_*`) and one new index; three new endpoints (`/auth/external/link-intent`, `/auth/email/change/verify`, the `/account-deletion-requests/steam/*` pair); two new email templates; one changed migration ordering; one changed slice ordering; 28 new or rewritten tests.

### Round 2 — adversarial review (security / spec / architecture lenses)

14 findings. **All 14 accepted and fixed; none rebutted.** Every claim was re-verified against the code and the spec before acting (`src/PartyUp.Domain/Users/User.cs:275,297-319`, `src/PartyUp.Domain/Visibility/ProfileVisibility.cs:42`, `src/PartyUp.Domain/Visibility/LookupVisibility.cs`, `tests/PartyUp.Application.Tests/PartyUp.Application.Tests.csproj`, `ARCHITECTURE.md:36-38`); the verification is cited inline in each fix. One-line disposition per finding, in the order received:

| # | Lens / severity | Finding | Disposition |
|---|---|---|---|
| 1 | security / blocking | A-046's `pending_password_hash` is a trojan-identifier pre-hijack: the attacker chooses the password, the victim verifies the mailbox | **Fixed** — A-046 rewritten to option (a): `POST /auth/register` takes **no password**, `pending_password_hash` is deleted from `auth_tokens` (§5.3, migration 3), and the credential is collected and hashed **inside** the `POST /auth/email/verify` transaction by the party that proved the mailbox. The A-029 "issuing consumes outstanding tokens" rule — which turned this into denial-of-registration and denial-of-recovery — is split by initiator: anonymously-initiated purposes leave outstanding tokens alive (bounded by §7.6 rate limits), authenticated-subject purposes still consume. Three new tests incl. `VerifyEmailUseCaseTests.Execute_TakesThePasswordFromTheRedeemerNotTheRequester_D016`. |
| 2 | security / important | The Steam public-deletion route destroys account access from an unconfirmed GET chain | **Fixed** — §7.4: the Steam callback is now **non-destructive**. It verifies the assertion (§6.4 steps 1–5), mints a 10-minute `DeletionRequestConfirmation` token bound to the resolved `UserId`, and redirects to `/account-deletion/confirm#token=…`; the antiforgery-protected `POST /account-deletion-requests/confirm` is the single place the public route calls `RequestDeletion`. `ConfirmSteamDeletionUseCase` is deleted; `ConfirmPublicDeletionUseCase` serves both legs. Tests renamed to assert the two-step, incl. one that the account is still `Active` after the callback. |
| 3 | security / important | Link, unlink and set-password need no re-auth and notify nobody — the exact A-048 threat, unfixed | **Fixed** — new **A-049**: re-authentication (current password, or a 5-minute `Reauthentication` token from `?mode=reauth`) is required on `link-intent`, `DELETE /auth/methods/{kind}`, `password/set`, `password/change` and `email/change`; the stamp rotates on `password/set` too; two new templates (`AuthMethodAdded`, `AuthMethodRemoved`) notify the address on file. `POST /auth/reauth` and `AuthTokenPurpose.Reauthentication` added; six new tests. |
| 4 | spec / blocking | A-047 lets an Onboarding user reach Active without a profile: `CancelDeletion` is untouched | **Fixed** — verified `User.cs:297-319` (unconditional `Lifecycle = Active`). §4.1: `CancelDeletion` now sets `IsProfileComplete ? Active : Onboarding`, with `IsProfileComplete` added to `User` as a derived predicate; DOMAIN_MODEL §3 gains the `PendingDeletion → Onboarding` return edge alongside A-047's entry edge. The second face is fixed too: A-026's `RequireOnboarded` now tests `IsProfileComplete`, not just the lifecycle enum, and A-024's projection and `CurrentUser` carry it. Five new tests across §14.1/§14.2/§14.3. |
| 5 | spec / blocking | A-043's `ProfileVisibility` rule leaves banned users resolvable by profile lookup, and contradicts its own test | **Fixed** — verified `ProfileVisibility.cs:42` (`viewer.IsFriendOf(subject.Id) ? Full : UsernameOnly`, no relationship filter). The guard swap is withdrawn and replaced by an explicit branch: a banned subject is `Hidden` unless the viewer is self or an **accepted friend**. `/u/{username}` resolution is stated to be gated by this function and to 404 on `Hidden`. A-043's table, its rationale, §1.3 row 7, the §17 A-043 row and both `ProfileVisibilityTests` now agree. |
| 6 | spec / important | Magic-link account creation is a third D-083/D-093 door with no D-numbered test | **Fixed** — §14.2 adds `ConsumeMagicLinkUseCaseTests.Execute_RejectsAnUnattestedAgeGateOnAccountCreation_D083` (asserting no `users`/`auth_users` row **and** that the token is not consumed), `..._RecordsTheAgeGateSourceWithoutABirthDate_D093`, and `..._SkipsTheAgeGateWhenAdoptingAnExistingAccount_D083` to pin the deliberate adoption-branch exemption. §14.5 requires D-083's cell to name all three doors. |
| 7 | spec / important | Several §14.2 tests cannot live in `PartyUp.Application.Tests` as the section declares | **Fixed** — verified the csproj references only `src/PartyUp.Application`. A §14 preamble table gives each misfiled test a home: `AdminSeederTests` (×3), the unlink-concurrency test and the two `auth_tokens`/credential-purge assertions move to **§14.3**; the template renderer **moves to `Application/Email/`** (A-041 — it is pure, I/O-free, and the templates are already Application resources), so its three tests stay put. No fourth test project, no ARCHITECTURE §2 amendment. |
| 8 | spec / important | The named D-127 Application test cannot verify what D-127 decides | **Fixed** — renamed to `CompleteOnboardingUseCaseTests.Execute_ActivatesTheAccountOnlyWithACompleteProfile_D127` with the reason stated (A-033 puts ordering on the client wizard), and §14.5 now requires D-127's traceability cell to name **both** it and `OnboardingWizard.test.tsx`, the artifact that actually pins the order. |
| 9 | spec / important | Unlinking a Steam *platform* link has undefined effects on `steam_identities` and the auth login | **Fixed** — A-030 gains the second direction: both routes delegate to one `UnlinkSteamUseCase` that removes the `platform_links`, `steam_identities` **and** `auth_user_logins('Steam', …)` rows atomically, under the same `FOR UPDATE` lock, the same D-137 last-method guard (409 from the platform route too) and the same A-049 re-auth. Satisfies PRODUCT_SPEC §8.6's "revoke their data access". Two new tests; §7.3's row updated. |
| 10 | spec / important | D-096 text policy is not wired into the gaming-handle endpoints | **Fixed** — A-036 now enumerates its complete Phase 2 call-site list (including `POST`/`PATCH /me/platform-links`), §7.3 annotates both rows with the before-the-domain-call ordering, and `TextPolicyTests.Check_RunsBeforeAGamingHandleReachesTheDomain_D096` asserts a rejected handle never reaches `User.LinkPlatform`/`ChangePlatformHandle`. |
| 11 | architecture / important | The post-authentication gate is placed in Api but tested (and transactionally scoped) as an Application use case | **Fixed** — §6.6 splits it: `PartyUp.Application/Accounts/PostAuthenticationGateUseCase.cs` owns the transaction and every domain call and returns `PostAuthenticationResult`; `Api/Auth/AuthenticateAndSignIn.cs` becomes a thin adapter that calls it, then `SignInAsync`. The one-file `SignInAsync` review rule survives and gains a companion (the gate use case is called from exactly that file), and every §14.2 D-158/D-148/D-180 assertion is now writable where it is filed. §8.1 and slice 2 updated. |
| 12 | architecture / important | Slice 4 cannot ship registration: A-046 makes it depend on slice 5 artifacts | **Fixed** — the slice plan is re-derived from A-046. New **slice 4** ships the token pipeline, the email port, the renderer and the templates (migration `AddAuthTokens`); **slice 5** ships registration + verification + login + onboarding + recovery, so `RegistrationFlowTests` can actually reach `Active` inside its own slice. Slices 6–12 are unchanged; the SMTP adapter moved into slice 5 with the flows that need it. |
| 13 | architecture / important | §14.2 assigns tests to `PartyUp.Application.Tests` for types living in Infrastructure or Api, and no Infrastructure test project exists | **Fixed** — same change as finding 7; each of the seven entries is dispositioned explicitly in the §14 preamble table, and the design still adds no test project. |
| 14 | architecture / important | `ISecurityTokenService` cannot carry the pending-registration payload A-046 requires | **Fixed** — A-029 replaces the positional `IssueAsync` with `IssueAsync(IssueTokenRequest, ct)` carrying `PendingRegistration?` and `RequestIpHash?`, and defines `IssuedToken`/`TokenRedemption`/`TokenRedemptionFailure` in full. The "where is the password hashed" ambiguity is resolved by removing the password from the token entirely (finding 1): hashing happens once, at redemption, inside `IUserAccountGateway.CreateAccountAsync` — Identity's own territory. |

Net additions from round 2 (the round-3 draft): **A-049**; one deleted column (`auth_tokens.pending_password_hash`) and one new `AuthTokenPurpose` member; two new endpoints (`POST /auth/reauth`, `GET /auth/external/{provider}/start?mode=reauth`); two new email templates; one deleted use case (`ConfirmSteamDeletionUseCase`) and four new ones (`PostAuthenticationGateUseCase`, `ReauthenticateUseCase`, `IssueLinkIntentUseCase`, `UnlinkSteamUseCase`); one new slice and one re-derived slice; 26 new, moved or renamed tests.

### Round 3 — adversarial review (security / spec / architecture lenses)

13 findings. **All 13 accepted and fixed; none rebutted.** Every claim was re-verified before acting — `src/PartyUp.Api/Program.cs:54` (`UseSerilogRequestLogging`) and `:71` (`MapFallbackToFile`), `src/PartyUp.Infrastructure/DependencyInjection.cs:13` (`npgsql.UseNodaTime()`), `src/PartyUp.Domain/Users/User.cs:85,88`, `src/PartyUp.Domain/Visibility/ProfileVisibility.cs`, `tests/PartyUp.Application.Tests/PartyUp.Application.Tests.csproj` — and the verification is cited inline in each fix. (One immaterial correction to a finding's citation: the A-044 review pipeline lines are `Program.cs:54`/`:71`, not `:52`/`:69`; the substance is unaffected and the design already carried the right numbers.) One-line disposition per finding, in the order received:

| # | Lens / severity | Finding | Disposition |
|---|---|---|---|
| 1 | security / blocking | `mode=login` external start is an unprotected GET: forced sign-in and silent account creation with an attacker-supplied age-gate attestation | **Fixed** — new **A-050** (replacing A-045 step 3): *every* external `/start`, in every mode, requires a one-time `ExternalStartIntent` minted by the antiforgery-protected `POST /auth/external/intent`; `mode`, `returnTo` and `ageGateAttested` are read **only** from the redeemed intent, and `/start` reads nothing from its own query string. `CompleteExternalSignInUseCase` may create an account only when the *intent* carried `ageGateAttested == true` — the "identity proved by the provider, consent proved by the POST" split §7.4 already uses. `AuthTokenPurpose.ExternalLinkIntent` → `ExternalStartIntent`; `auth_tokens` gains a non-credential `payload jsonb`; `IssueLinkIntentUseCase` → `IssueExternalIntentUseCase`; §12.1's provider buttons become POST-then-navigate; A-031's GET carve-out and §6.3/§6.4 start routes rewritten. Five new/rewritten tests, incl. `ExternalIntentTests.Start_IgnoresQueryStringModeReturnToAndAttestation`. |
| 2 | security / important | The public-deletion Steam leg cannot reuse §6.4 steps 1–5: the `pu.ext` path and pinned `return_to` do not match its routes | **Fixed** — new **A-051**: per-flow state cookie name **and** path (`pu.ext` at `/api/v1/auth/external/steam`, `pu.extdel` at `/api/v1/account-deletion-requests/steam`), per-flow data-protection purpose, a `flow` discriminator inside the protected payload that each callback asserts, and a per-flow pinned `return_to`. §6.4 steps 1 and 3 and the §7.4 rows rewritten accordingly. Two new integration tests asserting each flow's cookie is rejected by the other's callback. |
| 3 | security / important | Account lockout is an unenumerated fourth login outcome, defeating §6.1's response equalization | **Fixed** — §6.1 now enumerates **four** negative outcomes: the `IsLockedOut` branch returns the byte-identical `401 invalid_credentials`, emits no `Retry-After` or other distinguishing header, and **still calls `VerifyDummyPasswordAsync`** (the timing half is the sharpest: a locked-out account otherwise short-circuits before PBKDF2). §7.6 adds the matching exception (only the IP/email-hash limiter may 429 on `/auth/login`). Test extended to four cases plus `LoginEnumerationTests.Login_AgainstALockedOutAccount_IsIndistinguishableFromAnUnknownAddress_D067`. |
| 4 | security / important | `POST /me/deletion` is exempt from A-049 re-authentication — the largest cookie-to-permanent-harm conversion on the surface | **Fixed** — A-049 item 1 extended to `POST /me/deletion` (current password or `reauthToken`, 409 `reauth_required` otherwise, `reauth` rate limit); §7.3's row and §9.1 updated. §9.1 now states the address-less notification gap explicitly: a D-078 account receives no `DeletionRequested` mail at all, so the request-time proof is the *only* control between a stolen cookie and irreversible tombstoning — recorded so nobody later relaxes it on "the email covers it" grounds. Two new tests. |
| 5 | security / important | `?mode=reauth` must return an access-granting token through a redirect, but the delivery mechanism is unspecified and contradicts §6.3 and A-044 | **Fixed** — A-049 item 1 specifies it: `/auth/external/complete` redirects to **`/auth/callback#reauth=<raw>&status=reauth_ok`**; the SPA `replaceState`-clears the fragment before any render and holds the value in memory, exactly as §12.1 already requires for `/auth/magic`. §12.1's `/auth/callback` and §7.1's row updated. New route-level test `ExternalRedirectTests.NoRedirectLocationFromAnExternalLegContainsARawTokenInItsQuery`, the sibling of `Render_PutsTheTokenInTheFragment_D067`. |
| 6 | security / important | The A-049 re-authentication endpoints are unthrottled password oracles | **Fixed** — §7.6 gains a **`reauth` policy, 5 / 15 min / user** (partitioned on the authenticated `UserId`, not the IP, because the attacker already holds the session), applied to all seven proof endpoints; and A-049 item 1 requires `IUserAccountGateway.CheckPasswordAsync` to check and increment the **same** Identity lockout counters as `CheckPasswordSignInAsync`, so `/auth/reauth` and `/auth/login` cannot be played against each other. Locked-out re-auth returns the same 409 as a wrong password. New test `ReauthenticationTests.Reauth_SharesTheLoginLockoutCounters_D067`. |
| 7 | spec / blocking | A-048 and A-049 contradict each other on whether external re-authentication ships in Phase 2; the A-048 reading makes D-087/D-067 unreachable for D-078 social-only accounts | **Fixed** — the stale A-048 step 1 sentence is struck and replaced: **both proof branches ship**, A-049's external branch is authoritative, and the consequence of the stale reading is written down (three Confirmed decisions — D-087, D-067, D-137 — permanently 409 for a whole account class). Scheduling fixed too: §15 **slice 6** now ships `POST /auth/external/intent`, the intent gate and the `?mode=reauth` completion, and **slice 7** the Steam half — both before slice 8's unlink/link-intent/set-password. |
| 8 | spec / important | §10.1 says the ban hides users via `IsDiscoverable`, contradicting §4.1/A-043 | **Fixed** — the stale round-1 bullet is replaced by the A-043 three-predicate statement (`LookupVisibility.IsListed`, `User.CanBeFriended`, the explicit `ProfileVisibility.For` branch) with the explicit warning that **`User.IsDiscoverable` is deliberately untouched** (`src/PartyUp.Domain/Users/User.cs:85,88`), because adding `&& !IsBanned` there would 404 a banned profile to the user's own accepted friends and break `ProfileVisibilityTests.For_KeepsABannedUsersProfileVisibleToAnExistingFriend_D180`. |
| 9 | spec / important | D-078's only test is filed in §14.2 but asserts endpoint reachability, which `PartyUp.Application.Tests` structurally cannot do | **Fixed** — the test is **split**: `SocialOnlyAccountTests.Execute_CompletesOnboardingWithNoEmail_D078` stays in §14.2; `SocialOnlyAccountReachabilityTests.EveryMeSurface_ForAnAddresslessAccount_Succeeds_D078` moves to §14.3, where a real `auth_users` row with a null email exists. The §14 preamble table gains the row it was missed from, and §14.5 requires D-078's cell to name **both** artifacts. |
| 10 | spec / important | The Google account-creation branch never says what happens to the verified email, and `/auth/email/change` on an account with no current address is undefined | **Fixed** — new **A-045 step 6** pins the provider-CREATE email policy in a table (verified Google address → `Email`/`EmailConfirmed = true`; unverified Google or Steam → address-less D-078 account), with in-transaction re-resolution so the partial unique index can never surface as a 500 on a sign-in path. A-048 step 1 gains the **first-address case**: `/auth/email/change` is the route (there is no `/auth/email/set`), re-auth still required, the old-address notice is a no-op when there is none. Five new tests. |
| 11 | architecture / important | Slice 2 cannot compile: it consumes Application artifacts scheduled for slice 5 | **Fixed** — `Application/Abstractions/{ICurrentUserAccessor,CurrentUser,ICurrentUserProjectionReader,CurrentUserProjection,IUserAccountGateway,IAccountSessionService}` plus `IUserRepository`/`IUnitOfWork` and `PostAuthenticationGateUseCase` move **into slice 2**; slice 5's list is reduced to the ports it actually introduces. Slice 2 also now states its DI registrations explicitly so `AddInfrastructure`/Api resolve at the end of the slice. |
| 12 | architecture / important | `ICurrentUserAccessor` and the stamp validator put an EF two-table projection query in the composition root, contradicting §8.2 | **Fixed** — new **A-052**: `ICurrentUserProjectionReader` (Application) implemented by `Infrastructure/Persistence/CurrentUserProjectionReader.cs`; `Api/Auth/CurrentUserAccessor.cs` keeps only claims parsing, the request-scoped cache and the projection→`CurrentUser` mapping, and the validator reads the stamp off that same cached projection, so A-025's one-query-per-request cost is unchanged. A-024 rewritten to match; §8.2's port list updated; the query becomes reachable from the integration project. |
| 13 | architecture / important | Identity's `DateTimeOffset` columns under the Npgsql NodaTime plugin are unspecified | **Fixed** — §5.2 pins it: `AuthUserConfiguration` maps `LockoutEnd` with an explicit `DateTimeOffset? ↔ Instant?` value converter onto `timestamptz`, so the plugin sees the type it maps natively and no provider default is relied on (verified `src/PartyUp.Infrastructure/DependencyInjection.cs:13`). A-034 now points at it. New integration test `MigrationTests.Migrate_RoundTripsAnIdentityLockoutEnd`, landing in slice 2 with `AddIdentitySchema`. |

Net additions in the round-4 draft: **A-050, A-051, A-052**; one new `auth_tokens` column (`payload jsonb`) and one renamed `AuthTokenPurpose` member (`ExternalLinkIntent` → `ExternalStartIntent`); one renamed endpoint (`/auth/external/link-intent` → `/auth/external/intent`, now covering all three modes) and one renamed use case (`IssueLinkIntentUseCase` → `IssueExternalIntentUseCase`); one new rate-limit policy (`reauth`); one new port (`ICurrentUserProjectionReader`); two re-scoped slices (2 and 5) and two re-scoped slices (6 and 7); 20 new, moved, split or rewritten tests. No new packages, no new migrations, no new test project.


### Round 4 — adversarial review (security / spec / architecture lenses)

19 findings — seven security, four spec, eight architecture. **All 19 accepted and fixed; none rebutted.** Every claim was re-verified against the code and the spec before acting — `tests/PartyUp.Application.Tests/PartyUp.Application.Tests.csproj` (one `ProjectReference`, to `src/PartyUp.Application`), `ARCHITECTURE.md` §2 (exactly three test projects), `PRODUCT_SPEC.md:559` (passwordless email is a first-class D-067 method), `PRODUCT_SPEC.md:570,579` (D-078, D-087), and this document's own A-021 (default `UserStore`, hence `AutoSaveChanges = true`) and A-052. One-line disposition per finding, in the order received:

| # | Lens / severity | Finding | Disposition |
|---|---|---|---|
| 1 | security / blocking | `mode=reauth` never binds the external assertion to the caller's account — A-049's proof is forgeable with the attacker's own provider account | **Fixed** — A-045 step 4 rewritten from a `link`-only note into a per-mode table. `reauth` now requires **both** an authenticated `pu.auth` whose `UserId` equals the intent's bound `UserId` **and** `FindUserIdByLoginAsync(provider, providerKey)` returning that same `UserId`; failure is `?error=reauth_identity_mismatch`, minting nothing, linking nothing, creating nothing, and never falling through to the `link`/`create` branch. The `Reauthentication` row's `user_id` is re-checked against the calling principal at replay (409 `reauth_required`, no distinct code). §6.4 step 6 now states that `reauth` is outside the login/link/create branching. Three new §14.3 tests, incl. `ReauthenticationTests.ExternalReauth_WithAnUnlinkedProviderIdentity_MintsNoToken_D067` and the symmetric cross-cookie case. |
| 2 | security / blocking | `ExternalStartIntent` is not bound to the browser that minted it, so A-050 does not stop the forced provider navigation | **Fixed** — A-050 gains a browser binding: `POST /auth/external/intent` also sets `pu.intent` (`HttpOnly`, `Secure` outside dev, **`SameSite=Strict`**, `Path=/api/v1/auth/external`, 10 min) carrying the intent row's `id`; `/start` refuses **400 `intent_required`** — the same code and shape as a missing intent, so nothing is learnable — unless the cookie is present and matches the redeemed row, and it deletes the cookie in the same response. `Strict` is the load-bearing attribute (a cross-site-initiated top-level GET does not carry it, unlike `Lax`), and it is affordable here precisely because `pu.intent` is never needed on a provider *return* leg. The reasoning for why the round-3 antiforgery POST alone did not suffice is written into A-050. Two new tests, incl. `ExternalIntentTests.Start_WithAnIntentMintedInAnotherBrowser_IsRejected`. |
| 3 | security / important | The specified single-use redemption `UPDATE` does not filter on `purpose`, contradicting A-029's own cross-purpose guarantees | **Fixed** — A-029's redemption statement is now written out in full with **`AND purpose = @purpose` inside the `WHERE` clause** (not a post-read check, which would reintroduce the double-redemption race the single statement exists to prevent), and a purpose mismatch returns `TokenRedemptionFailure.NotFound`, so it is unobservable and consumes nothing. The concrete cross-purpose replays that were reachable — a `MagicLink` value at `/auth/password/reset`, an `ExternalStartIntent` value at `/account-deletion-requests/confirm` — are named in the text. New test `SecurityTokenServiceTests.Redeem_RefusesATokenOfAnotherPurpose_D067` (§14.3). |
| 4 | security / important | Anti-enumeration is equalized on register and login but not on `/auth/password/forgot` or `POST /account-deletion-requests` | **Fixed** — both halves of the finding's remedy ship. Two counterpart templates, `PasswordResetNoAccount` and `DeletionRequestNoAccount` (the `ExistingAccountNotice` pattern inverted), so every branch of both routes does exactly one render and one send — the shape `/auth/magic-link` already had; **and** §6.1/§8.3 now state that transactional mail is dispatched off the request path through `IEmailDispatcher`, a bounded `System.Threading.Channels` queue drained by one hosted service (no queue table, no package), so an SMTP round trip is never on a response's critical path. Two new §14.3 enumeration tests. |
| 5 | security / important | No retention or purge for `auth_tokens` rows holding raw email addresses that never become accounts | **Fixed** — new **§9.7**: a time-based purge (`expires_at`/`consumed_at` older than `Auth:TokenRetention`, default 30 days) on the **existing** `DeletionSweepHostedService` tick (A-037, every 15 minutes — no new machinery), in its own transaction, bounded by `ix_auth_tokens_expires_at`, an index §5.3 created for a purge the design never defined. It keys on expiry/consumption rather than creation so no live recovery, verification or intent flow can be broken by it. Hashing the address instead was considered and rejected in writing (it breaks §6.2's in-transaction re-resolution and §9.5's match). Config key added to §13.1; new test `AuthTokenRetentionTests.Purge_RemovesExpiredAndConsumedRowsIncludingPreAccountAddresses_D067`. |
| 6 | spec / important | §14.2's first D-016 test still asserts the round-1 behavior A-046 abolished | **Fixed** — renamed to `RegisterWithPasswordUseCaseTests.Execute_AcceptsARegistrationRequestFromAnyone_D016`, asserting only that self-service registration is open to any address and that a token is issued. §14.5 gains an explicit instruction that D-016's traceability cell must cite the new name, since the stale entry was the *first* D-016 row and therefore the one the cell would have quoted. |
| 7 | spec / important | `SecurityTokenServiceTests` is filed in `PartyUp.Application.Tests` but `SecurityTokenService` lives in Infrastructure | **Fixed** — verified the csproj again; the entry is dispositioned in the §14 preamble table exactly as its seven round-2 and one round-3 siblings were, and **moved to §14.3** together with the single-use, TTL and round-4 cross-purpose cases named in slice 4 (all of which need real rows and a real `UPDATE … RETURNING` anyway). Slice 4's test line updated. The preamble also gains a standing rule — if an assertion in the test's *name* is not expressible over a use case's inputs and return value with in-memory fakes, it belongs in §14.3 — so the fourth sweep is the last. |
| 8 | spec / important | Three §14.2 entries assert HTTP-level facts the Application test project structurally cannot observe | **Fixed** — each is split into a use-case half and an HTTP half, per the preamble table. §14.2 keeps the identical *result object* plus no session-issuing call on both register branches, a new `..._IssuesTheSameWorkOnBothBranches_D016` pinning §6.1's cost-equality claim, and the identical failure result plus dummy verify in all four login cases; §14.3 gains `RegistrationEnumerationTests.Register_ForAKnownAndAnUnknownAddress_AreByteIdentical_D016` and `RegistrationEnumerationTests.Register_WithAPasswordField_Returns400_D016`, with the login header-set half already carried by `LoginEnumerationTests`. |
| 9 | spec / important | A-049's `AuthMethodAdded` notification is pinned on the intent use case, which links no method; the set-password and Steam-unlink notifications have no test at all | **Fixed** — the assertion moves to `CompleteExternalSignInUseCaseTests.Execute_NotifiesTheAccountAddressWhenAMethodIsLinked_D087` (the intent use case only mints a row and is frequently abandoned, so pinning it there would specify mail for flows that linked nothing *and* leave the linking path untested), and the two missing obligations are added: `SetPasswordUseCaseTests.Execute_NotifiesTheAccountAddress_D087` (A-049 item 3's "or a first password was set") and `UnlinkSteamUseCaseTests.Execute_NotifiesTheAccountAddress_D137` (the shared path both unlink routes use, §7.3 included). A-049's own test list is updated to match. |
| 10 | architecture / blocking | A magic-link-only account can satisfy neither A-049 re-auth branch — D-087, D-067 and in-app deletion become permanently unreachable for it | **Fixed** — **both** resolution shapes the finding offers are taken, because they close different faces. (a) A-049 item 1 becomes a three-branch table with a new **`POST /auth/reauth/email`**: an emailed `Reauthentication`-purpose token to the confirmed address, same TTL, same single use, same `reauthToken` replay, reusing the whole A-029/A-044 pipeline; the link lands on a new SPA route `/auth/confirm-identity` that holds the value in memory only and renders the pending action in that same tab. (b) §6.5 makes `magicLink` a first-class `kind`, derived from `EmailConfirmed = true` plus an address on file, counted in the D-137 last-method count on every path that computes one, exposed with a `removable` flag, and answered **400 `method_not_removable`** on a direct unlink — which also corrects the count's error in the other direction, where an email-confirmed account could be refused a "last" unlink that magic link would have survived. The consequence is stated rather than left to be rediscovered: the last-method 409 is now reachable only for the address-less D-078 class. Template `ReauthenticationRequested`, one new endpoint, §7.6 and §13.1 rows, §14.2/§14.3 tests, and the D-067/D-087/D-137 traceability instructions all move together. |
| 11 | architecture / important | Slice 7 ships the `/me/platform-links` routes while their use cases are scheduled for slice 8 | **Fixed** — `Profile/{LinkPlatform,ChangePlatformHandle,UnlinkPlatform}UseCase` and `Accounts/UnlinkSteamUseCase` move **into slice 7** (the smaller change, since slice 7 needs `UnlinkSteamUseCase` for its own Steam flow); slice 8's file list is reduced accordingly. The unsafe repair the gap invited — deleting the `platform_links` row inline, bypassing A-030's atomic three-row removal and the D-137 guard for one slice — is written into the slice note so nobody re-derives it. The four use cases depend only on ports landing in slices 2 and 5, so nothing moves backwards. |
| 12 | architecture / important | `IUnitOfWork` semantics are never reconciled with Identity's `UserStore.AutoSaveChanges` | **Fixed** — new **A-053**, stating all three rules the finding names: `ExecuteAsync` begins and commits a real `IDbContextTransaction` (joining, never nesting, if one is already open); every gateway implementation resolves the **same** scoped `AppDbContext` so `UserManager`'s auto-saves enlist in it; and every failed `IdentityResult` throws a `DomainException` that aborts the transaction — without which `PurgeCredentialsAsync` can silently no-op on a concurrency-stamp mismatch while the tombstone and journal row commit, leaving live credentials on a "deleted" account. Two new §14.3 tests (`UnitOfWorkTests.Rollback_UndoesAnIdentityAutoSave_D165`, `DeletionPurgeTests.Sweep_WithAFailingCredentialPurge_LeavesNoTombstone_D092`); slice 2's file list updated. |
| 13 | architecture / important | A-025's "one query per authenticated request" cost claim is not reconcilable with re-issuing the principal, and the alternative silently freezes role claims | **Fixed** — one implementation is picked and written down: `PartyUpSecurityStampValidator` is a **from-scratch `ISecurityStampValidator`** that compares the projection's stamp to the principal's claim, calls `RejectPrincipal()` plus `SignOutAsync` on mismatch, and does **nothing** on match — explicitly *not* derived from `SecurityStampValidator<TUser>`, whose `ValidatePrincipalAsync` re-issues via `UserManager.GetUserAsync` and `SignInWithClaimsAsync` and would add an Identity user load and a role read to every authenticated request. The per-response `Set-Cookie` paragraph is struck. The frozen-role consequence is closed rather than accepted: **every role grant and revoke rotates the security stamp**, so `AdminSeeder` (A-039) signs the stale principal out on its next request and the next sign-in carries the role — "immediately" stays true through the mechanism A-025 already owns, and an idempotent re-seed rotates nothing. Two new tests. |
| 14 | security / minor | Google's `CorrelationCookie.SameSite = None` is justified by a rationale A-028 itself contradicts | **Fixed** — §6.3 sets `CorrelationCookie.SameSite = SameSiteMode.Lax` (`SecurePolicy = Always` outside Development, `SameAsRequest` in it), with the round-3 rationale corrected in place: the Google return leg is a **top-level GET redirect**, which `Lax` allows — the identical reasoning A-028 gives for `pu.auth` and §6.4 gives for `pu.ext`, both of which already cross that leg on `Lax`. `None` would have exposed the correlation nonce in every cross-site subresource context and left three sibling cookies on three policies. A future `form_post` provider would get a named exception, not a relaxed default. New test `ExternalRedirectTests.GoogleChallenge_SetsTheCorrelationCookieSameSiteLax`, which also asserts that **no** Phase 2 cookie is `SameSite=None`. |
| 15 | security / minor | Rate-limit numbers contradict between §6.1/§6.2/§7.1/A-046 and the §7.6 policy table | **Fixed** — §7.6 is made the single authority and absorbs every number that previously lived only in prose: `auth-ip` (20/15 min/IP) is now joined by **`login-email` (10/15 min), `register-ip` (5/h), `register-email` (3/h), `magic-link-ip` (20/h) and `deletion-request` (3/h/email + 10/h/IP)**, which is what the three disagreeing prose statements actually required; `auth-email` keeps 5/h/email. Every other mention — §6.1's timing paragraph, §6.2, A-046 step 5, and the §7.1/§7.4 `RL:` cells — now cites **policy names only, never numbers**, so the two can no longer drift. New guard test `RateLimitTests.EveryPolicyNamedInTheDesignIsRegistered` (a route silently falling through to `default` was the concrete failure mode). |
| 16 | architecture / minor | A-042 references a `providers` field on `GET /auth/session` that `SessionResponse` does not have | **Fixed** — `providers` is declared in the §7.1 `SessionResponse` contract with its semantics pinned: a **deployment capability list**, computed at startup from configuration (`password`/`magicLink` always, `google` iff `Google:ClientId` is set, `steam` iff the handler is registered), present and identical on the anonymous `{authenticated:false}` body because the sign-in page needs it before a session exists. Per-*account* linkage stays on the authenticated `GET /auth/methods` (§6.5), the only place `linked`/`removable` appear. Without the member, A-042's mechanism had nowhere to live and §7.7's `.Produces<SessionResponse>()` would have generated a client type missing the field. A-042's bullet and the §7.1 row updated; new test `SessionEndpointTests.Session_ForAnAnonymousCaller_ListsTheConfiguredProviders` in slice 3. |
| 17 | architecture / minor | A-039's seeder has no port to create the `admin` role row, and slice 2's Identity registration omits role support | **Fixed** — `IUserAccountGateway` gains **`EnsureRoleExistsAsync(string role, ct)`** (idempotent), keeping `RoleManager<AuthRole>` out of Application per §8.2, and `AddToRoleAsync` now returns `bool` (*did this grant change anything*) because A-025's stamp rotation is conditional on exactly that. Slice 2's registration line becomes `AddIdentityCore<AuthUser>()`**`.AddRoles<AuthRole>()`**`.AddEntityFrameworkStores<AppDbContext>()`, with the consequence written into A-039: without `.AddRoles`, there is no `IRoleStore`/`RoleManager<AuthRole>` **and** no role-aware claims factory, so `ClaimTypes.Role` (A-024) would never reach the principal and `RequireAdmin` would be unsatisfiable for everyone — a silent 404-for-all, not a startup error. New test `AdminSeederTests.Seed_CreatesTheAdminRoleRowWhenAbsent_D177`, which fails on either half. |
| 18 | architecture / minor | §4.1 specifies adding `User.IsProfileComplete`, which already exists in the code with the same definition | **Fixed** — verified at `src/PartyUp.Domain/Users/User.cs:90-91` (`Username.HasValue && DisplayName.HasValue && Avatar.HasValue && TimeZone is not null && Locale.HasValue`). §4.1 now states that Phase 2 **consumes** the predicate rather than adding it — an implementer following the round-3 wording literally would have written a duplicate member and, under `TreatWarningsAsErrors`, broken the build — and §2 ("current state verified against the code") gains the bullet it was missing, alongside the `IsDiscoverable`/`CanBeFriended` current-state note (`:85`, `:88`) that §4.2 amends. No behavioral change; the D-158/D-127 tests involved are unchanged. |
| 19 | architecture / minor | `ForwardedHeaders` is named as required but is assigned to no slice and no OWNER-ACTION row | **Fixed** — §7.6's throwaway note becomes a specified item in **slice 3**, next to `RateLimitPolicies.cs`: `UseForwardedHeaders` ahead of authentication, rate limiting and `UseSerilogRequestLogging`; `XForwardedFor | XForwardedProto`; `KnownProxies`/`KnownNetworks` **cleared and set** from the new `Network:TrustedProxies` config key (§13.1 — the framework default trusts loopback only, which is not where Caddy is); and `ForwardLimit = 1`, so a caller-supplied extra hop cannot be used to escape a limit. New **OWNER-ACTION 10** for the compose subnet, a `deploy/` note, and `RateLimitTests.PartitionKey_UsesTheForwardedClientIpNotTheProxy` to stop a later middleware reorder from silently reverting it. Left as-is, every IP partition and every `auth_tokens.request_ip_hash` would have been the Caddy container's address, making that column's stated abuse-forensics justification false. |

*(Rows 1–13 are the blocking and important findings, in the order received. **Rows 14–19 are the six `minor` findings, applied in a second pass**: the first revision loop hit its iteration cap after row 13 and stopped, so the six minors were carried into this pass unapplied rather than dropped — the header count is the count of findings received. Nothing in rows 1–13 was re-opened; each was re-verified as present in the body before rows 14–19 were written.)*

Net additions in the round-5 draft: **A-053**; one new predicate in the token redemption statement (`purpose`) and one new retention purge (§9.7) with its config key `Auth:TokenRetention`; one new cookie (`pu.intent`, the only `SameSite=Strict` cookie in the phase) and one corrected cookie policy (Google's correlation cookie, `None` → `Lax`); one new endpoint (`POST /auth/reauth/email`) and one new SPA route (`/auth/confirm-identity`); one new auth-method kind (`magicLink`) plus a `removable` flag on `GET /auth/methods`; one new `SessionResponse` member (`providers`, A-042); three new email templates (`PasswordResetNoAccount`, `DeletionRequestNoAccount`, `ReauthenticationRequested` — the set goes from ten to thirteen); one new Application port (`IEmailDispatcher`) with its hosted service, one new gateway method (`EnsureRoleExistsAsync`) and one changed gateway signature (`AddToRoleAsync` → `Task<bool>`); five new rate-limit policies (`login-email`, `register-ip`, `register-email`, `magic-link-ip`, `deletion-request`) reconciling §7.6 with the prose; one new config key (`Network:TrustedProxies`) and one new owner action (10, the trusted-proxy subnet); two re-scoped slices (7 and 8) and four amended slices (2, 3, 4, 6); 30 new, moved, split or renamed tests. No new packages, no new migrations beyond the five already listed, no new test project.

### Round 5 — adversarial review (security / spec / architecture lenses)

9 findings — one blocking, eight important — plus 10 minors carried in the same pass. **All 19 accepted and fixed; none rebutted.** Every claim was re-verified against the code and the spec before acting — `src/PartyUp.Domain/Visibility/ProfileVisibility.cs:27-48` (blocked → self → `IsListed` → `IsFriendOf ? Full : UsernameOnly`), `src/PartyUp.Domain/Visibility/LookupVisibility.cs:18-23` (`return candidate.IsDiscoverable;`), §5.2's Identity table list (no admin column on `auth_users`), `PRODUCT_SPEC.md:457` (banned users keep the public deletion route), `IMPLEMENTATION_PLAN.md:125` (D-072's public page filed under Phase 8) — and the verification is cited inline in each fix. One-line disposition per finding, in the order received:

| # | Lens / severity | Finding | Disposition |
|---|---|---|---|
| 1 | security / important | A-029 files `DeletionRequestConfirmation` as an "authenticated-subject" purpose, re-arming the round-2 token-consumption weapon on the D-072 path | **Fixed** — the purpose moves to the **anonymously-initiated** bucket (outstanding tokens left alive) and the false rationale sentence is replaced by the reason it is false: §7.4 mints it from two anonymous routes, one of which A-051/§7.4 deliberately exempt from antiforgery and from the A-050 intent. The consequence the old bucket produced — an attacker naming the victim's address, or forcing `/account-deletion-requests/steam/start` in a browser holding a Steam session, silently consuming the victim's only link, with no notification for the D-078 class (§9.1) — is written down. Secondarily, both `/account-deletion-requests/steam/*` routes gain the §7.6 policy they lacked (**`deletion-steam-ip`, 10/h/IP**), matching every sibling anonymous token-minting route. New test `SecurityTokenServiceTests.Issue_DoesNotConsumeAnotherPartysOutstandingDeletionConfirmation_D072`. |
| 2 | security / important | Anonymous `POST /auth/external/intent` (`mode=login`) carries no rate-limit policy — the one anonymous token-minting route §7.6 left uncovered | **Fixed** — new **`intent-ip` policy, 30/hour/IP**, applied to `POST /auth/external/intent` in **every** mode, stated in §7.6 and on §7.1's row; `reauth` (which partitions on an authenticated `UserId` that `mode=login` does not have) now also covers `mode=reauth`, closing the second half of the same gap and matching A-029's exemption rationale. Left as it was, an anonymous caller could pump ~430k `auth_tokens` rows/day/IP under `default` alone, retained until 30 days past expiry (§9.7) — the exact unbounded-growth surface §5.3's round-4 retention rationale identified. New test `RateLimitTests.AnonymousExternalIntent_IsBoundedByIntentIp`; `RateLimitTests.EveryPolicyNamedInTheDesignIsRegistered` now covers thirteen policies. |
| 3 | spec / important | §14.2 still files the post-authentication-gate assertions under `SignInWithPasswordUseCaseTests` / `ResetPasswordUseCaseTests`, which §6.6 makes structurally unable to hold them | **Fixed** — all five renamed to `PostAuthenticationGateUseCaseTests.*` (ban, tombstone, cancel, elapsed grace), which is where §6.6 puts the logic and the one-caller rule keeps it. The two credential use cases keep one assertion each — `SignInWithPasswordUseCaseTests.Execute_SignsInOnlyThroughTheGate_D180` and `ResetPasswordUseCaseTests.Execute_SignsInOnlyThroughTheGate_D165` — which is the part of the one-door rule those classes *can* verify, so §6.1's "the gate is not optional here" keeps a test. §6.6's own paragraph now records the mis-filing instead of claiming the assertions land "exactly as filed", and §14.5 instructs the D-148/D-158/D-180 cells to name the gate class. |
| 4 | spec / important | `RegisterWithPasswordUseCaseTests.Execute_RecordsTheAgeGateSourceWithoutABirthDate_D093` is stale after A-046 — the sibling defect round 4 fixed for D-016 | **Fixed** — moved to `VerifyEmailUseCaseTests`, the password-registration *creation* door, with the reason stated: after A-046 the register use case writes nothing to `users`/`auth_users`, `IssueTokenRequest`/`PendingRegistration` carry no `AgeGateSource`, and the field is set only by `User.BeginOnboarding` inside the redemption transaction. §14.5 gains an explicit D-093 instruction naming all three creation doors, since this was the first D-093 row and therefore the one a cell would quote. (The cosmetic half — the use case is still called `RegisterWithPasswordUseCase` though the endpoint takes no password — is left alone deliberately: renaming it would touch §8.1, three slices and eight test-class names for no behavioral gain, and A-046's text already states what the endpoint does.) |
| 5 | spec / important | The three round-4 magic-link auth-method tests are cited by §6.5 and required by §14.5 but exist in no §14.2 row | **Fixed** — all three added to the §14.2 table (`ListAuthMethodsUseCaseTests.Execute_ReportsMagicLinkForAConfirmedAddress_D067`, `UnlinkAuthMethodUseCaseTests.Execute_CountsMagicLinkTowardTheLastMethodRule_D137`, `UnlinkAuthMethodUseCaseTests.Execute_RefusesToRemoveMagicLinkDirectly_D137`) and named in **slice 8**'s test line, which is where their use cases and routes land. Without the rows an implementer building from the inventory writes none of them and the D-067/D-137 cells cite artifacts that do not exist. |
| 6 | spec / important | The public deletion pages are simultaneously Phase 2 SPA routes and Phase 8 deliverables | **Fixed, decided for Phase 2** — §7.4's opening is rewritten with the reason the choice is forced rather than preferred: A-044 makes `/account-deletion/confirm` the only possible consumer of the Steam callback's `#token=`, so without it the round-1 blocking gap (banned, email-less D-078 accounts, PRODUCT_SPEC:457 + §8.6) is closed only on paper and §14.3's `PublicDeletionRouteTests` prove an API no user can reach. §1.1, §12.1, §14.5 and **slice 12** now agree — slice 12 gains the two page files, the two routes and `PublicDeletionPage.test.tsx` (§14.4) — and the **closing slice** adds `IMPLEMENTATION_PLAN.md` to the documents it updates, since line 125 files the page under Phase 8. Phase 8 keeps the footer/store-listing surfacing only. |
| 7 | architecture / blocking | A-043's specified `ProfileVisibility` branch returns `Hidden` for an accepted friend of a banned user — it contradicts A-043, §10.1 and its own §14.1 test | **Fixed** — verified the trace against `ProfileVisibility.cs:27-48` and `LookupVisibility.cs:18-23`: the pre-filter is skipped for a friend, control reaches the `IsListed` gate, and row 1 of A-043's own table has just made `IsListed` false for banned users. The branch becomes **total** — `if (subject.IsBanned) return viewer.IsFriendOf(subject.Id) ? Full : Hidden;`, placed after the self branch — so the banned case never reaches the gate; the false inline comment is deleted and replaced by the interaction it missed. A-043's table row, §10.1's third bullet and the §17 record now say "total", and both §14.1 D-180 profile tests pin the two halves. This shipped in slice 1, so it was load-bearing from the first slice. |
| 8 | architecture / important | `DELETE /me/platform-links/{id}` has no re-authentication proof in the contract or the limit table, though A-030 requires it — the route is either permanently 409 or an A-049 bypass | **Fixed** — all three places an implementer reads now carry it: §7.3's row takes `{currentPassword?/reauthToken?}` and can answer 409 `reauth_required` (Steam links only — non-Steam links are not auth methods and need no proof), A-049 item 1 names the route with the two wrong resolutions spelled out, and §7.6's `reauth` row covers it. A-030 gains a round-5 paragraph recording that the obligation must live in the contract, not only in the decision. New test `UnlinkSteamUseCaseTests.Execute_ViaThePlatformLinkRoute_RequiresReauthentication_D137`, in slice 7's use case and slice 8's route. |
| 9 | architecture / important | `is_admin` has no source in A-024/A-025/A-052's two-table projection query | **Fixed** — the third relation is stated once, in full SQL, in **A-024** (an `EXISTS` over `auth_user_roles` joined to `auth_roles` on `normalized_name = 'ADMIN'`, inside the same statement), and **A-025** and **A-052** are corrected to match and to restate the cost as "**one indexed round trip** per authenticated request" rather than "a two-table read" — the three had repeated the same wrong shape verbatim. Without it an implementer either hard-codes `false` (permanently false `SessionResponse.isAdmin`, unsatisfiable admin checks) or adds a second query and silently breaks the claim A-025 spends two paragraphs defending. |

**The ten minors, all applied in the same pass:**

| # | Minor | Disposition |
|---|---|---|
| m1 | A single `pu.intent` name/path breaks the multi-tab case A-029 cites as the reason `ExternalStartIntent` is consumption-exempt | **Fixed** — the cookie carries a **capped list (5) of binder secrets**; `/start` removes only the binder it used and deletes the cookie when the list empties. Two new tests (`Start_WithTwoConcurrentlyMintedIntents_BothSucceed`, `Start_RemovesOnlyTheUsedBinderFromTheIntentCookie`). |
| m2 | §9.7's privacy purge is gated by `Deletion:SweepEnabled`, which the integration factory sets to `false` | **Fixed** — the purge becomes `PurgeExpiredAuthTokensUseCase` (§8.1), gated by `Auth:TokenRetention > 0` alone and invoked from the tick, from `POST /admin/maintenance/deletion-sweep` (whose response gains `tokensPurged`) and directly by its test. A retention control verified by a test that cannot run it is not verified. |
| m3 | A-025's reject-only validator has no specified behavior when the projection read returns `null` — it fails open | **Fixed** — a `null` projection **is** a mismatch: reject and sign out, and `ICurrentUserAccessor.GetAsync` returns `null` so every `Require*` policy fails closed. New test `SecurityStampTests.Response_ForAPrincipalWithNoProjectionRow_IsRejected`. |
| m4 | `pu.intent` is the cookie-value double-submit pattern A-031 explicitly rejects, with only `SameSite=Strict` carrying the load | **Fixed** — the cookie no longer carries the intent row's `id` (which the minting caller already knows) but a server-minted 32-byte binder secret compared against a stored `binderHash` on the row, so an injected cookie value matches nothing. `Strict` stays as the second factor. New test `Start_WithAnInjectedIntentCookieValue_IsRejected`. |
| m5 | §14.5 gives no instruction for the D-093 and D-144 cells although Phase 2 adds tests carrying those numbers | **Fixed** — both added to §14.5's update list with explicit cell contents (D-093's three creation doors; D-144's `FriendCodeFormatTests` and the `2` marker, noting the lookup surfaces are Phase 3). |
| m6 | §7.6's `reauth` policy does not cover `POST /auth/external/intent` in `mode=reauth`, contradicting A-029's exemption rationale | **Fixed** — the `reauth` row now reads `mode=link` **and** `mode=reauth`; all three modes additionally carry `intent-ip` (finding 2). |
| m7 | `SessionResponse.providers` reads as per-account linkage although A-042 defines it as a deployment capability list | **Fixed** — a two-line comment on the field in the §7.1 example says so and points per-account linkage at `GET /auth/methods`. |
| m8 | Which artifact authorizes `RequireAdmin` is unstated, and A-039's ops revocation never rotates the stamp | **Fixed** — A-026 states that `RequireAdmin` reads `CurrentUser.IsAdmin` from the **per-request projection**, never the frozen `ClaimTypes.Role`, with the revocation asymmetry that made it matter (A-039 grants rotate the stamp; ops revokes did not); A-039 adds the runbook stamp rotation and the closing slice adds `RUNBOOK-admin.md`. New test `AdminAuthorizationTests.Admin_AfterAnOutOfBandRoleRevocation_Returns404_D177`. |
| m9 | §14.1's `UserBanTests.Unban_RestoresDiscoverability_D180` names the predicate A-043 and §10.1 forbid touching | **Fixed** — renamed `Unban_RestoresLookupListing_D180`, which is what `Unban` actually restores (`IsListed`, `CanBeFriended`, the `ProfileVisibility` branch). |
| m10 | Two slice-assignment gaps: slice 2's `AuthenticateAndSignIn` needs slice 4's email port, and the `/auth/methods` routes are assigned to no slice | **Fixed** — slice 2 states explicitly that the adapter ships with **no** mail dependency and that the `DeletionCanceled` send is wired in slice 5 (so nobody drags the templates and renderer backwards or stubs a sender that then ships); slice 8's file list gains `GET /auth/methods` and `DELETE /auth/methods/{kind}` in `AuthEndpoints.cs`, alongside the use cases and every D-087/D-137 test that already landed there. |

Net changes in the round-6 draft (this document): **no new decision record** — every finding resolved inside A-024/A-025/A-026/A-029/A-030/A-039/A-043/A-049/A-050/A-052 or inside §7.4/§9.7/§14; two new rate-limit policies (`deletion-steam-ip`, `intent-ip`, taking §7.6 from eleven to thirteen); one changed cookie shape (`pu.intent`: a capped list of hashed binder secrets instead of the row id) and one new `auth_tokens.payload` member (`binderHash`); one new Application use case (`PurgeExpiredAuthTokensUseCase`) and one changed admin response (`tokensPurged`); one changed endpoint contract (`DELETE /me/platform-links/{id}` gains the A-049 proof); one phase reassignment (D-072's two public pages, Phase 8 → Phase 2, with `IMPLEMENTATION_PLAN.md` added to the closing slice); one corrected domain edit (A-043's total ban branch); 22 new, moved or renamed tests. No new packages, no new migrations, no new test project.

### Round 6 — adversarial review (security / spec / architecture lenses)

7 findings — one blocking, six important — plus 11 minors carried in the same pass. **All 18 accepted and fixed; none rebutted.** Every claim was re-verified before acting — `PRODUCT_SPEC.md:457` (banned users keep the public deletion route), `TRACEABILITY.md:119/120/171/175/185` (D-092, D-093, D-144, D-148, D-158 are all **Phase-1** rows with passing domain tests), A-053's two rules against §8.2's batch signature, and A-042's "Steam login still works with no API key". One-line disposition per finding, in the order received:

| # | Lens / severity | Finding | Disposition |
|---|---|---|---|
| 1 | security / blocking | Banned, address-less Google-only accounts have no public deletion route; §7.4's premise is falsified by A-045 step 6 | **Fixed, by option (b) — the class is removed rather than a third public variant added.** A-045 step 6 gains the invariant *no provider may create an account with no address on file unless it also has an assertion-based public deletion leg (§7.4)*; in Phase 2 that is Steam alone. Google's two address-less branches become refusals that write nothing (`?error=link_required` for `email_verified != true`, `?error=email_unavailable` for the address-taken race), so the partial-unique-index protection survives without manufacturing an unreachable account class. §7.4's false premise sentence is replaced by a closed enumeration that is true by construction, and the ops fallback is demoted to a backstop no account class depends on. Tests: `..._RefusesToCreateAnAccountFromAnUnverifiedGoogleEmail_D067`, `..._RefusesToCreateAnAccountWhenTheAddressWasTakenMeanwhile_D067`, `..._CreatesAnAddresslessAccountFromASteamAssertion_D078` (D-078 is now Steam's), and the table-driven invariant `CompleteExternalSignInUseCaseTests.Execute_NeverCreatesAnAddresslessAccountForAProviderWithNoPublicDeletionLeg_D072`, which §14.5's D-072 cell now names. Option (a) — a provider-agnostic `/account-deletion-requests/{provider}/…` leg — was rejected as the larger change for a class that should not exist. |
| 2 | security / important | A-050's binder check is unimplementable through `ISecurityTokenService` without consuming the intent, and two round-4/round-5 tests assert the opposite | **Fixed** — the binder becomes a **predicate in the redeeming statement**: `RedeemAsync(purpose, rawToken, byte[]? binderHash, ct)` and `AND (@binderHash IS NULL OR payload->>'binderHash' = @binderHash)` in the same `WHERE` as `purpose`. A mismatch returns `NotFound`, consumes nothing and stays indistinguishable from a missing intent; `ExternalStartIntent` is the only purpose that supplies a non-null value. This makes `Start_WithAnIntentMintedInAnotherBrowser_IsRejected` and `Start_WithAnInjectedIntentCookieValue_IsRejected` writable as named and removes the burn-a-leaked-intent denial (the weapon round 2 removed from `MagicLink`/`PasswordReset`) without a peek-then-redeem pair, which would have reintroduced the double-redemption race. New test `SecurityTokenServiceTests.Redeem_WithAMismatchedBinder_ConsumesNothing_D067`; A-029 and slice 4 both state it. |
| 3 | security / important | `/auth/callback` is not in the `returnTo` allow-list, so A-049's `#reauth=` transport cannot be produced and the raw proof token would land on `/app` | **Fixed** — A-045 step 5 gains the rule that **every** external completion redirects to `/auth/callback` and to nothing else, with the sanitized destination riding as `?next=`; `/auth/callback` stays out of the allow-list because it is never a user-requested destination, only the fixed landing the server chooses. §6.3, §6.4 step 7, A-049's transport paragraph and §12.1's route text now agree, and §12.1 states that the route `replaceState`-clears the fragment **before any render** and only then navigates to `next`. New assertion `ExternalRedirectTests.EveryCompletionLandsOnTheHashClearingCallbackRoute`. |
| 4 | security / important | §7.1 and §14.3 still give `/start` a query-string `mode`, contradicting A-050's "reads nothing from its own query string" | **Fixed** — the `…/start?mode=reauth` row is deleted and folded into the two provider rows, which now say `intent` is the only parameter read and that the redeemed intent is the sole source of `mode`/`returnTo`/`ageGateAttested`/`userId`. `Start_WithALoginIntentInLinkMode_IsRejected` (unwritable: `/start` has no requested mode to compare against) is replaced by `ExternalIntentTests.Complete_WithAMismatchedIntentMode_LinksNothing_D087` at `/complete`, where A-045 step 4's per-mode table lives; A-045 step 4 gains the explicit rule that the branch is selected by the intent's mode and by nothing else, so no "mode mismatch" code distinguishable from `intent_required` can appear on `/start`. |
| 5 | spec / important | §14.5's instructions overwrite existing Phase-1 rows for D-092, D-093, D-144 (and risk it for D-148/D-158) | **Fixed** — §14.5 opens with an explicit **additive** rule citing the five verified line numbers, and every affected instruction is restated as append-and-keep: D-092 → `1 (domain) / 2 (skeleton) / 8 (verify)`, D-093 → `1 (domain) / 2 (doors)` keeping `UserOnboardingTests…_D093`, D-148/D-158/D-180 → "in addition to, not instead of". **D-144 is marked `1 (format) / 3 (QR + lookup)`, not `2`** — its subject is QR-over-friend-code, which Phase 2 does not deliver; Phase 2 only generates, stores and exposes the code. |
| 6 | architecture / important | Deletion sweep batch vs A-053: `FOR UPDATE SKIP LOCKED` is void and a per-user failure contaminates the next user's transaction | **Fixed** — `FindDueForDeletionAsync` returns **ids only** (§8.2 signature changed), takes no lock and no tracking; `ExecuteDueDeletionsUseCase` opens no transaction of its own; each iteration runs on a **fresh DI scope** so a rolled-back user's dirty aggregate cannot be flushed by the next user's `SaveChangesAsync` (the A-016/A-017 violation the finding identified: a tombstone committed without its journal row). A-037's `FOR UPDATE SKIP LOCKED` claim is struck and re-sited on §9.4 step 1's per-user `FindForUpdateAsync`, which is now stated as the only lock in the sweep. New multi-user test `DeletionSweepTests.Sweep_WhereOneUserFails_CompletesTheOthersAndLeavesNoTombstoneWithoutItsJournalRow_D092`. |
| 7 | architecture / important | `SessionResponse.providers`: the condition for `steam` is undefined and contradicts A-042/A-019 | **Fixed** — new explicit **`Steam:Enabled` (default `true`)** in §13.1, gating the `providers` entry **and** the mapping of the two Steam **sign-in** routes from one flag, so the button and the sign-in routes can never disagree (the public-deletion leg is deliberately outside the flag — see sweep item 6 below). The dead "iff the Steam handler is registered" predicate is gone (there is no handler — A-019 makes the leg hand-rolled), and A-042 restates that `Steam:ApiKey` is enrichment-only and never a gate. New test `SessionEndpointTests.Session_ListsSteamWithNoSteamApiKeyConfigured_D067`. |

**The eleven minors, all applied in the same pass:**

| # | Minor | Disposition |
|---|---|---|
| n1 | A-025's "no `Set-Cookie` on a valid session" is contradicted by A-028's `SlidingExpiration = true` | **Fixed** — the claim is narrowed to "the *validator* writes no cookie header"; A-028's halfway-point renewal is named as the framework's own, claims-preserving and query-free. Test renamed `SecurityStampTests.Response_ForAValidSession_CarriesNoValidatorIssuedSetCookie` and scoped to a freshly issued cookie. |
| n2 | §9.5's tombstone purge misses `auth_tokens` rows keyed to the user's *former* addresses | **Fixed** — the rule that closes it is stated: **every token issued against a resolved account carries `user_id`, on anonymous routes too**, so the `user_id` arm reaches former-address rows and a `user_id IS NULL` row provably never belonged to any account (§9.7 owns those). `DeletionPurgeTests.Sweep_LeavesNoAuthTokenRowForTheDeletedUser_D092` changes the address before requesting deletion. |
| n3 | Register / forgot-password / deletion-request branches still differ by one INSERT after mail left the request path | **Fixed** — both branches of all three routes now write exactly one `auth_tokens` row; the counterpart row is never emailed and is unusable by the rules already stated (`user_id IS NULL` redeems to no subject; the register decoy hits §6.1's in-transaction re-resolution). The three enumeration tests additionally assert equal row counts. |
| n4 | `pu.intent`'s capped binder list is a read-modify-write with no stated concurrency rule | **Fixed** — last-write-wins is stated as the deliberate choice, with the loss made non-fatal: a lost binder yields the ordinary 400 `intent_required` and §12.2's client rule re-mints and retries **exactly once** (capped, so a real attack does not loop). |
| n5 | A-026's route enumeration contradicts §7.2/§7.3 on which `/me` routes are `RequireAuthenticated` | **Fixed** — the enumeration is completed: the four `/me/platform-links` routes (D-127 step 7) and `POST /me/deletion` (A-047) join `/auth/*`, `/onboarding/*` and `GET /me`. |
| n6 | §7.5's ban request body still declares `reasonNote?` after the paragraph that removes it | **Fixed** — the body is `{removeContent:false}`; the paragraph says the field is absent from the schema, not accepted-and-ignored. |
| n7 | Two `UnlinkSteamUseCaseTests` entries name a route an Application test cannot observe | **Fixed** — renamed to `Execute_ForASteamLinkRequestedAsAPlatformUnlink_*`, asserted over the use case's `UnlinkOrigin` argument, with the route half added as `PlatformLinkRouteTests.Delete_ForASteamLink_Returns409WithoutProofAndRemovesAllThreeRowsWithIt_D137` (§14.3). A-030's and slice 8's citations follow. |
| n8 | A-025's "no cookie header on the response" is over-broad given A-028's `SlidingExpiration = true` | **Fixed** — same edit as n1 (the two minors name the same sentence from either side). |
| n9 | A-039's rationale for `.AddRoles<AuthRole>()` is stale after A-026's round-5 change | **Fixed** — the rationale is now the `IRoleStore`/`RoleManager` requirement alone; the stale "otherwise `RequireAdmin` is unsatisfiable" half is deleted and replaced by the statement that nothing authorizes on `ClaimTypes.Role`, which is exactly what A-026 decided. **Second half completed in the sweep:** the finding also named §14.3's `AdminSeederTests.Seed_CreatesTheAdminRoleRowWhenAbsent_D177`, which still asserted the principal's claim; it now asserts the `auth_roles`/`auth_user_roles` rows. |
| n10 | `SecurityStampValidatorOptions.ValidationInterval = TimeSpan.Zero` is inert under A-025's from-scratch validator | **Fixed** — A-025 says so: the handler calls `ValidateAsync` every request regardless, the interval is consulted only inside the framework validator this design replaces, and the option stays set so a revert cannot silently reintroduce an interval. |
| n11 | Residual slice/inventory drift: duplicated test assignment and an incomplete §8.1 folder listing | **Fixed** — the duplicated `UnlinkSteamUseCaseTests…RequiresReauthentication_D137` assignment is resolved in the sweep (it and its `PlatformLinkRouteTests` route half live in **slice 7**, the slice that ships both the use case and `DELETE /me/platform-links/{id}`; slice 8's copy is gone); `ExternalStateTests` is removed from slice 6 (it covers the A-051 *Steam* state cookie, which lands in slice 7; slice 6's Google leg is covered by `ExternalRedirectTests.GoogleChallenge_SetsTheCorrelationCookieSameSiteLax`), and §8.1's `Auth/`, `Text/` and `Email/` folders now list `IssueTokenRequest`, `PendingRegistration`, `ExternalIntentPayload`, `ExternalStartMode`, `TokenRedemptionFailure`, `TextField`, `IEmailDispatcher` and `EmbeddedEmailTemplateRenderer`. |

#### Round-6 full-document consistency sweep (same pass, after the 18 dispositions)

Prior rounds kept re-finding cross-reference drift, so the round-6 pass ends with one deliberate sweep of the whole document against four invariants: **every route appears identically in the §7.x tables and in the A-decisions; every test entry names a layer that can observe what it asserts; every A-number cross-reference resolves; every slice ends green.** Verified clean: all 33 A-decisions (A-021..A-053) exist in §17 and every in-document `A-0xx` reference resolves (the ARCHITECTURE-owned ones cited are A-001/003/008/009/013/015/016/017/019/020); the four `§12-xx` DOMAIN_MODEL citations resolve (`DOMAIN_MODEL.md:40,223,225,234`); the five TRACEABILITY line numbers §14.5 leans on are re-verified as Phase-1 rows (`:119,120,171,175,185`), as is `PRODUCT_SPEC.md:457`. Fourteen corrections were applied:

| # | Drift | Correction |
|---|---|---|
| 1 | Two round-6 fixes contradicted each other on redirect targets: A-045 step 5 said **every** external completion lands on `/auth/callback` "and nothing else", while §7.4's public-deletion Steam callback lands on `/account-deletion/confirm#token=` — and the new `ExternalRedirectTests.EveryCompletionLandsOnTheHashClearingCallbackRoute` asserted the former | The rule is scoped to external **authentication** completions and the underlying invariant is stated generally: every external `Location` must name a route with a hash-clearing entry point — `/auth/callback` (auth) or `/account-deletion/confirm` (public deletion). A-045 step 5, §12.1's two route entries and the §14.3 assertion now agree |
| 2 | §7.1's `GET /auth/external/complete` row still read "→ 302 to SPA" | Row now reads `302 to /auth/callback?status=…[&next=…]`, matching §6.3, §6.4 step 7 and A-045 step 5 |
| 3 | Round-6 finding 4 deleted the `?mode=` query parameter from `/start`, but five places still described the re-auth flow as `GET /auth/external/{provider}/start?mode=reauth` (A-048 step 1, A-049 proof branch 2, slice 6's title and body, slice 7) | All five restated as "an intent minted `mode=reauth`, redeemed at `/start?intent=…`". The mode is a property of the intent row in every artifact now |
| 4 | §14.2 filed `ExecuteDueDeletionsUseCaseTests.{Execute_TombstonesOnlyElapsedGraces_D148, Execute_AppendsTheJournalEntry_D092, Execute_IsIdempotent_D148}` — assertions about the per-user transaction, on the class A-037's round-6 rules just reduced to a transaction-less loop over ids with a fresh DI scope per user | Renamed to `ExecuteDeletionForUserUseCaseTests.*` (§9.4 owns those steps). The loop's own behavior is `DeletionSweepTests.Sweep_WhereOneUserFails_…_D092` in §14.3 |
| 5 | Round 5 renamed two `ResetPasswordUseCaseTests` and two `SignInWithPasswordUseCaseTests` gate assertions into the **same** class, producing four §14.2 rows for two tests (`Execute_RefusesABannedAccount_D180` vs `…BeforeAnyCookie_D180`; `Execute_CancelsAPendingDeletion_D158` vs `…OnASuccessfulCredentialCheck_D158`) | Deduplicated to one row each, with the reason (the gate is credential-path-agnostic and has one caller); §6.1's citation updated to the surviving names |
| 6 | Round-6 finding 7's `Steam:Enabled` gated **all four** Steam routes, which lets an operator revoke round-6 finding 1's invariant by configuration: turning Steam off after any Steam-only account exists closes that account's login *and* its only public deletion route (`PRODUCT_SPEC.md:457`) | The flag gates the two **sign-in** routes and the `providers` entry only; `/account-deletion-requests/steam/{start,callback}` stays mapped, since it issues no cookie, creates nothing, and resolves an **already-existing** login row. §7.1, A-042, §13.1 and the §14.3 test all say so |
| 7 | Finding n9 was applied to A-039 but not to the test it also named: §14.3's `AdminSeederTests.Seed_CreatesTheAdminRoleRowWhenAbsent_D177` still asserted the principal's `ClaimTypes.Role` | The assertion is now over the `auth_roles`/`auth_user_roles` rows, per A-026 |
| 8 | Finding n11's duplicated test assignment survived: slice 7 claimed "all four `UnlinkSteamUseCaseTests`" while slice 8 named one of them again, and the new `PlatformLinkRouteTests` was filed in slice 8 although `DELETE /me/platform-links/{id}` ships in slice 7 | Both live in **slice 7**, with the route; slice 8's copies removed. Slice 7 still ends green — the `reauth` proof lands in slice 5 and its external branch in slice 6 |
| 9 | Slice 4's file list named six `Application/Auth/*` types while §8.1 (corrected by n11) names nine | Slice 4 now lists the full set, with the reason `ExternalIntentPayload`/`ExternalStartMode`/`TokenRedemptionFailure` ship with the port even though their supplier is slice 6 |
| 10 | §9.5's closing test line still cited `ExecuteDueDeletionsUseCaseTests.Execute_LeavesNoAuthTokenRowForTheDeletedUser_D092`, a name the round-2 preamble moved to §14.3 | Cites `DeletionPurgeTests.Sweep_LeavesNoAuthTokenRowForTheDeletedUser_D092` and `…_Sweep_PurgesTheIdentityCredentials_D092` |
| 11 | §7.4's test list used a shorthand (`…_ThenConfirmStartsTheGrace_D072`) that does not match §14.3's `SteamAssertion_ThenConfirm_StartsTheGrace_D072` | Names spelled out in full, matching §14.3 |
| 12 | §14.3 filed `ExternalStateTests.Callback_WithoutTheStateCookie_IsRejected` under "§6.3/§6.4"; §6.3's Google leg has no `pu.ext` at all | Re-filed under §6.4 / A-051, with Google's correlation-cookie assertion named instead |
| 13 | §17's A-041 rationale still said "six templates" after the set grew to thirteen (§11, round-4) | Says thirteen, citing §11 |
| 14 | §17 listed A-053 before A-052 | Re-ordered |

Two of the fourteen (7 and 8) are completions of round-6 findings the first pass applied only in part; the other twelve are drift the findings did not name.

Net changes in the round-7 draft (this document): **no new decision record** — every finding resolved inside A-024/A-025/A-026/A-029/A-030/A-037/A-039/A-042/A-045/A-049/A-050 or inside §6.1/§7.4/§9.5/§12/§14; one changed port signature (`RedeemAsync` gains `binderHash`) and one new predicate in the redeeming `UPDATE`; one changed repository signature (`FindDueForDeletionAsync` → `IReadOnlyList<UserId>`, no lock) and one new sweep rule (fresh DI scope per user); one new config key (`Steam:Enabled`); one deleted §7.1 row (`…/start?mode=reauth`) and one deleted account class (address-less Google creates); one fixed redirect target for every external **authentication** completion (`/auth/callback`, destination as `?next=`, the public-deletion leg landing on `/account-deletion/confirm`); 9 new, renamed or replaced tests, plus 4 renamed and 2 deduplicated by the consistency sweep, and two slice-assignment corrections. No new packages, no new migrations, no new test project, no phase reassignments.

### Round 7 — cross-family review (Codex gpt-5.6-sol, PASS_WITH_NOTES)

The first **cross-family** review of this design: a different model family, briefed on the binding documents and this file rather than on the six prior rounds' reasoning. 6 findings — five important, one minor. **All 6 accepted after triage and fixed; none rebutted.** All five important findings are *framework-contract* defects — places where the design specified behavior ASP.NET Core, Npgsql or Google OIDC does not provide by default — which is the class six same-family adversarial rounds did not surface. Every claim was re-verified against the framework contracts and this document before acting. One-line disposition per finding, in the order received:

| # | Lens / severity | Finding | Disposition |
|---|---|---|---|
| 1 | security / important (§6.3, A-045) | The Google handler maps `email` but **not** `email_verified`, so A-045's "verified on the IdP side" precondition has no mechanism — the verified branch is unreachable and the plausible "fix" is to treat `email` as proof | **Fixed** — §6.3 gains `ClaimActions.MapJsonKey("email_verified", "email_verified")` in `GoogleAuthSetup` (with `OnCreatingTicket` named as the equivalent fallback), plus the `ExternalAuthEndpoints` input mapping that turns the claim into a **fail-closed** `bool` (absent/empty/malformed/`false` all = unverified) as `CompleteExternalSignInUseCase`'s only source. A-045 step 1 restates the semantics and both refusal branches. Two new tests: `CompleteExternalSignInUseCaseTests.Execute_TreatsAnAbsentEmailVerifiedClaimAsUnverified_D067` (§14.2) and `GoogleClaimMappingTests.Callback_ProjectsEmailVerifiedOntoThePrincipal_D067` (§14.3), the latter failing if the `ClaimActions` line is dropped. |
| 2 | security / important (§6.1, A-029) | Dummy-hash equalization is backwards: wrong-password pays a real verify **plus** the dummy (two hashes) while unknown / password-less / locked-out pay one, making an existing password account the measurably slowest answer | **Fixed** — §6.1 restates the rule as an invariant over the request: **exactly one PBKDF2-cost verification per `POST /auth/login`, whatever the outcome**, with a per-branch table. The dummy runs only where no real verification occurred (unknown, password-less, and the lockout short-circuit Identity performs *before* verification); the real-verify branches run no dummy. The branch is not inferred from Identity internals: `PasswordCheckResult` carries **`HashVerified`** (§8.2) and the use-case rule is one line. §14.2's test is re-pinned from "the dummy ran in all four" to `realVerifies + dummyVerifies == 1` in all four negative cases and in the success case. |
| 3 | security / important (§6.2, A-029) | §6.2's "409 without consuming the token" is unimplementable through `RedeemAsync`: redemption was the only operation exposing the row's metadata, so the only shapes were consume-then-check (burning the victim's live link on a UI prompt) or a peek-then-redeem race | **Fixed** — the port contract gains an **atomic conditional** form: `RedeemAsync(RedeemTokenRequest, ct)` where the request carries the caller's attestation state, and A-029's statement becomes one `WITH candidate … , redeemed AS (UPDATE …) SELECT …` that refuses redemption — leaving the row live — when the row is pre-account (`user_id IS NULL` and no `auth_users` row on its address) and no attestation is present, returning the new **`TokenRedemptionFailure.RequiresAgeGate`** with what the 409 needs. Single-use and non-consumption are properties of the same statement (`consumed_at IS NULL` is repeated inside the `UPDATE`'s own `WHERE`, where Postgres re-evaluates it after a concurrent write), and the arm is inert when `AgeGateAttested` is null so `mode=login` intents are unaffected. §6.2's "check before the consuming UPDATE" instruction is withdrawn; slice 4 ships the statement, slice 5 the two call sites; two new `SecurityTokenServiceTests` (§14.3) and a restated `ConsumeMagicLinkUseCaseTests…_D083`. |
| 4 | security / important (§6.3, A-029, A-050) | The A-050 binder has no issuance data path — no type carries the hash into `auth_tokens.payload` — and the specced predicate compares jsonb `->>` (text) against a `byte[]` parameter, which Npgsql binds as `bytea`: a type error or a silent never-match | **Fixed** — `ExternalIntentPayload` gains **`BinderHash` (string)**, serialized into `payload` at issuance through `IssueTokenRequest.Intent`; `RedeemTokenRequest.BinderHash` is a `string`; and the encoding is defined **once** — `IntentBinder.Hash(binder) = Base64Url(SHA-256(binder))`, one helper (`Api/Auth/IntentBinder.cs`, slice 6) used at mint and at redeem, base64url so no `+`/`/`/`=` escaping can diverge between the ends. §5.3's payload description, A-050's cookie row and `/start` paragraph, and §14.3's binder test all name the text comparison. |
| 5 | security / important (§7.6, A-049) | ASP.NET Core honours **at most one** endpoint rate-limiting policy — a second `RequireRateLimiting` replaces rather than composes (aspnetcore#42691) — yet the table assigns two or three ceilings to several routes and assumes conjunction; wired as named policies, one ceiling per route silently disappears and `EveryPolicyNamedInTheDesignIsRegistered` still passes | **Fixed** — §7.6 states the mechanism explicitly and picks **one**: a single `options.GlobalLimiter = PartitionedRateLimiter.CreateChained(…)`, each §7.6 row a chain member (`PartitionedRateLimiter.Create<HttpContext,string>`) whose partition function inspects the matched endpoint and the identity and returns `GetNoLimiter` outside its route set. Chained = every member must permit, and leases already taken are released on refusal — the conjunction the table always meant. **No endpoint calls `RequireRateLimiting` and no named policy is registered**; per-user members key on the authenticated principal only (so `/auth/external/intent` counts every authenticated mint, needing no body read). Every ceiling, partition key and route assignment is unchanged. Slice 3's `RateLimitPolicies.cs` description rewritten; the registration test becomes `RateLimitTests.EveryDimensionCoversItsDocumentedRoutes` (which also asserts no `EnableRateLimitingAttribute` exists anywhere) and a new `RateLimitTests.Login_ExhaustsBothTheIpAndTheEmailCeiling` makes the two-ceiling case observable. |
| 6 | architecture / minor (§3, A-024, A-025) | The default `IUserClaimsPrincipalFactory` emits the user name, the **email address** and every `auth_user_claims` row on top of id/roles/stamp, contradicting A-024's three-claim cookie contract | **Fixed** — A-024 specifies `Infrastructure/Identity/PartyUpUserClaimsPrincipalFactory.cs`, an `IUserClaimsPrincipalFactory<AuthUser>` written **from scratch** (not derived from `UserClaimsPrincipalFactory<TUser,TRole>`) emitting exactly `UserIdClaimType`, `RoleClaimType` ×N and `SecurityStampClaimType`. Layer decided consistently with A-025: `Identity.Core` types with no `HttpContext`, so Infrastructure — only `SignInManager`/`AddIdentityCookies`/the stamp validator are shared-framework and stay in Api. Registered in slice 2 with `services.Replace(ServiceDescriptor.Scoped<…>())`, not `AddScoped`, so it cannot depend on `TryAdd` semantics. Test `AuthCookieTests.Login_IssuesAPrincipalWithIdRolesAndStampOnly` unprotects the ticket with the test host's `TicketDataFormat` and asserts **no email claim** for an account that has a confirmed address. |

Net changes in the round-8 draft (this document): **no new decision record** — all six findings resolved inside A-024/A-025, A-029, A-045 and A-050, or inside §6.1/§6.2/§6.3/§7.6. One changed port signature (`RedeemAsync(RedeemTokenRequest, ct)`) with one new request record and one new `TokenRedemptionFailure` member (`RequiresAgeGate`); one changed redemption statement (single `UPDATE` → single CTE statement: same atomicity, one more condition, one new non-consuming outcome); one new payload field (`ExternalIntentPayload.BinderHash`, string) and one type correction (`byte[]` → base64url text); one new Infrastructure type (`PartyUpUserClaimsPrincipalFactory`) with a `Replace` registration; one new Api helper (`IntentBinder`); one new handler line (`ClaimActions.MapJsonKey`) with one new input mapping; one rewritten rate-limiter wiring (thirteen named policies → fourteen members of one chained global limiter, `deletion-request` being an email/IP pair; ceilings unchanged); one new gateway result field (`PasswordCheckResult.HashVerified`); eight new or restated tests across §14.2/§14.3; five amended slices (2, 3, 4, 5, 6). No new packages, no new migrations, no new config keys, no new test project, no phase reassignments.
