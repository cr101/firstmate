#!/usr/bin/env bash
# Mutate only the controller's captured receipt targets, using existing owners.
set -euo pipefail
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
LOG=$(cygpath -u "${FM_PROBE_HOME:?}")
export FM_HOME
FM_HOME=$(cygpath -u "${FM_HOME:?}")
export PATH="$ROOT/bin/native-owner/tools:$PATH"
cd "$ROOT"
. bin/fm-session-lock-lib.sh
fm_session_lock_owned_by_self "$FM_HOME/state"
request="$LOG/notification-ack-request.json"
seq=$(jq -r .seq "$request")
generation=$(jq -r .generation "$request")
case "$seq" in ''|*[!0-9]*) exit 2 ;; esac
case "$generation" in ''|*[!A-Za-z0-9._-]*) exit 2 ;; esac
notes=$(jq -r '.notes[]' "$request")
if [ -n "$notes" ]; then
  while IFS= read -r note; do
    case "$note" in ''|*[!A-Za-z0-9_-]*) exit 2 ;; esac
    bin/fm-inbox.sh drain --ack "$note"
  done <<< "$notes"
fi
bin/fm-wake-drain.sh --ack-through "$seq" --recovery-generation "$generation"
printf '{"acknowledged":true}\n' > "$LOG/notification-ack.json"
