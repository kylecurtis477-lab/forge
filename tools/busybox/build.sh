#!/bin/sh
# Phase 11.1e: build BusyBox from verified source as a static musl Linux-ABI image, tag it
# EI_OSABI = 3 and seal it --foreign (level 3: runs only in the compat sandbox, no capabilities).
# No prebuilt third-party binaries: the tarballs below are source, pinned by SHA-256 (and checked
# against the upstream signatures when the keys are in $BB_SRC/gnupg). Host compiler: the box's gcc
# (Debian gcc 14.2.0); GNU make is built from source if the host has none. Network is never used
# here: put the tarballs in $BB_SRC (default /workspace/bb) first (see docs/linux-compat.md).
# usage: tools/busybox/build.sh OUT.elf SEALKEY
set -e
T=$(cd "$(dirname "$0")" && pwd)
OUT=$(realpath -m "$1"); KEY=$(realpath "$2")
S=${BB_SRC:-/workspace/bb}
W=${BB_WORK:-$S/work}
pin() {
    f=$S/$1
    [ -f "$f" ] || { echo "busybox: $f missing"; exit 2; }
    echo "$2  $f" | sha256sum -c --quiet - || { echo "busybox: $1 SHA-256 mismatch"; exit 3; }
    if [ -f "$f.$3" ] && [ -d "$S/gnupg" ]; then
        st=$(GNUPGHOME=$S/gnupg gpg --status-fd 1 --verify "$f.$3" "$f" 2>/dev/null || true)
        case "$st" in
        *GOODSIG*) ;;
        *EXPKEYSIG*) echo "busybox: $1: good signature by a key that has since expired" ;;
        *NO_PUBKEY*) echo "busybox: $1: signing key not on the box, SHA-256 pin only" ;;
        *) echo "busybox: $1 signature bad"; exit 4 ;;
        esac
    fi
}
[ -d "$S/gnupg" ] && GNUPGHOME=$S/gnupg gpg -q --import "$T"/keys/*.asc 2>/dev/null
pin busybox-1.38.0.tar.bz2 34f9ea6ff8636f2c9241153b9114eefa9e65674a45318ae1ef95bb5f31c53bb2 sig
pin musl-1.2.5.tar.gz a9a118bbe84d8764da0ea0d28b3ab3fae8477fc7e4085d90102b8596fc7c75e4 asc
mkdir -p $W/bin
cp $T/bzip2 $W/bin/bzip2; chmod +x $W/bin/bzip2
PATH=$W/bin:$PATH
if ! command -v make >/dev/null; then
    pin make-4.4.1.tar.gz dd16fb1d67bfab79a72f5e8390735c49e3e8e70b4945a15ab1f81ddb78658fb3 sig
    [ -x $W/bin/make ] || (cd $W && rm -rf make-4.4.1 && tar xzf $S/make-4.4.1.tar.gz && cd make-4.4.1 &&
        ./configure -q --disable-nls --disable-dependency-tracking >/dev/null && sh build.sh >/dev/null && cp make $W/bin/)
fi
if [ ! -x $W/musl/bin/musl-gcc ]; then
    (cd $W && rm -rf musl-1.2.5 && tar xzf $S/musl-1.2.5.tar.gz && cd musl-1.2.5 &&
        ./configure --prefix=$W/musl --disable-shared >/dev/null && make -j8 >/dev/null && make install >/dev/null)
fi
cd $W && rm -rf busybox-1.38.0
python3 -c "import tarfile,sys; tarfile.open(sys.argv[1]).extractall('.', filter='tar')" $S/busybox-1.38.0.tar.bz2
cd busybox-1.38.0
cp $T/busybox.config .config
make oldconfig </dev/null >/dev/null 2>&1
make -j8 CC=$W/musl/bin/musl-gcc >build.log 2>&1 || { tail -20 build.log; exit 5; }
python3 -c "import sys; d=bytearray(open(sys.argv[1],'rb').read()); d[7]=3; open(sys.argv[2],'wb').write(d)" busybox "$OUT"
# seal with tools/forgeseal.forge (tools/forgeseal.py is kept as the reference); built here if missing
S=$T/../../build/forgeseal
[ -x $S ] || { $T/../../build/forgec $T/../forgeseal.forge $S.s && $T/../../build/fas -o $S.o $S.s && $T/../../build/fld --base 0x100000000000 -o $S $S.o; }
$S --foreign "$OUT" "$KEY"
