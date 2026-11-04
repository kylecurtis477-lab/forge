# krel (tools/krel.forge)

`krel KERNEL.ELF OUT.s` builds the image-KASLR self-relocation table from a kernel linked with `ld -q`
(relocations kept). It's a Forge port of `tools/krel.py`. The output is the same byte-for-byte.

Each relocation whose place is in the high half (>= 0xFFFFFFFF80000000) and whose symbol isn't absolute becomes
one `.quad (offset_from_KBASE) | (kind << 56)`. The kinds are: 1 = R_X86_64_64 to the high half (u64 += slide),
2 = R_X86_64_32S to the high half (u32 += slide), and 3 = PC32/PLT32 to the low boot stub (i32 -= slide).
An R_X86_64_32 to the high half, or any other relocation type, is an error (exit 1). Entries are sorted. stdout
is `krel: N relocations`.

The production build doesn't use it: `fld --format kernel` writes `.krel` itself. krel is the reference that
tests/fld/run.sh compares fld against (GNU ld + linker.ld + krel, two passes).

Implementation note: Forge ints are signed 64-bit, so "unsigned >= KBASE" becomes `KBASE <= v < 0`, and hex
output is printed as two 32-bit halves.

Test: `tests/krel/run.sh` builds krel with build/forgec, build/fas and build/fld. On every input it requires the
same OUT.s, stdout and exit code as `tools/krel.py`. The inputs are kernel and kernel-next, a synthetic image
with all three kinds, a low-only image, and an R_X86_64_32 error image.
