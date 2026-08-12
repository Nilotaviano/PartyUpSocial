# Steam Catalog Cache + Search — design (D-194)

**Status:** ratified 2026-08-12 (owner settled ingestion mechanism, pg_trgm, 6-hour cadence, fallback-only App ID field in-session). Closes the D-176 shortfall (TODO.md: "Steam catalog selection is a raw App ID box").
**Basis:** PRODUCT_SPEC.md D-176/D-058, ARCHITECTURE.md §3, the D-176 shortfall entry in TODO.md.

## 1. Ingestion

One Hangfire recurring job, `SteamCatalogRefreshJob`, cron `0 */6 * * *` (every 6 hours, owner-chosen), registered in `RecurringJobRegistrationService` beside the existing seven. Each run:

1. Load the single `steam_catalog_state` row (advisory: created empty by migration). Record `runStartedAt = clock.GetCurrentInstant()` before the first request — the next run's `if_modified_since` watermark is *run start*, not max(`last_modified`), so clock skew between Steam and us can only re-fetch, never miss.
2. Call `IStoreService/GetAppList` v1 with the already-configured Steam Web API key: `include_games=true`, `include_dlc=false`, `include_software=false`, `include_videos=false`, `include_hardware=false`, `max_results=50000`, paging with `last_appid` from the previous response until `have_more_results` is false. When `steam_catalog_state.watermark` is non-null, also send `if_modified_since=watermark` (unix seconds) — first run (null watermark) is the full backfill; there is no separate backfill code path.
3. Upsert each `(appid, name, last_modified)` batch set-based (one statement per page, A-068 spirit — never per-row round trips). Rows are never deleted: `GetAppList` reports additions and modifications, not delistings, and a stale name row is harmless because free text always exists (D-058).
4. On full success, set `watermark = runStartedAt`. Any page failure aborts the run without advancing the watermark (the job is idempotent; the next run re-covers the window). Failures log and rely on the 6-hour cadence — no Hangfire retry storm against Steam.

The job holds the same posture as `AvatarRefreshJob`: outbound HTTP through the pinned-callback SSRF-hardened handler is NOT needed here (fixed first-party Steam host, no user-supplied URL), but timeouts and a sane `User-Agent` are.

## 2. Storage

- `steam_apps`: `app_id` bigint PK, `name` text not null, `last_modified` timestamptz null. ~150–250k rows.
- `steam_catalog_state`: single row — `watermark` timestamptz null, `last_run_at`, `last_run_succeeded`. Advisory only; queries never gate on it.
- Migration also runs `CREATE EXTENSION IF NOT EXISTS pg_trgm` (trusted on Postgres 17 — compose pins `postgres:17-alpine` everywhere, so the db owner can create it without superuser) and a GIN trigram index on `lower(name)`.
- These are Infrastructure-owned read models, not domain aggregates: no Domain entity, no visibility-engine involvement (catalog data is public), plain EF mapping + a repository interface in Application (`ISteamCatalogRepository`: `SearchAsync(term, limit)`, `UpsertBatchAsync`, state accessors).

## 3. Search endpoint

`GET /api/v1/steam-catalog?query={term}` — auth required (RequireOnboarded like other authenticated reads), own rate-limit dimension (`steam-catalog-search`; typeahead traffic must not eat the general read budget). Returns top 20 `{ appId, name }` ranked by trigram `similarity(lower(name), lower(term))` with a floor (`%` operator) so garbage terms return empty rather than everything; ties broken by `name`. `query` shorter than 2 chars → empty 200 (not an error — typeahead ergonomics). No pagination: pick-from-top-20 or refine.

Catalog empty or query failing → the endpoint returns 200 with an empty list plus `catalogAvailable: false` when the `steam_apps` table has no rows; the client uses that flag for D-058 degradation (below). OpenAPI documents all of it — mobile contract, no web-only conveniences.

## 4. Client

Create pages' Steam arm (sessions + polls; `SessionEditPage` follows the same component) replaces the interim always-visible App ID field with a typeahead combobox: debounced 300 ms, TanStack Query, WAI-ARIA combobox semantics (keyboard + SR paths, WCAG 2.2 AA as scope). Selecting a result fills `{ name, steamAppId }`. Typing without selecting stays legal and submits `{ name, steamAppId: null }` — free text is a first-class arm (D-058), not an error state.

Degradation (owner: fallback-only): when the search response carries `catalogAvailable: false` (or the query request errors), the combobox degrades to today's honest pair — plain name field plus the optional labeled "Steam app ID" field with URL-paste extraction (the b62ceb5 interim UI is kept as the degraded arm, not deleted). The wire contract `game: { name, steamAppId }` is unchanged in every arm.

## 5. Spec and docs (same commit as the behavior)

- PRODUCT_SPEC.md gains **D-194**: Steam catalog selection is served from a first-party cache of `IStoreService/GetAppList` (games only), refreshed every 6 hours incrementally via `if_modified_since`; search is fuzzy; free text remains valid alongside (D-058); when the catalog is unavailable the UI degrades to free text + optional manual App ID. D-176's row gets a pointer, not a rewrite.
- ARCHITECTURE.md ADR: one entry for the catalog cache (pg_trgm dependency, never-delete posture, watermark-by-run-start rationale).
- TRACEABILITY.md D-194 row; TODO.md shortfall entry closed.

## 6. Tests

- Job: unit tests with a fake Steam client — full backfill pagination, incremental `if_modified_since` propagation, watermark-only-on-success, upsert batch shape (`_D194` names).
- Endpoint: integration tests — similarity ranking returns the obvious match first, 2-char floor, empty-catalog `catalogAvailable: false`, rate-limit dimension inventory extension, op-id inventory extension (139 → 140).
- Web: combobox select fills appId; free-text submit keeps `steamAppId: null`; degraded arm renders the manual field; i18n en/pt-BR/es.
