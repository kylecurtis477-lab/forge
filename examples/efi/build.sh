#!/bin/sh
# Build the Forge UEFI loader (+ kernel) and a GPT test disk whose ESP holds it next to other loaders' directories.
set -e
cd "$(dirname "$0")"
B=../../build/efi; mkdir -p $B
../../build/forgec loader.forge $B/loader.s
as -o $B/loader.o $B/loader.s
ld -m i386pep --subsystem 10 --image-base 0x10000000 -e efi_main --dynamicbase -o $B/forgex64.efi $B/loader.o
../kernel/build.sh >/dev/null
rm -f $B/esp.fat
mformat -C -F -c 1 -i $B/esp.fat -T 131072 -v FORGE_ESP ::
mmd -i $B/esp.fat ::/EFI ::/EFI/BOOT ::/EFI/Forge ::/EFI/Microsoft ::/EFI/debian
# our loader lives in its own vendor directory; BOOT/BOOTX64.EFI is only the removable-media fallback for this test disk
mcopy -i $B/esp.fat $B/forgex64.efi ::/EFI/Forge/forgex64.efi
mcopy -i $B/esp.fat ../../build/kernel/kernel.elf ::/EFI/Forge/kernel.elf
mcopy -i $B/esp.fat $B/forgex64.efi ::/EFI/BOOT/BOOTX64.EFI
python3 mkgpt.py $B/disk.img $B/esp.fat 128
echo "built $B/forgex64.efi and $B/disk.img"
