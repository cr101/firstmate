#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
export FM_HOME
FM_HOME=$(cygpath -u "${FM_HOME:?}")
export FM_STATE_OVERRIDE="$FM_HOME/state"
cd "$ROOT"
. bin/fm-wake-lib.sh

read_token() {
  local token
  token=$(cat)
  [ -n "$token" ] || exit 2
  printf '%s' "$token"
}

legacy_token() {
  node -e '
    let input="";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data",chunk=>input+=chunk);
    process.stdin.on("end",()=>{
      const value=JSON.parse(input);
      if(!value||typeof value!=="object"||Array.isArray(value))throw Error("legacy evidence must be an object");
      const version=Number(value.version);
      if(![1,2,3].includes(version))throw Error("unsupported legacy evidence");
      if(typeof value.cutoff!=="string")throw Error("invalid legacy cutoff");
      const cutoff=String(value.cutoff);
      const rows=value.rows;
      if(!Array.isArray(rows)||rows.some(row=>typeof row!=="string"))throw Error("invalid legacy rows");
      let notes=[];
      if(version===1)notes=[value];
      else notes=value.notes;
      if(!Array.isArray(notes)||notes.some(note=>!note||typeof note!=="object"||Array.isArray(note)))throw Error("invalid legacy notes");
      if(version===3&&(cutoff!=="0"||rows.length!==0||notes.length!==0))throw Error("invalid legacy recovery target");
      const generation=version===3?value.recoveryGeneration:"legacy";
      const marker=version===3?value.recoveryMarker:"";
      if(typeof generation!=="string"||typeof marker!=="string")throw Error("invalid legacy recovery evidence");
      const line=["fm-wake-ack-evidence-legacy-v1",`cutoff\t${cutoff}`,`generation\t${generation}`,`marker\t${Buffer.from(marker).toString("base64")}`,`rows\t${rows.length}`];
      for(const row of rows)line.push(`row\t${Buffer.from(row).toString("base64")}`);
      line.push(`notes\t${notes.length}`);
      for(const note of notes){
        if(typeof note.note!=="string"||typeof note.noteSha256!=="string")throw Error("invalid legacy note");
        line.push(`note\t${note.note}\t${note.noteSha256}`);
      }
      process.stdout.write("legacy."+Buffer.from(line.join("\n")+"\n").toString("base64"));
    });
  '
}

case "${1:-}" in
  capture-json)
    fm_wake_ack_evidence_capture
    printf '{"seq":"%s","generation":"%s","notes":[' \
      "$FM_WAKE_ACK_EVIDENCE_CUTOFF" "$FM_WAKE_ACK_EVIDENCE_GENERATION"
    separator=
    while IFS=$'\t' read -r id digest; do
      [ -n "$id" ] || continue
      printf '%s"%s"' "$separator" "$id"
      separator=,
    done < "$FM_WAKE_ACK_EVIDENCE_NOTES"
    printf '],"ownerEvidence":"%s"}\n' "$FM_WAKE_ACK_EVIDENCE_TOKEN"
    fm_wake_ack_evidence_clear
    ;;
  capture-token)
    fm_wake_ack_evidence_capture
    printf '%s\n' "$FM_WAKE_ACK_EVIDENCE_TOKEN"
    fm_wake_ack_evidence_clear
    ;;
  capture-payload)
    mapfile -t expected < <(node -e '
      let input="";
      process.stdin.setEncoding("utf8");
      process.stdin.on("data",chunk=>input+=chunk);
      process.stdin.on("end",()=>{
        const value=JSON.parse(input);
        if(!value||typeof value!=="object"||Array.isArray(value))throw Error("invalid payload");
        const seq=String(value.seq),generation=value.generation;
        const notes=value.notes===undefined?[value.note]:value.notes;
        if(!/^\d+$/.test(seq)||typeof generation!=="string"||!Array.isArray(notes)||notes.some(note=>typeof note!=="string"))throw Error("invalid payload target");
        process.stdout.write([seq,generation,...new Set(notes)].join("\n")+"\n");
      });
    ')
    [ "${#expected[@]}" -ge 2 ] || exit 2
    fm_wake_ack_evidence_capture
    [ "${expected[0]}" = "$FM_WAKE_ACK_EVIDENCE_CUTOFF" ] \
      && [ "${expected[1]}" = "$FM_WAKE_ACK_EVIDENCE_GENERATION" ] || exit 2
    if [ "${#expected[@]}" -gt 2 ]; then
      printf '%s\n' "${expected[@]:2}" | LC_ALL=C sort -u > "$FM_WAKE_ACK_EVIDENCE_NOTES.expected"
    else
      : > "$FM_WAKE_ACK_EVIDENCE_NOTES.expected"
    fi
    cut -f1 "$FM_WAKE_ACK_EVIDENCE_NOTES" | LC_ALL=C sort -u > "$FM_WAKE_ACK_EVIDENCE_NOTES.actual"
    cmp -s "$FM_WAKE_ACK_EVIDENCE_NOTES.expected" "$FM_WAKE_ACK_EVIDENCE_NOTES.actual" || exit 2
    rm -f -- "$FM_WAKE_ACK_EVIDENCE_NOTES.expected" "$FM_WAKE_ACK_EVIDENCE_NOTES.actual"
    printf '%s\n' "$FM_WAKE_ACK_EVIDENCE_TOKEN"
    fm_wake_ack_evidence_clear
    ;;
  preflight-token)
    token=$(read_token)
    fm_wake_ack_evidence_precondition "$token"
    fm_wake_ack_evidence_clear
    ;;
  verify-token)
    token=$(read_token)
    fm_wake_ack_evidence_completed "$token"
    ;;
  verify-legacy)
    token=$(legacy_token)
    fm_wake_ack_evidence_completed "$token"
    ;;
  acknowledge-token)
    token=$(read_token)
    fm_wake_ack_evidence_acknowledge "$token"
    ;;
  *)
    printf 'usage: ack-evidence.sh capture-json|capture-token|capture-payload|preflight-token|verify-token|verify-legacy|acknowledge-token\n' >&2
    exit 2
    ;;
esac
