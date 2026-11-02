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
BIGH=$(python3 -c "
h=0xcbf29ce484222325
for i in range(98304): h=((h^((i*7+(i>>9))&255))*0x100000001b3)&(2**64-1)
print('%016x'%h)")
K="identity 0x0000000000100000 -> unmapped|stack guard 0xffffffffc002f000 -> unmapped|breakpoints handled: 2|live=0|demand paging: 3 faults|MADT: cpus=|memory reclaimed: |#DF on IST1|recovered from #DF|LAPIC timer ticks: 20|PIT IRQ0 via IOAPIC gsi 2: |CPU: FPU state |SMEP on, SMAP on|unmapped ptr -> 14, kernel ptr -> 14|SMAP blocks kernel read: 1, SMEP blocks kernel exec: 1|xmm0-15 intact after 20 yields|all cpus see the new frame: yes|6 threads x 2000 increments = 12000|checksum ok|wait queues + sleeping mutexes + condvars OK|SMP: 4 cpus online|cpus used 4/4|preemptive SMP scheduler OK|cpl 3|-> killed|frames leaked 0, heap leaked 0|ring 3 + syscall/sysret + ELF loader OK|virtio-blk (transitional)|MSI-X vector 0x50 -> cpu0, sleeping requests| MSI-X completions, |read-only|FAT32: ESP at LBA 2048|init.elf(|identical to the build output|from disk exited with|PCI + virtio-blk + FAT32 (read-only) OK|module hello.ko: |hello from a loaded module, calls=2|state restored|1000 load/call/unload cycles in |frames leaked 0, heap bytes leaked 0|rejected truncated hello.ko: |rejected badabi.ko: ABI version mismatch|rejected unresolved.ko: unresolved symbol (kernel_secret_internal)|rejected newer.ko: import newer than the module ABI (kabi_future_probe)|module ABI OK: 1002 loads, 1002 unloads, 4 rejected|blkd: pid |through the user-mode driver, identical; |blkd crashed (exit 0x000000000000010e), client got EIO and is still running; device reset, deaths=1|blkd: frames leaked 0, heap leaked 0|isolated driver service OK|Forge sh: builtins|sh$ ls|forktest  |forktest: fork ok|5 children reaped, ECHILD|mmap 16KiB rw ok|hello: args = 'from-exec 1 2 3'|forktest: PASS|It spans more than one 13-character LFN entry|[cat exited 0]|hello: args = 'a b'|sh: nosuch: command not found|[nosuch exited 127]|docs/|sh: bye|sh exited with 0|shell session: frames leaked 0, heap leaked 0|processes + VFS + keyboard/serial input + sh OK|crash: block driver will die on request 40|supervisor: blkd (pid |, restart 1, replaying request |pages, restart 1|sum: 98304 bytes fnv1a64=$BIGH|supervisor: blkd restarts 1, |supervised driver restart OK| with a partial line queued), pushed |, lost 0|tty hot-swap OK|livepatch: sys_getpid patched + reverted 25 times on 4 CPUs, stop-machine parked 3 CPUs per write| through the patch), wrong 0|livepatch: bad patch rejected before install (rejects 1), failed patch rolled back (rollbacks 1), text hash restored, frames leaked 0, heap leaked 0|live patching OK|KERNEL OK"
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
