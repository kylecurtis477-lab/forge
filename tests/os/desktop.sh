#!/bin/bash
# tests/os/desktop.sh: screen steps 1-6 (examples/kernel/gfx.fsi, input.fsi, wm.fsi, appwin.fsi, panel.fsi), one result, os/desktop.
# GRUB (BIOS) boot with `forge.desktop` on the multiboot2 command line: the kernel runs its self-tests (shell and
# kexec sessions are skipped in desktop mode), checks the gfx back buffer, draws the desktop (background, 8 colour
# bars, two text rows and a terminal that opens on the boot log) and presents it; from then on console text goes
# to serial and the terminal. The boot harness reads the terminal's last two rows back from the QEMU screendump
# (the desktop line, then KERNEL OK as a whole row, light 8x16 glyphs on the dark terminal), and the
# colour bars are checked pixel by pixel in the same screendump (build/os/desktop.ppm), so a wrong framebuffer
# byte order (red and blue swapped) fails. Step 4 (the compositor): the monitor moves the cursor onto the About
# window's title bar (under Help), presses, drags it 300 pixels right and releases, then sends a key; serial must
# show the focus change and the new position, and the screendump must show About's focused title bar on top of
# Help at the new place, Help's title bar (unfocused) where About no longer covers it, and terminal (not About's
# body) where About was. Step 5: before the desktop starts, gfxdeny (manifest caps(fs)) must be refused a
# window and gfxapp (caps(gfx) only) opens one, draws in it and is refused a file and a foreign handle; its window
# (at 600,420) must show the first colour bar it drew and an unfocused title bar. Step 6 (the desktop shell, panel.fsi): after
# the drag the cursor goes down to the taskbar and presses About's button (About is focused, so it is minimized), opens
# the launcher with the Forge button, chooses About Forge (restored and raised, menu closed), then presses Help's
# button (Help raised); serial must show each step, and the screendump must show the taskbar, Help's button and
# title focused, Help on top of About, About's title unfocused and no launcher left on screen. Approved addition: os/result-count went from 38 to 39 results.
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
# step 6 path: (500,178) -> About's taskbar button (404,748), the Forge button (44,748), the launcher's About Forge
# entry (44,672), Help's taskbar button (612,752); PS/2 moves stay under 128 pixels per packet
SH=""
for i in 1 2 3 4 5 6; do SH="${SH}mouse_move -16 95;"; done
SH="${SH}mouse_button 1;mouse_button 0;"
for i in 1 2 3 4; do SH="${SH}mouse_move -90 0;"; done
SH="${SH}mouse_button 1;mouse_button 0;mouse_move 0 -76;mouse_button 1;mouse_button 0;"
for i in 1 2 3 4 5 6 7 8; do SH="${SH}mouse_move 71 10;"; done
SH="${SH}mouse_button 1;mouse_button 0;"
R=$(FORGE_BOOT_INPUT="desktop: input ready|mouse_move -104 -103;mouse_move -104 -103;mouse_move -104 0;mouse_button 1;mouse_move 100 0;mouse_move 100 0;mouse_move 100 0;mouse_button 0;${SH}sendkey a" $B desktop $L/desktop.log 200 "multiboot2 (BIOS)|shell and kexec sessions skipped: desktop mode (forge.desktop)|gfxdeny: window denied|denied syscall 431: capability 'gfx' not declared in its manifest|denied syscall 2: capability 'fs' not declared in its manifest|gfxapp: window 1, 12 draws, open file denied, other handle refused|desktop app: gfxapp (pid |) drew 12 times into its 300x120 window; gfx without the cap, fs without the cap and another handle refused|desktop: gfx 1024x768 BGRX, self-test OK, |pixels presented, terminal 128x37, 4 windows|desktop: input ready, mouse on|mouse: buttons 1 at 200,178|wm: focus About Forge|mouse: buttons 0 at 500,178|wm: About Forge moved to 400,170|mouse: buttons 1 at 404,748|shell: About Forge minimized|shell: launcher open, 4 entries|shell: launch About Forge|shell: taskbar raise Help|key: scan code 1e|desktop: input events: |cursor at 612,752, dropped 0|raises, |damage repaints|shell: 1 launches, 1 minimized, 1 restored|KERNEL OK" "restored^KERNEL OK" -- $Q $VD -boot d -cdrom $T/desktop.iso)
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
def at(x, y):
    o = (y * w + x) * 3
    return (px[o] << 16) | (px[o + 1] << 8) | px[o + 2]
# step 4: About (focused title 3b6ea8) dragged to x=400 over Help; Help's title (unfocused 3a4250) left of it;
# About's old place shows the terminal, not About's body (2a3446)
# step 6: Help raised from the taskbar sits on top of About (focused title over About's body), About's title is
# unfocused; the taskbar shows the Forge button, Help's button focused, About's not; the launcher is closed
for x, y, c, what in [(410, 175, 0x3A4250, 'About title at its new place'), (384, 252, 0x3B6EA8, 'Help title'),
                     (500, 252, 0x3B6EA8, 'Help title over About'),
                     (603, 422, 0x3A4250, 'app title'), (622, 497, 0xE04040, 'app bar 0 (drawn by gfxapp)'),
                     (960, 750, 0x0C1220, 'taskbar'), (10, 744, 0x2D7D46, 'Forge button'),
                     (514, 742, 0x3B6EA8, 'Help button (focused)'), (306, 742, 0x3A4250, 'About button')]:
    if at(x, y) != c:
        bad.append('%s (%d,%d): %06x, want %06x' % (what, x, y, at(x, y), c))
if at(110, 250) == 0x2A3446:
    bad.append('About body still at its old place (110,250)')
if at(100, 650) == 0x2A3446:
    bad.append('launcher still open (100,650)')
print('; '.join(bad) if bad else 'ok')
P
)
if [ "$C" = ok ]; then echo "$R [8 colour bars exact, windows where dragged, app canvas, taskbar and launcher]"; else echo "FAIL os/desktop (colour bars: $C)"; fi
