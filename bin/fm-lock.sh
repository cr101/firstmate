#!/usr/bin/env bash
# Acquire or inspect the per-home firstmate session lock.
# Writes the harness session identity, normally the process PID found by walking
# the shell's ancestry and an opaque launch-bound identity for the native owner.
# Usage: fm-lock.sh           acquire; exit 1 unless ownership is verified
#        fm-lock.sh status    print holder and liveness; always exits 0
#        fm-lock.sh native-admission-predicate
#                             exit 0 only when no owner excludes a native launch
set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FM_ROOT="${FM_ROOT_OVERRIDE:-$(cd "$SCRIPT_DIR/.." && pwd)}"
FM_HOME="${FM_HOME:-${FM_ROOT_OVERRIDE:-$FM_ROOT}}"
STATE="${FM_STATE_OVERRIDE:-$FM_HOME/state}"
LOCK="$STATE/.lock"

# Harness identity (FM_HARNESS_RE, ancestry walk, holder liveness) is owned by
# the shared session-lock lib so the Claude Stop auto-arm applies the exact
# same identity contract.
# shellcheck source=bin/fm-session-lock-lib.sh
. "$SCRIPT_DIR/fm-session-lock-lib.sh"

fm_lock_owner_label() {
  case "$1" in
    native:*) printf 'native owner identity %s' "$1" ;;
    *) printf 'harness pid %s' "$1" ;;
  esac
}

fm_lock_holder_label() {
  case "$1" in
    native:*) printf 'native owner identity %s' "$1" ;;
    *) printf 'pid %s' "$1" ;;
  esac
}

fm_lock_conflict_message() {
  if ! fm_session_pid_valid "$1"; then
    printf 'error: session lock owner is unrecognized; operate read-only until resolved'
    return 0
  fi
  if fm_harness_pid_alive "$1"; then
    case "$1" in
      native:*) printf 'error: another live firstmate session holds the lock (native owner identity %s); operate read-only until resolved' "$1" ;;
      *) printf 'error: another live firstmate session holds the lock (pid %s); operate read-only until resolved' "$1" ;;
    esac
    return 0
  fi
  if fm_harness_pid_excludes "$1"; then
    printf 'error: another firstmate session may hold the lock (%s); operate read-only until resolved' "$(fm_lock_holder_label "$1")"
    return 0
  fi
  return 1
}

fm_lock_native_admission_proves_dead() {
  local wanted=${1#native:} list=${FM_NATIVE_PROVEN_DEAD_GENERATIONS:-} generation found=1
  local IFS=,
  [ -n "$list" ] || return 1
  case "$list" in ,*|*,|*,,*) return 2 ;; esac
  for generation in $list; do
    [ "${#generation}" -eq 32 ] || return 2
    case "$generation" in *[!0-9a-f]*) return 2 ;; esac
    [ "$generation" != "$wanted" ] || found=0
  done
  return "$found"
}

if [ "${1:-}" = "native-admission-predicate" ]; then
  [ "$#" -eq 1 ] || {
    echo "usage: fm-lock.sh native-admission-predicate" >&2
    exit 2
  }
  if [ ! -e "$LOCK" ] && [ ! -L "$LOCK" ]; then
    exit 0
  fi
  if [ ! -f "$LOCK" ] || [ -L "$LOCK" ]; then
    echo "error: session lock is not a readable regular file; native launch refused" >&2
    exit 1
  fi
  old=$(cat "$LOCK" 2>/dev/null) || {
    echo "error: session lock is unreadable; native launch refused" >&2
    exit 1
  }
  if ! fm_session_pid_valid "$old"; then
    echo "error: session lock owner is unrecognized; native launch refused" >&2
    exit 1
  fi
  case "$old" in
    native:*)
      if fm_lock_native_admission_proves_dead "$old"; then
        exit 0
      else
        dead_rc=$?
      fi
      if [ "$dead_rc" -eq 2 ]; then
        echo "error: native dead-generation evidence is unrecognized; native launch refused" >&2
        exit 1
      fi
      ;;
  esac
  if fm_harness_pid_excludes "$old"; then
    if conflict=$(fm_lock_conflict_message "$old"); then
      echo "$conflict" >&2
    else
      echo "error: another firstmate session may hold the lock; native launch refused" >&2
    fi
    exit 1
  fi
  exit 0
fi

mkdir -p "$STATE" 2>/dev/null || {
  echo "error: cannot create session-lock state directory $STATE; operate read-only until resolved" >&2
  exit 1
}

