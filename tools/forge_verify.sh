#!/bin/bash
# forge verify [files...] : one gate over the checkers that exist today, per file:
#   parse  - strict frozen-grammar parser (forgec --emit=ast-json)
#   fmt    - canonical formatting (forge fmt --check)
#   build  - full compile: typecheck + codegen + as + ld (forgec)
#   test   - every `test "name":` block passes (forge test), if the file has any
#   unsafe - with --unsafe-report: freestanding files get the unsafe-region summary (functions, blocks, ops, asm)
# Prints one JSON report and exits 1 if any check fails. Default set: tests/unit/*.forge.
D="$(cd "$(dirname "$0")/.." && pwd)"
UR=0; [ "$1" = --unsafe-report ] && { UR=1; shift; }
[ $# = 0 ] && set -- "$D"/tests/unit/*.forge
T=$(mktemp -d); trap 'rm -rf $T' EXIT
esc() { sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' -e 's/\t/\\t/g' | tr -d '\r\n'; }
[ "$D/build/fmt" -nt "$D/tools/fmt.forge" ] || "$D/forge" "$D/tools/fmt.forge" -o "$D/build/fmt" >/dev/null
bad=0; first=1
printf '{"forge_verify":1,"files":['
for f in "$@"; do
  [ $first = 1 ] || printf ','; first=0
  printf '{"file":"%s","checks":{' "$(printf '%s' "$f" | esc)"
  ck() { # name status [message]
    [ "$2" = ok ] || [ "$2" = skip ] || bad=1
    [ "$cf" = 1 ] || printf ','; cf=0
    if [ -n "$3" ]; then printf '"%s":{"status":"%s","message":"%s"}' "$1" "$2" "$(printf '%s' "$3" | esc)"
    else printf '"%s":{"status":"%s"}' "$1" "$2"; fi
  }
  cf=1
  if "$D/build/forgec" "$f" $T/a.json --emit=ast-json 2>$T/e >/dev/null; then ck parse ok; else ck parse fail "$(head -1 $T/e)"; fi
  if "$D/build/fmt" --check "$f" >$T/e 2>&1; then ck fmt ok; else ck fmt fail "$(head -1 $T/e)"; fi
  if "$D/build/forgec" "$f" $T/b.s $FORGEC_FLAGS 2>$T/e >/dev/null && as -o $T/b.o $T/b.s 2>>$T/e && ld -Ttext-segment=0x100000000000 -o $T/b $T/b.o 2>>$T/e; then ck build ok; else ck build fail "$(head -1 $T/e)"; fi
  if grep -q '^test "' "$f"; then
    "$D/tools/forge_test.sh" "$f" > $T/t.json
    if [ $? = 0 ]; then ck test ok; else ck test fail "$(python3 -c 'import json,sys;r=json.load(open(sys.argv[1]));print(str(r["failed"])+" failing: "+", ".join(t["name"] for f in r["files"] for t in f["tests"] if t["status"]!="pass") or "build failed")' $T/t.json)"; fi
  else ck test skip; fi
  if [ $UR = 1 ]; then
    u=$(cd "$(dirname "$f")" && "$D/build/forgec" "$(basename "$f")" $T/u.s --unsafe-report 2>&1 | grep '^unsafe: [0-9]* functions')
    if [ -n "$u" ]; then ck unsafe ok "${u#unsafe: }"; else ck unsafe skip "hosted or did not build"; fi
  fi
  printf '}}'
done
printf '],"ok":%s}\n' $([ $bad = 0 ] && echo true || echo false)
exit $bad
