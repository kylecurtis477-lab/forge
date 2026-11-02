#!/bin/sh
# Build the Forge kernel (ELF64 multiboot2) and a BIOS+UEFI hybrid GRUB ISO.
set -e
cd "$(dirname "$0")"
B=../../build/kernel; mkdir -p $B/iso/boot/grub
sh ../user/build.sh
sh ../modules/build.sh
../../build/forgec kernel.forge $B/kernel.s $FORGEC_FLAGS
as -I ../../build/user -o $B/kernel.o $B/kernel.s
ld -n -T linker.ld -o $B/kernel.elf $B/kernel.o
grub-file --is-x86-multiboot2 $B/kernel.elf
# a genuinely different build for the kexec test (\EFI\Forge\kernel-next.elf): another version string
sed 's/"forge-kernel 0.2.6"/"forge-kernel 0.2.6-next"/' kernel.forge > .kernel_next.forge
grep -q '"forge-kernel 0.2.6-next"' .kernel_next.forge
../../build/forgec .kernel_next.forge $B/kernel-next.s $FORGEC_FLAGS; rm -f .kernel_next.forge
as -I ../../build/user -o $B/kernel-next.o $B/kernel-next.s
ld -n -T linker.ld -o $B/kernel-next.elf $B/kernel-next.o
cmp -s $B/kernel.elf $B/kernel-next.elf && { echo "kernel-next.elf is identical to kernel.elf"; exit 1; }
cp $B/kernel.elf $B/iso/boot/kernel.elf
cp grub.cfg $B/iso/boot/grub/grub.cfg
grub-mkrescue -o $B/forge.iso $B/iso >/dev/null 2>&1
echo "built $B/kernel.elf and $B/forge.iso"
