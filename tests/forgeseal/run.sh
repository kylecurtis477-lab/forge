#!/bin/sh
# tests/forgeseal: tools/forgeseal.forge must seal byte-identically to tools/forgeseal.py (the reference):
# same sealed image, stdout, stderr and exit status for v3 (default), --extra-key (two slots, and an
# empty key file = no extra slot), --v2, --alg-id, --proof-byte, --module, caps lines, an EI_OSABI = 3
# image, --foreign, --pub (seeds of several lengths) and the error paths with a Python message
# (already sealed, not in the baseline, unknown capability, unknown option, verifier rejection,
# --foreign on a non-Linux ELF). Unreadable inputs: Python dies with a traceback, Forge with
# "forgeseal: PATH: cannot read ..."; both must exit 1.
cd "$(dirname "$0")/../.." || exit 1
T=$(mktemp -d); trap 'rm -rf $T' EXIT
S=build/forgeseal.t   # must sit directly in build/: the repo root is dirname(dirname(/proc/self/exe))
build/forgec tools/forgeseal.forge $T/s.s && build/fas -o $T/s.o $T/s.s && build/fld --base 0x100000000000 -o $S $T/s.o || { echo "FAIL forgeseal build"; exit 1; }
trap 'rm -rf $T $S' EXIT
fail=0
BL=tests/unsafe/baseline.txt
python3 -c "import random; r=random.Random(7); [open('$T/k%d'%n,'wb').write(bytes(r.randrange(256) for _ in range(n))) for n in (0,1,32,100)]"
mkdir -p $T/u
for p in hello cat captest lxtest; do
  build/forgec examples/user/$p.forge $T/u/$p.s && build/fas -o $T/u/$p.o $T/u/$p.s && build/fld -o $T/u/$p.raw $T/u/$p.o || { echo "FAIL forgeseal build $p"; fail=1; }
