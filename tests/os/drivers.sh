#!/bin/bash
# Phase 8.7: NVMe, AHCI, xHCI, e1000 and virtio-net drivers on q35. Both test disks are
# generated here, read through the drivers (LBA 0 hash must match), and must be byte-identical afterwards.
# Each NIC resolves the user-network gateway 10.0.2.2 by ARP, then pings it (ICMP echo, checked reply).
cd "$(dirname "$0")/../.."
T=$(mktemp -d /tmp/forge-drv.XXXXXX)
python3 - "$T" <<'P'
import sys,hashlib
t=sys.argv[1]
def fnv(b):
    h=0xcbf29ce484222325
    for x in b: h=((h^x)*0x100000001b3)&0xffffffffffffffff
    return '%016x'%h
for n in ('nvme','ahci'):
    d=bytearray()
    i=0
    while len(d)<1<<20:
        d+=hashlib.sha256(b'forge-%s-%d'%(n.encode(),i)).digest(); i+=1
    d[:32]=('FORGE %s TEST DISK'%n.upper()).encode().ljust(32,b'.')
    open('%s/%s.img'%(t,n),'wb').write(d)
    open('%s/%s.h'%(t,n),'w').write(fnv(d[:4096]))
P
cp build/efi/disk.img $T/vdisk.img
(cd $T && sha256sum nvme.img ahci.img > before.sha)
timeout 120 qemu-system-x86_64 -machine q35 -cpu max -display none -no-reboot -m 256 -smp 4 \
  -serial file:$T/s.log -device isa-debug-exit,iobase=0xf4,iosize=0x04 \
  -drive file=$T/vdisk.img,format=raw,if=none,id=vd,readonly=on -device virtio-blk-pci,drive=vd \
  -boot d -cdrom build/kernel/forge.iso \
  -drive file=$T/nvme.img,format=raw,if=none,id=nv -device nvme,drive=nv,serial=forge-nv0 \
  -drive file=$T/ahci.img,format=raw,if=none,id=ad -device ide-hd,drive=ad,bus=ide.4 \
  -device qemu-xhci,id=xh -device usb-kbd,bus=xh.0 \
  -netdev user,id=n0,restrict=on -device e1000,netdev=n0 \
  -netdev user,id=n1,restrict=on -device virtio-net-pci,netdev=n1,disable-modern=on >/dev/null 2>&1 &
Q=$!
for i in $(seq 120); do grep -aq '^Forge sh: builtins' $T/s.log 2>/dev/null && break; sleep 1; done
kill $Q 2>/dev/null; wait $Q 2>/dev/null
S=$T/s.log; ok=1; miss=""
for p in "NVMe: read LBA 0 (4096 bytes), fnv1a64=$(cat $T/nvme.h)" "AHCI: read LBA 0 (4096 bytes), fnv1a64=$(cat $T/ahci.h)" \
  "xHCI: command ring OK" "xHCI: Address Device OK" "xHCI: device descriptor: USB 0200" \
  "e1000: ARP reply: 10.0.2.2 is at" "virtio-net: ARP reply: 10.0.2.2 is at" \
  "e1000: ICMP echo reply from 10.0.2.2: seq 1, 56 data bytes, checksums ok" \
  "virtio-net: ICMP echo reply from 10.0.2.2: seq 1, 56 data bytes, checksums ok" "out-of-window accesses 0"; do
  grep -aqF "$p" $S || { ok=0; miss="$miss [$p]"; }
done
grep -aq PANIC $S && { ok=0; miss="$miss [panic]"; }
if [ $ok = 1 ]; then echo "PASS os/drivers (NVMe, AHCI, xHCI, e1000, virtio-net; ARP + ICMP echo)"; else echo "FAIL os/drivers:$miss"; fi
if (cd $T && sha256sum -c --quiet before.sha); then echo "PASS os/drivers-no-disk-write"; else echo "FAIL os/drivers-no-disk-write"; fi
rm -rf $T
