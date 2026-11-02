#!/bin/sh
# Build the Forge UEFI app and a GPT test disk whose ESP holds it next to other loaders' directories.
set -e
cd "$(dirname "$0")"
B=../../build/efi; mkdir -p $B
../../build/forgec hello.forge $B/hello.s
as -o $B/hello.o $B/hello.s
ld -m i386pep --subsystem 10 --image-base 0x10000000 -e efi_main --dynamicbase -o $B/forgex64.efi $B/hello.o
rm -f $B/esp.fat
mformat -C -F -c 1 -i $B/esp.fat -T 131072 -v FORGE_ESP ::
mmd -i $B/esp.fat ::/EFI ::/EFI/BOOT ::/EFI/Forge ::/EFI/Microsoft ::/EFI/debian
# our loader lives in its own vendor directory; BOOT/BOOTX64.EFI is only the removable-media fallback for this test disk
mcopy -i $B/esp.fat $B/forgex64.efi ::/EFI/Forge/forgex64.efi
mcopy -i $B/esp.fat $B/forgex64.efi ::/EFI/BOOT/BOOTX64.EFI
python3 mkgpt.py $B/disk.img $B/esp.fat 128
echo "built $B/forgex64.efi and $B/disk.img"
