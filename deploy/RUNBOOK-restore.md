# Restore runbook

> A restore drill using these exact steps is a Phase 9 go-live gate, before the first beta
> invite (IMPLEMENTATION_PLAN.md Phase 9, ARCHITECTURE.md §10) — this runbook isn't done until
> someone has followed it against a real dump and confirmed the app comes back up correctly.
> Update it with anything that surprised you.

Run all of this from the directory holding `compose.yaml` and `.env` on the VM (`/opt/partyup`);
the bare `docker compose` calls below rely on that file being auto-discovered there.

## 1. Stop the stack

```sh
docker compose down
```

Containers stop; named volumes (`pg_data`, `caddy_data`, `caddy_config`, `app_dataprotection`,
`backups`) are untouched.

## 2. Recreate a fresh `pg_data` volume

If this is a disaster-recovery restore (the existing volume is gone or suspect), drop it first:

```sh
docker volume rm partyup_pg_data   # skip if it's already gone (fresh VM, lost disk, etc.)
docker volume create partyup_pg_data
```

(Volume name is prefixed with the compose project name — confirm the exact name with
`docker volume ls` if `partyup` isn't it.)

## 3. Start only Postgres against the fresh volume

```sh
docker compose up -d postgres
docker compose ps   # wait for postgres to report healthy
```

Don't start `app` yet — nothing should write to the database until the restored data has been
verified and step 5 below is done.

## 4. Restore the dump

Copy the chosen `.dump` file (see `RUNBOOK-backup.md` for where backups land) into the container
and run `pg_restore`:

```sh
# .env is read by Docker Compose, not by your shell — export it first or the -U/-d
# arguments below expand to empty strings.
set -a; . ./.env; set +a

docker cp partyup_20260730_030000.dump "$(docker compose ps -q postgres)":/tmp/restore.dump
docker compose exec postgres pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
  --clean --if-exists -1 /tmp/restore.dump
```

`--clean --if-exists` drops existing objects before recreating them (safe against the fresh,
mostly-empty database from step 2); `-1` runs the whole restore in a single transaction so it
either fully applies or fully rolls back.

Sanity-check the result before going further — e.g. confirm row counts and the most recent
row timestamps in a few key tables match what you'd expect from the dump's snapshot time.

## 5. Required: re-apply account deletions requested after the snapshot

Record the dump's snapshot instant before changing the old environment. If the source API is still reachable, export its authenticated restore-replay feed first:

```http
GET /api/v1/admin/deletion-journal?since=<snapshot-instant>&limit=1000
```

This is A-017's product feed and returns `{ entries: [{ userId, executedAt }] }`. It reads the transactional `deletion_journal` table. Keep that response with the incident record, but do not depend on it being available during total VM loss.

For disaster recovery, read the offsite monthly NDJSON files from the `/data/deletion-journal` backup set and select every unique entry whose `executedAt` is newer than the snapshot. The journal directory, not the restored database, is authoritative for the post-snapshot window: restoring the dump necessarily restores an older copy of `deletion_journal`. The endpoint on the restored instance is still useful to inspect what the snapshot already knew and to cross-check the NDJSON set.

**Closing blocker:** Phase 2 exposes the read feed and `POST /api/v1/admin/maintenance/deletion-sweep`, but it does not expose a targeted journal-replay/import endpoint. The sweep only executes accounts already due in the restored database; it cannot reapply a post-snapshot tombstone represented only by `userId`. If the NDJSON/feed contains any newer entry, keep the app closed and escalate for a product-code replay path. Do not improvise direct SQL or hand-delete related rows.

If the offsite journal is missing or the post-snapshot window cannot be reconstructed, treat that as a compliance incident to record and disclose. Do not reopen on the assumption that no entries means no deletions.

## 6. Bring the app back

```sh
docker compose up -d
```

Confirm `/health` reports healthy and the app behaves normally against the restored data before
considering the restore complete.
