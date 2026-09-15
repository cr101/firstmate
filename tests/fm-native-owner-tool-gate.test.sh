#!/usr/bin/env bash
# Portable behavioral tests of the host-side notification request policy.
set -eu
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
node --test "$ROOT/tests/fixtures/native-owner/tool-gate.test.mjs"
