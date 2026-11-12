#!/bin/sh
# tests/c2forge: the C-to-Forge translator (tools/c2forge.forge, plan item 11.4).
# Step 1: the lexer's token dump of lex.c must match lex.expected; a bad character is a clean error (exit 1).
# Step 2+: each program in PROGS is translated to @unsafe freestanding Forge, built at -O2 and -O0, and its
# output must equal the gcc build of the same C (with shim.c providing put_i64/put_u64/nl via printf).
# Step 4: ptrs.c (pointers, arrays, structs); ptr-int-error: `int *p = 5;` is rejected (no implicit int->pointer).
# Step 5: floats.c (float/double, conversions, casts, void *); union-error and float-mod-error are clean errors.
# Step 6: pp.c (object-like #define/#undef, standard headers); fnmacro-error, if-error, header-error and
# typedef-without-header are clean errors.
# Step 7: lib.c, a first small real C library (zlib-style CRC-32 and Adler-32, the XTEA reference cipher),
# translated and run against the gcc build, including the standard check values for "123456789".
# 0.15.1: remaining subset gaps are clean errors (globals, string literals as values, ++/-- in
# expressions, static locals, same-name/same-type inner-block shadowing); array sizes may be
# integer constant expressions (arrconst.c). Listed in docs/BUILD_PLAN.md item 4.
cd "$(dirname "$0")/../.." || exit 1
T=$(mktemp -d); trap 'rm -rf $T' EXIT
fail=0
PROGS="ints flow ptrs floats pp lib arrconst"
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
printf 'int g = 1; int main(void) { return g; }\n' > $T/gv.c
out=$($T/c2forge $T/gv.c $T/gv.forge); rc=$?
[ $rc = 1 ] && [ "$out" = "c2forge: line 1: global variables are not supported yet (at '=')" ] && echo "PASS c2forge global-error" || { echo "FAIL c2forge global-error (rc=$rc out=$out)"; fail=1; }
printf 'int main(void) { const char *s = "hi"; return 0; }\n' > $T/sl.c
out=$($T/c2forge $T/sl.c $T/sl.forge); rc=$?
[ $rc = 1 ] && [ "$out" = "c2forge: line 1: expected an expression (at '\"hi\"')" ] && echo "PASS c2forge strlit-error" || { echo "FAIL c2forge strlit-error (rc=$rc out=$out)"; fail=1; }
printf 'int main(void) { char s[] = "hi"; return 0; }\n' > $T/sa.c
out=$($T/c2forge $T/sa.c $T/sa.forge); rc=$?
[ $rc = 1 ] && [ "$out" = "c2forge: line 1: array needs a positive size (at '=')" ] && echo "PASS c2forge strarr-error" || { echo "FAIL c2forge strarr-error (rc=$rc out=$out)"; fail=1; }
printf 'int main(void) { int n = 4; int a[n]; return 0; }\n' > $T/ae.c
out=$($T/c2forge $T/ae.c $T/ae.forge); rc=$?
[ $rc = 1 ] && [ "$out" = "c2forge: line 1: array size must be an integer constant (at 'n')" ] && echo "PASS c2forge arrsize-vla-error" || { echo "FAIL c2forge arrsize-vla-error (rc=$rc out=$out)"; fail=1; }
printf 'int main(void) { int *p = 5; return 0; }\n' > $T/pi.c
out=$($T/c2forge $T/pi.c $T/pi.forge); rc=$?
[ $rc = 1 ] && [ "$out" = "c2forge: line 1: cannot convert i32 to ptr[i32] without a cast (at ';')" ] && echo "PASS c2forge ptr-int-error" || { echo "FAIL c2forge ptr-int-error (rc=$rc)"; fail=1; }
printf 'union u { int a; };\n' > $T/un.c
out=$($T/c2forge $T/un.c $T/un.forge); rc=$?
[ $rc = 1 ] && [ "$out" = "c2forge: line 1: unions are not supported (Forge has no unions) (at 'union')" ] && echo "PASS c2forge union-error" || { echo "FAIL c2forge union-error (rc=$rc)"; fail=1; }
printf 'int main(void) { double d = 1.0; int x = d %% 2; return 0; }\n' > $T/fm.c
out=$($T/c2forge $T/fm.c $T/fm.forge); rc=$?
[ $rc = 1 ] && [ "$out" = "c2forge: line 1: invalid float operands to % (at ';')" ] && echo "PASS c2forge float-mod-error" || { echo "FAIL c2forge float-mod-error (rc=$rc)"; fail=1; }
printf '#define SQ(x) ((x) * (x))\nint main(void) { return 0; }\n' > $T/fn.c
out=$($T/c2forge $T/fn.c $T/fn.forge); rc=$?
[ $rc = 1 ] && [ "$out" = "c2forge: line 1: function-like macro SQ is not supported (only object-like #define)" ] && echo "PASS c2forge fnmacro-error" || { echo "FAIL c2forge fnmacro-error (rc=$rc)"; fail=1; }
printf 'int x;\n#ifdef X\n#endif\n' > $T/if.c
out=$($T/c2forge $T/if.c $T/if.forge); rc=$?
[ $rc = 1 ] && [ "$out" = "c2forge: line 2: #ifdef is not supported (only #define, #undef, #include <standard header>)" ] && echo "PASS c2forge if-error" || { echo "FAIL c2forge if-error (rc=$rc)"; fail=1; }
printf '#include <stdio.h>\n' > $T/hd.c
out=$($T/c2forge $T/hd.c $T/hd.forge); rc=$?
[ $rc = 1 ] && [ "$out" = "c2forge: line 1: #include <stdio.h>: unsupported header (supported: <stdint.h> <stddef.h> <sys/types.h> <limits.h>)" ] && echo "PASS c2forge header-error" || { echo "FAIL c2forge header-error (rc=$rc)"; fail=1; }
printf 'uint32_t f(void) { return 1; }\n' > $T/td.c
$T/c2forge $T/td.c $T/td.forge >/dev/null; rc=$?
[ $rc = 1 ] && echo "PASS c2forge typedef-without-header" || { echo "FAIL c2forge typedef-without-header (rc=$rc)"; fail=1; }
printf 'int main(void) { static int x = 0; return x; }\n' > $T/st.c
out=$($T/c2forge $T/st.c $T/st.forge); rc=$?
[ $rc = 1 ] && [ "$out" = "c2forge: line 1: static local variables are not supported yet (at 'static')" ] && echo "PASS c2forge static-local-error" || { echo "FAIL c2forge static-local-error (rc=$rc out=$out)"; fail=1; }
printf 'int main(void) { int x = 1; { int x = 2; } return x; }\n' > $T/sh.c
out=$($T/c2forge $T/sh.c $T/sh.forge); rc=$?
[ $rc = 1 ] && [ "$out" = "c2forge: line 1: inner-block declaration of x shadows an outer variable (block scoping is not supported yet) (at 'x')" ] && echo "PASS c2forge shadow-error" || { echo "FAIL c2forge shadow-error (rc=$rc out=$out)"; fail=1; }
exit $fail
