#!/bin/sh
# tests/cpython-build: tools/cpython/build.forge (built to build/cpython-build) pins and installs
# CPython 3.13.5 from official source (kept out of the repo in $CP_SRC, default /workspace/cpython).
# Cases: tarball missing (rc 2), SHA-256 pin mismatch (rc 3), a bad upstream signature (rc 4),
# a cold (or first) install that produces python3 3.13.5, Python.h and libpython3.13.a, and a warm
# re-run that is a no-op ("up to date"). Without the tarball the script reports SKIP. A full
# compile is only done when the work prefix is missing or stale; later suite runs should be warm.
cd "$(dirname "$0")/../.." || exit 1
S=${CP_SRC:-/workspace/cpython}
[ -f $S/Python-3.13.5.tar.xz ] || { echo "SKIP cpython-build (no Python-3.13.5.tar.xz in $S)"; exit 0; }
T=$(mktemp -d); trap 'rm -rf $T' EXIT
fail=0
./forge tools/cpython/build.forge -o build/cpython-build >/dev/null || { echo "FAIL cpython-build build"; exit 1; }
chk_err() {
  # name src want-rc
  CP_SRC=$2 CP_WORK=$T/w build/cpython-build >$T/o 2>$T/e; rc=$?
  if [ $rc = $3 ]; then echo "PASS cpython-build/$1"; else echo "FAIL cpython-build/$1 (rc $rc, want $3)"; cat $T/o $T/e | head -8; fail=1; fi
}
mkdir $T/none
chk_err missing $T/none 2
mkdir $T/bad; echo fake >$T/bad/Python-3.13.5.tar.xz
chk_err pin-mismatch $T/bad 3
mkdir $T/sig
ln -s $S/Python-3.13.5.tar.xz $T/sig/
echo junk >$T/sig/Python-3.13.5.tar.xz.asc
chk_err bad-signature $T/sig 4

# real install into $S/work (durable; warm on later runs)
W=${CP_WORK:-$S/work}
CP_SRC=$S CP_WORK=$W build/cpython-build >$T/b.out 2>$T/b.err; rc=$?
P=$W/prefix
ver=$($P/bin/python3 -c 'import sys; print(sys.version.split()[0])' 2>/dev/null)
got=$($P/bin/python3 -c 'print(6*7)' 2>/dev/null)
if [ $rc = 0 ] && [ "$ver" = "3.13.5" ] && [ "$got" = "42" ] \
   && [ -f $P/include/python3.13/Python.h ] && [ -f $P/lib/libpython3.13.a ] \
   && grep -q "at $P" $T/b.out
then echo "PASS cpython-build/install"; else echo "FAIL cpython-build/install (rc $rc ver=$ver)"; cat $T/b.out $T/b.err | tail -15; fail=1; fi

CP_SRC=$S CP_WORK=$W build/cpython-build >$T/w.out 2>$T/w.err; rc=$?
if [ $rc = 0 ] && grep -q "cpython: up to date at $P" $T/w.out
then echo "PASS cpython-build/warm"; else echo "FAIL cpython-build/warm (rc $rc)"; cat $T/w.out $T/w.err; fail=1; fi
exit $fail
