#!/bin/sh
# Build the Forge kernel (ELF64 multiboot2) and a BIOS+UEFI hybrid GRUB ISO.
set -e
cd "$(dirname "$0")"
B=../../build/kernel; mkdir -p $B/iso/boot/grub
../../build/forgec kernel.forge $B/kernel.s $FORGEC_FLAGS
as -o $B/kernel.o $B/kernel.s
ld -n -T linker.ld -o $B/kernel.elf $B/kernel.o
grub-file --is-x86-multiboot2 $B/kernel.elf
cp $B/kernel.elf $B/iso/boot/kernel.elf
cp grub.cfg $B/iso/boot/grub/grub.cfg
grub-mkrescue -o $B/forge.iso $B/iso >/dev/null 2>&1
echo "built $B/kernel.elf and $B/forge.iso"
