#!/usr/bin/env bash
# Read actual work only. No synthetic note or scripted model prompt is generated.
set -euo pipefail
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
LOG=$(cygpath -u "${FM_PROBE_HOME:?}")
export FM_HOME
FM_HOME=$(cygpath -u "${FM_HOME:?}")
export PATH="$ROOT/bin/native-owner/tools:$PATH"
cd "$ROOT"
. bin/fm-session-lock-lib.sh
fm_session_lock_owned_by_self "$FM_HOME/state"
set +e
bin/fm-watch-checkpoint.sh --seconds 1 > "$LOG/checkpoint.log" 2>&1
rc=$?
set -e
case "$rc" in 0|124) ;; *) exit "$rc" ;; esac
bin/fm-wake-drain.sh > "$LOG/delivery.log" 2>&1
ack=$(grep 'WAKE_ACK_REQUIRED:' "$LOG/delivery.log" | tail -1 || true)
if [ -z "$ack" ]; then
  if grep -Eq 'OPEN DECISIONS|UNREAD STATUS|RECORD DIVERGENCE|STATUS OUTCOME BACKSTOP' "$LOG/checkpoint.log" "$LOG/delivery.log"; then
    printf 'Unqueued work requires reconciliation; captured in %s\n' "$LOG" >&2
    exit 2
  fi
  printf '{"quiet":true}\n' > "$LOG/notification-check.json"
  exit 0
fi
bin/fm-inbox.sh drain > "$LOG/inbox.log"
seq=$(awk '{for(i=1;i<NF;i++)if($i=="--ack-through")print $(i+1)}' <<< "$ack")
generation=$(awk '{for(i=1;i<NF;i++)if($i=="--recovery-generation")print $(i+1)}' <<< "$ack")
case "$seq" in ''|*[!0-9]*) exit 2 ;; esac
case "$generation" in ''|*[!A-Za-z0-9._-]*) exit 2 ;; esac
notes=$(awk -F '\t' -v cutoff="$seq" '$2<=cutoff && $4 ~ /^inbox:/ {print substr($4,7)}' "$FM_HOME/state/.wake-queue" | sort -u | jq -Rsc 'split("\n")|map(select(length>0))')
challenge=$(od -An -N12 -tx1 /dev/urandom | tr -d ' \n')
message="$(<"$LOG/checkpoint.log")
$(<"$LOG/delivery.log")
$(<"$LOG/inbox.log")"
jq -n --arg message "$message" --arg challenge "$challenge" --arg seq "$seq" --arg generation "$generation" --argjson notes "$notes" \
  '{message:$message,challenge:$challenge,seq:$seq,generation:$generation,notes:$notes}' > "$LOG/notification-check.json"
