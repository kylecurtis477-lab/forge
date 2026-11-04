#!/bin/sh
# Reference for `fmt --list` (tools/fmt.forge, which tests/fmt/run.sh checks against this). Sources kept
# canonically formatted by `forge fmt`. Excluded: the
# generated selfhost/forgec.forge and the intentionally malformed / column-sensitive
# error fixtures (tests/diag, tests/errors, tests/typeerrors, tests/grammar/invalid).
cd "$(dirname "$0")/.."
find selfhost tools tests examples -name '*.forge' -o -name '*.fsi' | grep -v -e '^selfhost/forgec.forge$' -e '^tests/diag/' -e '^tests/errors/' -e '^tests/typeerrors/' -e '^tests/grammar/invalid/' -e '^tests/fmt/' -e '/\.pp_' | LC_ALL=C sort
