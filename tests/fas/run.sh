#!/bin/bash
# tests/fas/run.sh: tools/fas.forge (built-in x86-64 assembler) must produce .o files
# byte-identical to GNU as for compiler output (every tests/*.forge, the compiler itself,
# fas itself) and for the kernel / EFI / module / user asm in build/ (when present).
cd "$(dirname "$0")/../.." || exit 1
FC=build/forgec; T=$(mktemp -d); fail=0
./forge tools/fas.forge -o build/fas >/dev/null || { echo "FAIL fas-build"; exit 1; }
chk() { # name file [as-flags]
  local n=$1 f=$2; shift 2
  as "$@" -o $T/g.o "$f" 2>/dev/null || { echo "SKIP fas $n (as failed)"; return; }
  if timeout 120 build/fas "$@" -o $T/f.o "$f" >/dev/null 2>&1 && cmp -s $T/g.o $T/f.o; then echo "PASS fas $n"; else echo "FAIL fas $n"; fail=1; fi
}
bad=0
for t in tests/*.forge tests/runtime/*.forge; do
  case "$t" in tests/mod_*) continue;; esac
  $FC "$t" $T/t.s $FORGEC_FLAGS >/dev/null 2>&1 || continue
  as -o $T/g.o $T/t.s && build/fas -o $T/f.o $T/t.s >/dev/null 2>&1 && cmp -s $T/g.o $T/f.o || { echo "  differs: $t"; bad=1; }
done
[ $bad = 0 ] && echo "PASS fas tests-corpus" || { echo "FAIL fas tests-corpus"; fail=1; }
$FC selfhost/compiler.forge $T/c.s $FORGEC_FLAGS >/dev/null 2>&1 && chk compiler $T/c.s
$FC tools/fas.forge $T/a.s $FORGEC_FLAGS >/dev/null 2>&1 && chk self $T/a.s
for f in build/user/*.s build/efi/*.s build/modules/*.s build/os/*.s build/kernel*.s; do
  [ -f "$f" ] && chk "$f" "$f" -I build/user
done
printf 'mov $-9223372036854775808, %%rax\n.bss\n.space 2147483648\n' > $T/e.s; chk edge $T/e.s
# missing input: clean "fas: cannot read PATH" on stderr, exit 1, no output file
build/fas -o $T/m.o $T/no_such.s >$T/mo 2>$T/me; rc=$?
[ $rc = 1 ] && [ "$(cat $T/me)" = "fas: cannot read $T/no_such.s" ] && [ ! -s $T/mo ] && [ ! -e $T/m.o ] && echo "PASS fas missing-input" || { echo "FAIL fas missing-input (rc=$rc)"; fail=1; }
rm -rf $T; exit $fail
