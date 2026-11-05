#!/bin/sh
# tests/c2forge: the C-to-Forge translator (tools/c2forge.forge, plan item 11.4).
# Step 1: the lexer's token dump of lex.c must match lex.expected; a bad character is a clean error (exit 1).
# Step 2+: each program in PROGS is translated to @unsafe freestanding Forge, built at -O2 and -O0, and its
# output must equal the gcc build of the same C (with shim.c providing put_i64/put_u64/nl via printf).
cd "$(dirname "$0")/../.." || exit 1
T=$(mktemp -d); trap 'rm -rf $T' EXIT
fail=0
PROGS="ints flow"
build/forgec tools/c2forge.forge $T/c2.s && build/fas -o $T/c2.o $T/c2.s && build/fld --base 0x100000000000 -o $T/c2forge $T/c2.o || { echo "FAIL c2forge build"; exit 1; }
$T/c2forge --tokens tests/c2forge/lex.c | cmp -s - tests/c2forge/lex.expected && echo "PASS c2forge lex" || { echo "FAIL c2forge lex"; fail=1; }
printf 'int x = @;\n' > $T/bad.c
out=$($T/c2forge --tokens $T/bad.c); rc=$?
[ $rc = 1 ] && [ "$out" = "c2forge: line 1: unexpected character '@'" ] && echo "PASS c2forge lex-error" || { echo "FAIL c2forge lex-error (rc=$rc)"; fail=1; }
cp tests/fs/linux.fsi $T/linux.fsi
for p in $PROGS; do
  ok=1
  $T/c2forge tests/c2forge/$p.c $T/$p.forge || ok=0
  gcc -O1 -o $T/$p.ref tests/c2forge/$p.c tests/c2forge/shim.c 2>/dev/null && $T/$p.ref > $T/$p.want || ok=0
  for o in -O2 -O0; do
    [ $ok = 1 ] && build/forgec $T/$p.forge $T/$p.s $o && build/fas -o $T/$p.o $T/$p.s && build/fld -o $T/$p $T/$p.o && $T/$p > $T/$p.got && cmp -s $T/$p.got $T/$p.want || ok=0
  done
  [ $ok = 1 ] && echo "PASS c2forge $p (O2 and O0 output == gcc)" || { echo "FAIL c2forge $p"; fail=1; }
done
printf 'int main(void) { int x = 0; x = x++ + 1; return 0; }\n' > $T/inc.c
out=$($T/c2forge $T/inc.c $T/inc.forge); rc=$?
[ $rc = 1 ] && [ "$out" = "c2forge: line 1: ++/-- inside an expression is not supported yet (at '++')" ] && echo "PASS c2forge expr-incdec-error" || { echo "FAIL c2forge expr-incdec-error (rc=$rc)"; fail=1; }
exit $fail
