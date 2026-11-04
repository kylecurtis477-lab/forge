#!/bin/sh
# tests/forge-part: tools/forge-part.forge must match tools/forge-part.py byte-for-byte: the resulting disk
# image, stdout, stderr and exit code, for create (seeded GUID via FORGE_GUID_SEED) on several layouts, every
# refusal path, read of files from a formatted partition, and the usage text. Also checks that the unseeded
# Forge build writes a random RFC 4122 v4 GUID while leaving every other byte equal to the seeded image.
cd "$(dirname "$0")/../.." || exit 1
T=$(mktemp -d); trap 'rm -rf $T' EXIT
fail=0
build/forgec tools/forge-part.forge $T/fp.s && build/fas -o $T/fp.o $T/fp.s && build/fld -o $T/fp $T/fp.o || { echo "FAIL forge-part build"; exit 1; }
command -v python3 >/dev/null || { echo "SKIP forge-part (needs python3 for the reference)"; exit 0; }
export FORGE_GUID_SEED=4242
# case name, base image, args...: run both on copies, compare image + stdout + stderr + rc
chk() {
  n=$1; img=$2; shift 2
  cp $img $T/a.img; cp $img $T/b.img
  python3 tools/forge-part.py "$@" >$T/p.out 2>$T/p.err; pr=$?
  set -- $(echo "$@" | sed "s|$T/a.img|$T/b.img|g")
  $T/fp "$@" >$T/f.out 2>$T/f.err; fr=$?
  if [ $pr = $fr ] && cmp -s $T/a.img $T/b.img && cmp -s $T/p.out $T/f.out && cmp -s $T/p.err $T/f.err; then
    echo "PASS forge-part $n (rc=$fr)"
  else echo "FAIL forge-part $n (rc py=$pr forge=$fr)"; fail=1; fi
}
# disks: mkgpt-style ESP+data+linux (seeded) at 64 and 128 MiB; a full-disk-ish one; a corrupted one
head -c 1048576 /dev/zero | tr '\0' 'E' >$T/esp.fat
python3 examples/efi/mkgpt.py $T/d64.img $T/esp.fat 64 && python3 examples/efi/mkgpt.py $T/d128.img $T/esp.fat 128
python3 examples/efi/mkgpt.py $T/d40.img $T/esp.fat 40
cp $T/d64.img $T/bad.img; printf 'X' | dd of=$T/bad.img bs=1 seek=600 conv=notrunc 2>/dev/null
cp $T/d64.img $T/badent.img; printf 'X' | dd of=$T/badent.img bs=1 seek=1100 conv=notrunc 2>/dev/null
head -c 4194304 /dev/zero >$T/nogpt.img
chk create-64-16 $T/d64.img create $T/a.img 16 --yes-add-forge-partition
chk create-128-32 $T/d128.img create $T/a.img 32 --yes-add-forge-partition
chk create-128-1 $T/d128.img create $T/a.img 1 --yes-add-forge-partition
FORGE_GUID_SEED=18446744073709551615 chk seed-max $T/d128.img create $T/a.img 4 --yes-add-forge-partition
chk no-gap $T/d40.img create $T/a.img 64 --yes-add-forge-partition
chk needs-flag $T/d64.img create $T/a.img 16 --yes
chk bad-header-crc $T/bad.img create $T/a.img 16 --yes-add-forge-partition
chk bad-entry-crc $T/badent.img create $T/a.img 16 --yes-add-forge-partition
chk no-gpt $T/nogpt.img create $T/a.img 16 --yes-add-forge-partition
python3 tools/forge-part.py create $T/d64.img 16 --yes-add-forge-partition >/dev/null
chk already-exists $T/d64.img create $T/a.img 8 --yes-add-forge-partition
chk read-unformatted $T/d64.img read $T/a.img hello.txt
chk read-no-forge $T/d128.img read $T/a.img hello.txt
# format the Forge partition like the kernel: superblock FORGEFS1, 64-byte entries (name[40], size, start, nblocks)
python3 - $T/d64.img <<'P'
import struct, sys
f = open(sys.argv[1], 'r+b'); lo = None
d = open(sys.argv[1], 'rb').read()
ents = d[1024:1024 + 128 * 128]
for i in range(128):
    e = ents[i * 128:(i + 1) * 128]
    if e[:4] == bytes.fromhex('a1e3c2d0'): lo = struct.unpack_from('<Q', e, 32)[0]
f.seek(lo * 512); f.write(b'FORGEFS1' + b'\0' * 4088)
it = b''
files = [(b'hello.txt', b'written by the Forge kernel\n', 2), (b'bin.dat', bytes(range(256)) * 20, 3), (b'empty', b'', 5)]
for name, data, st in files:
    it += name.ljust(40, b'\0') + struct.pack('<QQQ', len(data), st, (len(data) + 4095) // 4096)
    f.seek(lo * 512 + st * 4096); f.write(data)
it += b'\0hidden'.ljust(40, b'\0') + struct.pack('<QQQ', 4, 2, 1)
f.seek(lo * 512 + 4096); f.write(it)
P
chk read-text $T/d64.img read $T/a.img hello.txt
chk read-binary $T/d64.img read $T/a.img bin.dat
chk read-empty $T/d64.img read $T/a.img empty
chk read-missing $T/d64.img read $T/a.img nope
chk read-deleted-entry $T/d64.img read $T/a.img hidden
chk usage $T/d64.img
chk usage-bad $T/d64.img frob $T/a.img
# unseeded: a random v4 GUID at the new entry's bytes 16..31, all else equal to the seeded image
cp $T/d128.img $T/s.img; cp $T/d128.img $T/r.img
$T/fp create $T/s.img 16 --yes-add-forge-partition >/dev/null
env -u FORGE_GUID_SEED $T/fp create $T/r.img 16 --yes-add-forge-partition >/dev/null
python3 - $T/s.img $T/r.img <<'P' && echo "PASS forge-part random-guid" || { echo "FAIL forge-part random-guid"; exit 1; }
import sys, uuid
a, b = open(sys.argv[1], 'rb').read(), open(sys.argv[2], 'rb').read()
diff = [i for i in range(len(a)) if a[i] != b[i]] if len(a) == len(b) else None
assert diff, 'no difference'
o = 1024 + 3 * 128 + 16  # entry 4 (index 3) unique GUID in the primary array
u = uuid.UUID(bytes_le=b[o:o + 16]); assert u.version == 4 and u.variant == uuid.RFC_4122
crcs = {528, 529, 530, 531, 600, 601, 602, 603}  # header CRC + entry CRC fields, primary
allowed = set(range(o, o + 16)) | crcs
n = len(a) // 512; bel = n - 33
allowed |= set(range(bel * 512 + 3 * 128 + 16, bel * 512 + 3 * 128 + 32))
allowed |= {(n - 1) * 512 + k for k in (16, 17, 18, 19, 88, 89, 90, 91)}
bad = [i for i in diff if i not in allowed]
assert not bad, bad[:5]
P
exit $fail
