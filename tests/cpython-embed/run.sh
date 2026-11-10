#!/bin/sh
# tests/cpython-embed: Python calls into Forge @export functions through the
# 11.5(a) embed API (tools/cpython/embed.fsi, written in Forge) and the 11.2 C ABI.
# Builds the pinned CPython if needed (warm when the prefix is current), compiles
# api.forge and main.forge with forgec/fas, and links with gcc -no-pie against
# prefix/lib/libpython3.13.a and the box libc (not fld). SKIP if there is no
# tarball and no usable prefix.
cd "$(dirname "$0")/../.." || exit 1
S=${CP_SRC:-/workspace/cpython}
W=${CP_WORK:-$S/work}
P=${CP_PREFIX:-$W/prefix}
if [ ! -f $P/include/python3.13/Python.h ] || [ ! -f $P/lib/libpython3.13.a ]; then
  if [ ! -f $S/Python-3.13.5.tar.xz ]; then
    echo "SKIP cpython-embed (no Python-3.13.5.tar.xz in $S and no prefix at $P)"
    exit 0
  fi
fi
if [ -f $S/Python-3.13.5.tar.xz ]; then
  ./forge tools/cpython/build.forge -o build/cpython-build >/dev/null || { echo "FAIL cpython-embed build-tool"; exit 1; }
  CP_SRC=$S CP_WORK=$W build/cpython-build >/dev/null || { echo "FAIL cpython-embed cpython-build"; exit 1; }
  P=$W/prefix
fi
[ -f $P/include/python3.13/Python.h ] && [ -f $P/lib/libpython3.13.a ] || { echo "FAIL cpython-embed missing Python.h or libpython3.13.a at $P"; exit 1; }

T=$(mktemp -d); trap 'rm -rf $T' EXIT
fail=0
build/forgec tests/cpython-embed/api.forge $T/api.s $FORGEC_FLAGS && build/fas -o $T/api.o $T/api.s \
  || { echo "FAIL cpython-embed compile-api"; exit 1; }
build/forgec tests/cpython-embed/main.forge $T/main.s $FORGEC_FLAGS && build/fas -o $T/main.o $T/main.s \
  || { echo "FAIL cpython-embed compile-main"; exit 1; }
gcc -no-pie -o $T/embed $T/main.o $T/api.o $P/lib/libpython3.13.a -ldl -lm -lpthread \
  || { echo "FAIL cpython-embed link"; exit 1; }
CP_PREFIX=$P $T/embed >$T/o 2>$T/e; rc=$?
if [ $rc = 0 ] \
   && grep -q 'layout sizeof(PyConfig)=448' $T/o \
   && grep -q 'layout offsetof(home)=296' $T/o
then echo "PASS cpython-embed/layout"; else echo "FAIL cpython-embed/layout (rc $rc)"; cat $T/o $T/e; fail=1; fi
if [ $rc = 0 ] \
   && grep -q 'python-to-forge add(20,22)=42' $T/o \
   && grep -q 'python-to-forge mul(6,7)=42' $T/o \
   && grep -q 'python-to-forge neg(42)=-42' $T/o \
   && grep -q 'python-to-forge answer()=42' $T/o \
   && grep -q 'python-to-forge sum_to(10)=55' $T/o
then echo "PASS cpython-embed/python-to-forge"; else echo "FAIL cpython-embed/python-to-forge (rc $rc)"; cat $T/o $T/e; fail=1; fi
exit $fail
