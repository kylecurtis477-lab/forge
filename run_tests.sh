#!/bin/sh
# tests/*.forge: stdout must match .expected
# tests/runtime/*.forge: stdout+stderr+exit code must match .expected (line-numbered runtime errors)
# tests/errors/*.forge: compiler must fail with "error: line N: ..."
# tests/os/run.sh: QEMU boot tests (kernel via multiboot2 on BIOS and OVMF, native PE32+ EFI app on a GPT disk)
# tests/fs/*.forge: freestanding (--freestanding / no-runtime pragma) programs linked with plain ld, no runtime
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
  m=$(build/forgec "$t" build/t_err.s 2>&1) && { echo "FAIL $t (no error)"; fail=1; } || { echo "$m" | grep -q "error: line" && echo "PASS $t: $m" || { echo "FAIL $t"; fail=1; }; }
done
for t in tests/typeerrors/*.forge; do
  build/forgec "$t" build/t_err.s > build/t_err.txt 2>&1 && { echo "FAIL $t (no error)"; fail=1; } || { diff -q build/t_err.txt "${t%.forge}.expected" >/dev/null && echo "PASS $t: $(head -1 build/t_err.txt)" || { echo "FAIL $t"; fail=1; }; }
done
for t in tests/fs/*.forge; do
  c=""; [ -f "${t%.forge}.c" ] && gcc -O1 -ffreestanding -fno-pic -fno-stack-protector -c -o build/t_fsc.o "${t%.forge}.c" && c=build/t_fsc.o
  build/forgec "$t" build/t_fs.s && as -o build/t_fs.o build/t_fs.s && ld -o build/t_fs build/t_fs.o $c && build/t_fs | diff -q - "${t%.forge}.expected" >/dev/null && echo "PASS $t" || { echo "FAIL $t"; fail=1; }
done
# tests/os: boot the Forge kernel (BIOS + UEFI via GRUB multiboot2) and the native EFI app in headless QEMU
if [ -z "$SKIP_OS" ] && command -v qemu-system-x86_64 >/dev/null && command -v grub-mkrescue >/dev/null; then
  r=$(tests/os/run.sh); echo "$r"; echo "$r" | grep -q "^FAIL" && fail=1
else
  echo "SKIP tests/os (needs qemu-system-x86_64, grub-mkrescue, mtools, OVMF; or SKIP_OS set)"
fi
exit $fail
