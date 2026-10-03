#!/usr/bin/env bash
# Live Windows/Git Bash driver for the tagged session-lock identity (run from the gate worktree).
set -u
export MSYS=winsymlinks:nativestrict
unset FM_ROOT_OVERRIDE FM_STATE_OVERRIDE FM_DATA_OVERRIDE FM_CONFIG_OVERRIDE FM_PROJECTS_OVERRIDE NO_MISTAKES_GATE FM_GATE_REFUSE_BYPASS
SELF=${CLAUDE_PID:?}      # this live claude.exe session
FOREIGN=$1                # another live claude.exe (observed read-only via ps -W)
DEAD=$2                   # a Windows pid absent from ps -W
LAB=$(mktemp -d "${TMPDIR:-/tmp}/fm-lab.XXXXXX"); bin/fm-lab-home.sh create "$LAB" >/dev/null
export FM_HOME=$LAB
T() { timeout 30 "$@"; }
echo "== platform: $(uname -s); self=win:$SELF foreign=win:$FOREIGN dead=win:$DEAD"
echo "== ps -o support check:"; ps -o comm= -p $$ 2>&1 | head -1
echo; echo "== S1 acquire on free home (fm-lock.sh)"; T bin/fm-lock.sh; echo "exit=$?"; echo "state/.lock line1: $(head -1 "$LAB/state/.lock")"
echo "== status"; T bin/fm-lock.sh status
echo "== re-acquire by same session"; T bin/fm-lock.sh; echo "exit=$?"
echo; echo "== S2 owned_by_self / foreign_owner_live (own lock)"
( . bin/fm-session-lock-lib.sh; fm_session_lock_owned_by_self "$LAB/state" && echo owned_by_self=yes || echo owned_by_self=no
  fm_session_lock_foreign_owner_live "$LAB/state" && echo foreign_live=yes || echo foreign_live=no )
echo; echo "== S3 second live session holds the lock (win:$FOREIGN)"
printf 'win:%s\n' "$FOREIGN" > "$LAB/state/.lock"; rm -f "$LAB/state/.lock-session"
T bin/fm-lock.sh; echo "acquire exit=$? (expect refusal)"; echo "lock still: $(head -1 "$LAB/state/.lock")"
T bin/fm-lock.sh status
( . bin/fm-session-lock-lib.sh; fm_session_lock_owned_by_self "$LAB/state" && echo owned_by_self=yes || echo owned_by_self=no
  fm_session_lock_foreign_owner_live "$LAB/state" && echo "foreign_live=yes pid=$FM_SESSION_LOCK_FOREIGN_OWNER_PID" || echo foreign_live=no )
echo "== inbox ready lock fields"; T bin/fm-inbox.sh ready 2>&1 | grep -i lock
echo; echo "== S4 dead tagged holder (win:$DEAD) -> stale, reclaimed"
printf 'win:%s\n' "$DEAD" > "$LAB/state/.lock"
T bin/fm-lock.sh status
T bin/fm-lock.sh; echo "acquire exit=$?"; echo "lock now: $(head -1 "$LAB/state/.lock")"
echo; echo "== S5 malformed tagged values are rejected"
( . bin/fm-session-lock-lib.sh; for v in 'win:7abc' 'win:' 'win:7 x' "win:$FOREIGN"; do fm_session_pid_valid "$v" && echo "valid   [$v]" || echo "invalid [$v]"; done )
for v in 'win:7abc' 'win:'; do printf '%s\n' "$v" > "$LAB/state/.lock"; T bin/fm-lock.sh status; done
echo; echo "== S6 harness publishing no pid stays read-only"
rm -f "$LAB/state/.lock" "$LAB/state/.lock-session"
env -u CLAUDE_PID -u CLAUDE_CODE_SESSION_ID timeout 30 bin/fm-lock.sh; echo "exit=$?"; ls "$LAB/state/.lock" 2>&1
echo "== S6b CLAUDE_PID naming a non-harness live process (explorer/bash) stays read-only"
NONH=$(ps -W | awk '$NF ~ /explorer.exe$/ {print $4; exit}')
CLAUDE_PID=$NONH timeout 30 env -u CLAUDE_CODE_SESSION_ID bin/fm-lock.sh; echo "exit=$? (CLAUDE_PID=$NONH explorer)"; ls "$LAB/state/.lock" 2>&1
echo; echo "== S7 task lease records and honors tagged holder"
printf 'win:%s\n' "$SELF" > "$LAB/state/.lock"
FM_SUPERVISION_ACTOR=branch FM_LEASE_HOLDER_PID="win:$SELF" T bin/fm-lease.sh claim demo-task; echo "claim exit=$?"
cat "$LAB/state"/*lease* 2>/dev/null | head -3; ls "$LAB/state" | grep -i lease
FM_SUPERVISION_ACTOR=main T bin/fm-lease.sh claim demo-task; echo "main claim exit=$? (expect refusal while branch lease live)"
rm -rf "$LAB"; echo "== lab removed: $( [ -e "$LAB" ] && echo no || echo yes)"
