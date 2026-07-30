# PartyUp — Architecture Foundation

**Status:** v1 — foundation for Phase 0/1 implementation
**Basis:** PRODUCT_SPEC.md (D-001..D-176), IMPLEMENTATION_PLAN.md
**Scope:** Repository structure, technology baseline, and cross-cutting conventions. Product behavior lives in PRODUCT_SPEC.md; phasing lives in IMPLEMENTATION_PLAN.md.

## 1. System shape

Single deployable web application on one VM:

- **API:** ASP.NET Core (.NET 10) minimal APIs, REST, OpenAPI-documented, all routes under `/api/v1`.
- **Web:** React + TypeScript SPA (Vite). In production the API container serves the built SPA as static files; Caddy terminates TLS in front. In development, Vite dev server proxies `/api`, `/health`, and `/openapi` to Kestrel.
- **Database:** PostgreSQL 17. EF Core + Npgsql, NodaTime type mapping, snake_case naming.
- **Background jobs:** Hangfire (Hangfire.PostgreSql), in-process in the API host. Sweeper *transition functions* are Phase 1 domain code; Hangfire itself is wired when the first scheduled jobs ship (fixed-session sweeper in Phase 4, poll jobs in Phase 6 — plan). Materialized status is an invariant from Phase 1: session/poll status is a stored column that queries trust; only the transition functions (called by jobs and by request-path code) change it, transitions are idempotent (re-running a sweep is a no-op), and a late sweep delays a transition but never corrupts it.
- **Deploy:** Docker Compose on a single Hetzner/DO VM — `caddy` + `app` + `postgres` + nightly `pg_dump` backup with 30-day retention. CI/CD via GitHub Actions → GHCR → SSH `compose pull && up`.

Sized deliberately for friend-group scale (spec §11 leaves formal targets open): single-digit-thousands MAU fits one VM with headroom. The Phase 8 k6 load pass is the checkpoint that either validates this or forces the topology conversation with data.

## 2. Solution layout

```
PartyUp.sln
global.json                      — pins .NET SDK major
Directory.Build.props            — shared build settings (all projects)
Directory.Packages.props         — central package management (CPM)
.config/dotnet-tools.json        — dotnet-ef local tool
/src
  PartyUp.Domain                 — entities, state machines, visibility/overlap engines. Pure C#. NodaTime only.
  PartyUp.Application            — use cases, ports (interfaces), domain-event contracts, authorization rules.
  PartyUp.Infrastructure         — EF Core (AppDbContext, migrations), Npgsql, and later: Identity, Hangfire, Web Push, Steam, email.
  PartyUp.Api                    — composition root. Minimal API endpoints, OpenAPI, health, static SPA hosting.
/web                             — React app (see §5)
/deploy                          — Dockerfile, compose files, Caddyfile, backup + restore runbooks
/tests
  Directory.Build.props          — test-only build settings (IsPackable=false, relaxes CA1707 for test names)
  PartyUp.Domain.Tests           — pure unit tests; domain-rule D-numbers land here (Phase 1)
  PartyUp.Application.Tests      — use-case tests with fakes
  PartyUp.Api.IntegrationTests   — WebApplicationFactory + Testcontainers (real Postgres)
/.github/workflows               — ci.yml, cd.yml
```

**Dependency rule (enforced by project references):**
`Domain → (nothing)` · `Application → Domain` · `Infrastructure → Application, Domain` · `Api → Infrastructure, Application, Domain`.
Domain references NodaTime and nothing else. Application references abstractions only (no EF, no HTTP). Api is the only project that composes DI.

