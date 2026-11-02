#!/bin/bash
# forge test [files...] : build each file with `forgec --test`, run every `test "name":` block in
# its own process (10 s timeout) and print one JSON report on stdout. Exit 1 if any test fails or a
# file does not build. Default file set: tests/unit/*.forge.
D="$(cd "$(dirname "$0")/.." && pwd)"
[ $# = 0 ] && set -- "$D"/tests/unit/*.forge
T=$(mktemp -d); trap 'rm -rf $T' EXIT
esc() { sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' -e 's/\t/\\t/g' | tr -d '\r\n'; }
np=0; nf=0; first=1
printf '{"forge_test":1,"files":['
for f in "$@"; do
  [ $first = 1 ] || printf ','; first=0
  fe=$(printf '%s' "$f" | esc)
  if ! "$D/build/forgec" "$f" $T/t.s --test $FORGEC_FLAGS 2>$T/err || ! "$D/build/fas" -o $T/t.o $T/t.s 2>>$T/err || ! "$D/build/fld" --base 0x100000000000 -o $T/t $T/t.o 2>>$T/err; then
    nf=$((nf+1)); printf '{"file":"%s","build":"fail","message":"%s","tests":[]}' "$fe" "$(head -1 $T/err | esc)"; continue
  fi
  # the program's own top-level output comes first, then the marker line, then one name per line
  (cd "$(dirname "$f")" && timeout 10 $T/t --forge-test-list) 2>/dev/null | sed -n '/^--forge-tests--$/,$p' | tail -n +2 > $T/names
  n=$(wc -l < $T/names)
  printf '{"file":"%s","build":"ok","tests":[' "$fe"
  i=0; tf=1
  while [ $i -lt $n ]; do
    name=$(sed -n "$((i+1))p" $T/names | esc)
    s=$(date +%s%N)
    (cd "$(dirname "$f")" && timeout 10 $T/t --forge-test-run $i >/dev/null 2>$T/e); rc=$?
    ms=$(( ($(date +%s%N) - s) / 1000000 ))
    [ $tf = 1 ] || printf ','; tf=0
    if [ $rc = 0 ]; then np=$((np+1)); printf '{"name":"%s","status":"pass","ms":%d}' "$name" $ms
    else nf=$((nf+1)); m=$(head -1 $T/e | esc); [ $rc = 124 ] && m="timeout"; printf '{"name":"%s","status":"fail","exit":%d,"message":"%s","ms":%d}' "$name" $rc "$m" $ms; fi
    i=$((i+1))
  done
  printf ']}'
done
printf '],"passed":%d,"failed":%d}\n' $np $nf
[ $nf = 0 ]