if [ "${1:-}" = "status" ]; then
  if [ ! -f "$LOCK" ]; then echo "lock: free"; exit 0; fi
  old=$(cat "$LOCK" 2>/dev/null) || {
    echo "lock: unreadable"
    exit 0
  }
  if ! fm_session_pid_valid "$old"; then
    echo "lock: held by unrecognized owner with unknown health"
  elif fm_harness_pid_alive "$old"; then
    echo "lock: held by live $(fm_lock_owner_label "$old")"
  elif fm_harness_pid_excludes "$old"; then
    case "$old" in
      native:*) echo "lock: held by native owner with unconfirmed health $old" ;;
      *) echo "lock: held by owner with unconfirmed health ($(fm_lock_holder_label "$old"))" ;;
    esac
  else
    echo "lock: stale ($(fm_lock_holder_label "$old") dead or not a harness)"
  fi
  exit 0
fi

me=$(fm_harness_ancestry_pid) || {
  if fm_win_boundary_applies; then
    # Here the parent link does not reach the harness at all, so "not in the
    # ancestry" would describe the wrong problem and send the reader hunting a
    # process tree that can never contain the answer.
    echo "error: cannot identify this harness session on Windows: it publishes no session pid this build recognizes (see FM_WIN_HARNESS_PID_VARS in bin/fm-session-lock-lib.sh); operate read-only until resolved" >&2
  else
    echo "error: cannot locate harness process in ancestry" >&2
  fi
  exit 1
}
probe=$(mktemp "$STATE/.lock-write.XXXXXX" 2>/dev/null) || {
  echo "error: cannot write session lock; operate read-only until resolved" >&2
  exit 1
}
rm -f "$probe" 2>/dev/null || {
  echo "error: cannot clean session-lock publication probe; operate read-only until resolved" >&2
  exit 1
}
# shellcheck source=bin/fm-wake-lib.sh
. "$SCRIPT_DIR/fm-wake-lib.sh"
CLAIM_LOCK="$STATE/.lock.acquire"
CLAIM_LOCK_HELD=0
release_claim_lock() {
  if [ "$CLAIM_LOCK_HELD" -eq 1 ]; then
    fm_lock_release "$CLAIM_LOCK"
    CLAIM_LOCK_HELD=0
  fi
}
trap release_claim_lock EXIT
trap 'exit 1' HUP INT TERM

if [ -f "$LOCK" ] && [ ! -L "$LOCK" ]; then
  old=$(cat "$LOCK" 2>/dev/null || true)
  if [ "$old" = "$me" ]; then
    echo "lock acquired: $(fm_lock_owner_label "$me")"
    exit 0
  fi
  if conflict=$(fm_lock_conflict_message "$old"); then
    echo "$conflict" >&2
    exit 1
  fi
fi

if ! fm_lock_try_acquire "$CLAIM_LOCK"; then
  sweep_pid=$(sed -n 's/^pid=//p' "$STATE/.startup-network.status" 2>/dev/null | tail -1)
  if [ -n "${FM_LOCK_HELD_PID:-}" ] && [ "$FM_LOCK_HELD_PID" = "$sweep_pid" ]; then
    echo "error: the prior session's bounded startup sweep is finishing; operate read-only until it releases the fleet lock" >&2
    exit 1
  fi
  fm_lock_acquire_wait "$CLAIM_LOCK"
fi
CLAIM_LOCK_HELD=1

if [ -e "$LOCK" ] || [ -L "$LOCK" ]; then
  if [ ! -f "$LOCK" ] || [ -L "$LOCK" ]; then
    echo "error: session lock is not a regular file; operate read-only until resolved" >&2
    exit 1
  fi
  old=$(cat "$LOCK" 2>/dev/null) || {
    echo "error: session lock is unreadable; operate read-only until resolved" >&2
    exit 1
  }
  if [ "$old" != "$me" ]; then
    if conflict=$(fm_lock_conflict_message "$old"); then
      echo "$conflict" >&2
      exit 1
    fi
  fi
fi
if ! { printf '%s\n' "$me" > "$LOCK"; } 2>/dev/null; then
  echo "error: cannot write session lock; operate read-only until resolved" >&2
  exit 1
fi
written=$(cat "$LOCK" 2>/dev/null) || {
  echo "error: cannot verify session lock ownership; operate read-only until resolved" >&2
  exit 1
}
if [ ! -f "$LOCK" ] || [ -L "$LOCK" ] || [ "$written" != "$me" ]; then
  echo "error: session lock ownership verification failed; operate read-only until resolved" >&2
  exit 1
fi
release_claim_lock
echo "lock acquired: $(fm_lock_owner_label "$me")"
