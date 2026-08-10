# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

PartyUp (partyup.social) — friends scheduling multiplayer game sessions. Four documents are authoritative, in this order:

1. **PRODUCT_SPEC.md** — product behavior as 182 numbered decisions (D-001..D-182). Confirmed decisions are settled; code that contradicts one is wrong, not a judgment call. Propose spec changes before implementing them.
2. **ARCHITECTURE.md** — binding technical conventions and the architecture decision record (A-001..A-020). Read §3 (cross-cutting conventions) before writing backend code.
3. **DOMAIN_MODEL.md** — the ratified domain design the Phase 1 code implements: aggregates, state machines, visibility rules, and the §12 interpretation notes that resolve spec-silent points. Domain changes must stay consistent with it (update it in the same commit when behavior legitimately changes).
4. **IMPLEMENTATION_PLAN.md** — phase ordering and what each phase owns. **TRACEABILITY.md** maps every decision to its test or owning phase — keep it current when adding D-numbered tests.

## Commands

```sh
# Environment Check: Requires .NET 10 SDK and Node.js v20+ LTS. 
# Ensure local Postgres container is running via compose before writing/testing schema.

dotnet build PartyUp.sln                                   # must be 0 warnings (TreatWarningsAsErrors)
dotnet test PartyUp.sln --filter "Category!=Integration"   # unit tests, no Docker needed
dotnet test PartyUp.sln                                    # + integration tests (needs a reachable Docker daemon; CI always runs them)

# Full suite incl. integration tests locally (Docker lives in WSL, not the Windows host).
# MANDATORY before any push — never push integration tests that have not run:
wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && dotnet test PartyUp.sln -p:ArtifactsPath=/tmp/partyup-artifacts"

dotnet test --filter "FullyQualifiedName~SomeTestName"     # single test
dotnet run --project src/PartyUp.Api                       # API on http://localhost:5000

# Migrations (EF Core, from repo root; dotnet tool restore once)
# Note: 'add' generates local code structures and does not require a live database connection.
dotnet dotnet-ef migrations add <Name> --project src/PartyUp.Infrastructure --startup-project src/PartyUp.Api
dotnet dotnet-ef migrations list --project src/PartyUp.Infrastructure --startup-project src/PartyUp.Api --no-connect

# Web (from web/)
npm run dev            # Vite on :5173, proxies /api /health /openapi to :5000
npm run lint && npm run build && npm test
npx vitest run src/App.test.tsx        # single test file

# Codegen constraint: 'dotnet run' API project must be running concurrently in a separate background terminal first.
npm run gen:api        # regenerates src/api/schema.d.ts from the RUNNING API's OpenAPI doc

# Local Postgres
docker compose -f deploy/compose.dev.yml up -d
```

## Architecture (the parts that span files)

- **Dependency rule, enforced by project references:** `Domain → nothing` · `Application → Domain` · `Infrastructure → Application, Domain` · `Api → all`. Api is the only composition root. Domain references NodaTime and nothing else.
- **Time:** NodaTime everywhere in Domain/Application — `Instant` for fixed points; BCL `DateTime`/`DateTimeOffset` are forbidden there. Clock is always injected (`IClock`). Postgres `timestamptz ↔ Instant` via Npgsql.NodaTime; snake_case naming via EFCore.NamingConventions.
- **Materialized status invariant:** session/poll status is a stored column that queries trust. Only shared transition functions (used by both request paths and Hangfire sweeper jobs) mutate it; transitions are idempotent. Never re-derive status from timestamps in queries.
- **Visibility is the security boundary:** every session/poll/availability read AND write goes through the visibility engine and fails closed (no access → 404, indistinguishable from nonexistent). Blocking/tombstone "Anonymous User" masking is render-time presentation on top of real access — not access denial.
- **Transactional side effects (A-016):** in-app notification records and anything else that must survive a crash commit in the same transaction as the state change. Post-commit dispatch is only for idempotent transports.
- **Concurrency:** invariant-touching commands (RSVP, approval, promotion, finalization) lock the parent session/poll row (`SELECT … FOR UPDATE`) before reading occupancy. Logical uniqueness is backed by DB constraints, not app checks.
- **Security tokens ≠ IDs (A-015):** entity IDs are `Guid.CreateVersion7()`; anything granting access (guest links, magic links) is ≥256-bit CSPRNG, stored hashed, never logged.
- **Frontend:** server state lives in TanStack Query; the API client is generated (`gen:api`) — never hand-write API types. All user-facing strings go through i18next (en, pt-BR, es) from the first commit; UI needs keyboard paths and SR semantics (WCAG 2.2 AA is scope, not polish).
- **Prod topology:** one container serves API + SPA (`MapFallbackToFile` with an `/api/v1` 404 guard — don't let the SPA fallback swallow API routes). Migrations apply on startup behind `Database:MigrateOnStartup`.

## Conventions with teeth

- Tests proving a spec rule carry its D-number in the name (e.g. `..._D074`) — this is the traceability mechanism, greppable.
- No changelog comments in code; rationale goes in commit messages. A comment is only for non-obvious current-state constraints.
- Conventional Commits, imperative, subject ≤72 chars.
- Central Package Management: versions only in `Directory.Packages.props`, never in csproj files. Don't add Identity/Hangfire/WebPush/MediatR packages before their phase (A-013).
- `deploy/compose.yaml` must keep exactly that filename (bare `docker compose` auto-discovery on the VM depends on it).
- OpenAPI document is the future mobile contract — no web-client-only conveniences in endpoints.
