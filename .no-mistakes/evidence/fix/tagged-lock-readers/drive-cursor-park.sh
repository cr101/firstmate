#!/usr/bin/env bash
# Live drive of bin/fm-turnend-guard-cursor.sh on real Git Bash (real uname, real ps -W,
# real CLAUDE_PID of this Claude session). Usage: <root> <label>
ROOT=$1; LABEL=$2
LAB=$(mktemp -d "${TMPDIR:-/tmp}/fm-lab.XXXXXX")
PAYLOAD='{"session_id":"sess-cursor","generation_id":"gen-1","loop_count":0,"status":"completed","hook_event_name":"stop","cursor_version":"2026.08.11-e8db854"}'
mk() {
  local d=$LAB/$1
  mkdir -p "$d/state" "$d/bin" "$d/docs"; git init -q "$d"; git -C "$d" commit -q --allow-empty -m init; : > "$d/AGENTS.md"
  cp "$ROOT"/bin/*.sh "$ROOT"/bin/*.mjs "$d/bin/" 2>/dev/null
  cp -R "$ROOT/docs/supervision-protocols" "$d/docs/"
  cat > "$d/bin/fm-watch-arm.sh" <<'SH'
#!/usr/bin/env bash
printf '%s\n' "$$" >> "$FM_HOME/state/arm-ran"
printf 'watcher: started pid=%s (beacon fresh)\n' "$$"
printf 'stale: fixture-win needs a look\n'
SH
  chmod +x "$d"/bin/*.sh; : > "$d/state/task1.meta"; printf '%s\n' "$2" > "$d/state/.lock"; echo "$d"
}
DEAD=99991; while ps -W | awk '{print $4}' | grep -qx "$DEAD"; do DEAD=$((DEAD+2)); done
OTHER=$(ps -W | awk -v me="$CLAUDE_PID" 'tolower($0) ~ /claude\.exe/ && $4 != me {print $4; exit}')
echo "== $LABEL  uname=$(uname -s) CLAUDE_PID=$CLAUDE_PID dead=win:$DEAD live-other=win:$OTHER"
for spec in "own|win:$CLAUDE_PID" "dead-holder|win:$DEAD" "live-other|win:$OTHER" "malformed|win:${CLAUDE_PID}x"; do
  name=${spec%%|*}; lock=${spec#*|}
  d=$(mk "$name" "$lock")
  out=$(printf '%s' "$PAYLOAD" | env -u PI_CODING_AGENT -u NO_MISTAKES_GATE -u FM_GATE_REFUSE_BYPASS MSYS=winsymlinks:nativestrict FM_HOME="$d" FM_CURSOR_PARK_POLL=1 timeout 300 bash "$d/bin/fm-turnend-guard-cursor.sh" 2>/dev/null); rc=$?
  printf -- '-- %-11s lock before=%-12s after=%-12s armed=%s rc=%s\n   output=%s\n' "$name" "$lock" "$(cat "$d/state/.lock")" "$([ -e "$d/state/arm-ran" ] && echo yes || echo no)" "$rc" "${out:-<none>}"
done
rm -rf "$LAB"
