#!/bin/sh
# Ed25519 verifier (examples/kernel/ed25519.fsi) against signatures made by `cryptography`
cd "$(dirname "$0")"
B=../../build/ed25519; mkdir -p $B
want=$(python3 gen.py edtest.forge) || { echo "FAIL ed25519/gen"; exit 1; }
../../build/forgec edtest.forge $B/edtest.s $FORGEC_FLAGS && as -o $B/edtest.o $B/edtest.s && ld -static -nostdlib -e _start -o $B/edtest $B/edtest.o || { echo "FAIL ed25519/build"; exit 1; }
got=$($B/edtest)
if [ "$got" = "$want" ]; then echo "PASS ed25519/verify ($want)"; else echo "FAIL ed25519/verify want $want got $got"; exit 1; fi
