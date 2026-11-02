#!/bin/bash
# Phase 7.5: scripted LSP sessions against `forge lsp` (tools/lsp.forge).
D="$(cd "$(dirname "$0")/../.." && pwd)"
rm -f "$D/build/lsp"   # rebuild with the current compiler and flags
python3 "$D/tests/lsp/client.py"
