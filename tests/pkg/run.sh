#!/bin/bash
# Phase 7.4: forge pkg (tools/pkg.forge). Fixture registry tests/pkg/registry, project tests/pkg/app.
D="$(cd "$(dirname "$0")/../.." && pwd)"
F="$D/forge"; R="$D/tests/pkg/registry"
T=$(mktemp -d); trap 'rm -rf $T' EXIT
ok() { echo "PASS pkg/$1"; }; bad() { echo "FAIL pkg/$1"; fail=1; }
fail=0
cp -r "$D/tests/pkg/app" $T/a && cd $T/a || exit 1
# our SHA-256 agrees with sha256sum, including bytes >= 0x80 and an empty file
printf '\xc3\xa9t\xe9\n' > $T/h1; : > $T/h2; cp "$D/tools/pkg.forge" $T/h3
[ "$("$F" pkg sha256 $T/h1 $T/h2 $T/h3)" = "$(sha256sum $T/h1 $T/h2 $T/h3)" ] && ok sha256-matches-sha256sum || bad sha256-matches-sha256sum
# resolution: highest compatible versions (strutil 1.1.0 needs mathx ^1.1; mathx 2.0.0 excluded by ^1)
"$F" pkg --registry $R lock > $T/l1 2>&1
grep -q "^locked mathx 1.2.0$" $T/l1 && grep -q "^locked strutil 1.1.0$" $T/l1 && ok resolve-highest-compatible || bad resolve-highest-compatible
# the recorded package hash is SHA-256 over forge.toml + files as documented (independent check)
python3 - "$R/strutil/1.1.0" forge.lock <<'PY' && ok package-hash-independent || bad package-hash-independent
import hashlib, sys, re
d = sys.argv[1]
s = b""
for f in ["forge.toml", "strutil.forge"]:
    t = open(d + "/" + f, "rb").read()
    s += f.encode() + b"\0" + str(len(t)).encode() + b"\0" + t
lock = open(sys.argv[2]).read()
m = re.search(r'name = "strutil"\nversion = "1.1.0"\nsha256 = "([0-9a-f]{64})"', lock)
sys.exit(0 if m and m.group(1) == hashlib.sha256(s).hexdigest() else 1)
PY
cp forge.lock $T/lock1; "$F" pkg --registry $R lock > /dev/null 2>&1
cmp -s forge.lock $T/lock1 && ok lock-deterministic || bad lock-deterministic
# reproducible build: two clean builds, and a build of a copy in another directory, give the same hash
h1=$("$F" pkg --registry $R build 2>&1 | sed -n 's/.*sha256 //p'); out=$(./target/app)
rm -rf target; h2=$("$F" pkg --registry $R build 2>&1 | sed -n 's/.*sha256 //p')
cp -r $T/a $T/b; rm -rf $T/b/target; h3=$(cd $T/b && "$F" pkg --registry $R build 2>&1 | sed -n 's/.*sha256 //p')
[ -n "$h1" ] && [ "$h1" = "$h2" ] && [ "$h1" = "$h3" ] && ok "reproducible-build ($h1)" || bad "reproducible-build ($h1 $h2 $h3)"
[ "$out" = "$(printf '[hello pkg]\n[0, 1, 8, 27]\n49 1.2.0')" ] && ok program-runs || bad "program-runs ($out)"
# a tampered registry package is refused
cp -r $R $T/r; echo "# changed" >> $T/r/mathx/1.2.0/mathx.forge
"$F" pkg --registry $T/r build > $T/e 2>&1; [ $? != 0 ] && grep -q "hash mismatch for mathx 1.2.0" $T/e && ok tamper-detected || bad tamper-detected
# an edited lock is refused
sed 's/version = "1.2.0"/version = "1.0.0"/' $T/lock1 > forge.lock
"$F" pkg --registry $R build > $T/e 2>&1; [ $? != 0 ] && grep -q "hash mismatch\|forge.lock" $T/e && ok edited-lock-refused || bad edited-lock-refused
cp $T/lock1 forge.lock
# forge.toml changed without relocking
sed -i 's/mathx = "\^1"/mathx = "^1.2"/' forge.toml
"$F" pkg --registry $R build > $T/e 2>&1; [ $? != 0 ] && grep -q "out of date" $T/e && ok stale-lock-refused || bad stale-lock-refused
# an exact pin to 1.0.0 conflicts with strutil 1.1.0's mathx ^1.1 -> falls back to strutil 1.0.0
sed -i 's/mathx = "\^1.2"/mathx = "1.0.0"/' forge.toml
"$F" pkg --registry $R lock > $T/l2 2>&1 && grep -q "^locked strutil 1.0.0$" $T/l2 && grep -q "^locked mathx 1.0.0$" $T/l2 && ok "backtrack-to-compatible" || bad backtrack-to-compatible
# unsatisfiable requirement
sed -i 's/mathx = "1.0.0"/mathx = "^3"/' forge.toml
"$F" pkg --registry $R lock > $T/e 2>&1; [ $? != 0 ] && grep -q "no version of mathx satisfies" $T/e && ok unsatisfiable-reported || bad unsatisfiable-reported
# two packages providing the same module
cp "$D/tests/pkg/app/forge.toml" forge.toml; printf 'clash = "1.0.0"\n' >> forge.toml; sed -i 's/"greet.forge"/"greet.forge", "clash.forge"/' forge.toml; cp greet.forge clash.forge
"$F" pkg --registry $R lock > /dev/null 2>&1; "$F" pkg --registry $R build > $T/e 2>&1; [ $? != 0 ] && grep -q "module clash.forge is provided by both" $T/e && ok module-clash-refused || bad module-clash-refused
exit $fail
