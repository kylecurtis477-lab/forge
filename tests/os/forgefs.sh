#!/bin/bash
# Phase 8.9: writable Forge FS only on an explicitly created Forge-type GPT partition (tools/forge-part.py).
# A shared GPT disk (ESP + Windows + Linux placeholders filled with data) is attached as NVMe:
#   boot 0: no Forge partition -> the kernel writes nothing, disk byte-identical
#   create the partition (host, explicit flag); boot 1 formats + writes; boot 2 finds the files again
# After every boot, every byte outside the Forge partition (ESP, other partitions, GPT) must be unchanged.
cd "$(dirname "$0")/../.."
T=$(mktemp -d /tmp/forge-ffs.XXXXXX)
cp build/efi/disk.img $T/nv.img
python3 - $T/nv.img <<'P'
import sys,struct,hashlib
f=open(sys.argv[1],'r+b'); f.seek(512); h=f.read(92); elba=struct.unpack_from('<Q',h,72)[0]
f.seek(elba*512); e=f.read(128*128)
for i in (1,2):  # fill the Windows and Linux placeholder partitions so stray writes would show
    s,l=struct.unpack_from('<QQ',e,i*128+32); f.seek(s*512)
    f.write(b''.join(hashlib.sha256(b'%d-%d'%(i,k)).digest() for k in range((l-s+1)*512//32)))
P
cp build/efi/disk.img $T/vdisk.img
# sha256 of the image with the Forge partition's range (if any) and nothing else masked out, plus per partition
snap() { python3 - "$1" <<'P'
import sys,struct,hashlib,uuid
f=open(sys.argv[1],'rb'); d=f.read(); h=d[512:604]; elba,n=struct.unpack_from('<QI',h,72)
F=uuid.UUID('D0C2E3A1-6B7F-4F0A-9E11-466F72676546').bytes_le; lo=hi=0; out=[]
for i in range(n):
    e=d[elba*512+i*128:elba*512+i*128+128]
    if e[:16]==b'\0'*16: continue
    s,l=struct.unpack_from('<QQ',e,32)
    if e[:16]==F: lo,hi=s*512,(l+1)*512
    else: out.append('p%d=%s'%(i+1,hashlib.sha256(d[s*512:(l+1)*512]).hexdigest()[:16]))
print('outside=%s %s'%(hashlib.sha256(d[:lo]+d[hi:]).hexdigest()[:16],' '.join(out)))
P
}
boot() {
  rm -f $T/s.log
  timeout 180 qemu-system-x86_64 -machine q35 -cpu max -display none -no-reboot -m 256 -smp 4 -net none \
    -serial file:$T/s.log -device isa-debug-exit,iobase=0xf4,iosize=0x04 \
    -drive file=$T/vdisk.img,format=raw,if=none,id=vd,readonly=on -device virtio-blk-pci,drive=vd \
    -boot d -cdrom build/kernel/forge.iso \
    -drive file=$T/nv.img,format=raw,if=none,id=nv,cache=writethrough -device nvme,drive=nv,serial=forge-nv0 >/dev/null 2>&1 &
  Q=$!
  for i in $(seq 180); do grep -aqE '^(Forge FS OK|Forge FS: not mounted|PANIC|panic)' $T/s.log 2>/dev/null && break; sleep 1; done
  sleep 1; kill $Q 2>/dev/null; wait $Q 2>/dev/null
}
S0=$(snap $T/nv.img); D0=$(sha256sum < $T/nv.img)
boot
if grep -aq "Forge FS: no Forge partition on nvme0, nothing written" $T/s.log && [ "$(sha256sum < $T/nv.img)" = "$D0" ]; then
  echo "PASS os/forgefs-no-partition-no-write"; else echo "FAIL os/forgefs-no-partition-no-write"; fi
python3 tools/forge-part.py create $T/nv.img 16 >/dev/null 2>&1 && echo "FAIL os/forgefs-tool-needs-flag" || echo "PASS os/forgefs-tool-needs-flag"
python3 tools/forge-part.py create $T/nv.img 16 --yes-add-forge-partition > $T/tool.out
S1=$(snap $T/nv.img)
P1=$(echo "$S1" | cut -d' ' -f2-); P0=$(echo "$S0" | cut -d' ' -f2-)
[ "$P1" = "$P0" ] && echo "PASS os/forgefs-tool-keeps-partitions" || echo "FAIL os/forgefs-tool-keeps-partitions ($S0 / $S1)"
boot; cp $T/s.log $T/b1.log; S2=$(snap $T/nv.img)
boot; cp $T/s.log $T/b2.log; S3=$(snap $T/nv.img)
if grep -aq "formatted (blank Forge partition)" $T/b1.log && grep -aq "created persist.dat" $T/b1.log && grep -aq "^Forge FS OK" $T/b1.log; then
  echo "PASS os/forgefs-format-write"; else echo "FAIL os/forgefs-format-write: $(grep -a 'Forge FS\|PANIC' $T/b1.log | tail -2)"; fi
if grep -aq ", mounted" $T/b2.log && grep -aq "survived the reboot, hashes match" $T/b2.log && grep -aq "^Forge FS OK" $T/b2.log; then
  echo "PASS os/forgefs-persists"; else echo "FAIL os/forgefs-persists: $(grep -a 'Forge FS\|PANIC' $T/b2.log | tail -2)"; fi
[ "$S1" = "$S2" ] && [ "$S2" = "$S3" ] && echo "PASS os/forgefs-outside-unchanged ($S3)" || echo "FAIL os/forgefs-outside-unchanged ($S1 / $S2 / $S3)"
for b in 1 2; do grep -aq "NVMe service hot-swap + supervised restart OK" $T/b$b.log && echo "PASS os/nvme-service-swap-restart-boot$b" || echo "FAIL os/nvme-service-swap-restart-boot$b: $(grep -a 'NVMe s\|PANIC' $T/b$b.log | tail -2)"; done
# Phase 9.2: object-store log on the Forge partition; boot 1 commits then tears a record, boot 2 recovers
F1=$(grep -ao "durable commits, clock [0-9]*, fingerprint [0-9a-fx]*" $T/b1.log | sed 's/.*fingerprint //')
if [ -n "$F1" ] && grep -aq "crash simulated (torn record" $T/b1.log && grep -aE "torn tail dropped \([1-9][0-9]* words\), fingerprint $F1\$" $T/b2.log >/dev/null && grep -aq "^objstore crash recovery OK" $T/b2.log; then
  echo "PASS os/objstore-crash-recover"; else echo "FAIL os/objstore-crash-recover: $(grep -a 'objstore\|PANIC' $T/b1.log $T/b2.log | tail -3)"; fi
python3 tools/forge-part.py read $T/nv.img hello.txt | grep -q "written by the Forge kernel on its own GPT partition" && echo "PASS os/forgefs-host-read" || echo "FAIL os/forgefs-host-read"
grep -ah "Forge FS: remount" $T/b2.log
rm -rf $T
