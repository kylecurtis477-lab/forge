#!/bin/sh
# tests/bzip2: tools/busybox/bzip2.forge (built to build/bzip2; what tools/busybox/build.forge puts on the
# BusyBox build's PATH for tar -xjf) must be byte-identical to the tools/busybox/bzip2 shim (Python's bz2,
# kept as the reference): compress at -9 and decompress `-d`, same stdout and exit code, and on bad input
# the same last stderr line (the Python exception). Inputs cover empty and tiny data, RLE1 run edges
# (4, 5, 255, 259), periodic blocks (origPtr tie order; mainSort's budget running out into fallbackSort),
# small blocks (< 10000: fallbackSort only), random and skewed data (2..6 Huffman tables), a 1 MB+ input
# (two blocks, a run carried over), concatenated streams, trailing garbage, truncation and corruption,
# and the BusyBox tarball when $BB_SRC (default /workspace/bb) has it.
cd "$(dirname "$0")/../.." || exit 1
T=$(mktemp -d); trap 'rm -rf $T' EXIT
fail=0
./forge tools/busybox/bzip2.forge -o build/bzip2 >/dev/null || { echo "FAIL bzip2 build"; exit 1; }
python3 - "$T" <<'PY' || { echo "FAIL bzip2 inputs"; exit 1; }
import bz2, random, sys
t = sys.argv[1]
r = random.Random(5)
def rb(n): return bytes(r.randrange(256) for _ in range(n))
c = {"empty": b"", "one": b"a", "ab": b"ab", "run4": b"aaaa", "run5": b"aaaaa", "run1000": b"a" * 1000,
     "run-edges": b"q" * 259 + b"r" * 4 + b"s" * 255 + b"t" * 256, "hello": b"hello world\n" * 50,
     "periodic3": b"abc" * 5000, "periodic3-big": b"abc" * 20000, "periodic7-2blk": b"xyzzy12" * 200000,
     "zeros-1m": bytes(1000000), "random1k": rb(1000), "random20k": rb(20000), "random-1m": rb(1000000),
     "allbytes": bytes(range(256)) * 40, "text": open("tools/busybox/bzip2.forge", "rb").read() * 20,
     "runs": b"".join(bytes([r.randrange(4)]) * r.randrange(1, 300) for _ in range(20000)),
     "skewed": bytes(r.choice(b"aaaaaaaabbbbccd\n") for _ in range(300000))}
for k, v in c.items():
    open(t + "/c." + k, "wb").write(v)
a = bz2.compress(b"one\n"); b = bz2.compress(b"two\n" * 99); x = bytearray(b); x[30] ^= 4
d = {"two-streams": a + b, "trailing-garbage": a + b"garbage", "second-truncated": a + b[:20],
     "truncated": a[:-3], "corrupt": bytes(x), "not-bzip2": b"xyz", "trailing-header": a + b"BZh9"}
for k, v in d.items():
    open(t + "/d." + k, "wb").write(v)
PY
S=${BB_SRC:-/workspace/bb}
[ -f $S/busybox-1.38.0.tar.bz2 ] && ln -s $S/busybox-1.38.0.tar.bz2 $T/d.busybox-tarball
# chk name args input
chk() {
  python3 tools/busybox/bzip2 $2 <$3 >$T/s.o 2>$T/s.e; rs=$?
  build/bzip2 $2 <$3 >$T/f.o 2>$T/f.e; rf=$?
  if [ $rs = $rf ] && cmp -s $T/s.o $T/f.o && [ "$(tail -1 $T/s.e)" = "$(tail -1 $T/f.e)" ]
  then echo "PASS bzip2/$1"; else echo "FAIL bzip2/$1 (rc $rs/$rf)"; fail=1; fi
}
for f in $T/c.*; do
  n=${f#$T/c.}
  chk compress-$n "" $f
  python3 tools/busybox/bzip2 <$f >$T/z
  chk decompress-$n -d $T/z
done
for f in $T/d.*; do chk decompress-${f#$T/d.} -d $f; done
python3 tools/busybox/bzip2 -d <$T/d.busybox-tarball >$T/bb.tar 2>/dev/null && chk compress-busybox-tar "" $T/bb.tar
exit $fail
