#!/bin/sh
# Nightly pg_dump loop for the `backup` service in compose.yaml.
# Relies on libpq env vars (PGHOST/PGUSER/PGPASSWORD/PGDATABASE) set by that
# service definition. Wait time uses `date -u +%s` (epoch seconds) mod 86400
# rather than parsing %H/%M/%S, so zero-padded fields (e.g. "08") never hit
# shell arithmetic's octal rules, and no `date -d` parsing is needed either —
# both matter because /bin/sh here is busybox ash, not bash.
#
# Offsite copy is intentionally not implemented here — see
# deploy/RUNBOOK-backup.md for the rclone setup that pushes this directory
# off the VM.
set -eu

BACKUP_DIR=/backups
RETENTION_DAYS=30
TARGET_HOUR=3 # 03:00 UTC

while true; do
  now_secs=$(($(date -u +%s) % 86400))
  target_secs=$((TARGET_HOUR * 3600))

  if [ "$now_secs" -ge "$target_secs" ]; then
    wait_secs=$(((24 * 3600) - now_secs + target_secs))
  else
    wait_secs=$((target_secs - now_secs))
  fi

  echo "backup: sleeping ${wait_secs}s until next 0${TARGET_HOUR}:00 UTC run"
  sleep "$wait_secs"

  stamp=$(date -u +%Y%m%d_%H%M%S)
  dump_file="$BACKUP_DIR/partyup_${stamp}.dump"
  echo "backup: dumping to $dump_file"
  pg_dump -Fc -f "$dump_file"

  echo "backup: purging dumps older than $RETENTION_DAYS days"
  find "$BACKUP_DIR" -type f -name '*.dump' -mtime "+$RETENTION_DAYS" -delete
done
