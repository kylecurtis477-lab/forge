#!/bin/sh
# tests/*.forge: stdout must match .expected
# tests/runtime/*.forge: stdout+stderr+exit code must match .expected (line-numbered runtime errors)
# tests/errors/*.forge: compiler must fail with "error: line N: ..."
# tests/os/run.sh: QEMU boot tests (kernel via multiboot2 on BIOS and OVMF, native PE32+ EFI app on a GPT disk)
# tests/fs/*.forge: freestanding (--freestanding / no-runtime pragma) programs linked with plain ld, no runtime
#   (each one again with --fs-opt, which must not change its output)
# tests/ast/*.sh: --emit=ast-json / --emit=pretty round trips (AST equal, pretty fixpoint, same behaviour, self-host)
# tests/grammar/: docs/GRAMMAR.md conformance (valid/ parse, invalid/ rejected) + grammar-based fuzzer
# tests/diag/*.forge: --diagnostics=json output must match the golden .json (one object per error, schema v1)
# tests/typeerrors/*.forge: static type errors; full diagnostic (line, col, source, caret) must match .expected
# tests/bench/: tools/bench.forge regression-check logic on fixed records (bench timing never gates)
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
{ ./forge examples/kernel/build.forge -o build/kernel-build && build/kernel-build && ./forge examples/efi/build.forge -o build/efi-build && build/efi-build; } >/dev/null 2>&1 || { echo "FAIL os-artifacts-build (fas/fld artifact checks would be skipped)"; fail=1; }
# the boot harness asm is one of the fas artifacts; build it here so every run checks the same file set
# (before, build/os/boot.s existed only after an earlier OS run, so O2 and O0 counted 638 vs 639)
mkdir -p build/os && build/forgec tests/os/boot.forge build/os/boot.s || { echo "FAIL os-boot-harness-asm"; fail=1; }
tests/fas/run.sh || fail=1
tests/fld/run.sh || fail=1
tests/krel/run.sh || fail=1
tests/forge-part/run.sh || fail=1
tests/mkgpt/run.sh || fail=1
tests/ed25519-tool/run.sh || fail=1
tests/grammar-fuzz/run.sh || fail=1
tests/mkfont/run.sh || fail=1
tests/c2forge/run.sh || fail=1
tests/forgeseal/run.sh || fail=1
tests/modules-build/run.sh || fail=1
tests/user-build/run.sh || fail=1
tests/busybox-build/run.sh || fail=1
tests/bzip2/run.sh || fail=1
tests/cpython-build/run.sh || fail=1
tests/cpython-embed/run.sh || fail=1
tests/efi-build/run.sh || fail=1
tests/kernel-build/run.sh || fail=1
tests/rtport/run.sh || fail=1
tests/pecheck/run.sh || fail=1
tests/memimg/run.sh || fail=1
tests/oslib/run.sh || fail=1
tests/reseed/run.sh || fail=1
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
# tests/bench: tools/bench.forge record parser and regression logic on fixed records (timing itself never gates)
./forge tools/bench.forge -o build/bench >/dev/null 2>&1 || { echo "FAIL bench-build"; fail=1; }
for c in ok slow ratio; do
  (build/bench check --history tests/bench/$c.tsv --baseline tests/bench/base.tsv; echo "exit $?") | diff -q - tests/bench/$c.expected >/dev/null && echo "PASS bench-check-$c" || { echo "FAIL bench-check-$c"; fail=1; }
