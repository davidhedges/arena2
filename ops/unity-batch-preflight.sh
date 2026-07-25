#!/usr/bin/env bash
#
# Make a Unity batch launch safe and repeatable without hiding a live editor.
#
# The preflight:
#   - refuses while this project has a live Unity editor;
#   - terminates only orphaned, long-running batch editors for this project;
#   - moves an unowned UnityLockfile to /tmp instead of deleting it.
#
# Usage: ops/unity-batch-preflight.sh [project-root]
# Exit: 0 = ready for a fresh batch launch, 2 = live editor or bad input.

set -euo pipefail

REQUESTED_ROOT="${1:-$(cd "$(dirname "$0")/.." && pwd)}"
if ! PROJECT_ROOT="$(cd "$REQUESTED_ROOT" 2>/dev/null && pwd -P)"; then
  echo "PREFLIGHT REFUSING: project root does not exist: $REQUESTED_ROOT"
  exit 2
fi

STALE_BATCH_SECONDS="${UNITY_BATCH_STALE_SECONDS:-21600}"
if ! [[ "$STALE_BATCH_SECONDS" =~ ^[0-9]+$ ]]; then
  echo "PREFLIGHT REFUSING: UNITY_BATCH_STALE_SECONDS must be a non-negative integer."
  exit 2
fi

elapsed_seconds() {
  local raw="${1//[[:space:]]/}"
  local days=0
  local first=0
  local second=0
  local third=0
  if [[ "$raw" == *-* ]]; then
    days="${raw%%-*}"
    raw="${raw#*-}"
  fi

  IFS=: read -r first second third <<<"$raw"
  if [ -z "${third:-}" ]; then
    third="$second"
    second="$first"
    first=0
  fi
  days=$((10#$days))
  first=$((10#$first))
  second=$((10#$second))
  third=$((10#$third))
  echo $((days * 86400 + first * 3600 + second * 60 + third))
}

terminate_pids() {
  local label="$1"
  shift
  if [ "$#" -eq 0 ]; then
    return
  fi

  echo "PREFLIGHT: terminating stale $label PID(s): $*"
  kill -TERM "$@" 2>/dev/null || true
  sleep 1

  local survivors=()
  local pid
  for pid in "$@"; do
    if kill -0 "$pid" 2>/dev/null; then
      survivors+=("$pid")
    fi
  done
  if [ "${#survivors[@]}" -gt 0 ]; then
    echo "PREFLIGHT: force-stopping unresponsive stale $label PID(s): ${survivors[*]}"
    kill -KILL "${survivors[@]}" 2>/dev/null || true
  fi
}

scan_editors() {
  project_editor_pids=()
  stale_project_batch_pids=()
  project_editor_rows=()

  local pid
  local ppid
  local etime
  local state
  local command
  local age
  while read -r pid ppid etime state command; do
    if [[ "$command" != *"/Unity.app/Contents/MacOS/Unity"* ]] ||
       [[ "$state" == Z* ]]; then
      continue
    fi

    if [[ "$command" != *"-projectPath $PROJECT_ROOT"* ]]; then
      continue
    fi

    age="$(elapsed_seconds "$etime")"
    if [ "$ppid" -eq 1 ] &&
       [[ " $command " == *" -batchmode "* ]] &&
       [ "$age" -ge "$STALE_BATCH_SECONDS" ]; then
      stale_project_batch_pids+=("$pid")
      continue
    fi

    project_editor_pids+=("$pid")
    project_editor_rows+=("PID $pid (elapsed $etime): $command")
  done < <(ps -axo pid=,ppid=,etime=,state=,command=)
}

scan_editors
if [ "${#stale_project_batch_pids[@]}" -gt 0 ]; then
  terminate_pids \
    "orphaned Unity batch editor" \
    "${stale_project_batch_pids[@]}"
fi
scan_editors

if [ "${#project_editor_pids[@]}" -gt 0 ]; then
  echo "PREFLIGHT REFUSING: this project already has a live Unity editor:"
  printf '  %s\n' "${project_editor_rows[@]}"
  exit 2
fi

LOCK_PATH="$PROJECT_ROOT/Temp/UnityLockfile"
if [ -e "$LOCK_PATH" ]; then
  PROJECT_SLUG="$(basename "$PROJECT_ROOT" | tr -cs '[:alnum:]_.-' '_')"
  STALE_LOCK="/tmp/${PROJECT_SLUG}.UnityLockfile.stale.$(date -u +%Y%m%dT%H%M%SZ).$$"
  mv "$LOCK_PATH" "$STALE_LOCK"
  echo "PREFLIGHT: moved unowned UnityLockfile to $STALE_LOCK"
fi

echo "PREFLIGHT: ready for Unity batch launch at $PROJECT_ROOT"
