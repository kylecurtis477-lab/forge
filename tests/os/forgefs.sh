#!/bin/bash
# Phase 8.9: writable Forge FS only on an explicitly created Forge-type GPT partition (tools/forge-part.py).
# A shared GPT disk (ESP + Windows + Linux placeholders filled with data) is attached as NVMe:
#   boot 0: no Forge partition -> the kernel writes nothing, disk byte-identical
#   create the partition (host, explicit flag); boot 1 formats + writes; boot 2 finds the files again; boot 3 checks the 9.4 rewind persisted
#   (boots 1-3 get the user key through fw_cfg; Phase 10.1) boot 4 has a wrong key and boot 5 none: the store stays locked and untouched
#   boot 6 runs on a copy with one ciphertext bit flipped: the HMAC rejects that slot (the good image is restored after)
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
KEY=forge-test-passphrase-uid1000
boot() {  # boot [userkey]: the user key reaches the kernel as QEMU fw_cfg file opt/forge/userkey
  rm -f $T/s.log
  K=(); [ -n "${1:-}" ] && K=(-fw_cfg "name=opt/forge/userkey,string=$1")
  timeout 180 qemu-system-x86_64 -machine q35 -cpu max -display none -no-reboot -m 256 -smp 4 -net none \
    -serial file:$T/s.log -device isa-debug-exit,iobase=0xf4,iosize=0x04 \
    -drive file=$T/vdisk.img,format=raw,if=none,id=vd,readonly=on -device virtio-blk-pci,drive=vd \
    -boot d -cdrom build/kernel/forge.iso \
    -drive file=$T/nv.img,format=raw,if=none,id=nv,cache=writethrough -device nvme,drive=nv,serial=forge-nv0 "${K[@]}" >/dev/null 2>&1 &
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
boot $KEY; cp $T/s.log $T/b1.log; S2=$(snap $T/nv.img)
boot $KEY; cp $T/s.log $T/b2.log; S3=$(snap $T/nv.img)
boot $KEY; cp $T/s.log $T/b3.log; S4=$(snap $T/nv.img)
if grep -aq "formatted (blank Forge partition)" $T/b1.log && grep -aq "created persist.dat" $T/b1.log && grep -aq "^Forge FS OK" $T/b1.log; then
  echo "PASS os/forgefs-format-write"; else echo "FAIL os/forgefs-format-write: $(grep -a 'Forge FS\|PANIC' $T/b1.log | tail -2)"; fi
if grep -aq ", mounted" $T/b2.log && grep -aq "survived the reboot, hashes match" $T/b2.log && grep -aq "^Forge FS OK" $T/b2.log; then
  echo "PASS os/forgefs-persists"; else echo "FAIL os/forgefs-persists: $(grep -a 'Forge FS\|PANIC' $T/b2.log | tail -2)"; fi
[ "$S1" = "$S2" ] && [ "$S2" = "$S3" ] && [ "$S3" = "$S4" ] && echo "PASS os/forgefs-outside-unchanged ($S4)" || echo "FAIL os/forgefs-outside-unchanged ($S1 / $S2 / $S3 / $S4)"
for b in 1 2; do grep -aq "NVMe service hot-swap + supervised restart OK" $T/b$b.log && echo "PASS os/nvme-service-swap-restart-boot$b" || echo "FAIL os/nvme-service-swap-restart-boot$b: $(grep -a 'NVMe s\|PANIC' $T/b$b.log | tail -2)"; done
# Phase 9.2: object-store log on the Forge partition; boot 1 commits then tears a record, boot 2 recovers
F1=$(grep -ao "durable commits, clock [0-9]*, fingerprint [0-9a-fx]*" $T/b1.log | sed 's/.*fingerprint //')
if [ -n "$F1" ] && grep -aq "crash simulated (torn record" $T/b1.log && grep -aE "torn tail dropped \([1-9][0-9]* words\), fingerprint $F1\$" $T/b2.log >/dev/null && grep -aq "^objstore crash recovery OK" $T/b2.log; then
  echo "PASS os/objstore-crash-recover"; else echo "FAIL os/objstore-crash-recover: $(grep -a 'objstore\|PANIC' $T/b1.log $T/b2.log | tail -3)"; fi
# Phase 9.4: boot 2 commits a bad change, detects it and rewinds the whole store; boot 3 finds the rewound state
G2=$(grep -ao "boot done, state fingerprint [0-9a-fx]*" $T/b2.log | sed 's/.*fingerprint //')
if [ -n "$G2" ] && grep -aq "bad change committed, health check: 8 broken slots" $T/b2.log && grep -aq "^objstore system rewind OK" $T/b2.log; then
  echo "PASS os/objstore-system-rewind"; else echo "FAIL os/objstore-system-rewind: $(grep -a 'objstore\|PANIC' $T/b2.log | tail -3)"; fi
if [ -n "$G2" ] && grep -aq "after reboot, health check: 0 broken slots, state fingerprint $G2\$" $T/b3.log && grep -aq "^objstore system rewind persisted OK" $T/b3.log; then
  echo "PASS os/objstore-rewind-persists"; else echo "FAIL os/objstore-rewind-persists: $(grep -a 'objstore\|PANIC' $T/b3.log | tail -3)"; fi
# Phase 9.5: files as objects, read through the /obj VFS view (latest, as of an old clock, listings); boot 3 again
if grep -aqE "objfs: /obj lists 2 files; /obj/motd reads v2 \(200 bytes\), /obj/@[0-9]+/motd reads v1 \(300 bytes\), /obj/@0 lists 0" $T/b2.log && grep -aq "^objstore VFS view OK" $T/b2.log; then
  echo "PASS os/objfs-vfs-view"; else echo "FAIL os/objfs-vfs-view: $(grep -a 'objfs\|PANIC' $T/b2.log | tail -3)"; fi
if grep -aq "objfs: after reboot /obj lists 2 files, /obj/motd is 200 bytes" $T/b3.log && grep -aq "^objstore VFS view persisted OK" $T/b3.log; then
  echo "PASS os/objfs-persists"; else echo "FAIL os/objfs-persists: $(grep -a 'objfs\|PANIC' $T/b3.log | tail -3)"; fi
# Phase 10.1: objects encrypted at rest. The image must hold no object plaintext, no key material in the
# clear, and look random where the log was written; a wrong key or no key leaves the store locked and untouched.
grep -aq "objstore: encrypted at rest (ChaCha20, encrypt-then-MAC HMAC-SHA256), uid 1000 data key created, wrapped by the platform key (no TPM)" $T/b1.log && grep -aq "data key unwrapped with the platform key" $T/b3.log && echo "PASS os/objstore-key-wrap" || echo "FAIL os/objstore-key-wrap: $(grep -a 'objstore: \(enc\|lock\)' $T/b1.log $T/b3.log | tail -2)"
python3 tools/forge-part.py read $T/nv.img objects.log > $T/olog.bin; python3 tools/forge-part.py read $T/nv.img objects.key > $T/okey.bin
PT=$(python3 - $T/nv.img $T/olog.bin $KEY <<'P'
import sys,zlib,struct
import uuid
d=open(sys.argv[1],'rb').read(); elba,n=struct.unpack_from('<QI',d,512+72); F=uuid.UUID('D0C2E3A1-6B7F-4F0A-9E11-466F72676546').bytes_le
for i in range(n):
    e=d[elba*512+i*128:elba*512+i*128+128]
    if e[:16]==F: s,l=struct.unpack_from('<QQ',e,32); d=d[s*512:(l+1)*512]  # the Forge partition only
L=open(sys.argv[2],'rb').read(); bad=[]
for pat in (b'motd\0', b'notes\0', sys.argv[3].encode()):
    if pat in d: bad.append('found %r'%pat)
w=[L[i:i+4096] for i in range(0,len(L),4096)]
used=[b[:4056] for b in w if struct.unpack_from('<Q',b,4056)[0]!=0]
ct=b''.join(used)
if len(used)<4: bad.append('only %d written slots'%len(used))
elif len(zlib.compress(ct,9))<len(ct)*0.99: bad.append('ciphertext compresses (%d -> %d)'%(len(ct),len(zlib.compress(ct,9))))
print('%d written slots, %d ciphertext bytes'%(len(used),len(ct)) if not bad else 'BAD '+'; '.join(bad))
P
)
case "$PT" in BAD*) echo "FAIL os/objstore-no-plaintext: $PT";; *) echo "PASS os/objstore-no-plaintext ($PT)";; esac
# every slot ever written has its own counter (= nonce with the block number), and objects.ctr's
# durable high-water mark covers all of them, so no counter can be handed out again after a reboot
python3 tools/forge-part.py read $T/nv.img objects.ctr > $T/octr.bin
NC=$(python3 - $T/olog.bin $T/octr.bin <<'P'
import sys,struct
L=open(sys.argv[1],'rb').read(); C=open(sys.argv[2],'rb').read()
wc=[struct.unpack_from('<Q',L,i+4056)[0] for i in range(0,len(L),4096)]; wc=[w for w in wc if w]
h=max(struct.unpack_from('<Q',C,i+8)[0] for i in (0,4096))
print('BAD' if len(set(wc))!=len(wc) or not wc or h<max(wc) else '%d slot counters distinct, max %d <= durable high-water %d'%(len(wc),max(wc),h))
P
)
case "$NC" in BAD*|"") echo "FAIL os/objstore-nonce-unique: $NC";; *) echo "PASS os/objstore-nonce-unique ($NC)";; esac
O0=$(cat $T/olog.bin $T/okey.bin | sha256sum)
boot wrong-passphrase-for-uid1000; cp $T/s.log $T/b4.log
boot; cp $T/s.log $T/b5.log
python3 tools/forge-part.py read $T/nv.img objects.log > $T/olog.bin; python3 tools/forge-part.py read $T/nv.img objects.key > $T/okey.bin
if grep -aq "objstore: locked: wrong user key for uid 1000, nothing decrypted or written" $T/b4.log && grep -aq "objstore: locked: no user key" $T/b5.log && [ "$(cat $T/olog.bin $T/okey.bin | sha256sum)" = "$O0" ]; then
  echo "PASS os/objstore-wrong-key-locked"; else echo "FAIL os/objstore-wrong-key-locked: $(grep -a 'objstore: lock\|PANIC' $T/b4.log $T/b5.log | tail -2)"; fi
