#!/bin/sh
# tests/*.forge: stdout must match .expected
# tests/runtime/*.forge: stdout+stderr+exit code must match .expected (line-numbered runtime errors)
# tests/errors/*.forge: compiler must fail with "error: line N: ..."
# tests/os/run.sh: QEMU boot tests (kernel via multiboot2 on BIOS and OVMF, native PE32+ EFI app on a GPT disk)
# tests/fs/*.forge: freestanding (--freestanding / no-runtime pragma) programs linked with plain ld, no runtime
# tests/ast/*.sh: --emit=ast-json / --emit=pretty round trips (AST equal, pretty fixpoint, same behaviour, self-host)
# tests/grammar/: docs/GRAMMAR.md conformance (valid/ parse, invalid/ rejected) + grammar-based fuzzer
# tests/diag/*.forge: --diagnostics=json output must match the golden .json (one object per error, schema v1)
# tests/typeerrors/*.forge: static type errors; full diagnostic (line, col, source, caret) must match .expected
cd "$(dirname "$0")"; fail=0; mkdir -p build
for t in tests/*.forge; do
  case "$t" in tests/mod_*) continue;; esac
  ./forge "$t" -o build/t_bin && build/t_bin a b | diff -q - "${t%.forge}.expected" >/dev/null && echo "PASS $t" || { echo "FAIL $t"; fail=1; }
done
for t in tests/runtime/*.forge; do
  ./forge "$t" -o build/t_bin && (build/t_bin 2>&1; echo "exit $?") | diff -q - "${t%.forge}.expected" >/dev/null && echo "PASS $t" || { echo "FAIL $t"; fail=1; }
done
for t in tests/errors/*.forge; do
  m=$(timeout 20 build/forgec "$t" build/t_err.s 2>&1) && { echo "FAIL $t (no error)"; fail=1; } || { echo "$m" | grep -q "error: line" && echo "PASS $t: $m" || { echo "FAIL $t"; fail=1; }; }
done
for t in tests/typeerrors/*.forge; do
  build/forgec "$t" build/t_err.s > build/t_err.txt 2>&1 && { echo "FAIL $t (no error)"; fail=1; } || { diff -q build/t_err.txt "${t%.forge}.expected" >/dev/null && echo "PASS $t: $(head -1 build/t_err.txt)" || { echo "FAIL $t"; fail=1; }; }
done
for t in tests/diag/*.forge; do
  build/forgec "$t" build/t_err.s --diagnostics=json > build/t_diag.json 2>&1 && { echo "FAIL $t (no error)"; fail=1; } || { diff -q build/t_diag.json "${t%.forge}.json" >/dev/null && echo "PASS $t" || { echo "FAIL $t"; fail=1; }; }
done
if command -v python3 >/dev/null; then
  python3 -c 'import json,sys,glob
for f in glob.glob("tests/diag/*.json"):
    for l in open(f):
        d=json.loads(l); assert d["version"]==1 and d["code"][0]=="E" and d["line"]>0 and d["col"]>0 and isinstance(d["fixits"],list), f' && echo "PASS diag-json-schema" || { echo "FAIL diag-json-schema"; fail=1; }
fi
build/forgec build/no_such_input.forge build/t_inc.s --diagnostics=json 2>&1 | grep -q '"code":"E0001"' && echo "PASS diag-json-input-missing" || { echo "FAIL diag-json-input-missing"; fail=1; }
tests/ast/roundtrip.sh selfhost/compiler.forge selfhost/prelude.forge tools/*.forge tests/*.forge tests/runtime/*.forge tests/fs/*.forge tests/unit/*.forge tests/testblocks/*.forge tests/bounds/*.forge tests/nonnull/*.forge tests/unsafe/*.forge tests/own/*.forge tests/defer/*.forge tests/generic/*.forge tests/atomic/*.forge std/*.fsi examples/*/*.forge examples/*/*.fsi || fail=1
tests/ast/semantic.sh || fail=1
tests/grammar/run.sh || fail=1
tests/fmt/run.sh || fail=1
# tests/fas and tests/fld check every OS artifact (user programs, modules, EFI loader, kernel); build them
# first so a clean tree (e.g. straight from the seed) runs the same checks instead of skipping them
{ examples/kernel/build.sh && examples/efi/build.sh; } >/dev/null 2>&1 || { echo "FAIL os-artifacts-build (fas/fld artifact checks would be skipped)"; fail=1; }
tests/fas/run.sh || fail=1
tests/fld/run.sh || fail=1
tests/krel/run.sh || fail=1
tests/forge-part/run.sh || fail=1
tests/mkgpt/run.sh || fail=1
tests/pkg/run.sh || fail=1
tests/lsp/run.sh || fail=1
tests/testblocks/run.sh || fail=1
tests/bounds/run.sh || fail=1
tests/nonnull/run.sh || fail=1
tests/unsafe/run.sh || fail=1
tests/own/run.sh || fail=1
tests/defer/run.sh || fail=1
tests/generic/run.sh || fail=1
tests/ed25519/run.sh || fail=1
tests/atomic/run.sh || fail=1
tests/ast/selfpp.sh || fail=1
build/forgec tests/tuples.forge build/t_ast.json --emit=ast-json && python3 -c 'import json;d=json.load(open("build/t_ast.json"));assert d["forge_ast"]==1 and d["root"]["k"]=="Module"' && echo "PASS ast-json-schema" || { echo "FAIL ast-json-schema"; fail=1; }
for t in tests/fs/*.forge; do
  c=""; [ -f "${t%.forge}.c" ] && gcc -O1 -ffreestanding -fno-pic -fno-stack-protector -c -o build/t_fsc.o "${t%.forge}.c" && c=build/t_fsc.o
  build/forgec "$t" build/t_fs.s $FORGEC_FLAGS && build/fas -o build/t_fs.o build/t_fs.s && build/fld -o build/t_fs build/t_fs.o $c && build/t_fs | diff -q - "${t%.forge}.expected" >/dev/null && echo "PASS $t" || { echo "FAIL $t"; fail=1; }
done
# missing input / include files: clean diagnostic, exit 1, no crash
printf 'include "no_such_file.fsi"\n' > build/t_inc.forge
build/forgec build/t_inc.forge build/t_inc.s --freestanding > build/t_inc.txt 2>&1; rc=$?
[ $rc = 1 ] && grep -q "^error: cannot read include file .*no_such_file.fsi" build/t_inc.txt && echo "PASS include-missing" || { echo "FAIL include-missing (rc=$rc)"; fail=1; }
build/forgec build/no_such_input.forge build/t_inc.s > build/t_inc.txt 2>&1; rc=$?
[ $rc = 1 ] && grep -q "^error: cannot read input file" build/t_inc.txt && echo "PASS input-missing" || { echo "FAIL input-missing (rc=$rc)"; fail=1; }
# tests/os: boot the Forge kernel (BIOS + UEFI via GRUB multiboot2) and the native EFI app in headless QEMU
if [ -z "$SKIP_OS" ] && command -v qemu-system-x86_64 >/dev/null && command -v grub-mkrescue >/dev/null; then
  r=$(tests/os/run.sh); echo "$r"; echo "$r" | grep -q "^FAIL" && fail=1
else
  echo "SKIP tests/os (needs qemu-system-x86_64, grub-mkrescue, mtools, OVMF; or SKIP_OS set)"
fi
exit $fail
