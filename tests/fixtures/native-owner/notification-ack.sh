#!/usr/bin/env bash
# Only controller-validated receipt data is accepted; no command text is parsed.
set -eu
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
LOG=$(cygpath -u "${FM_PROBE_HOME:?}")
export FM_HOME
FM_HOME=$(cygpath -u "${FM_HOME:?}")
BUILD=$(cd "$ROOT/.." && pwd)
export PATH="$BUILD/tools:$PATH"
cd "$ROOT"
. bin/fm-session-lock-lib.sh
fm_session_lock_owned_by_self "$FM_HOME/state"
request="$LOG/notification-ack-request.json"
note=$(jq -r .note "$request")
seq=$(jq -r .seq "$request")
generation=$(jq -r .generation "$request")
case "$note" in ''|*[!A-Za-z0-9._-]*) exit 2 ;; esac
case "$seq" in ''|*[!0-9]*) exit 2 ;; esac
case "$generation" in ''|*[!A-Za-z0-9._-]*) exit 2 ;; esac
bin/fm-inbox.sh drain --ack "$note" > "$LOG/cycle-inbox-ack.log"
bin/fm-wake-drain.sh --ack-through "$seq" --recovery-generation "$generation" > "$LOG/cycle-ack.log" 2>&1
[ ! -s "$FM_HOME/state/.wake-queue" ]
[ -f "$FM_HOME/state/inbox/handled/$note.note" ]
printf '{"acknowledged":true,"queueEmpty":true}\n' > "$LOG/notification-ack.json"
