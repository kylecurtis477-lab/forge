#!/bin/sh
# tests/c2forge: the C-to-Forge translator (tools/c2forge.forge, plan item 11.4).
# Step 1: the lexer's token dump of lex.c must match lex.expected; a bad character is a clean error (exit 1).
cd "$(dirname "$0")/../.." || exit 1
T=$(mktemp -d); trap 'rm -rf $T' EXIT
fail=0
build/forgec tools/c2forge.forge $T/c2.s && build/fas -o $T/c2.o $T/c2.s && build/fld --base 0x100000000000 -o $T/c2forge $T/c2.o || { echo "FAIL c2forge build"; exit 1; }
$T/c2forge --tokens tests/c2forge/lex.c | cmp -s - tests/c2forge/lex.expected && echo "PASS c2forge lex" || { echo "FAIL c2forge lex"; fail=1; }
printf 'int x = @;\n' > $T/bad.c
out=$($T/c2forge --tokens $T/bad.c); rc=$?
[ $rc = 1 ] && [ "$out" = "c2forge: line 1: unexpected character '@'" ] && echo "PASS c2forge lex-error" || { echo "FAIL c2forge lex-error (rc=$rc)"; fail=1; }
exit $fail
