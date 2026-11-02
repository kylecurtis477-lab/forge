#!/bin/bash
# Headless QEMU boot tests for the Forge OS demos. Prints PASS/FAIL lines like run_tests.sh.
#   1. kernel via GRUB multiboot2, legacy BIOS (SeaBIOS)
#   2. same hybrid ISO via GRUB x86_64-efi under OVMF (UEFI)
#   3. native Forge UEFI loader (\EFI\Forge\forgex64.efi) from the ESP of a GPT disk shared with
#      Windows/Linux partitions: reads \EFI\Forge\kernel.elf, GOP, ExitBootServices, jumps to the kernel
# Every path must reach KERNEL OK with the same checks, and the framebuffer's first text row is
# decoded from a QEMU screendump (tests/os/boot.py).
cd "$(dirname "$0")/../.."
OVMF_CODE=${OVMF_CODE:-/usr/share/OVMF/OVMF_CODE_4M.fd}
OVMF_VARS=${OVMF_VARS:-/usr/share/OVMF/OVMF_VARS_4M.fd}
Q="-display none -no-reboot -net none -m 256 -serial stdio -device isa-debug-exit,iobase=0xf4,iosize=0x04"
examples/kernel/build.sh >/dev/null || { echo "FAIL os/kernel-build"; exit 0; }
examples/efi/build.sh >/dev/null || { echo "FAIL os/efi-build"; exit 0; }
L=build/os; mkdir -p $L
readelf -lW build/kernel/kernel.elf | grep -q "LOAD.* RWE " && echo "FAIL os/kernel-wx (RWX PT_LOAD)" || echo "PASS os/kernel-wx (no RWX PT_LOAD)"
FB="Forge kernel: framebuffer console 1024x768"
K="identity 0x0000000000100000 -> unmapped|stack guard 0xffffffffc002f000 -> unmapped|breakpoints handled: 2|live=0|demand paging: 3 faults|MADT: cpus=|#DF on IST1|recovered from #DF|LAPIC timer ticks: 20|PIT IRQ0 via IOAPIC gsi 2: 1|KERNEL OK"
B=tests/os/boot.py
python3 $B kernel-bios $L/bios.log 60 "multiboot2 (BIOS)|$K" "$FB" -- $Q -cdrom build/kernel/forge.iso
cp $OVMF_VARS $L/vars.fd
UEFI="-machine q35 -drive if=pflash,format=raw,readonly=on,file=$OVMF_CODE -drive if=pflash,format=raw,file=$L/vars.fd"
python3 $B kernel-uefi $L/uefi.log 90 "multiboot2 (UEFI)|ACPI rev 2 XSDT|$K" "$FB" -- $Q $UEFI -cdrom build/kernel/forge.iso
cp $OVMF_VARS $L/vars.fd
python3 $B efi-loader-gpt $L/efi.log 90 "hello from PE32+|signature OK|GOP 1024x768|exiting boot services|Forge UEFI loader (ExitBootServices done)|ACPI rev 2 XSDT|$K" "$FB" -- $Q $UEFI -drive file=build/efi/disk.img,format=raw
