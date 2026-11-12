#!/bin/sh
# tests/asm-audit/run.sh: keeps the "Kernel asm and --fs-opt" checklist in docs/SYSTEMS.md complete (Phase 12.1).
# --fs-opt keeps Forge locals in registers, which is only safe while every piece of hand-written asm keeps the SysV
# register rules, so each one must be audited. This lists every asm routine in the tree:
#   - every @naked function (examples/kernel, examples/user),
#   - every top-level asm(...) block in examples/kernel (its .globl symbols, or its first label when it has none),
#   - every ordinary function with inline asm in examples/kernel or examples/user (the enclosing def),
# and fails when one is missing from a "- [x]" line of that SYSTEMS.md section, when the section has an unchecked
# "- [ ]" line, when a checked name no longer exists in examples/ or selfhost/compiler.forge, or when examples/modules or examples/efi gain asm.
# usage: tests/asm-audit/run.sh [--list]     (run from the repo root; --list prints the required names)
cd "$(dirname "$0")/../.." || exit 1
fail=0
T=${TMPDIR:-/tmp}/asm-audit.$$
trap 'rm -f $T.*' EXIT
# required names, one per line: "<group> <name> <file>"
for f in examples/kernel/*.fsi examples/kernel/*.forge examples/user/*.fsi examples/user/*.forge; do
  case $f in examples/kernel/*) g=kernel ;; *) g=user ;; esac
  awk -v F="$f" -v G="$g" '
    /^@naked/ { nk = 1; next }
    /^@/ { next }
    /^def / { fn = $2; sub(/\(.*/, "", fn); if (nk) print G, fn, F; naked = nk; nk = 0; inasm = 0; next }
    /^asm\(/ { blk = 1; gl = 0; first = ""; fn = ""; naked = 0 }
    blk {
      s = $0
      while (match(s, /\.globl[ \t]+[A-Za-z_][A-Za-z0-9_]*/)) { n = substr(s, RSTART, RLENGTH); sub(/\.globl[ \t]+/, "", n); print G, n, F; gl++; s = substr(s, RSTART + RLENGTH) }
      if (first == "" && match($0, /"[ \t]*[A-Za-z_][A-Za-z0-9_]*:/)) { first = substr($0, RSTART + 1, RLENGTH - 2); sub(/^[ \t]*/, "", first) }
      if ($0 ~ /\)[ \t]*$/ && $0 !~ /^[ \t]+asm\(/ ) { if ($0 ~ /"\)[ \t]*$/ || $0 ~ /^\)/) { if (!gl && first != "") print G, first, F; blk = 0 } }
      next
    }
    /^[^ \t#]/ { fn = "" }
    /^[ \t]+asm\(/ { if (fn != "" && !naked && !done[fn]++) print G, fn, F }
  ' "$f"
done | sort -u > $T.req
[ "$1" = "--list" ] && { cat $T.req; exit 0; }
# the checklist: "- [x]" / "- [ ]" lines between the section heading and the next "## "
awk '/^## Kernel asm and --fs-opt/ { on = 1; next } on && /^## / { on = 0 } on && /^- \[[ xX]\]/' docs/SYSTEMS.md > $T.list
grep -q . $T.list || { echo "FAIL asm-audit-checklist (no checklist in docs/SYSTEMS.md)"; exit 1; }
grep '^- \[ \]' $T.list > $T.open
[ -s $T.open ] && { echo "FAIL asm-audit-unchecked:"; cat $T.open; fail=1; }
grep -i '^- \[x\]' $T.list | grep -o '`[A-Za-z_][A-Za-z0-9_]*`' | tr -d '`' | sort -u > $T.names
for g in kernel user; do
  n=0; miss=""
  for name in $(awk -v G=$g '$1 == G { print $2 }' $T.req | sort -u); do
    n=$((n + 1)); grep -qx "$name" $T.names || miss="$miss $name"
  done
  if [ -z "$miss" ]; then echo "PASS asm-audit-$g ($n routines checked off)"; else echo "FAIL asm-audit-$g: not in the docs/SYSTEMS.md checklist:$miss"; fail=1; fi
done
stale=""
for name in $(cat $T.names); do grep -rqw -- "$name" examples selfhost/compiler.forge || stale="$stale $name"; done
if [ -z "$stale" ]; then echo "PASS asm-audit-stale"; else echo "FAIL asm-audit-stale: checklist names not found in examples/ or the compiler:$stale"; fail=1; fi
if grep -rlE '(^|[^A-Za-z0-9_])asm\(|^@naked' examples/modules examples/efi > $T.none 2>/dev/null; then echo "FAIL asm-audit-none: asm in"; cat $T.none; fail=1; else echo "PASS asm-audit-none (examples/modules, examples/efi)"; fi
exit $fail
