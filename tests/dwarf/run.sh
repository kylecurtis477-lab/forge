#!/bin/bash
# Phase 7.3: DWARF line tables. The kernel built with `forgec -g` (fas + fld --format kernel keep
# .debug_abbrev/.debug_info/.debug_ranges/.debug_line) boots under QEMU with its gdbstub; gdb loads
# the symbols at the image-KASLR slide the kernel logs, breaks at `schedule`, and must report the
# Forge source file and line, and show that source line.
# Needs the user programs and modules built (examples/kernel/build.sh) and gdb (GDB=... to override).
cd "$(dirname "$0")/../.."
# gdb must be built with XML support (QEMU describes the x86-64 registers in XML target descriptions)
GDB=${GDB:-$(command -v gdb || echo $HOME/gdbd/bin/gdb)}
if [ ! -x "$GDB" ]; then echo "SKIP dwarf/gdb-schedule (no gdb)"; exit 0; fi
D=build/dwarf; mkdir -p $D/iso/boot/grub
fail() { echo "FAIL dwarf/$1"; pkill -f "qemu-system.*$D/forge-g.iso" 2>/dev/null; exit 0; }
(cd examples/kernel && ../../build/forgec kernel.forge ../../$D/kernel.s --fs-opt -g) || fail "build (forgec -g)"
build/fas -I build/user -o $D/kernel.o $D/kernel.s || fail "build (fas)"
build/fld --format kernel -o $D/kernel.elf $D/kernel.o || fail "build (fld)"
# -g changes only the debug sections: the loaded image equals the normal build's
{ build/forgec tests/fld/memimg.forge $D/memimg.s && build/fas -o $D/memimg.o $D/memimg.s && build/fld --base 0x100000000000 -o $D/memimg $D/memimg.o; } || fail "build (memimg)"
$D/memimg $D/kernel.elf build/kernel/kernel.elf > /dev/null && echo "PASS dwarf/image-unchanged-by-g" || echo "FAIL dwarf/image-unchanged-by-g"
readelf --debug-dump=decodedline $D/kernel.elf 2>&1 | grep -q "^sched.fsi  *157 " && echo "PASS dwarf/line-table (readelf decodes sched.fsi:157)" || echo "FAIL dwarf/line-table"
cp $D/kernel.elf $D/iso/boot/kernel.elf && cp examples/kernel/grub.cfg $D/iso/boot/grub/grub.cfg
grub-mkrescue -o $D/forge-g.iso $D/iso >/dev/null 2>&1 || fail "iso"
PORT=$((20000 + RANDOM % 20000))
cp build/efi/disk.img $D/vdisk.img
rm -f $D/serial.log
qemu-system-x86_64 -cpu max -display none -no-reboot -net none -m 256 -smp 4 -serial file:$D/serial.log \
  -device isa-debug-exit,iobase=0xf4,iosize=0x04 -gdb tcp:127.0.0.1:$PORT \
  -drive file=$D/vdisk.img,format=raw,if=none,id=vd,readonly=on -device virtio-blk-pci,drive=vd \
  -boot d -cdrom $D/forge-g.iso > /dev/null 2>&1 &
QP=$!
SL=""
for i in $(seq 1 600); do
  SL=$(grep -ao 'KASLR: kernel image at 0x[0-9a-f]* (slide 0x[0-9a-f]*' $D/serial.log 2>/dev/null | sed 's/.*slide //')
  [ -n "$SL" ] && break; sleep 0.1
done
[ -n "$SL" ] || { kill $QP; fail "gdb-schedule (no KASLR line on serial)"; }
timeout 60 "$GDB" -nx -batch -ex "set pagination off" -ex "directory examples/kernel" \
  -ex "symbol-file -o $SL $D/kernel.elf" -ex "target remote 127.0.0.1:$PORT" \
  -ex "hbreak schedule" -ex "continue" -ex "info line *\$pc" -ex "frame" -ex "delete" -ex "kill" > $D/gdb.log 2>&1
kill $QP 2>/dev/null; wait $QP 2>/dev/null
SRC=$(grep -n "^def schedule(" examples/kernel/sched.fsi | cut -d: -f1)
FIRST=$(awk -v s=$SRC 'NR>s && $0 !~ /^ *#/ && $0 ~ /[^ ]/ {print NR; exit}' examples/kernel/sched.fsi)
TXT=$(sed -n "${FIRST}p" examples/kernel/sched.fsi | sed 's/^ *//')
if grep -q "Breakpoint 1, schedule (" $D/gdb.log && grep -q "sched.fsi:$FIRST" $D/gdb.log && grep -qF "$TXT" $D/gdb.log; then
  echo "PASS dwarf/gdb-schedule (slide $SL: stopped at sched.fsi:$FIRST, source line shown)"
else
  echo "FAIL dwarf/gdb-schedule (see $D/gdb.log)"
fi
