# pecheck.py IMAGE.efi: structural checks on the fld PE32+ image (what UEFI firmware relies on)
import struct, sys
d = open(sys.argv[1], 'rb').read()
assert d[:2] == b'MZ'
pe, = struct.unpack_from('<I', d, 0x3c)
assert d[pe:pe + 4] == b'PE\0\0'
mach, nsec, _, _, _, optsz, ch = struct.unpack_from('<HHIIIHH', d, pe + 4)
o = pe + 24
magic, = struct.unpack_from('<H', d, o)
entry, = struct.unpack_from('<I', d, o + 16)
base, salign, falign = struct.unpack_from('<QII', d, o + 24)
imgsz, hdrsz = struct.unpack_from('<II', d, o + 56)
subsys, dllch = struct.unpack_from('<HH', d, o + 68)
assert mach == 0x8664 and magic == 0x20b and subsys == 10 and base == 0x10000000
assert salign == 0x1000 and falign == 0x200 and dllch & 0x100, 'NX_COMPAT'
sh = o + optsz
ok_entry = False
for i in range(nsec):
    name, vs, va, raw, ptr, _, _, _, _, c = struct.unpack_from('<8sIIIIIIHHI', d, sh + 40 * i)
    assert not (c & 0x20000000 and c & 0x80000000), 'W^X: section both writable and executable'
    assert va % salign == 0 and ptr % falign == 0 and va + max(vs, raw) <= imgsz
    if c & 0x20000000 and va <= entry < va + vs:
        ok_entry = True
assert ok_entry, 'entry point not in an executable section'
print("PASS fld/pe-structure (%d sections, entry 0x%x)" % (nsec, entry))
