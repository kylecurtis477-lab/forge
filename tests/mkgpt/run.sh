#!/bin/sh
# tests/mkgpt: examples/efi/mkgpt.forge must write the same disk image as examples/efi/mkgpt.py
# (FORGE_GUID_SEED fixes the GUIDs) for several sizes and ESP images, with the same exit code; unseeded,
# the Forge build must produce a valid GPT (header/entry CRCs) with random RFC 4122 v4 GUIDs.
cd "$(dirname "$0")/../.." || exit 1
T=$(mktemp -d); trap 'rm -rf $T' EXIT
fail=0
build/forgec examples/efi/mkgpt.forge $T/m.s && build/fas -o $T/m.o $T/m.s && build/fld -o $T/mkgpt $T/m.o || { echo "FAIL mkgpt build"; exit 1; }
command -v python3 >/dev/null || { echo "SKIP mkgpt (needs python3 for the reference)"; exit 0; }
chk() { # name esp [size]
  rm -f $T/p.img $T/f.img
  FORGE_GUID_SEED=${SEED:-31337} python3 examples/efi/mkgpt.py $T/p.img "$2" $3 >/dev/null 2>&1; pr=$?
  FORGE_GUID_SEED=${SEED:-31337} $T/mkgpt $T/f.img "$2" $3 >/dev/null 2>&1; fr=$?
  if [ $pr = $fr ] && { [ $pr != 0 ] || cmp -s $T/p.img $T/f.img; }; then echo "PASS mkgpt $1 (rc=$fr$( [ $fr = 0 ] && echo ", $(wc -c <$T/f.img) bytes identical"))"
  else echo "FAIL mkgpt $1 (rc py=$pr forge=$fr)"; fail=1; fi
}
head -c 3000000 /dev/urandom >$T/rand.fat; head -c 1 /dev/urandom >$T/one.fat; : >$T/empty.fat
head -c 1048576 /dev/urandom >$T/exact.fat; head -c 1048577 /dev/zero >$T/plus1.fat
head -c 67108864 /dev/urandom >$T/big.fat
chk default-128 $T/rand.fat
chk size-64 $T/rand.fat 64
chk size-40 $T/rand.fat 40
chk esp-1-byte $T/one.fat 64
chk esp-empty $T/empty.fat 64
chk esp-1MiB-exact $T/exact.fat 64
chk esp-1MiB-plus-1 $T/plus1.fat 64
SEED=0 chk seed-0 $T/rand.fat 64
SEED=18446744073709551615 chk seed-max $T/rand.fat 64
chk esp-64MiB-256 $T/big.fat 256
chk does-not-fit $T/big.fat 64
# unseeded: valid GPT, random v4 GUIDs (4 distinct)
env -u FORGE_GUID_SEED $T/mkgpt $T/r.img $T/rand.fat 64 && python3 - $T/r.img <<'P' && echo "PASS mkgpt random-guids" || { echo "FAIL mkgpt random-guids"; fail=1; }
import sys, struct, zlib, uuid
d = open(sys.argv[1], 'rb').read(); n = len(d) // 512
ents = d[1024:1024 + 16384]
gs = []
for lba in (1, n - 1):
    h = bytearray(d[lba * 512:lba * 512 + 92]); c = struct.unpack_from('<I', h, 16)[0]; h[16:20] = b'\0' * 4
    assert zlib.crc32(h) == c and struct.unpack_from('<I', h, 88)[0] == zlib.crc32(ents)
    gs.append(bytes(h[56:72]))
assert gs[0] == gs[1] and d[(n - 33) * 512:(n - 1) * 512] == ents
gs = gs[:1] + [ents[i * 128 + 16:i * 128 + 32] for i in range(3)]
for g in gs:
    u = uuid.UUID(bytes_le=g); assert u.version == 4 and u.variant == uuid.RFC_4122
assert len(set(gs)) == 4
P
exit $fail
