#!/bin/bash
# tests/os/bootpol.sh: boot-time DMA policy switch (examples/kernel/bootpol.fsi), one boot per mode.
#   bootpol-cmdline-strict  GRUB (BIOS, no IOMMU) with `forge.iommu=strict` on the multiboot2 command line and a
#                           tampered boot.cfg on the ESP: the config is refused, the command line tightens, and the
#                           user-mode block driver is refused its DMA region end to end (blkd exits 1, the kernel
#                           driver keeps the disk, nothing leaks).
#   bootpol-iommu-kexec     q35 + intel-iommu, the same command line and a sealed `iommu=permissive` boot.cfg: the
#                           command line still wins (strict), remapping is active so every service runs, and the
#                           kexec'd generation-1 kernel resolves the same policy from the carried command line.
#   bootpol-config-strict   native UEFI loader (no command line) with a sealed `iommu=strict` boot.cfg: the sealed
#                           config sets strict and blkd is refused.
# The default (no boot.cfg, empty command line) is checked on every other boot path by tests/os/run.sh.
# Loosening a strict build default is covered by the in-kernel resolver truth table (18/18) on every boot.
cd "$(dirname "$0")/../.."
OVMF_CODE=${OVMF_CODE:-/usr/share/OVMF/OVMF_CODE_4M.fd}
OVMF_VARS=${OVMF_VARS:-/usr/share/OVMF/OVMF_VARS_4M.fd}
Q="-cpu max -display none -no-reboot -net none -m 256 -smp 4 -serial stdio -device isa-debug-exit,iobase=0xf4,iosize=0x04"
L=build/os
B=$L/boot
FB="back == front^KERNEL OK"
T=$(mktemp -d /tmp/forge-bootpol.XXXXXX)
trap 'rm -rf $T' EXIT
fail_all() { for n in bootpol-cmdline-strict bootpol-iommu-kexec bootpol-config-strict; do echo "FAIL os/$n ($1)"; done; exit 0; }
[ -x $B ] && [ -f $L/vdisk.img ] && [ -x build/forgeseal ] && [ -f build/user/seal.key ] || fail_all "needs tests/os/run.sh outputs"
# the ISO with a command line (a test copy; build/kernel/forge.iso is untouched)
cp -r build/kernel/iso $T/iso
sed -i 's|multiboot2 /boot/kernel.elf$|multiboot2 /boot/kernel.elf forge.iommu=strict|' $T/iso/boot/grub/grub.cfg
grep -q 'forge.iommu=strict' $T/iso/boot/grub/grub.cfg || fail_all "grub.cfg edit"
env -u SOURCE_DATE_EPOCH grub-mkrescue -o $T/strict.iso $T/iso >/dev/null 2>&1 || fail_all "grub-mkrescue"
# boot configs: sealed strict, sealed permissive, and a sealed one edited afterwards (hash mismatch)
for m in strict permissive; do
  printf '# Forge boot config, sealed with forgeseal --config (bootpol.fsi)\niommu=%s\n' $m > $T/$m.cfg
  build/forgeseal --config $T/$m.cfg build/user/seal.key >/dev/null || fail_all "forgeseal --config"
done
cp $T/permissive.cfg $T/tampered.cfg
python3 - $T/tampered.cfg <<'P' || fail_all "tamper"
import sys
b = bytearray(open(sys.argv[1], 'rb').read())
i = b.index(b'iommu=permissive')
b[i:i + 16] = b'iommu=strict    '
open(sys.argv[1], 'wb').write(b)
P
for v in tampered permissive strict; do
  cp $L/vdisk.img $T/$v.img && mcopy -o -i $T/$v.img@@1048576 $T/$v.cfg ::/EFI/Forge/boot.cfg || fail_all "mcopy"
done
vd() { echo "-drive file=$T/$1.img,format=raw,if=none,id=vd,readonly=on -device virtio-blk-pci,drive=vd$2"; }
STRICT="strict mode refuses drv 402 without a remapping unit: EPERM|blkd: no DMA region|blkd: DMA region refused under the strict policy (no remapping unit), driver exit 1; the in-kernel driver reads \\EFI\\Forge\\init.elf, identical; frames leaked 0, heap leaked 0|shell and kexec sessions skipped: both need the user-mode block driver|live patching OK|KERNEL OK"
$B bootpol-cmdline-strict $L/bootpol1.log 200 "boot policy: \\EFI\\Forge\\boot.cfg refused (image hash mismatch), ignored|boot policy: forge.iommu=strict on the command line (tightens)|boot policy: DMA policy strict (command line); resolver truth table 18/18|VT-d policy: no remapping unit, strict (command line): drv 402 DMA grants refused|$STRICT" "$FB" -- $Q $(vd tampered) -boot d -cdrom $T/strict.iso
$B bootpol-iommu-kexec $L/bootpol2.log 200 "boot policy: sealed \\EFI\\Forge\\boot.cfg verified: iommu=permissive|boot policy: forge.iommu=strict on the command line (tightens)|boot policy: DMA policy strict (command line); resolver truth table 18/18|VT-d policy: remapping active (deny by default, grants confined)|isolated driver service OK|kexec handoff from the previous kernel (generation 1)|boot policy: DMA policy strict (command line, kexec generation 1); resolver truth table 18/18|kexec handoff OK|KERNEL OK" "$FB" -- $Q -machine q35 -device intel-iommu,intremap=on $(vd permissive ",disable-legacy=on,iommu_platform=on") -device pcie-root-port,id=rp1,bus=pcie.0,addr=0x5,chassis=1 -device pcie-pci-bridge,id=pb1,bus=rp1 -device edu,bus=pb1,addr=0x1 -boot d -cdrom $T/strict.iso
cp $OVMF_VARS $T/vars.fd
cp build/efi/disk.img $T/disk.img
UEFI="-machine q35 -drive if=pflash,format=raw,readonly=on,file=$OVMF_CODE -drive if=pflash,format=raw,file=$T/vars.fd"
$B bootpol-config-strict $L/bootpol3.log 360 "Forge UEFI loader (ExitBootServices done)|boot policy: sealed \\EFI\\Forge\\boot.cfg verified: iommu=strict|boot policy: DMA policy strict (sealed boot config); resolver truth table 18/18|VT-d policy: no remapping unit, strict (sealed boot config): drv 402 DMA grants refused|$STRICT" "$FB" -- $Q $UEFI $(vd strict) -drive file=$T/disk.img,format=raw