# tamper: flip one ciphertext bit of the newest slot in a copy of the image; HMAC must reject that slot
cp $T/nv.img $T/nv.good
python3 - $T/nv.img $T/olog.bin <<'P'
import sys,struct
d=bytearray(open(sys.argv[1],'rb').read()); L=open(sys.argv[2],'rb').read()
sl=[L[i:i+4096] for i in range(0,len(L),4096)]
top=max(sl,key=lambda b:struct.unpack_from('<Q',b,4056)[0])
o=d.find(top); assert o>0 and d.find(top,o+1)<0
d[o+100]^=0x10; open(sys.argv[1],'wb').write(d)
P
boot $KEY; cp $T/s.log $T/b6.log
R3=$(grep -ao "[0-9]* never written or superseded-and-torn slots rejected" $T/b3.log | cut -d' ' -f1)
R6=$(grep -ao "[0-9]* never written or superseded-and-torn slots rejected" $T/b6.log | cut -d' ' -f1)
[ -n "$R3" ] && [ "$R6" = "$((R3 + 1))" ] && echo "PASS os/objstore-tamper-rejected (rejected slots $R3 -> $R6)" || echo "FAIL os/objstore-tamper-rejected: '$R3' -> '$R6' $(grep -a PANIC $T/b6.log)"
cp $T/nv.good $T/nv.img
python3 tools/forge-part.py read $T/nv.img hello.txt | grep -q "written by the Forge kernel on its own GPT partition" && echo "PASS os/forgefs-host-read" || echo "FAIL os/forgefs-host-read"
grep -ah "Forge FS: remount" $T/b2.log
[ -n "${FORGEFS_KEEP:-}" ] && cp -a $T /tmp/ffkeep; rm -rf $T
