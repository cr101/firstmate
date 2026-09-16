#!/usr/bin/env bash
# Token-free real app-server effective-catalog guard for the native host policy.
set -eu
# shellcheck source=tests/lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
fm_live_gate default-on FM_LIVE_NATIVE_APP_POLICY node codex
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) ;; *) printf 'Native app-server policy tests require Windows\n' >&2; exit 1 ;; esac
node "$ROOT/tests/fixtures/native-owner/AppServerPolicy.mjs"