done
python3 -c "import sys; d=bytearray(open(sys.argv[1],'rb').read()); d[7]=3; open(sys.argv[2],'wb').write(d)" $T/u/lxtest.raw $T/u/lxtest.lx
build/forgec examples/modules/hello.forge $T/u/mod.s && build/fas -o $T/u/mod.raw $T/u/mod.s || fail=1
printf 'print(1)\n' > $T/plain.forge
# scratch copies of hello with other caps lines (the user .fsi files alongside, for its include)
cp examples/user/*.fsi $T/
{ printf '# forge: caps(fs, bogus)\n'; grep -v 'caps()' examples/user/hello.forge; } > $T/badcap.forge
{ printf '#forge:  caps( ipc ,, grant)  # note\n# forge: caps()\n#   forge:caps(spawn)\n'; grep -v 'caps()' examples/user/hello.forge; } > $T/caps.forge
printf 'def f(:\n' > $T/syntax.forge
: > $T/empty.txt
# an audited baseline for the scratch sources (relpath from the repo root, as forgeseal computes it)
R=$(python3 -c "import os,sys; print(os.path.relpath(sys.argv[1], os.getcwd()))" $T)
for f in caps badcap; do echo "$R $f.forge 999 999 999 999"; done > $T/bl.txt
printf '# comment\n%s caps.forge 1 2\n' "$R" >> $T/bl.txt
# chk NAME IMAGE-OR-- ARGS...: run both on fresh copies of IMAGE (named alike), compare everything
chk() {
  n=$1; img=$2; shift 2
  rm -rf $T/p $T/f; mkdir $T/p $T/f
  if [ "$img" != - ]; then cp $img $T/p/img; cp $img $T/f/img; fi
  a=$(echo "$*" | sed "s|@|$T/p/img|g"); b=$(echo "$*" | sed "s|@|$T/f/img|g")
  python3 tools/forgeseal.py $a >$T/p.out 2>$T/p.err; pr=$?
  $S $b >$T/f.out 2>$T/f.err; fr=$?
  sed -i "s|$T/p/|X/|g" $T/p.err $T/p.out; sed -i "s|$T/f/|X/|g" $T/f.err $T/f.out
  if [ $pr = $fr ] && cmp -s $T/p.out $T/f.out && cmp -s $T/p.err $T/f.err && { [ "$img" = - ] || cmp -s $T/p/img $T/f/img; }; then
    echo "PASS forgeseal $n (rc=$fr)"
  else echo "FAIL forgeseal $n (rc py=$pr forge=$fr)"; head -3 $T/p.err $T/f.err; fail=1; fi
}
U=examples/user
chk "v3 hello" $T/u/hello.raw $U/hello.forge @ $T/k32 $BL
chk "v3 cat --extra-key" $T/u/cat.raw --extra-key $T/k100 $U/cat.forge @ $T/k32 $BL
chk "empty --extra-key" $T/u/cat.raw --extra-key $T/k0 $U/cat.forge @ $T/k32 $BL
chk "v3 captest caps" $T/u/captest.raw $U/captest.forge @ $T/k1 $BL
chk "--v2" $T/u/hello.raw --v2 $U/hello.forge @ $T/k32 $BL
chk "--alg-id 2" $T/u/hello.raw --alg-id 2 $U/hello.forge @ $T/k32 $BL
chk "--proof-byte" $T/u/hello.raw --proof-byte $U/hello.forge @ $T/k32 $BL
chk "all options" $T/u/hello.raw --module --proof-byte --alg-id 7 --extra-key $T/k1 $U/hello.forge @ $T/k100 $BL
chk "--v2 --module" $T/u/mod.raw --v2 --module examples/modules/hello.forge @ $T/k32 $BL
chk "--module" $T/u/mod.raw --module examples/modules/hello.forge @ $T/k32 $BL
chk "EI_OSABI=3 lxtest" $T/u/lxtest.lx $U/lxtest.forge @ $T/k32 $BL
chk "caps parse, own baseline" $T/u/hello.raw $T/caps.forge @ $T/k32 $T/bl.txt
chk "relative src" $T/u/hello.raw ./examples/user/../user/hello.forge @ $T/k32 $BL
chk "--foreign" $T/u/lxtest.lx --foreign @ $T/k32
for k in 0 1 32 100; do chk "--pub ${k}B seed" - --pub $T/k$k; done
# error paths
python3 tools/forgeseal.py $U/hello.forge $T/u/hello.raw.s $T/k32 $BL >/dev/null 2>&1
cp $T/u/hello.raw $T/sealed; python3 tools/forgeseal.py $U/hello.forge $T/sealed $T/k32 $BL >/dev/null
cp $T/u/hello.raw $T/sealed2; python3 tools/forgeseal.py --v2 $U/hello.forge $T/sealed2 $T/k32 $BL >/dev/null
chk "already sealed v3" $T/sealed $U/hello.forge @ $T/k32 $BL
chk "already sealed v2" $T/sealed2 $U/hello.forge @ $T/k32 $BL
chk "foreign already sealed" $T/sealed --foreign @ $T/k32
chk "foreign not Linux ABI" $T/u/hello.raw --foreign @ $T/k32
chk "not in baseline" $T/u/hello.raw $U/hello.forge @ $T/k32 $T/empty.txt
chk "not in baseline (scratch)" $T/u/hello.raw $T/plain.forge @ $T/k32 $BL
chk "unknown capability" $T/u/hello.raw $T/badcap.forge @ $T/k32 $T/bl.txt
sed -i "1s/.*/# forge: caps(fs, ipc) x)/" $T/badcap.forge
chk "greedy caps group" $T/u/hello.raw $T/badcap.forge @ $T/k32 $T/bl.txt
chk "unknown option" $T/u/hello.raw --bogus $U/hello.forge @ $T/k32 $BL
chk "verifier rejected" $T/u/hello.raw $T/syntax.forge @ $T/k32 $BL
for c in "$U/hello.forge $T/nope $T/k32 $BL" "$U/hello.forge $T/u/hello.raw $T/nope $BL" "$U/hello.forge $T/u/hello.raw $T/k32 $T/nope" "--foreign $T/nope $T/k32" "--pub $T/nope"; do
  cp $T/u/hello.raw $T/u/h.bak
  $S $c >/dev/null 2>$T/e; r=$?
  cp $T/u/h.bak $T/u/hello.raw
  if [ $r = 1 ] && grep -q "^forgeseal: .*cannot read" $T/e; then echo "PASS forgeseal missing input ($c)"; else echo "FAIL forgeseal missing input ($c) rc=$r"; fail=1; fi
done
exit $fail
