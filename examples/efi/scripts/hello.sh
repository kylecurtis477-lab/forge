# Forge 11.1 acceptance script: run by the sandboxed BusyBox ash (builtins only, no fork).
greet() { echo "hello from ash, $1"; }
greet forge
n=0
for w in alpha beta gamma; do n=$((n + 1)); echo "word $n: $w"; done
i=1; f=1
while [ $i -le 5 ]; do f=$((f * i)); i=$((i + 1)); done
echo "5! = $f"
case $f in 120) echo "case ok";; *) echo "case bad";; esac
if test -f /efi/forge/docs/big.bin; then echo "big.bin present"; fi
lines=0
while read -r l; do lines=$((lines + 1)); done < "/efi/forge/docs/Long File Name Notes.txt"
echo "notes has $lines lines"
printf 'script done: %s\n' ok
