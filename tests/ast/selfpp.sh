#!/bin/sh
# The pretty-printed compiler source must build a compiler whose output is byte-identical to the real one.
cd "$(dirname "$0")/../.."
build/forgec selfhost/forgec.forge selfhost/.pp_c.forge --emit=pretty && build/forgec selfhost/.pp_c.forge build/pp_c.s && build/fas -o build/pp_c.o build/pp_c.s && build/fld -o build/pp_c build/pp_c.o && build/forgec selfhost/forgec.forge build/pp_ref.s && build/pp_c selfhost/forgec.forge build/pp_out.s && cmp -s build/pp_ref.s build/pp_out.s; r=$?
rm -f selfhost/.pp_c.forge
[ $r = 0 ] && echo "PASS ast-pretty-selfhost" || echo "FAIL ast-pretty-selfhost"
exit $r
