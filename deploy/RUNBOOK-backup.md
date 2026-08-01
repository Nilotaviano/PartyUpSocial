# Backup runbook

## How it runs

The `backup` service in `compose.yaml` is a `postgres:17-alpine` container running
`deploy/backup-entrypoint.sh` as its entrypoint. That script loops forever: it sleeps
until the next 03:00 UTC, runs

```sh
pg_dump -Fc -f /backups/partyup_<UTC timestamp>.dump
```

against the `postgres` service (connection via libpq env vars `PGHOST`/`PGUSER`/`PGPASSWORD`/`PGDATABASE`,
set from `.env`), then deletes any `*.dump` file in `/backups` older than 30 days, then goes
back to sleep. `-Fc` is pg_dump's custom format — compressed, and restorable with `pg_restore`
(including selective/parallel restore), unlike a plain SQL dump.

## Where backups land

`/backups` inside the `backup` container is the named volume `backups` (declared under
`volumes:` in `compose.yaml`). To find its path on the VM or copy a dump out:

```sh
docker volume inspect partyup_backups   # Mountpoint is the host path (needs root to read directly)

# or, without needing the host path:
docker run --rm -v partyup_backups:/backups -v "$PWD":/out alpine \
  cp /backups/partyup_20260730_030000.dump /out/
```

(Volume name is prefixed with the compose project name — `partyup` if the stack was brought up
from a directory named `partyup`; confirm with `docker volume ls`.)

## Deletion-journal backup set (A-038)

Database dumps are only half of the compliance backup. The app's `Deletion:JournalPath` defaults to `/data/deletion-journal`, where `FileDeletionJournalShipper` appends monthly NDJSON files after the tombstone transaction commits. That directory must live on a durable Compose mount (expected volume name `partyup_deletion_journal`) and must be copied offsite with the dumps. Keep the files mode 600; the stable user ids are pseudonymous personal data, not anonymous telemetry.

**Closing blocker:** the checked-in `deploy/compose.yaml` does not currently mount `/data/deletion-journal` into `app` or declare the expected volume. Do not report A-038 backup coverage as healthy until that Compose wiring is added and deployed. After it exists, locate it with:

```sh
docker volume inspect partyup_deletion_journal
```

The offsite job must copy both:

- `partyup_backups/_data/*.dump`, with the existing 30-day dump retention; and
- the complete `partyup_deletion_journal/_data/*.ndjson` directory. Do not prune journal files merely because the matching dump aged out; retention follows the privacy/legal policy and must still cover every snapshot that could be restored.

## Why 30-day retention

This isn't just disk hygiene. Per PRODUCT_SPEC.md §8.6, account deletion has a 7-day cancelable
grace period, after which the account's friendships, future participation, owned sessions, and
owned polls are removed and its Completed-session history is anonymized to **Anonymous User**.
A restore from a backup taken before that cleanup ran would resurrect the deleted user's personal
data as if the deletion had never happened. Bounding retention to 30 days bounds how much
post-deletion state a restore can resurrect, and keeps the required "re-apply deletions newer than
the snapshot" restore step (see `RUNBOOK-restore.md`) a tractable, recent window instead of an
open-ended audit.

## Offsite copy (rclone) — target TBD

`/backups` currently only exists on the VM itself — a disk failure or VM loss takes the backups
with it. This must be closed at Phase 9 go-live, before the first beta invite. Setup, once a
remote target (object storage provider) is chosen:

1. Install `rclone` on the **host** (not in the `backup` container — it doesn't need docker-in-docker,
   and a host cron surviving container restarts is simpler than adding rclone to the image):
   ```sh
   curl https://rclone.org/install.sh | sudo bash
   ```
2. Configure the remote interactively (`rclone config`), name it `offsite`. Whatever the provider,
   store the credentials in `rclone`'s config file only — never in this repo.
3. Point it at the same named volume's host path (see `docker volume inspect partyup_backups`
   above) with a host cron entry that runs after the nightly dump, e.g. `crontab -e`:
   ```
   15 3 * * * rclone sync /var/lib/docker/volumes/partyup_backups/_data offsite:partyup-backups --min-age 5m
   ```
   `--min-age 5m` skips a dump file still being written. Adjust the path to whatever
   `docker volume inspect` actually reports.
4. Apply the same 30-day retention offsite as local, either via the remote's own lifecycle rule
   (preferred, e.g. an S3/B2 bucket lifecycle policy) or `rclone delete` with an age filter in the
   same cron job.
5. Verify: after the first successful run, confirm a `.dump` file is visible via `rclone ls offsite:partyup-backups`.

Add a second sync for the deletion-journal volume after its Compose blocker is closed, for example `rclone sync /var/lib/docker/volumes/partyup_deletion_journal/_data offsite:partyup-deletion-journal`, and verify at least the current `<yyyy-MM>.ndjson` object is visible. A dump-only offsite run is a failed backup for A-017/A-038 purposes.

Until this is done, treat the VM's local disk as the only copy of the database and weigh that risk
accordingly — this is the gap the Phase 9 restore drill (`RUNBOOK-restore.md`) does not cover.
