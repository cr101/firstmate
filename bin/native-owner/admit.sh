#!/usr/bin/env bash
# Usage: FM_HOME=<home> admit.sh
# Required environment: FM_HOME.
set -euo pipefail
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
export FM_HOME
FM_HOME=$(cygpath -u "${FM_HOME:?}")
cd "$ROOT"
. bin/fm-tasks-axi-lib.sh
. bin/fm-backlog-transition-lib.sh
. bin/fm-supervision-lib.sh
if ! fm_backlog_empty_fleet_preflight "$FM_HOME/state" "$FM_HOME/data"; then
  printf '%s\n' "${FM_BACKLOG_EMPTY_ERROR:-the home contains work-bearing records}" >&2
  exit 2
fi
if ! fm_supervision_residual_inputs_absent "$FM_HOME/state"; then
  printf '%s\n' "${FM_SUP_RESIDUAL_ERROR:-the home contains residual supervision work}" >&2
  exit 2
fi
if [ -e "$FM_HOME/state" ] || [ -L "$FM_HOME/state" ]; then
  export FM_STATE_OVERRIDE="$FM_HOME/state"
  . bin/fm-wake-lib.sh
  if ! fm_wake_native_empty_fleet_preflight "$FM_HOME/state"; then
    printf '%s\n' "${FM_WAKE_NATIVE_ADMISSION_ERROR:-the home contains unsupported wake state}" >&2
    exit 2
  fi
fi
