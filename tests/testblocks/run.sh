#!/bin/bash
# test blocks + forge test + forge verify
cd "$(dirname "$0")/../.." || exit 1
T=$(mktemp -d); fail=0
ok() { echo "PASS $1"; }; no() { echo "FAIL $1"; fail=1; }
./forge test > $T/u.json && python3 -c 'import json,sys;r=json.load(open(sys.argv[1]));assert r["forge_test"]==1 and r["failed"]==0 and r["passed"]==7' $T/u.json && ok testblocks-unit-json || no testblocks-unit-json
./forge test tests/testblocks/failing.forge > $T/f.json && no testblocks-fail-detected || python3 -c '
import json,sys;r=json.load(open(sys.argv[1]));t=r["files"][0]["tests"]
assert r["passed"]==1 and r["failed"]==1 and t[1]["name"]=="fails \"quoted\"" and t[1]["status"]=="fail" and "line 6" in t[1]["message"]' $T/f.json && ok testblocks-fail-detected || no testblocks-fail-detected
# a normal build ignores test blocks (they become uncalled functions) and keeps line numbers
printf 'test "t":\n    assert 0 == 1\nx = 1\nprint(x)\nassert x == 2\n' > $T/n.forge
./forge $T/n.forge -o $T/n >/dev/null && $T/n > $T/o 2>$T/e; [ "$(cat $T/o)" = 1 ] && grep -q 'line 5' $T/e && ok testblocks-normal-build || no testblocks-normal-build
build/forgec tests/unit/strings.forge $T/a.json --emit=ast-json && python3 -c '
import json,sys;r=json.load(open(sys.argv[1]));ts=[n for n in r["root"]["c"] if n["k"]=="Test"]
assert [n["v"] for n in ts]==["upper and concat","split and join","imported module test blocks are ignored"]' $T/a.json && ok testblocks-ast || no testblocks-ast
build/forgec tests/unit/strings.forge $T/p.forge --emit=pretty && grep -q '^test "split and join":$' $T/p.forge && ok testblocks-pretty || no testblocks-pretty
./forge verify > $T/v.json && python3 -c 'import json,sys;assert json.load(open(sys.argv[1]))["ok"] is True' $T/v.json && ok verify-ok || no verify-ok
printf 'x  =  1\nprint(x)\n' > $T/ugly.forge
./forge verify $T/ugly.forge > $T/v2.json && no verify-catches-fmt || python3 -c 'import json,sys;c=json.load(open(sys.argv[1]))["files"][0]["checks"];assert c["fmt"]["status"]=="fail" and c["build"]["status"]=="ok" and c["test"]["status"]=="skip"' $T/v2.json && ok verify-catches-fmt || no verify-catches-fmt
printf 'def f(:\n' > $T/bad.forge
./forge verify $T/bad.forge > $T/v3.json && no verify-catches-parse || python3 -c 'import json,sys;c=json.load(open(sys.argv[1]))["files"][0]["checks"];assert c["parse"]["status"]=="fail" and c["build"]["status"]=="fail"' $T/v3.json && ok verify-catches-parse || no verify-catches-parse
rm -rf $T; exit $fail