**Naming:** projects and namespaces use `PartyUp.*` (product name; the plan's earlier `Gss.*` placeholder is superseded — recorded here so the rename never happens later).

## 3. Cross-cutting conventions (.NET)

- **Language/build:** nullable enable, implicit usings, `TreatWarningsAsErrors=true`, .NET analyzers `AnalysisLevel=latest-recommended`, file-scoped namespaces, `LangVersion=latest`. All set once in `Directory.Build.props`.
- **Packages:** Central Package Management — every version lives in `Directory.Packages.props`; csproj files carry `PackageReference` without versions.
- **IDs:** `Guid.CreateVersion7()` generated app-side (time-ordered, Postgres-index-friendly). *Entity identifiers only* — anything that grants access is a security token, not an ID (A-015).
- **Security tokens (guest links, magic links):** ≥256 bits from `RandomNumberGenerator`, URL-safe base64, stored hashed (SHA-256) so a database leak doesn't leak live bearer credentials, never written to logs. Guest-link handling additionally follows the plan's Phase 4 security package (first-visit exchange for a guest cookie, `Referrer-Policy: no-referrer`, non-mutating GET, rate limiting, revocation).
- **Time:** NodaTime everywhere in domain code. `Instant` for fixed points (sessions, one-time blocks, poll options — spec §8.5); weekly availability anchors to `(IsoDayOfWeek, LocalTime, Period, DateTimeZone)` *plus* per-occurrence exception/split records — D-134/D-143 require delete-one, edit-one, and edit-this-and-future, which a bare recurrence tuple cannot express; Phase 5 owns the exact model. BCL `DateTime`/`DateTimeOffset` do not appear in Domain/Application. Clock is injected (`IClock`, NodaTime's interface) — never `SystemClock` inline, so state machines are testable.
- **Concurrency and transactions:** every invariant-touching command (RSVP, join approval, waitlist promotion, capacity/minimum edits, poll finalization, guest response) runs in one transaction that locks the parent session/poll row (`SELECT … FOR UPDATE`) before reading occupancy, so capacity math and confirmation transitions are race-free. Poll first-response timestamps are written once and never updated (D-172). Logical uniqueness is enforced by database constraints, not application checks alone: username (case-insensitive), friendship pair, one participation per (user, session), poll option per (poll, instant). Everything else uses optimistic concurrency (Postgres `xmin`). Guest multi-device edits are last-write-wins by design (D-126) — no lock.
- **Database mapping:** `timestamptz` ↔ `Instant` via `Npgsql...NodaTime`; snake_case tables/columns via `EFCore.NamingConventions`. Migrations live in Infrastructure; forward-only from Phase 2 on.
- **Migrations at runtime:** `Database.Migrate()` on startup when `Database:MigrateOnStartup=true` (single instance — no concurrent migrators; flag off in tests that manage schema themselves). Failure semantics: EF wraps each migration in a transaction on Postgres, and a failed migration aborts startup — the app crash-loops rather than serving a half-migrated schema. Recovery: fix forward, or roll back by deploying the previous commit-sha image tag (every CD image is immutably sha-tagged). Avoid long-lock migrations (e.g., non-concurrent index builds on hot tables) once real data exists.
- **Use cases:** plain application-service classes registered in DI. No MediatR — one first-party client and a solo maintainer don't need pipeline indirection. Domain events are plain records collected on aggregates. Anything that must survive a crash — above all the in-app notification records the spec requires — is written **in the same transaction as the state change** (A-016). Post-commit dispatch exists only for transports (Web Push later), which are idempotent via the Phase 7 send-log and tolerate loss/lateness by spec.
- **Endpoints:** minimal APIs in feature-grouped files under `PartyUp.Api/Endpoints/`, mapped onto a single `/api/v1` route group. No versioning library — the prefix constant is the version until a second API version actually exists.
- **OpenAPI:** `Microsoft.AspNetCore.OpenApi`; document served at `/openapi/v1.json` in every environment — it is the public API contract (mobile clients consume it later) and contains nothing secret. The web client's types are generated from it (§5). No web-only conveniences in endpoints.
- **Logging:** Serilog, console sink, compact JSON in production, human-readable in dev. Container log rotation is configured in compose (json-file, capped) so the VM disk can't fill.
- **Health:** `/health/live` — process-only liveness (no dependency checks); `/health` — readiness including Postgres. Both outside `/api/v1`. Ops checks that must distinguish "app dead" from "DB down" use live; "safe to serve traffic" uses ready.
- **Authorization boundary:** friendship/visibility *is* the isolation boundary of this product — there are no orgs/tenants. Every session/poll/availability query and command passes through the visibility engine (plan principle 4) and fails closed: no rule match → not found, indistinguishable from nonexistent. Blocking-pair and tombstone masking (Anonymous User) is render-time presentation, distinct from access denial — masked users still hold real access and capacity effects (D-076/D-086).
- **Data Protection:** keys persisted to `/data/dataprotection` when the path exists (volume in prod) — cookies survive container replacement.

## 4. Testing strategy

- **Traceability rule (from the plan):** every behavior-bearing Confirmed decision in the log — visibility rules D-018/D-019/D-043/D-047 included, not just D-053 onward — gets at least one test naming its ID *at the layer that owns the behavior*. Domain rules land in Domain.Tests during Phase 1; UI-only decisions (e.g., D-103, D-110, D-135) are tracked in the decision-traceability table and verified in their UI phase. The table is the completeness check, the D-number-in-test-name convention is what makes it auditable (`grep D_074`).
- **Domain.Tests:** xunit, no infrastructure.
- **Application.Tests:** use cases with in-memory fakes for ports.
- **Api.IntegrationTests:** `WebApplicationFactory<Program>` + `Testcontainers.PostgreSql`. Real Postgres, real migrations, real HTTP. Marked `[Trait("Category","Integration")]`; requires Docker (CI always runs them; locally run `dotnet test --filter Category!=Integration` if Docker is absent).
- **Web:** Vitest + React Testing Library; ESLint + `tsc` (via `npm run build`) gate in CI.
- **Browser E2E lane:** Playwright, introduced no later than Phase 5 — the drag calendar and poll grid are the two accessibility hotspots and cannot be verified by component tests. Automated a11y checks (axe) ride the same lane; the D-138 cross-browser matrix and WCAG audit are Phase 8 passes over it.

## 5. Web app

- **Stack:** Vite + React + TypeScript (strict), Tailwind CSS v4, shadcn/ui + Radix primitives (added when first real UI ships), TanStack Query v5, React Router v8 (library mode), react-i18next.
- **i18n:** locales `en`, `pt-BR`, `es` as JSON resources under `web/src/i18n/`; `en` is fallback. All user-facing strings go through i18next from the first screen — retrofitting is forbidden by principle 6 (recipient-locale rendering extends this to notifications/emails server-side).
- **API client:** `openapi-typescript` generates types from the served OpenAPI document into `web/src/api/schema.d.ts`; `openapi-fetch` provides the typed client. Regenerate with `npm run gen:api` (API running locally).
- **Proxy:** Vite dev server proxies `/api`, `/health`, `/openapi` → `http://localhost:5000` (Kestrel dev port, fixed in launchSettings).
- **State:** server state in TanStack Query; local UI state in components. No global client-state library until proven necessary.

## 6. Local development workflow

```
# once
dotnet tool restore
cd web && npm install

# db (needs Docker; otherwise point ConnectionStrings__Db at any Postgres 17)
docker compose -f deploy/compose.dev.yml up -d

# api
dotnet run --project src/PartyUp.Api        # http://localhost:5000

# web
cd web && npm run dev                        # http://localhost:5173, proxies to API

# tests
dotnet test                                  # add --filter Category!=Integration without Docker
cd web && npm test
```

Connection string default (dev): `Host=localhost;Port=5432;Database=partyup;Username=partyup;Password=partyup` — overridden everywhere real by `ConnectionStrings__Db` env var.

## 7. CI/CD

- **ci.yml** (push/PR to main): restore/build/test .NET (integration tests included — ubuntu runners have Docker), then web `npm ci && lint && build && test`.
- **cd.yml** (`workflow_run` after CI succeeds on main): checkout pinned to the CI-validated sha, build multi-stage image (web dist baked into API `wwwroot`), push `ghcr.io/<owner>/partyup:{latest,<sha>}`, SSH to VM, `docker compose pull && docker compose up -d`. A red CI run never deploys. Gated off until repo variable `DEPLOY_ENABLED=true` and secrets `VM_SSH_KEY`, `VM_HOST`, `VM_USER` exist (VM provisioning is a Phase 0 operational task).
- **Rollback:** images are immutably sha-tagged; roll back by pointing the VM's compose file at the previous sha and `docker compose up -d`. Data rollback, if a migration made the old code unrunnable, is the restore runbook.
- **Migrations on deploy:** app container applies EF migrations on startup (§3). Single instance — no coordination needed.

## 8. Deployment topology (single VM)

```
Internet → Caddy (443, auto-TLS, access-log scrubbing for guest paths)
              → app (Kestrel :8080 — API + SPA static)
                  → postgres:17 (named volume)
backup sidecar: nightly pg_dump → offsite (rclone target TBD), 30-day retention purge
volumes: pg_data, caddy_data, app_dataprotection, backups
```

The production compose file lives on the VM as `/opt/partyup/compose.yaml` (auto-discovered name — cd.yml and the runbooks invoke `docker compose` without `-f`).

Restore runbook (deploy/RUNBOOK-restore.md) includes re-applying account deletions newer than the snapshot — retention is a deletion-compliance requirement (spec §8.6), not just hygiene. What makes that step executable is the **deletion journal** (A-017): when a grace period expires, the deletion executor appends `(user_id, executed_at)` — PII-free — to an append-only journal shipped offsite with the backups; after a restore, replay journal entries newer than the snapshot through the product's deletion code path. Ships with the Phase 2 deletion skeleton.

Secrets at this scale are the VM's `.env` (mode 600, never committed — `.gitignore` enforces) plus GitHub repo secrets; rotation is manual and documented per credential as it's introduced. No vault until there's more than one operator or one machine.

## 9. Decision record (foundation)

| ID | Decision | Rationale |
|---|---|---|
| A-001 | Namespaces/projects `PartyUp.*`, solution `PartyUp.sln` | Brand is partyup.social; plan's `Gss.*` was a placeholder. Renaming later touches every file. |
| A-002 | Minimal APIs, no MVC controllers | .NET 10 idiom; less ceremony; OpenAPI first-class. |
| A-003 | No MediatR/CQRS framework — plain use-case classes | Solo project, one client; indirection cost exceeds benefit. Revisit only on real pain. |
| A-004 | No API-versioning library — `/api/v1` route-group prefix | One client; a constant is enough until v2 exists. |
| A-005 | Guid v7 app-generated IDs | Time-ordered (index locality) without DB round-trip; native in .NET 10. |
| A-006 | snake_case DB naming via EFCore.NamingConventions | Postgres convention; readable SQL during ops. |
| A-007 | `Database.Migrate()` on startup, flag-gated | Single-instance deploy; simplest correct migration transport. |
| A-008 | SPA served by API container; Caddy only proxies + TLS | One image, one deploy unit; no CORS in prod, cookie auth stays same-origin (BFF). |
| A-009 | Central Package Management | One place to audit/bump versions. |
| A-010 | xunit + Testcontainers for integration; category-tagged | Real Postgres over in-memory provider — Npgsql/NodaTime/snake_case must be exercised. |
| A-011 | openapi-typescript + openapi-fetch (no heavy codegen) | Typed client with zero runtime magic; regenerates from the served document. |
| A-012 | Serilog console-only (JSON in prod), rotated by Docker | Single VM, `docker logs` + Caddy logs suffice at friend-group scale; an external uptime monitor (Phase 9, plan) is the alerting layer. Revisit on Phase 8 load evidence. |
| A-013 | Identity/Hangfire/WebPush packages deferred to their phases | Foundation ships no dead dependencies; CPM makes adding them one line. Hangfire wiring lands with its first jobs (Phase 4/6 per plan), not Phase 1. |
| A-014 | Domain project starts empty | Phase 1 owns domain content; foundation guarantees the dependency skeleton only. Opening Phase 1 task: aggregate + tombstone data-model design (entities, ownership, uniqueness constraints, deletion lifecycle) before code. |
| A-015 | Security tokens are not IDs | Guest/magic links: ≥256-bit CSPRNG, URL-safe base64, hashed at rest, never logged. Guid v7 is for entity identity only — it is partially predictable and must never gate access. |
| A-016 | Crash-surviving side effects are transactional with the state change | In-app notification records commit atomically with the domain change that caused them; post-commit dispatch feeds only idempotent transports. Closes the lost-event window without an outbox framework. |
| A-017 | PII-free deletion journal, shipped offsite | `(user_id, executed_at)` appended at grace-expiry execution; restore replays entries newer than the snapshot. Makes the runbook's compliance step executable instead of forensic. |

## 10. Deferred (tracked, not forgotten)

- **Phase 0 exit criteria (plan):** VM provisioning + DNS + GHCR/SSH secrets, first deploy over TLS, **restore drill against a real dump**, Spike A (calendar), Spike B (Steam), Spike C (Web Push), Apple developer account started (external lead time).
- **Phase 1 opener:** aggregate/tombstone data-model design doc (A-014).
- **Phase 2:** ASP.NET Identity + cookie BFF auth — with antiforgery, `HttpOnly`/`Secure`/`SameSite=Lax` cookies, and security-stamp-based sign-out-everywhere (deletion start invalidates all sessions, successful login cancels deletion — D-158/D-165); transactional email + mail.partyup.social DNS/SPF/DKIM/DMARC; deletion skeleton incl. deletion journal (A-017).
- **Phase 4/6:** Hangfire wiring + sweeper jobs; guest-link security package (A-015 + plan Phase 4).
- **Phase 5:** Playwright E2E + axe lane (§4).
- **Phase 7:** Web Push/VAPID transport + send-log.
- **Later:** Steam OpenID + Web API (Phase 2/3, Spike B), OpenAPI client staleness check in CI once real endpoints exist (A-011), uptime monitor + Hangfire dashboard auth-gated (Phase 9).
