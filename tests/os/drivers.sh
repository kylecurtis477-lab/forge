#!/bin/bash
# Phase 8.7: NVMe, AHCI, xHCI, e1000 and virtio-net drivers on q35. Both test disks are
# generated here, read through the drivers (LBA 0 hash must match), and must be byte-identical afterwards.
# The xHCI controller has a usb-kbd: the kernel reads its configuration descriptor and configures it as a HID boot
# keyboard (SET_CONFIGURATION, SET_PROTOCOL(boot), SET_IDLE(0)); the expected interface and endpoint are QEMU's.
# HID step 2: Configure Endpoint adds its interrupt IN endpoint, and the host types "forge usb" + Enter through the
# QEMU monitor; the kernel must decode exactly that text from the boot reports.
# Each NIC resolves the user-network gateway 10.0.2.2 by ARP, pings it (ICMP echo, checked reply), then
# serves UDP echo on port 7. Each netdev forwards a free host UDP port to guest port 7, and a host-side
# sender waits for the NIC's "listening" line, then sends a datagram every 0.25 s (at most 60 tries) until
# the same bytes come back. The netdevs are restrict=off: slirp's restricted mode drops every guest UDP
# datagram except DHCP/TFTP, the echo replies to a hostfwd port included (with restrict=on the guest logs
# the echo and the host never receives it). The kernel only sends to the gateway, which slirp maps to the
# host's loopback.
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
read U0 U1 <<PORTS
$(python3 -c 'import socket
s=[socket.socket(socket.AF_INET,socket.SOCK_DGRAM) for _ in "ab"]
[x.bind(("127.0.0.1",0)) for x in s]
print(*[x.getsockname()[1] for x in s])')
PORTS
timeout 120 qemu-system-x86_64 -machine q35 -cpu max -display none -no-reboot -m 256 -smp 4 \
  -serial file:$T/s.log -device isa-debug-exit,iobase=0xf4,iosize=0x04 \
  -drive file=$T/vdisk.img,format=raw,if=none,id=vd,readonly=on -device virtio-blk-pci,drive=vd \
  -boot d -cdrom build/kernel/forge.iso \
  -drive file=$T/nvme.img,format=raw,if=none,id=nv -device nvme,drive=nv,serial=forge-nv0 \
  -drive file=$T/ahci.img,format=raw,if=none,id=ad -device ide-hd,drive=ad,bus=ide.4 \
  -device qemu-xhci,id=xh -device usb-kbd,bus=xh.0 -monitor unix:$T/mon,server,nowait \
  -netdev user,id=n0,restrict=off,hostfwd=udp:127.0.0.1:$U0-:7 -device e1000,netdev=n0 \
  -netdev user,id=n1,restrict=off,hostfwd=udp:127.0.0.1:$U1-:7 -device virtio-net-pci,netdev=n1,disable-modern=on >/dev/null 2>&1 &
Q=$!
python3 - $T/s.log $U0 $U1 > $T/udp.out 2>&1 <<'U' &
import socket, sys, time
log, ports = sys.argv[1], [int(sys.argv[2]), int(sys.argv[3])]
for dev, port in zip(["e1000", "virtio-net"], ports):
    want = ("%s: UDP echo: listening on 10.0.2.15:7" % dev).encode()
    t = time.time() + 110
    while time.time() < t:
        try:
            if want in open(log, "rb").read():
                break
        except OSError:
            pass
        time.sleep(0.1)
    else:
        print("host: %s never listened" % dev)
        continue
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.settimeout(0.25)
    for k in range(60):
        msg = b"forge udp echo via %s, try %d" % (dev.encode(), k + 1)
        s.sendto(msg, ("127.0.0.1", port))
        try:
            got = s.recv(2048)
        except socket.timeout:
            continue
        print("host: %s echoed %d bytes %s on try %d" % (dev, len(got), "identical" if got == msg else "DIFFERENT", k + 1))
        break
    else:
        print("host: %s no echo after 60 tries" % dev)
U
H=$!
# HID step 2: once the kernel polls the keyboard's interrupt endpoint, type "forge usb" + Enter through the
# monitor (sendkey goes to the usb-kbd, QEMU's active keyboard handler since the device was added)
python3 - $T/s.log $T/mon > $T/kbd.out 2>&1 <<'K' &
import socket, sys, time
log, mon = sys.argv[1], sys.argv[2]
want = b"xHCI: HID boot keyboard: waiting for key reports"
t = time.time() + 110
while time.time() < t:
    try:
        if want in open(log, "rb").read():
            break
    except OSError:
        pass
    time.sleep(0.05)
else:
    print("host: keyboard never polled")
    sys.exit(0)
m = socket.socket(socket.AF_UNIX)
m.connect(mon)
for k in "f o r g e spc u s b ret".split():
    m.sendall(b"sendkey " + k.encode() + b"\n")
    time.sleep(0.25)
print("host: typed forge usb + ret on the usb-kbd")
K
KB=$!
for i in $(seq 120); do grep -aq '^Forge sh: builtins' $T/s.log 2>/dev/null && break; sleep 1; done
kill $Q 2>/dev/null; wait $Q 2>/dev/null
wait $H 2>/dev/null
wait $KB 2>/dev/null
S=$T/s.log; ok=1; miss=""
for p in "NVMe: read LBA 0 (4096 bytes), fnv1a64=$(cat $T/nvme.h)" "AHCI: read LBA 0 (4096 bytes), fnv1a64=$(cat $T/ahci.h)" \
  "xHCI: command ring OK" "xHCI: Address Device OK" "xHCI: device descriptor: USB 0200" \
  "xHCI: HID boot keyboard: interface 0, interrupt IN endpoint 0x81, max packet 8" \
  "xHCI: SET_CONFIGURATION 1 OK, SET_PROTOCOL(boot) OK, SET_IDLE(0) OK" \
  "xHCI: Configure Endpoint OK: endpoint 0x81 -> DCI 3, interrupt IN, interval 2^" ", state running; 8 report TRBs queued" \
  ' errors, typed "forge usb" + Enter' \
  "e1000: ARP reply: 10.0.2.2 is at" "virtio-net: ARP reply: 10.0.2.2 is at" \
  "e1000: ICMP echo reply from 10.0.2.2: seq 1, 56 data bytes, checksums ok" \
  "virtio-net: ICMP echo reply from 10.0.2.2: seq 1, 56 data bytes, checksums ok" "out-of-window accesses 0" \
  "e1000: UDP echo: listening on 10.0.2.15:7" "virtio-net: UDP echo: listening on 10.0.2.15:7"; do
  grep -aqF "$p" $S || { ok=0; miss="$miss [$p]"; }
done
for d in e1000 virtio-net; do
  grep -aqE "^$d: UDP echo: [0-9]+ bytes from 10\.0\.2\.2:[0-9]+ echoed back \(IP \+ UDP checksums ok" $S || { ok=0; miss="$miss [$d UDP echo in the log]"; }
  grep -aqE "^host: $d echoed [0-9]+ bytes identical" $T/udp.out || { ok=0; miss="$miss [host: $(grep -a "$d" $T/udp.out)]"; }
done
grep -aq PANIC $S && { ok=0; miss="$miss [panic]"; }
if [ $ok = 1 ]; then echo "PASS os/drivers (NVMe, AHCI, xHCI + HID boot keyboard (configured, key reports read), e1000, virtio-net; ARP + ICMP echo + UDP echo)"; else echo "FAIL os/drivers:$miss"; fi
if (cd $T && sha256sum -c --quiet before.sha); then echo "PASS os/drivers-no-disk-write"; else echo "FAIL os/drivers-no-disk-write"; fi
mkdir -p build/os; grep -a "^host:" $T/udp.out > build/os/drivers-udp.txt
rm -rf $T
