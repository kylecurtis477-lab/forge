#!/bin/bash
# forge fmt CI: golden rules, --help / bad option / missing file are clean errors (no segfault), repo is fmt-clean, fmt is idempotent and AST-preserving
# (ast-json identical incl. line numbers on a formatted copy of every source).
cd "$(dirname "$0")/../.." || exit 1
FMT=build/fmt; FC=build/forgec; T=$(mktemp -d); fail=0
./forge tools/fmt.forge -o $FMT >/dev/null || { echo "FAIL fmt-build"; exit 1; }
cp tests/fmt/messy.forge.in $T/m.forge; $FMT $T/m.forge >/dev/null
cmp -s $T/m.forge tests/fmt/messy.forge.expected && echo "PASS fmt-golden" || { echo "FAIL fmt-golden"; fail=1; }
$FMT --check $T/m.forge >/dev/null && echo "PASS fmt-idempotent-golden" || { echo "FAIL fmt-idempotent-golden"; fail=1; }
cp tests/fmt/messy.forge.in $T/n.forge; $FMT --check $T/n.forge >/dev/null && { echo "FAIL fmt-check-detects"; fail=1; } || echo "PASS fmt-check-detects"
FILES=$($FMT --list)
[ -n "$FILES" ] && [ "$FILES" = "$(tools/fmt_files.sh)" ] && echo "PASS fmt-list-matches-sh" || { echo "FAIL fmt-list-matches-sh"; fail=1; }
$FMT --check $FILES && echo "PASS fmt-repo-clean" || { echo "FAIL fmt-repo-clean"; fail=1; }
bad=0
for f in $FILES; do mkdir -p $T/c/$(dirname $f); cp $f $T/c/$f; done
(cd $T/c && $OLDPWD/$FMT $FILES >/dev/null)
for f in $FILES; do
  $FC $f $T/a.json --emit=ast-json >/dev/null 2>&1 || continue
  $FC $T/c/$f $T/b.json --emit=ast-json >/dev/null 2>&1 || { echo "  broke $f"; bad=1; continue; }
  sed 's/"file":"[^"]*",//' $T/a.json > $T/a2; sed 's/"file":"[^"]*",//' $T/b.json > $T/b2
  cmp -s $T/a2 $T/b2 || { echo "  astdiff $f"; bad=1; }
done
[ $bad = 0 ] && echo "PASS fmt-ast-preserving" || { echo "FAIL fmt-ast-preserving"; fail=1; }
out=$($FMT --help); rc=$?
[ $rc = 0 ] && echo "$out" | grep -q '^usage: fmt' && echo "PASS fmt-help" || { echo "FAIL fmt-help (rc=$rc)"; fail=1; }
out=$($FMT --bogus 2>&1); rc=$?
[ $rc = 2 ] && echo "$out" | grep -q '^fmt: unknown option --bogus' && echo "PASS fmt-bad-option" || { echo "FAIL fmt-bad-option (rc=$rc)"; fail=1; }
out=$($FMT --check $T/missing.forge 2>&1); rc=$?
[ $rc = 2 ] && [ "$out" = "fmt: no such file: $T/missing.forge" ] && echo "PASS fmt-missing-file" || { echo "FAIL fmt-missing-file (rc=$rc)"; fail=1; }
rm -rf $T; exit $fail
