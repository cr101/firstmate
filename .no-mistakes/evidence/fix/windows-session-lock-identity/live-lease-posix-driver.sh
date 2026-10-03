#!/usr/bin/env bash
set -u
export MSYS=winsymlinks:nativestrict
unset FM_ROOT_OVERRIDE FM_STATE_OVERRIDE FM_DATA_OVERRIDE FM_CONFIG_OVERRIDE FM_PROJECTS_OVERRIDE NO_MISTAKES_GATE FM_GATE_REFUSE_BYPASS
SELF=${CLAUDE_PID:?}; FOREIGN=$1
LAB=$(mktemp -d "${TMPDIR:-/tmp}/fm-lab.XXXXXX"); bin/fm-lab-home.sh create "$LAB" >/dev/null; export FM_HOME=$LAB
echo "== S7 lease: lock=win:$SELF, branch claims with the host-style export (whole first line of state/.lock)"
printf 'win:%s\n' "$SELF" > "$LAB/state/.lock"
HOLDER=$(sed -n '1p' "$LAB/state/.lock" 2>/dev/null)   # same expression as bin/fm-supervision-host.sh handle_wake
FM_SUPERVISION_ACTOR=branch FM_LEASE_HOLDER_PID="$HOLDER" timeout 30 bin/fm-lease.sh claim demo-task; echo "claim exit=$?"
echo "lease file: $(tr '\t' ' ' < "$LAB/state/.lease-demo-task")"
timeout 30 bin/fm-lease.sh check demo-task; echo "check exit=$?"
FM_SUPERVISION_ACTOR=main timeout 30 bin/fm-lease.sh claim demo-task; echo "main claim exit=$? (6 = refused, branch lease live)"
echo "== S7b lease held by a dead tagged holder reads stale; main may take it"
printf 'win:999996\n' > "$LAB/state/.lock"
timeout 30 bin/fm-lease.sh check demo-task; echo "check exit=$?"
FM_SUPERVISION_ACTOR=main timeout 30 bin/fm-lease.sh claim demo-task; echo "main claim exit=$?"
echo; echo "== S8 POSIX unchanged: same live win:$FOREIGN lock read with uname reporting Linux"
FAKE=$(mktemp -d); printf '#!/bin/sh\necho Linux\n' > "$FAKE/uname"; chmod +x "$FAKE/uname"
printf 'win:%s\n' "$FOREIGN" > "$LAB/state/.lock"
echo "-- uname now: $(PATH="$FAKE:$PATH" uname -s)"
PATH="$FAKE:$PATH" timeout 30 bin/fm-lock.sh status
( PATH="$FAKE:$PATH"; . bin/fm-session-lock-lib.sh
  fm_session_pid_valid "win:$FOREIGN" && echo "pid_valid=yes" || echo "pid_valid=no (rejected like main)"
  fm_session_lock_foreign_owner_live "$LAB/state" && echo foreign_live=yes || echo foreign_live=no )
echo "-- Stop auto-arm hook with a win:N lock on Linux (must stay inert, lock untouched)"
printf '{}' | PATH="$FAKE:$PATH" timeout 30 bin/fm-claude-stop-autoarm.sh; echo "hook exit=$?"; echo "lock after hook: $(head -1 "$LAB/state/.lock")"
echo "-- same hook on real Windows uname (foreign live holder): lock must also remain"
printf '{}' | timeout 30 bin/fm-claude-stop-autoarm.sh; echo "hook exit=$?"; echo "lock after hook: $(head -1 "$LAB/state/.lock")"
rm -rf "$FAKE" "$LAB"; echo "== lab removed: $( [ -e "$LAB" ] && echo no || echo yes)"