done
build/forgec tests/tuples.forge build/t_ast.json --emit=ast-json && python3 -c 'import json;d=json.load(open("build/t_ast.json"));assert d["forge_ast"]==1 and d["root"]["k"]=="Module"' && echo "PASS ast-json-schema" || { echo "FAIL ast-json-schema"; fail=1; }
for t in tests/fs/*.forge; do
  c=""; [ -f "${t%.forge}.c" ] && gcc -O1 -ffreestanding -fno-pic -fno-stack-protector -c -o build/t_fsc.o "${t%.forge}.c" && c=build/t_fsc.o
  build/forgec "$t" build/t_fs.s $FORGEC_FLAGS && build/fas -o build/t_fs.o build/t_fs.s && build/fld -o build/t_fs build/t_fs.o $c && build/t_fs | diff -q - "${t%.forge}.expected" >/dev/null && echo "PASS $t" || { echo "FAIL $t"; fail=1; }
done
# --fs-opt (Phase 12.1 step 1, opt-in): every freestanding program must print the same with the flag, and
# tests/fs/regalloc.forge must come out with ra_leaf fully in registers and ra_addr (address taken) untouched
for t in tests/fs/*.forge; do
  c=""; [ -f "${t%.forge}.c" ] && gcc -O1 -ffreestanding -fno-pic -fno-stack-protector -c -o build/t_fsc.o "${t%.forge}.c" && c=build/t_fsc.o
  build/forgec "$t" build/t_fs.s $FORGEC_FLAGS --fs-opt && build/fas -o build/t_fs.o build/t_fs.s && build/fld -o build/t_fs build/t_fs.o $c && build/t_fs | diff -q - "${t%.forge}.expected" >/dev/null && echo "PASS fs-opt $t" || { echo "FAIL fs-opt $t"; fail=1; }
done
build/forgec tests/fs/regalloc.forge build/t_ra.s $FORGEC_FLAGS --fs-opt && [ "$(awk '/^ra_leaf:/,/ret$/' build/t_ra.s | grep -c '(%rbp)')" = 0 ] && [ "$(awk '/^ra_addr:/,/ret$/' build/t_ra.s | grep -c '(%rbp)')" -gt 0 ] && echo "PASS fs-opt-shape" || { echo "FAIL fs-opt-shape"; fail=1; }
# constant division: in c_i64_7 and c_u64_641 only the three divisions by a variable keep div/idiv
build/forgec tests/fs/cdiv.forge build/t_cd.s $FORGEC_FLAGS --fs-opt && [ "$(awk '/^c_i64_7:/,/ret$/' build/t_cd.s | grep -c '    idiv ')" = 3 ] && [ "$(awk '/^c_u64_641:/,/ret$/' build/t_cd.s | grep -c '    div ')" = 3 ] && echo "PASS fs-opt-cdiv-shape" || { echo "FAIL fs-opt-cdiv-shape"; fail=1; }
# missing input / include files: clean diagnostic, exit 1, no crash
printf 'include "no_such_file.fsi"\n' > build/t_inc.forge
build/forgec build/t_inc.forge build/t_inc.s --freestanding > build/t_inc.txt 2>&1; rc=$?
[ $rc = 1 ] && grep -q "^error: cannot read include file .*no_such_file.fsi" build/t_inc.txt && echo "PASS include-missing" || { echo "FAIL include-missing (rc=$rc)"; fail=1; }
build/forgec build/no_such_input.forge build/t_inc.s > build/t_inc.txt 2>&1; rc=$?
[ $rc = 1 ] && grep -q "^error: cannot read input file" build/t_inc.txt && echo "PASS input-missing" || { echo "FAIL input-missing (rc=$rc)"; fail=1; }
# tests/os: boot the Forge kernel (BIOS + UEFI via GRUB multiboot2) and the native EFI app in headless QEMU
if [ -z "$SKIP_OS" ] && command -v qemu-system-x86_64 >/dev/null && command -v grub-mkrescue >/dev/null; then
  # capture through a regular file, not $(...): a child that sets O_NONBLOCK on the shared pipe made later
  # writes fail with EAGAIN, silently dropping result lines (inv83: 14 os/dwarf results missing at O0)
  tests/os/run.sh > build/os-results.txt 2>&1; r=$(cat build/os-results.txt); echo "$r"; echo "$r" | grep -q "^FAIL" && fail=1
  miss=""
  for n in $(grep -v '^#' tests/os/expected-results.txt); do
    echo "$r" | grep -qE "^(PASS|FAIL) $n( |$)" || miss="$miss $n"
  done
  [ -z "$miss" ] && echo "PASS os/result-count ($(grep -vc '^#' tests/os/expected-results.txt) results reported)" || { echo "FAIL os/result-count (missing:$miss)"; fail=1; }
else
  echo "SKIP tests/os (needs qemu-system-x86_64, grub-mkrescue, mtools, OVMF; or SKIP_OS set)"
fi
exit $fail
