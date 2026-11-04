#!/bin/bash
# Phase 7.5: scripted LSP sessions against `forge lsp` (tools/lsp.forge), driven by tests/lsp/client.forge.
# FORGE_LSP_PY=1 also runs tests/lsp/client.py (the reference) and requires the same output.
D="$(cd "$(dirname "$0")/../.." && pwd)"
rm -f "$D/build/lsp"   # rebuild with the current compiler and flags
"$D/forge" "$D/tests/lsp/client.forge" -o "$D/build/lsp_client" >/dev/null || { echo "FAIL lsp/client-build"; exit 1; }
timeout 300 "$D/build/lsp_client" "$D" > "$D/build/lsp_client.out"; rc=$?
cat "$D/build/lsp_client.out"
[ $rc = 0 ] || { [ $rc = 124 ] && echo "FAIL lsp/session-timeout"; exit 1; }
if [ "$FORGE_LSP_PY" = 1 ]; then
  python3 "$D/tests/lsp/client.py" | cmp -s - "$D/build/lsp_client.out" && echo "PASS lsp/matches-client-py" || { echo "FAIL lsp/matches-client-py"; exit 1; }
fi
