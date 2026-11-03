#!/bin/sh
# Build the Forge UEFI loader (+ kernel) and a GPT test disk whose ESP holds it next to other loaders' directories.
set -e
cd "$(dirname "$0")"
B=../../build/efi; mkdir -p $B
../../build/forgec loader.forge $B/loader.s $FORGEC_FLAGS
../../build/fas -o $B/loader.o $B/loader.s
../../build/fld --format pe --subsystem 10 --image-base 0x10000000 -e efi_main -o $B/forgex64.efi $B/loader.o
../kernel/build.sh >/dev/null
rm -f $B/esp.fat
mformat -C -F -c 1 -i $B/esp.fat -T 131072 -v FORGE_ESP ::
mmd -i $B/esp.fat ::/EFI ::/EFI/BOOT ::/EFI/Forge ::/EFI/Microsoft ::/EFI/debian
# our loader lives in its own vendor directory; BOOT/BOOTX64.EFI is only the removable-media fallback for this test disk
mcopy -i $B/esp.fat $B/forgex64.efi ::/EFI/Forge/forgex64.efi
mcopy -i $B/esp.fat ../../build/kernel/kernel.elf ::/EFI/Forge/kernel.elf
mcopy -i $B/esp.fat ../../build/kernel/kernel-next.elf ::/EFI/Forge/kernel-next.elf
mcopy -i $B/esp.fat ../../build/user/hello.elf ::/EFI/Forge/init.elf
mmd -i $B/esp.fat ::/EFI/Forge/bin ::/EFI/Forge/docs ::/EFI/Forge/mod
for m in hello badabi unresolved newer tty1 tty2 getpid2 unsealed tty1u tampered userimg; do mcopy -i $B/esp.fat ../../build/modules/$m.ko ::/EFI/Forge/mod/$m.ko; done
for p in sh hello cat forktest fault blkd crash sum bg captest chantest sbtest grtest wxtest capdeny badptr kbdd kbdd2 nvmed nvmed2 unsealed tampered forged linuxabi lxtest lxthreads lxfs lxexec; do mcopy -i $B/esp.fat ../../build/user/$p.elf ::/EFI/Forge/bin/$p; done
printf 'Forge long file name test: this file lives at \\EFI\\Forge\\docs\\Long File Name Notes.txt\nIt spans more than one 13-character LFN entry and is read with open/read/stat/close.\n' > $B/notes.txt
mcopy -i $B/esp.fat $B/notes.txt "::/EFI/Forge/docs/Long File Name Notes.txt"
python3 -c "import sys; sys.stdout.buffer.write(bytes((i * 7 + (i >> 9)) & 255 for i in range(98304)))" > $B/big.bin
mcopy -i $B/esp.fat $B/big.bin ::/EFI/Forge/docs/big.bin
mmd -i $B/esp.fat ::/EFI/Forge/apps
for a in ../apps/*.mf; do mcopy -i $B/esp.fat $a ::/EFI/Forge/apps/$(basename $a); done
mcopy -i $B/esp.fat $B/forgex64.efi ::/EFI/BOOT/BOOTX64.EFI
python3 mkgpt.py $B/disk.img $B/esp.fat 128
echo "built $B/forgex64.efi and $B/disk.img"
