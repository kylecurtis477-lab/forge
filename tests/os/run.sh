#!/bin/bash
# Headless QEMU boot tests for the Forge OS demos. Prints PASS/FAIL lines like run_tests.sh.
#   1. kernel via GRUB multiboot2, legacy BIOS (SeaBIOS)
#   2. same hybrid ISO via GRUB x86_64-efi under OVMF (UEFI)
#   3. native Forge UEFI loader (\EFI\Forge\forgex64.efi) from the ESP of a GPT disk shared with
#      Windows/Linux partitions: reads \EFI\Forge\kernel.elf, GOP, ExitBootServices, jumps to the kernel
# Every path must reach KERNEL OK with the same checks, and the framebuffer text is
# decoded from a QEMU screendump (tests/os/boot.py): the (scrolled) framebuffer console must show KERNEL OK.
cd "$(dirname "$0")/../.."
OVMF_CODE=${OVMF_CODE:-/usr/share/OVMF/OVMF_CODE_4M.fd}
OVMF_VARS=${OVMF_VARS:-/usr/share/OVMF/OVMF_VARS_4M.fd}
Q="-cpu max -display none -no-reboot -net none -m 256 -smp 4 -serial stdio -device isa-debug-exit,iobase=0xf4,iosize=0x04"
examples/kernel/build.sh >/dev/null || { echo "FAIL os/kernel-build"; exit 0; }
examples/efi/build.sh >/dev/null || { echo "FAIL os/efi-build"; exit 0; }
L=build/os; mkdir -p $L
readelf -lW build/kernel/kernel.elf | grep -q "LOAD.* RWE " && echo "FAIL os/kernel-wx (RWX PT_LOAD)" || echo "PASS os/kernel-wx (no RWX PT_LOAD)"
FB="KERNEL OK"
K="identity 0x0000000000100000 -> unmapped|stack guard 0xffffffffc002f000 -> unmapped|breakpoints handled: 2|live=0|demand paging: 3 faults|MADT: cpus=|memory reclaimed: |#DF on IST1|recovered from #DF|LAPIC timer ticks: 20|PIT IRQ0 via IOAPIC gsi 2: |CPU: FPU state |SMEP on, SMAP on|unmapped ptr -> 14, kernel ptr -> 14|SMAP blocks kernel read: 1, SMEP blocks kernel exec: 1|xmm0-15 intact after 20 yields|all cpus see the new frame: yes|6 threads x 2000 increments = 12000|checksum ok|wait queues + sleeping mutexes + condvars OK|SMP: 4 cpus online|cpus used 4/4|preemptive SMP scheduler OK|cpl 3|-> killed|frames leaked 0, heap leaked 0|ring 3 + syscall/sysret + ELF loader OK|virtio-blk (transitional)|read-only|FAT32: ESP at LBA 2048|INIT.ELF(|identical to the build output|from disk exited with|PCI + virtio-blk + FAT32 (read-only) OK|KERNEL OK"
B=tests/os/boot.py
# every path also gets the GPT disk as a read-only virtio-blk data disk (removable-media fallback loader
# deleted so firmware never boots from it); the kernel reads \EFI\Forge\init.elf from its ESP
cp build/efi/disk.img $L/vdisk.img && mdel -i $L/vdisk.img@@1048576 ::/EFI/BOOT/BOOTX64.EFI
VD="-drive file=$L/vdisk.img,format=raw,if=none,id=vd,readonly=on -device virtio-blk-pci,drive=vd"
python3 $B kernel-bios $L/bios.log 60 "multiboot2 (BIOS)|$K" "$FB" -- $Q $VD -boot d -cdrom build/kernel/forge.iso
cp $OVMF_VARS $L/vars.fd
UEFI="-machine q35 -drive if=pflash,format=raw,readonly=on,file=$OVMF_CODE -drive if=pflash,format=raw,file=$L/vars.fd"
python3 $B kernel-uefi $L/uefi.log 90 "multiboot2 (UEFI)|ACPI rev 2 XSDT|$K" "$FB" -- $Q $UEFI $VD -cdrom build/kernel/forge.iso
cp $OVMF_VARS $L/vars.fd
python3 $B efi-loader-gpt $L/efi.log 90 "hello from PE32+|signature OK|GOP 1024x768|exiting boot services|Forge UEFI loader (ExitBootServices done)|ACPI rev 2 XSDT|$K" "$FB" -- $Q $UEFI $VD -drive file=build/efi/disk.img,format=raw
