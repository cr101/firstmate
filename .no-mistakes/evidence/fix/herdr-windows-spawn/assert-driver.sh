#!/usr/bin/env bash
# Run fm-spawn.sh's own enter+assert functions (extracted verbatim) against a lab Herdr pane.
WT_ROOT=$1; WT_TARGET=$2; WT=$3
export FM_HOME=$(mktemp -d /tmp/fm-asserthome.XXXX)
. "$WT_ROOT/bin/backends/herdr.sh"
for f in real_path_or_raw spawn_current_path spawn_send_text_line shell_quote spawn_enter_recorded_worktree spawn_assert_agent_worktree; do
  eval "$(awk -v n="$f() " 'index($0, n)==1 && $0 ~ /{ *(#.*)?$/ {p=1} p {print} p && /^}/ {exit}' "$WT_ROOT/bin/fm-spawn.sh")"
done
KIND=ship BACKEND=herdr ID=lab-task
trap 'rm -rf "$FM_HOME"' EXIT
start=$(date +%s)
spawn_enter_recorded_worktree
( spawn_assert_agent_worktree ); rc=$?
echo "spawn_assert_agent_worktree rc=$rc elapsed=$(( $(date +%s) - start ))s"
