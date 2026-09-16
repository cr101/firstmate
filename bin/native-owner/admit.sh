#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
export FM_HOME
FM_HOME=$(cygpath -u "${FM_HOME:?}")
cd "$ROOT"
. bin/fm-tasks-axi-lib.sh
. bin/fm-backlog-transition-lib.sh
if ! fm_backlog_empty_fleet_preflight "$FM_HOME/state" "$FM_HOME/data"; then
  printf '%s\n' "${FM_BACKLOG_EMPTY_ERROR:-the home contains work-bearing records}" >&2
  exit 2
fi
