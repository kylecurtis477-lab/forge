#!/bin/sh
# Refresh the checked-in seed compiler from a green build (run ./build.sh first; commit the result).
set -e
cd "$(dirname "$0")/.."
cmp build/stage2.s build/stage4.s
cp build/forgec seed/forgec-seed
cp build/fas seed/fas-seed
cp build/fld seed/fld-seed
(cd seed && sha256sum forgec-seed fas-seed fld-seed > seed.sha256)
cat seed/seed.sha256
