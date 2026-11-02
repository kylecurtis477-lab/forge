#!/usr/bin/env python3
# krel.py KERNEL.ELF OUT.s -- build the kernel's self-relocation table for image KASLR.
# Reference implementation only: the build uses tools/fld --format kernel; tests/fld/run.sh checks they agree.
# Input: a kernel linked with `ld -q` (relocations kept). Every place in the high half (-2 GiB) image that
# encodes an absolute high-half address, or a PC-relative reference from the high half to the low boot stub,
# becomes one u64 entry: (offset from KBASE) | (kind << 56). At boot the kernel adds its slide s:
#   kind 1: u64 += s (R_X86_64_64)   kind 2: u32 += s (R_X86_64_32S)   kind 3: i32 -= s (PC32/PLT32 to low)
import struct, sys
KBASE = 0xFFFFFFFF80000000
d = open(sys.argv[1], 'rb').read()
shoff, = struct.unpack_from('<Q', d, 0x28)
shentsize, shnum = struct.unpack_from('<HH', d, 0x3A)
sh = [struct.unpack_from('<IIQQQQIIQQ', d, shoff + i * shentsize) for i in range(shnum)]
ents = []
bad = 0
for s in sh:
    if s[1] != 4:  # SHT_RELA
        continue
    tgt = sh[s[7]]
    if not (tgt[2] & 2):  # target not SHF_ALLOC
        continue
    symtab = sh[s[6]]
    for off in range(s[4], s[4] + s[5], 24):
        r_off, info, add = struct.unpack_from('<QQq', d, off)
        typ, si = info & 0xFFFFFFFF, info >> 32
        name, inf, oth, shndx, val, sz = struct.unpack_from('<IBBHQQ', d, symtab[4] + si * 24)
        loc = r_off  # ET_EXEC: r_offset is the virtual address
        if loc < KBASE or shndx == 0xFFF1:
            continue  # boot stub runs unslid; absolute symbols never move
        t = (val + add) & 0xFFFFFFFFFFFFFFFF
        if typ == 1:
            if t >= KBASE: ents.append((loc - KBASE) | 1 << 56)
        elif typ == 11:
            if t >= KBASE: ents.append((loc - KBASE) | 2 << 56)
        elif typ in (2, 4):
            if t < KBASE: ents.append((loc - KBASE) | 3 << 56)
        elif typ == 10:
            if t >= KBASE: print('krel: R_X86_64_32 to high half at %x' % loc); bad += 1
        else:
            print('krel: unsupported relocation type %d at %x' % (typ, loc)); bad += 1
if bad:
    sys.exit(1)
ents.sort()
with open(sys.argv[2], 'w') as f:
    f.write('.section .krel,"a"\n.p2align 3\n')
    for e in ents:
        f.write('.quad 0x%x\n' % e)
print('krel: %d relocations' % len(ents))
