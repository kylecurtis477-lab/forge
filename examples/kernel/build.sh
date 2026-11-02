#!/bin/sh
# Build the Forge kernel (ELF64 multiboot2) and a BIOS+UEFI hybrid GRUB ISO.
set -e
cd "$(dirname "$0")"
B=../../build/kernel; mkdir -p $B/iso/boot/grub
# image KASLR: link once keeping relocations, build the self-relocation table from them, link again with
# the table appended (.krel is last, so nothing moves), and check the final image yields the same table
klink() {
    ld -n -q -T linker.ld -o $1.pass1.elf $1.o
    python3 ../../tools/krel.py $1.pass1.elf $1.krel.s
    as -o $1.krel.o $1.krel.s
    ld -n -q -T linker.ld -o $1.check.elf $1.o $1.krel.o
    python3 ../../tools/krel.py $1.check.elf $1.krel2.s >/dev/null
    cmp -s $1.krel.s $1.krel2.s || { echo "krel: table changed between links"; exit 1; }
    # the shipped image carries no ELF relocation sections (GRUB refuses those): only the .krel table
    ld -n -T linker.ld -o $1.elf $1.o $1.krel.o
    rm -f $1.pass1.elf $1.check.elf $1.krel2.s
}
sh ../user/build.sh
sh ../modules/build.sh
../../build/forgec kernel.forge $B/kernel.s $FORGEC_FLAGS
as -I ../../build/user -o $B/kernel.o $B/kernel.s
klink $B/kernel
grub-file --is-x86-multiboot2 $B/kernel.elf
# a genuinely different build for the kexec test (\EFI\Forge\kernel-next.elf): another version string
sed 's/"forge-kernel 0.2.6"/"forge-kernel 0.2.6-next"/' kernel.forge > .kernel_next.forge
grep -q '"forge-kernel 0.2.6-next"' .kernel_next.forge
../../build/forgec .kernel_next.forge $B/kernel-next.s $FORGEC_FLAGS; rm -f .kernel_next.forge
as -I ../../build/user -o $B/kernel-next.o $B/kernel-next.s
klink $B/kernel-next
cmp -s $B/kernel.elf $B/kernel-next.elf && { echo "kernel-next.elf is identical to kernel.elf"; exit 1; }
cp $B/kernel.elf $B/iso/boot/kernel.elf
cp grub.cfg $B/iso/boot/grub/grub.cfg
grub-mkrescue -o $B/forge.iso $B/iso >/dev/null 2>&1
echo "built $B/kernel.elf and $B/forge.iso"
