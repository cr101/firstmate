#!/usr/bin/env bash
# Live drive of bin/fm-guard.sh (extension supervision model) on real Git Bash: a Pi
# extension-owned hand-off whose state/.lock holds a tagged win:<pid>. Usage: <root> <label>
ROOT=$1; LABEL=$2
LAB=$(mktemp -d "${TMPDIR:-/tmp}/fm-lab.XXXXXX")
DEAD=99991; while ps -W | awk '{print $4}' | grep -qx "$DEAD"; do DEAD=$((DEAD+2)); done
echo "== $LABEL  uname=$(uname -s) live harness=win:$CLAUDE_PID ($(ps -W | awk -v w=$CLAUDE_PID '$4==w{print $NF}')) dead=win:$DEAD"
for spec in "live-tagged|win:$CLAUDE_PID" "dead-tagged|win:$DEAD" "malformed-tagged|win:${CLAUDE_PID}x"; do
  name=${spec%%|*}; lock=${spec#*|}
  home=$LAB/$name/home; root=$LAB/$name/root
  mkdir -p "$home/state" "$home/config" "$root/.pi/extensions"
  printf 'window=firstmate:fm-task\nkind=ship\n' > "$home/state/task.meta"
  cp "$ROOT/.pi/extensions/fm-primary-pi-watch.ts" "$ROOT/.pi/extensions/fm-primary-turnend-guard.ts" "$root/.pi/extensions/"
  for f in fm-primary-pi-watch.ts fm-primary-turnend-guard.ts; do
    v="sha256:$(sha256sum "$root/.pi/extensions/$f" | awk '{print $1}')"
    case $f in
      *watch*) printf '%s\n%s\ngeneration=1 phase=active\n' "$v" "$lock" > "$home/state/.pi-watch-extension-loaded" ;;
      *) printf '%s\n%s\n' "$v" "$lock" > "$home/state/.pi-turnend-extension-loaded" ;;
    esac
  done
  printf '%s\n' "$lock" > "$home/state/.lock"; touch "$home/state/.last-watcher-beat"
  out=$(env -u NO_MISTAKES_GATE -u PI_CODING_AGENT FM_ROOT_OVERRIDE="$root" FM_HOME="$home" FM_GUARD_GRACE=999 \
        FM_SUPERVISION_MODEL=extension "$ROOT/bin/fm-guard.sh" 2>&1)
  printf -- '-- %-16s lock=%-12s guard output: %s\n' "$name" "$lock" "${out:-<silent: supervision owned by the live session>}" | head -3
done
rm -rf "$LAB"
