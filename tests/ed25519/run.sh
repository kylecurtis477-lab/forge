#!/bin/sh
# Ed25519 verifier (examples/kernel/ed25519.fsi) against vectors from tests/ed25519/gen.forge (signed by
# tools/ed25519.forge). The generated program must equal the committed edtest.forge byte for byte.
# FORGE_ED25519_PY=1 also runs gen.py (signatures from `cryptography`, independent of the Forge signer)
# and requires the same program and expectations.
cd "$(dirname "$0")"
B=../../build/ed25519; mkdir -p $B
../../build/forgec ../../tools/ed25519.forge $B/sign.s && ../../build/fas -o $B/sign.o $B/sign.s && ../../build/fld -o $B/sign $B/sign.o || { echo "FAIL ed25519/signer-build"; exit 1; }
../../forge gen.forge -o $B/gen >/dev/null || { echo "FAIL ed25519/gen-build"; exit 1; }
want=$($B/gen $B/edtest.forge $B/sign) || { echo "FAIL ed25519/gen"; exit 1; }
cmp -s $B/edtest.forge edtest.forge && echo "PASS ed25519/gen-matches-committed" || { echo "FAIL ed25519/gen-matches-committed"; exit 1; }
if [ "$FORGE_ED25519_PY" = 1 ]; then
  pw=$(python3 gen.py $B/edtest_py.forge) && [ "$pw" = "$want" ] && cmp -s $B/edtest_py.forge edtest.forge && echo "PASS ed25519/gen-matches-py" || { echo "FAIL ed25519/gen-matches-py"; exit 1; }
fi
../../build/forgec edtest.forge $B/edtest.s $FORGEC_FLAGS && ../../build/fas -o $B/edtest.o $B/edtest.s && ../../build/fld -o $B/edtest $B/edtest.o || { echo "FAIL ed25519/build"; exit 1; }
got=$($B/edtest)
if [ "$got" = "$want" ]; then echo "PASS ed25519/verify ($want)"; else echo "FAIL ed25519/verify want $want got $got"; exit 1; fi
