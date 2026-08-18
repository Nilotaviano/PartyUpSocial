#!/usr/bin/env bash
# Deterministic wrapper around local `act` runs of .github/workflows/ci.yml (9 jobs:
# CI/web; CI/unit; CI/integration matrix sessions/polls/social/rest; CI/browser matrix
# chromium/firefox/webkit).
#
# Current-state constraint: under act's full parallel run, the three latency-sensitive
# jobs (CI/web, CI/browser (firefox), CI/browser (webkit)) starve for CPU alongside the
# other six and flake red; each is deterministically green when run solo. Backend jobs
# (unit, the four integration shards) and CI/browser (chromium) never flake under
# parallelism. This script therefore treats the full run as advisory ONLY for that
# trio: any of the other six jobs failing is real signal (exit 1, no rerun); a red
# trio member gets exactly one solo rerun, and a red solo run is real signal too
# (exit 1, no second retry).
#
# act also has a known quirk ("job-failed-with-green-tests") where a job's test totals
# print all-green but the job itself still fails. Trust only the act footer line
# ("🏁  Job succeeded/failed"), never the dotnet test "Passed!/Failed!" totals.
#
# Run from WSL (Ubuntu-24.04), repo root:
#   scripts/act-gate.sh
# or from Windows:
#   wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && scripts/act-gate.sh"
#
# Logs land one file per run in $LOG_DIR (default ~/act-gate — NOT /tmp, which WSL
# restarts wipe), overwritten on each invocation.
set -uo pipefail

LOG_DIR=${LOG_DIR:-"$HOME/act-gate"}
mkdir -p "$LOG_DIR"

STRIP_ANSI='s/\x1b\[[0-9;]*m//g'

TRIO_WEB="CI/web"
TRIO_FF="CI/browser (firefox)"
TRIO_WK="CI/browser (webkit)"

ALL_JOBS=(
  "CI/web"
  "CI/unit"
  "CI/integration (sessions)"
  "CI/integration (polls)"
  "CI/integration (social)"
  "CI/integration (rest)"
  "CI/browser (chromium)"
  "CI/browser (firefox)"
  "CI/browser (webkit)"
)

is_trio() {
  case "$1" in
    "$TRIO_WEB"|"$TRIO_FF"|"$TRIO_WK") return 0 ;;
    *) return 1 ;;
  esac
}

# Emits "NORMALIZED_LABEL|succeeded"  or  "NORMALIZED_LABEL|failed" for every
# "🏁  Job succeeded/failed" footer line found in $1, ANSI-stripped first.
# Matrix jobs run in parallel get an act-appended "-N" suffix on the label
# (e.g. "CI/browser (firefox)-3"); that suffix is stripped here.
job_verdicts() {
  local log="$1"
  sed "$STRIP_ANSI" "$log" \
    | sed -nE 's/^\[([^]]+)\][[:space:]]*🏁[[:space:]]+Job (succeeded|failed).*/\1|\2/p' \
    | sed -E 's/[[:space:]]+\|/|/' \
    | sed -E 's/^(.*)-[0-9]+\|/\1|/'
}

# Populates the assoc array named by $2 with label->verdict parsed from log $1.
# A job whose footer line never appears (crash, timeout, act error) is left
# unset by this function; callers default missing entries to "missing".
parse_into() {
  local log="$1" out_name="$2" line label verdict
  while IFS='|' read -r label verdict; do
    [ -n "$label" ] || continue
    eval "$out_name[\"\$label\"]=\"\$verdict\""
  done < <(job_verdicts "$log")
}

echo "== stage 1: full parallel run =="
act >"$LOG_DIR/full.log" 2>&1
FULL_EXIT=$?
echo "act exit=$FULL_EXIT (informational only; verdicts below are parsed from the footer lines)"

declare -A STAGE1
parse_into "$LOG_DIR/full.log" STAGE1

declare -A FINAL
declare -A RESCUED
FAILED_NONTRIO=()

for job in "${ALL_JOBS[@]}"; do
  verdict="${STAGE1[$job]:-missing}"
  FINAL["$job"]="$verdict"
  if [ "$verdict" != "succeeded" ] && ! is_trio "$job"; then
    FAILED_NONTRIO+=("$job")
  fi
done

if [ "${#FAILED_NONTRIO[@]}" -ne 0 ]; then
  echo "== GATE FAILED: non-trio job(s) failed, no rerun applies =="
  for job in "${FAILED_NONTRIO[@]}"; do
    echo "  - $job: ${FINAL[$job]}"
  done
  exit 1
fi

rerun_solo() {
  local job="$1" logfile="$2"
  shift 2
  echo "== solo rerun: $job =="
  "$@" >"$LOG_DIR/$logfile" 2>&1
  local exit_code=$?
  declare -A SOLO
  parse_into "$LOG_DIR/$logfile" SOLO
  local verdict="${SOLO[$job]:-missing}"
  echo "  act exit=$exit_code, footer verdict=$verdict"
  printf '%s' "$verdict"
}

if [ "${FINAL[$TRIO_WEB]}" != "succeeded" ]; then
  v=$(rerun_solo "$TRIO_WEB" "solo-web.log" act -j web)
  FINAL["$TRIO_WEB"]="$v"
  [ "$v" = "succeeded" ] && RESCUED["$TRIO_WEB"]=1
fi

if [ "${FINAL[$TRIO_FF]}" != "succeeded" ]; then
  v=$(rerun_solo "$TRIO_FF" "solo-firefox.log" act -j browser --matrix browser:firefox)
  FINAL["$TRIO_FF"]="$v"
  [ "$v" = "succeeded" ] && RESCUED["$TRIO_FF"]=1
fi

if [ "${FINAL[$TRIO_WK]}" != "succeeded" ]; then
  v=$(rerun_solo "$TRIO_WK" "solo-webkit.log" act -j browser --matrix browser:webkit)
  FINAL["$TRIO_WK"]="$v"
  [ "$v" = "succeeded" ] && RESCUED["$TRIO_WK"]=1
fi

echo "== summary =="
GATE_FAILED=0
for job in "${ALL_JOBS[@]}"; do
  verdict="${FINAL[$job]}"
  tag=""
  if [ -n "${RESCUED[$job]:-}" ]; then
    tag=" (flake-rescued via solo rerun)"
  fi
  echo "  [$verdict] $job$tag"
  if [ "$verdict" != "succeeded" ]; then
    GATE_FAILED=1
  fi
done

if [ "$GATE_FAILED" -ne 0 ]; then
  echo "== GATE FAILED: solo rerun(s) still red — real signal, no further retry =="
  exit 1
fi

echo "== GATE GREEN =="
exit 0
