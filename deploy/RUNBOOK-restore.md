# Restore runbook

> A restore drill using these exact steps is a Phase 0 exit criterion (IMPLEMENTATION_PLAN.md
> Phase 0, ARCHITECTURE.md §10) — this runbook isn't done until someone has followed it against
> a real dump and confirmed the app comes back up correctly. Update it with anything that
> surprised you.

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

A `pg_dump` only reflects data as of the moment it ran. Per ARCHITECTURE.md §8 and
PRODUCT_SPEC.md §8.6, this step is not optional: if any account's 7-day deletion grace period
expired — or a deletion was requested — between the snapshot timestamp and now, the restored
database will show that account (and its friendships, sessions, polls, history) as if the
deletion had never completed. Before reopening the app to users:

1. Identify accounts whose deletion grace period expired, or that requested deletion, in the
   window between the dump's snapshot time and the incident. Cross-reference application logs,
   notification records, or any audit trail available — there is no single query for this until
   the deletion feature ships its own tracking (Phase 2+).
2. For each one, re-run the deletion-completion effects (friendship removal, future-participation
   removal, owned-session cancellation, owned-poll expiry, Completed-history anonymization to
   **Anonymous User**) through the product's own deletion code path once it exists, not by hand
   in SQL — that's the only way to guarantee every effect in D-092 is applied consistently.
3. If the window can't be reliably reconstructed (no usable log), treat that as a compliance
   incident to record and disclose, not a footnote — the deletion promise was violated for an
   unknown set of accounts.

## 6. Bring the app back

```sh
docker compose up -d
```

Confirm `/health` reports healthy and the app behaves normally against the restored data before
considering the restore complete.
