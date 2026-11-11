#!/bin/sh
# tests/cpython-embed: Python calls into Forge @export functions, and Forge calls
# back into Python (eval, object calls, and a Python function that calls Forge),
# through the 11.5(a) embed API (tools/cpython/embed.fsi, written in Forge) and
# the 11.2 C ABI. Also: tools/cpython/pylayout.fsi must match what tools/cpython/pylayout.forge
# generates from the pinned headers (and the generator refuses non-pinned, free-threaded and debug
# header sets), and the libpython guard refuses non-pinned and free-threaded Py_GetVersion strings.
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
# pylayout: the checked-in tools/cpython/pylayout.fsi must be exactly what the generator writes from
# the pinned headers; non-pinned, free-threaded and debug header sets are refused with no output.
./forge tools/cpython/pylayout.forge -o build/pylayout >/dev/null || { echo "FAIL cpython-embed pylayout build"; exit 1; }
I=$P/include/python3.13
build/pylayout $I $T/gen.fsi 2>$T/ge; rc=$?
if [ $rc = 0 ] && cmp -s $T/gen.fsi tools/cpython/pylayout.fsi && grep -q '^PYCONFIG_SIZE = 448$' $T/gen.fsi \
   && grep -q '^PYCONFIG_OFF_HOME = 296$' $T/gen.fsi && grep -q '^PYSTATUS_SIZE = 32$' $T/gen.fsi && grep -q '^PYMETHODDEF_SIZE = 32$' $T/gen.fsi
then echo "PASS cpython-embed/pylayout-generated"; else echo "FAIL cpython-embed/pylayout-generated (rc $rc; regenerate: build/pylayout $I tools/cpython/pylayout.fsi)"; cat $T/ge; diff $T/gen.fsi tools/cpython/pylayout.fsi | head -20; fail=1; fi
refuse() { # name rc pattern -- run pylayout on $T/inc (a modified copy of the pinned headers)
  rm -f $T/bad.fsi; build/pylayout $T/inc $T/bad.fsi >$T/ro 2>$T/re; r=$?
  if [ $r = $2 ] && [ ! -e $T/bad.fsi ] && grep -q "$3" $T/re
  then echo "PASS cpython-embed/pylayout-$1"; else echo "FAIL cpython-embed/pylayout-$1 (rc $r, want $2)"; cat $T/re; fail=1; fi
}
mkinc() { rm -rf $T/inc; mkdir -p $T/inc/cpython; cp $I/patchlevel.h $I/pyconfig.h $I/methodobject.h $T/inc/; cp $I/cpython/initconfig.h $T/inc/cpython/; }
mkinc; sed -i 's/^#define PY_VERSION .*/#define PY_VERSION              "3.13.4"/' $T/inc/patchlevel.h
refuse not-pinned 3 'headers are CPython 3.13.4, not the pinned 3.13.5'
mkinc; sed -i 's|^/\* #undef Py_GIL_DISABLED \*/|#define Py_GIL_DISABLED 1|' $T/inc/pyconfig.h
refuse free-threaded 4 'free-threaded CPython (Py_GIL_DISABLED) is not supported'
mkinc; printf '#define Py_DEBUG 1\n' >> $T/inc/pyconfig.h
refuse debug 5 'debug CPython (Py_DEBUG) is not supported'
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
   && grep -q 'libpython pinned release build: ok (kind 0)' $T/o \
   && grep -q 'libpython 3.13.4 refused (kind 1)' $T/o \
   && grep -q 'libpython 3.13.50 refused (kind 1)' $T/o \
   && grep -q 'libpython free-threaded refused (kind 2)' $T/o \
   && grep -q 'linked libpython is 3.13.4 (main.*not the pinned 3.13.5; PyConfig would not match' $T/e \
   && grep -q 'linked libpython is a free-threaded build (3.13.5 experimental' $T/e
then echo "PASS cpython-embed/libpython-guard"; else echo "FAIL cpython-embed/libpython-guard (rc $rc)"; cat $T/o $T/e; fail=1; fi
if [ $rc = 0 ] \
   && grep -q 'python-to-forge add(20,22)=42' $T/o \
   && grep -q 'python-to-forge mul(6,7)=42' $T/o \
   && grep -q 'python-to-forge neg(42)=-42' $T/o \
   && grep -q 'python-to-forge answer()=42' $T/o \
   && grep -q 'python-to-forge sum_to(10)=55' $T/o
then echo "PASS cpython-embed/python-to-forge"; else echo "FAIL cpython-embed/python-to-forge (rc $rc)"; cat $T/o $T/e; fail=1; fi
if [ $rc = 0 ] \
   && grep -Fq 'forge-to-python eval(6*7)=42' $T/o \
   && grep -Fq 'forge-to-python mul(6,7)=42' $T/o \
   && grep -Fq 'forge-to-python neg(42)=-42' $T/o \
   && grep -Fq 'forge-to-python answer()=42' $T/o \
   && grep -Fq 'forge-to-python-to-forge twice_add(20,22)=84' $T/o
then echo "PASS cpython-embed/forge-to-python"; else echo "FAIL cpython-embed/forge-to-python (rc $rc)"; cat $T/o $T/e; fail=1; fi
exit $fail
