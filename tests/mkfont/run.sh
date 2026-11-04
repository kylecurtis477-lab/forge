#!/bin/sh
# tools/mkfont.forge must be byte-identical to tools/mkfont.py (the reference, PIL/FreeType): the
# same out.fsi and the same stdout preview. Checked against the committed examples/kernel/font.fsi
# (always) and against a live run of the Python tool when PIL is installed, for DejaVu Sans Mono and
# the other fonts the minimal hinter covers. A font using an opcode outside the supported subset must
# fail loudly (exit 1, "unsupported TrueType opcode"), never produce a different font.
cd "$(dirname "$0")/../.." || exit 1
B=build/mkfont; T=$(mktemp -d); trap 'rm -rf $T' EXIT; mkdir -p $B
build/forgec tools/mkfont.forge $B/mk.s && build/fas -o $B/mk.o $B/mk.s && build/fld --base 0x100000000000 -o $B/mkfont $B/mk.o || { echo "FAIL mkfont-port/build"; exit 1; }
fail=0
D=/usr/share/fonts/truetype/dejavu
if [ ! -f $D/DejaVuSansMono.ttf ]; then echo "SKIP mkfont-port (no $D/DejaVuSansMono.ttf)"; exit 0; fi
$B/mkfont $D/DejaVuSansMono.ttf $T/f.fsi > $T/f.out; rc=$?
if [ $rc = 0 ] && cmp -s $T/f.fsi examples/kernel/font.fsi; then echo "PASS mkfont-port/font.fsi: identical to examples/kernel/font.fsi"; else echo "FAIL mkfont-port/font.fsi (rc $rc)"; fail=1; fi
if python3 -c "import PIL" 2>/dev/null; then
  for f in DejaVuSansMono DejaVuSansMono-Bold DejaVuSansMono-Oblique DejaVuSans-Bold; do
    [ -f $D/$f.ttf ] || { echo "SKIP mkfont-port/$f (not installed)"; continue; }
    python3 tools/mkfont.py $D/$f.ttf $T/p.fsi > $T/p.out; prc=$?
    $B/mkfont $D/$f.ttf $T/g.fsi > $T/g.out; frc=$?
    if [ $prc = $frc ] && cmp -s $T/p.fsi $T/g.fsi && cmp -s $T/p.out $T/g.out; then
      echo "PASS mkfont-port/$f: identical .fsi and preview"
    else
      echo "FAIL mkfont-port/$f (rc py=$prc forge=$frc)"; fail=1
    fi
  done
else
  echo "SKIP mkfont-port/live-python (PIL not installed; committed font.fsi checked above)"
fi
if [ -f $D/DejaVuSerif.ttf ]; then
  $B/mkfont $D/DejaVuSerif.ttf $T/u.fsi > /dev/null 2> $T/u.err; urc=$?
  if [ $urc = 1 ] && grep -q "unsupported TrueType opcode" $T/u.err; then echo "PASS mkfont-port/unsupported-font: exit 1, $(cat $T/u.err)"; else echo "FAIL mkfont-port/unsupported-font (rc $urc)"; fail=1; fi
fi
exit $fail
