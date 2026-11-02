#!/bin/bash
# Headless QEMU boot tests for the Forge OS demos. Prints PASS/FAIL lines like run_tests.sh.
#   1. kernel via GRUB multiboot2, legacy BIOS (SeaBIOS)
#   2. same hybrid ISO via GRUB x86_64-efi under OVMF (UEFI)
#   3. native Forge PE32+ EFI app from the ESP of a GPT disk shared with Windows/Linux partitions
cd "$(dirname "$0")/../.."
OVMF_CODE=${OVMF_CODE:-/usr/share/OVMF/OVMF_CODE_4M.fd}
OVMF_VARS=${OVMF_VARS:-/usr/share/OVMF/OVMF_VARS_4M.fd}
Q="qemu-system-x86_64 -display none -no-reboot -net none -m 256 -serial stdio -device isa-debug-exit,iobase=0xf4,iosize=0x04"
examples/kernel/build.sh >/dev/null || { echo "FAIL os/kernel-build"; exit 0; }
examples/efi/build.sh >/dev/null || { echo "FAIL os/efi-build"; exit 0; }
check() { # name rc-log expected-markers...
    local name=$1 log=$2; shift 2
    local rc=$(tail -1 $log)
    for m in "$@"; do grep -q "$m" $log || { echo "FAIL os/$name (missing: $m, qemu rc=$rc)"; return; }; done
    [ "$rc" = 33 ] && echo "PASS os/$name" || echo "FAIL os/$name (qemu rc=$rc)"
}
L=build/os; mkdir -p $L
timeout 60 $Q -cdrom build/kernel/forge.iso < /dev/null > $L/bios.log 2>&1; echo $? >< /dev/null > $L/bios.log
K="framebuffer console 1024x768|identity 0x0000000000100000 -> unmapped|breakpoints handled: 2|live=0|demand paging: 3 faults|MADT: cpus=|#DF on IST1|LAPIC timer ticks: 20|PIT IRQ0 via IOAPIC gsi 2: 1|KERNEL OK"
IFS='|' read -ra KM <<< "$K"
check kernel-bios $L/bios.log "multiboot2 (BIOS)" "${KM[@]}"
cp $OVMF_VARS $L/vars.fd
timeout 90 $Q -machine q35 -drive if=pflash,format=raw,readonly=on,file=$OVMF_CODE -drive if=pflash,format=raw,file=$L/vars.fd -cdrom build/kernel/forge.iso < /dev/null > $L/uefi.log 2>&1; echo $? >< /dev/null > $L/uefi.log
check kernel-uefi $L/uefi.log "multiboot2 (UEFI)" "ACPI rev 2 XSDT" "${KM[@]}"
cp $OVMF_VARS $L/vars.fd
timeout 90 $Q -machine q35 -drive if=pflash,format=raw,readonly=on,file=$OVMF_CODE -drive if=pflash,format=raw,file=$L/vars.fd -drive file=build/efi/disk.img,format=raw < /dev/null > $L/efi.log 2>&1; echo $? >< /dev/null > $L/efi.log
check efi-app-gpt $L/efi.log "hello from PE32+" "signature OK" "EFI OK"
