#!/usr/bin/env python3
"""Create the Forge FS partition on a GPT disk image, explicitly.

usage: forge-part.py create DISK SIZE_MIB --yes-add-forge-partition
       forge-part.py read DISK NAME          (print a Forge FS file to stdout)

'create' adds one partition with the Forge type GUID D0C2E3A1-6B7F-4F0A-9E11-466F72676546 in the
largest free gap, rewrites only the primary and backup GPT headers and entry arrays, and zeroes the
first 4 KiB of the new partition (so the kernel will format it). It refuses without the flag, on a
disk without a valid GPT, if a Forge partition already exists, or if no gap is large enough.
No existing partition and no byte outside the GPT structures and the new partition is changed."""
import sys, struct, zlib, uuid
SEC = 512
FORGE = uuid.UUID('D0C2E3A1-6B7F-4F0A-9E11-466F72676546')

def die(m):
    print('forge-part: ' + m, file=sys.stderr); sys.exit(1)

def load(f):
    f.seek(SEC); h = bytearray(f.read(SEC))
    if h[:8] != b'EFI PART': die('no GPT header at LBA 1')
    hs = struct.unpack_from('<I', h, 12)[0]
    c = struct.unpack_from('<I', h, 16)[0]
    t = bytearray(h[:hs]); t[16:20] = b'\0\0\0\0'
    if zlib.crc32(t) != c: die('GPT header CRC mismatch')
    first, last = struct.unpack_from('<QQ', h, 40)
    elba, n, esz, ecrc = struct.unpack_from('<QIII', h, 72)
    if esz != 128: die('unsupported entry size')
    f.seek(elba * SEC); ents = bytearray(f.read(n * esz))
    if zlib.crc32(ents) != ecrc: die('GPT entry CRC mismatch')
    return h, hs, first, last, elba, n, ents

def parts(ents, n):
    for i in range(n):
        e = ents[i * 128:(i + 1) * 128]
        if e[:16] != b'\0' * 16:
            yield i, uuid.UUID(bytes_le=bytes(e[:16])), struct.unpack_from('<QQ', e, 32)

def create(disk, mib):
    with open(disk, 'r+b') as f:
        h, hs, first, last, elba, n, ents = load(f)
        used = sorted(r for _, _, r in parts(ents, n))
        if any(t == FORGE for _, t, _ in parts(ents, n)): die('a Forge partition already exists')
        free = [i for i in range(n) if ents[i * 128:i * 128 + 16] == b'\0' * 16]
        if not free: die('no free GPT entry')
        need = mib * 2048
        gaps, cur = [], first
        for s, e in used:
            if s > cur: gaps.append((cur, s - 1))
            cur = max(cur, e + 1)
        gaps.append((cur, last))
        best = None
        for s, e in gaps:
            s = (s + 2047) // 2048 * 2048
            if e - s + 1 >= need and (best is None or e - s > best[1] - best[0]): best = (s, e)
        if not best: die('no free gap of %d MiB' % mib)
        s, e = best[0], best[0] + need - 1
        i = free[0]
        ents[i * 128:(i + 1) * 128] = FORGE.bytes_le + uuid.uuid4().bytes_le + struct.pack('<QQQ', s, e, 0) + 'Forge FS'.encode('utf-16-le').ljust(72, b'\0')
        ecrc = zlib.crc32(ents)
        f.seek(0, 2); nsec = f.tell() // SEC
        def hdr(my, alt, el):
            x = bytearray(h[:hs])
            struct.pack_into('<QQ', x, 24, my, alt)
            struct.pack_into('<Q', x, 72, el)
            struct.pack_into('<I', x, 88, ecrc)
            x[16:20] = b'\0\0\0\0'
            struct.pack_into('<I', x, 16, zlib.crc32(x))
            return bytes(x).ljust(SEC, b'\0')
        bel = nsec - 1 - (n * 128 + SEC - 1) // SEC
        f.seek(elba * SEC); f.write(ents)
        f.seek(SEC); f.write(hdr(1, nsec - 1, elba))
        f.seek(bel * SEC); f.write(ents)
        f.seek((nsec - 1) * SEC); f.write(hdr(nsec - 1, 1, bel))
        f.seek(s * SEC); f.write(b'\0' * 4096)
        print('forge-part: created Forge FS partition %d: LBA %d..%d (%d MiB)' % (i + 1, s, e, mib))

def read(disk, name):
    with open(disk, 'rb') as f:
        h, hs, first, last, elba, n, ents = load(f)
        p = [r for _, t, r in parts(ents, n) if t == FORGE]
        if len(p) != 1: die('need exactly one Forge partition')
        lo = p[0][0]
        f.seek(lo * SEC); sb = f.read(4096); it = f.read(4096)
        if sb[:8] != b'FORGEFS1': die('not formatted')
        for k in range(64):
            e = it[k * 64:(k + 1) * 64]
            if e[:40].rstrip(b'\0') == name.encode() and e[0]:
                size, st, nb = struct.unpack_from('<QQQ', e, 40)
                f.seek(lo * SEC + st * 4096)
                sys.stdout.buffer.write(f.read(size)); return
        die('no such file')

if len(sys.argv) == 5 and sys.argv[1] == 'create':
    if sys.argv[4] != '--yes-add-forge-partition': die('refusing without --yes-add-forge-partition')
    create(sys.argv[2], int(sys.argv[3]))
elif len(sys.argv) == 4 and sys.argv[1] == 'read':
    read(sys.argv[2], sys.argv[3])
else:
    print(__doc__); sys.exit(2)
