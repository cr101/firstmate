#!/usr/bin/env bash
set -eu
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)
export FM_HOME
FM_HOME=$(cygpath -u "${2:?}")
export FM_STATE_OVERRIDE="$FM_HOME/state"
mkdir -p "$FM_HOME/state"
cd "$ROOT"
case "${1:-}" in
  present)
    : > "$FM_HOME/state/.wake-queue"
    . bin/fm-wake-lib.sh
    fm_recovery_marker_publish "$FM_HOME/state/.watcher-down" downtime
    error=$(mktemp "$FM_HOME/state/.zero-recovery.XXXXXX")
    trap 'rm -f -- "$error"' EXIT
    bin/fm-wake-drain.sh > /dev/null 2> "$error"
    ack=$(grep '^WAKE_ACK_REQUIRED:' "$error" | tail -1)
    seq=$(awk '{for(i=1;i<NF;i++)if($i=="--ack-through")print $(i+1)}' <<< "$ack")
    generation=$(awk '{for(i=1;i<NF;i++)if($i=="--recovery-generation")print $(i+1)}' <<< "$ack")
    [ "$seq" = 0 ]
    case "$generation" in ''|*[!A-Za-z0-9._-]*) exit 2 ;; esac
    printf '%s\t%s\n' "$seq" "$generation"
    ;;
  acknowledge)
    generation=${3:-}
    case "$generation" in ''|*[!A-Za-z0-9._-]*) exit 2 ;; esac
    bin/fm-wake-drain.sh --ack-through 0 --recovery-generation "$generation"
    ;;
  *) exit 2 ;;
esac
