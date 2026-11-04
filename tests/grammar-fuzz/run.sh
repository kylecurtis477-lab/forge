#!/bin/sh
# tools/grammar_fuzz.forge must be byte-identical to tools/grammar_fuzz.py (the reference): same
# stdout and exit code, and the same generated build/fuzz tree (every valid/invalid program plus
# the last a.json / b.json / p.forge), for several seeds including 0, a multi-word seed and a
# negative one (Python seeds with abs()).
cd "$(dirname "$0")/../.." || exit 1
B=build/grammar-fuzz; T=$(mktemp -d); trap 'rm -rf $T' EXIT; mkdir -p $B
build/forgec tools/grammar_fuzz.forge $B/gf.s && build/fas -o $B/gf.o $B/gf.s && build/fld -o $B/grammar_fuzz $B/gf.o || { echo "FAIL grammar-fuzz-port/build"; exit 1; }
fail=0
for sn in "1 160" "2 40" "3 40" "0 30" "1099511627783 30" "-5 30" "123456789 1"; do
  set -- $sn
  rm -rf build/fuzz; python3 tools/grammar_fuzz.py --seed $1 --n $2 > $T/py.out; prc=$?; mv build/fuzz $T/py
  rm -rf build/fuzz; $B/grammar_fuzz --seed $1 --n $2 > $T/fg.out; frc=$?; mv build/fuzz $T/fg
  if [ $prc = $frc ] && cmp -s $T/py.out $T/fg.out && diff -r $T/py $T/fg > /dev/null; then
    echo "PASS grammar-fuzz-port/seed $1 n $2: identical output, rc $frc, $(ls $T/fg | wc -l) files: $(cat $T/fg.out)"
  else
    echo "FAIL grammar-fuzz-port/seed $1 n $2 (rc py=$prc forge=$frc)"; fail=1
  fi
  rm -rf $T/py $T/fg
done
# defaults (--seed 1 --n 200 when no flags)
rm -rf build/fuzz; python3 tools/grammar_fuzz.py > $T/py.out; mv build/fuzz $T/py
rm -rf build/fuzz; $B/grammar_fuzz > $T/fg.out; mv build/fuzz $T/fg
if cmp -s $T/py.out $T/fg.out && diff -r $T/py $T/fg > /dev/null; then echo "PASS grammar-fuzz-port/defaults: $(cat $T/fg.out)"; else echo "FAIL grammar-fuzz-port/defaults"; fail=1; fi
exit $fail
