#!/usr/bin/env bash
# The mandatory pre-push gate (CLAUDE.md), parallelized the same way as .github/workflows/ci.yml:
# one build, then concurrent --no-build test processes — unit projects plus the same four
# namespace-anchored integration shards. Keep the shard filters in lockstep with ci.yml's
# `integration` matrix: the partition is exhaustive by construction only while the exclusion
# list in the `rest` shard names every carved-out namespace.
#
# Run from the repo root inside WSL:
#   scripts/wsl-gate.sh
# or from Windows:
#   wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && scripts/wsl-gate.sh"
#
# Testcontainers gives every factory its own randomized host port, so shards may run
# concurrently against Docker. Logs land one file per lane in $LOG_DIR; the script exits
# non-zero if any lane fails and prints the failing lanes' tails.
set -u

ARTIFACTS=${ARTIFACTS:-/tmp/partyup-artifacts}
LOG_DIR=${LOG_DIR:-/tmp/partyup-gate-logs}
mkdir -p "$LOG_DIR"
rm -f "$LOG_DIR"/*.log

echo "== build =="
dotnet build PartyUp.sln -p:ArtifactsPath="$ARTIFACTS" -warnaserror > "$LOG_DIR/build.log" 2>&1
if [ $? -ne 0 ]; then
  echo "BUILD FAILED"; tail -30 "$LOG_DIR/build.log"; exit 1
fi

declare -A LANES=(
  [unit-domain]="tests/PartyUp.Domain.Tests|"
  [unit-application]="tests/PartyUp.Application.Tests|"
  [api-nonintegration]="tests/PartyUp.Api.IntegrationTests|Category!=Integration"
  [int-sessions]="tests/PartyUp.Api.IntegrationTests|Category=Integration&FullyQualifiedName~IntegrationTests.Sessions."
  [int-polls]="tests/PartyUp.Api.IntegrationTests|Category=Integration&FullyQualifiedName~IntegrationTests.Polls."
  [int-social]="tests/PartyUp.Api.IntegrationTests|Category=Integration&(FullyQualifiedName~IntegrationTests.Friends.|FullyQualifiedName~IntegrationTests.Notifications.|FullyQualifiedName~IntegrationTests.Availability.)"
  [int-rest]="tests/PartyUp.Api.IntegrationTests|Category=Integration&FullyQualifiedName!~IntegrationTests.Sessions.&FullyQualifiedName!~IntegrationTests.Polls.&FullyQualifiedName!~IntegrationTests.Friends.&FullyQualifiedName!~IntegrationTests.Notifications.&FullyQualifiedName!~IntegrationTests.Availability."
)

declare -A PIDS
for lane in "${!LANES[@]}"; do
  project=${LANES[$lane]%%|*}
  filter=${LANES[$lane]#*|}
  if [ -n "$filter" ]; then
    dotnet test "$project" --no-build -p:ArtifactsPath="$ARTIFACTS" --filter "$filter" \
      > "$LOG_DIR/$lane.log" 2>&1 &
  else
    dotnet test "$project" --no-build -p:ArtifactsPath="$ARTIFACTS" \
      > "$LOG_DIR/$lane.log" 2>&1 &
  fi
  PIDS[$lane]=$!
  echo "started $lane (pid ${PIDS[$lane]})"
done

FAILED=()
for lane in "${!PIDS[@]}"; do
  if ! wait "${PIDS[$lane]}"; then
    FAILED+=("$lane")
  fi
done

echo "== results =="
for lane in "${!LANES[@]}"; do
  grep -hE 'Passed!|Failed!' "$LOG_DIR/$lane.log" | sed "s/^/[$lane] /"
done

if [ ${#FAILED[@]} -ne 0 ]; then
  echo "== GATE FAILED: ${FAILED[*]} =="
  for lane in "${FAILED[@]}"; do
    echo "---- $lane tail ----"
    grep -B2 -A12 '\[FAIL\]' "$LOG_DIR/$lane.log" | head -60
  done
  exit 1
fi

echo "== GATE GREEN =="
