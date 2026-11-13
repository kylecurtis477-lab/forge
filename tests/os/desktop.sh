#!/bin/bash
# tests/os/desktop.sh: screen step 1 (examples/kernel/gfx.fsi), one result, os/desktop.
# GRUB (BIOS) boot with `forge.desktop` on the multiboot2 command line: the kernel runs its self-tests (shell and
# kexec sessions are skipped in desktop mode), checks the gfx back buffer, draws the desktop (background, 8 colour
# bars, a framed area and two text rows) and presents it, then prints KERNEL OK on serial only. The boot harness
# reads the two text rows back from the QEMU screendump (white 8x16 glyphs on the dark background), and the
# colour bars are checked pixel by pixel in the same screendump (build/os/desktop.ppm), so a wrong framebuffer
# byte order (red and blue swapped) fails. Approved addition: os/result-count went from 38 to 39 results.
cd "$(dirname "$0")/../.."
Q="-cpu max -display none -no-reboot -net none -m 256 -smp 4 -serial stdio -device isa-debug-exit,iobase=0xf4,iosize=0x04"
L=build/os
B=$L/boot
T=$(mktemp -d /tmp/forge-desktop.XXXXXX)
trap 'rm -rf $T' EXIT
[ -x $B ] && [ -f $L/vdisk.img ] || { echo "FAIL os/desktop (needs tests/os/run.sh outputs)"; exit 0; }
cp -r build/kernel/iso $T/iso
sed -i 's|multiboot2 /boot/kernel.elf$|multiboot2 /boot/kernel.elf forge.desktop|' $T/iso/boot/grub/grub.cfg
grep -q 'forge.desktop' $T/iso/boot/grub/grub.cfg || { echo "FAIL os/desktop (grub.cfg edit)"; exit 0; }
env -u SOURCE_DATE_EPOCH grub-mkrescue -o $T/desktop.iso $T/iso >/dev/null 2>&1 || { echo "FAIL os/desktop (grub-mkrescue)"; exit 0; }
VD="-drive file=$L/vdisk.img,format=raw,if=none,id=vd,readonly=on -device virtio-blk-pci,drive=vd"
rm -f $L/desktop.ppm
R=$($B desktop $L/desktop.log 200 "multiboot2 (BIOS)|shell and kexec sessions skipped: desktop mode (forge.desktop)|desktop: gfx |, back-buffer self-test OK, |pixels presented, console text now serial only|KERNEL OK" "Forge desktop (screen step 1^gfx: 1024x768 back buffer, 8 colour bars, self-test OK" -- $Q $VD -boot d -cdrom $T/desktop.iso)
case "$R" in PASS*) ;; *) echo "$R"; exit 0;; esac
# the colour bars: centre of bar i (of 8, across the full width, 96 pixels tall) must be the 0xRRGGBB it was drawn in
C=$(python3 - $L/desktop.ppm <<'P'
import sys
d = open(sys.argv[1], 'rb').read()
t = d.split(maxsplit=4)
w, h, px = int(t[1]), int(t[2]), t[4]
want = [0xFFFFFF, 0xFFFF00, 0x00FFFF, 0x00FF00, 0xFF00FF, 0xFF0000, 0x0000FF, 0x808080]
bw = min(w, 1024) // 8
bad = []
for i, c in enumerate(want):
    o = (48 * w + i * bw + bw // 2) * 3
    got = (px[o] << 16) | (px[o + 1] << 8) | px[o + 2]
    if got != c:
        bad.append('bar %d: %06x, want %06x' % (i, got, c))
print('; '.join(bad) if bad else 'ok')
P
)
if [ "$C" = ok ]; then echo "$R [8 colour bars exact]"; else echo "FAIL os/desktop (colour bars: $C)"; fi
