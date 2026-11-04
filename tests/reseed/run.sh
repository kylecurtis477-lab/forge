#!/bin/sh
# tests/reseed: tools/reseed.forge must do exactly what tools/reseed.sh (kept as the reference) does, in two
# scratch repo skeletons (build/{forgec,fas,fld,stage2.s,stage4.s}, seed/): same stdout, stderr, exit code
# and resulting seed/ files (bytes and modes) for a green stage2 == stage4, a byte difference, and each
# file being a prefix of the other (cmp's "EOF on ... line" / "in line" forms). Plus a missing-input check.
cd "$(dirname "$0")/../.." || exit 1
T=$(mktemp -d); trap 'rm -rf $T' EXIT
fail=0
./forge tools/reseed.forge -o $T/reseed >/dev/null || { echo "FAIL reseed build"; exit 1; }
mk() { # dir
  mkdir -p $1/tools $1/build $1/seed
  cp build/forgec build/fas build/fld build/stage2.s $1/build/
  cp tools/reseed.sh $T/reseed $1/tools/
}
run() { # name stage4-maker
  rm -rf $T/a $T/b; mk $T/a; mk $T/b
  for d in $T/a $T/b; do (cd $d/build && eval "$2"); done
  (cd / && $T/a/tools/reseed.sh >$T/a.out 2>$T/a.err); ra=$?
  (cd / && $T/b/tools/reseed >$T/b.out 2>$T/b.err); rb=$?
  sed -i 's/^cmp: //' $T/a.err $T/b.err
  if [ $ra = $rb ] && cmp -s $T/a.out $T/b.out && cmp -s $T/a.err $T/b.err && diff -r $T/a/seed $T/b/seed >/dev/null &&
     [ "$(cd $T/a/seed && stat -c '%n %a' * 2>/dev/null)" = "$(cd $T/b/seed && stat -c '%n %a' * 2>/dev/null)" ]; then
    echo "PASS reseed $1 (rc $rb)"
  else
    echo "FAIL reseed $1"; fail=1
  fi
}
run green 'cp stage2.s stage4.s'
[ "$(sha256sum < build/fas | cut -d" " -f1)  fas-seed" = "$(grep fas-seed $T/b/seed/seed.sha256)" ] && echo "PASS reseed sha256-vs-sha256sum" || { echo "FAIL reseed sha256-vs-sha256sum"; fail=1; }
run byte-diff 'cp stage2.s stage4.s; printf X | dd of=stage4.s bs=1 seek=5000 conv=notrunc 2>/dev/null'
run short-in-line 'head -c 100 stage2.s > stage4.s'
run short-line 'head -n 20 stage2.s > stage4.s'
run long 'cp stage2.s stage4.s; echo >> stage4.s'
rm -rf $T/c; mk $T/c; (cd $T/c && rm build/fas && cp build/stage2.s build/stage4.s && tools/reseed 2>$T/e); r=$?
[ $r = 1 ] && grep -q "reseed: cannot read build/fas" $T/e && echo "PASS reseed missing-input" || { echo "FAIL reseed missing-input"; fail=1; }
exit $fail
