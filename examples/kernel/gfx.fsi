# gfx.fsi: framebuffer pixel graphics (screen step 1; Phase 8 graphics pulled forward, see docs/BUILD_PLAN.md
# "Current execution order"). The desktop draws into gfx_buf, a back buffer in ordinary memory (at most
# GFX_MAXW x GFX_MAXH pixels, bounds-checked like every array), and gfx_present copies the damaged rectangle to
# the framebuffer through fb_px (console.fsi), the one bounds-checked store to the screen. No new unsafe code.
# Colours are 0xRRGGBB everywhere; gfx_px converts to the framebuffer's byte order once per stored pixel.
# Text: gfx_char / gfx_text draw the 8x16 font (font.fsi) at any pixel position in any fg/bg colour.
# The desktop runs when the boot command line has `forge.desktop` (tests/os/desktop.sh).
GFX_MAXW = 1024
GFX_MAXH = 768
gfx_buf: u32[786432]
gfx_w: u64
gfx_h: u64
gfx_bgrx: u64
# damage: the bounding box of everything drawn since the last gfx_present (x0 >= x1 means none)
gfx_dx0: u64
gfx_dy0: u64
gfx_dx1: u64
gfx_dy1: u64
gfx_presented: u64

def gfx_init(fmt: u64) -> u64:
# 1 when there is a 32-bit linear framebuffer to draw on; the back buffer starts black, damage empty
    if fb_base == 0:
        return 0
    gfx_w = fb_w
    if gfx_w > GFX_MAXW:
        gfx_w = GFX_MAXW
    gfx_h = fb_h
    if gfx_h > GFX_MAXH:
        gfx_h = GFX_MAXH
    gfx_bgrx = 0
    if fmt == FB_BGRX:
        gfx_bgrx = 1
    i: u64 = 0
    while i < gfx_w * gfx_h:
        gfx_buf[i] = 0
        i += 1
    gfx_dx0 = gfx_w
    gfx_dy0 = gfx_h
    gfx_dx1 = 0
    gfx_dy1 = 0
    gfx_presented = 0
    return 1

def gfx_px(c: u64) -> u32:
# 0xRRGGBB to the framebuffer word: BGRX (byte 0 = blue) is the same number, RGBX swaps red and blue
    if gfx_bgrx != 0:
        return u32(c & 0xFFFFFF)
    return u32(((c >> 16) & 0xFF) | (c & 0xFF00) | ((c & 0xFF) << 16))

def gfx_damage(x0: u64, y0: u64, x1: u64, y1: u64):
    if x0 < gfx_dx0:
        gfx_dx0 = x0
    if y0 < gfx_dy0:
        gfx_dy0 = y0
    if x1 > gfx_dx1:
        gfx_dx1 = x1
    if y1 > gfx_dy1:
        gfx_dy1 = y1

def gfx_fill(x: u64, y: u64, w: u64, h: u64, c: u64) -> u64:
# fill a rectangle clipped to the screen; returns the number of pixels written
    if x >= gfx_w or y >= gfx_h or w == 0 or h == 0:
        return 0
    x1 = x + w
    if x1 > gfx_w or x1 < x:
        x1 = gfx_w
    y1 = y + h
    if y1 > gfx_h or y1 < y:
        y1 = gfx_h
    yy = y
    while yy < y1:
        row = yy * gfx_w
        xx = x
        while xx < x1:
            gfx_buf[row + xx] = u32(c)
            xx += 1
        yy += 1
    gfx_damage(x, y, x1, y1)
    return (x1 - x) * (y1 - y)

def gfx_frame(x: u64, y: u64, w: u64, h: u64, t: u64, c: u64):
# a rectangle outline t pixels thick
    gfx_fill(x, y, w, t, c)
    gfx_fill(x, y + h - t, w, t, c)
    gfx_fill(x, y, t, h, c)
    gfx_fill(x + w - t, y, t, h, c)

def gfx_get(x: u64, y: u64) -> u64:
    if x >= gfx_w or y >= gfx_h:
        return 0xFFFFFFFF
    return u64(gfx_buf[y * gfx_w + x])

def gfx_char(x: u64, y: u64, ch: u64, fg: u64, bg: u64):
# one 8x16 glyph at pixel (x, y); characters outside 32..126 draw as a space
    g: u64 = 0
    if ch >= 32 and ch <= 126:
        g = (ch - 32) * 16
    r: u64 = 0
    while r < 16:
        bits = u64(font8x16[g + r])
        k: u64 = 0
        while k < 8:
            c = bg
            if (bits >> (7 - k)) & 1:
                c = fg
            if x + k < gfx_w and y + r < gfx_h:
                gfx_buf[(y + r) * gfx_w + x + k] = u32(c)
            k += 1
        r += 1
    gfx_damage(x, y, x + 8, y + 16)

def gfx_text(x: u64, y: u64, s: ptr[u8], fg: u64, bg: u64) -> u64:
# a NUL-terminated kernel string literal at pixel (x, y); returns the x after the last character
    i: u64 = 0
    while ffs_ch(s, i) != 0:
        gfx_char(x + i * 8, y, ffs_ch(s, i), fg, bg)
        i += 1
    return x + i * 8

def gfx_num(x: u64, y: u64, v: u64, fg: u64, bg: u64) -> u64:
    d: u8[24]
    n: u64 = 0
    while 1:
        d[n] = u8(48 + v % 10)
        n += 1
        v = v / 10
        if v == 0 or n == 24:
            break
    while n > 0:
        n -= 1
        gfx_char(x, y, u64(d[n]), fg, bg)
        x += 8
    return x

def gfx_present() -> u64:
# copy the damaged rectangle of the back buffer to the screen; returns the pixels copied
    if gfx_dx0 >= gfx_dx1 or gfx_dy0 >= gfx_dy1:
        return 0
    y = gfx_dy0
    while y < gfx_dy1:
        x = gfx_dx0
        while x < gfx_dx1:
            fb_px(y * fb_pitch + x * 4, gfx_px(u64(gfx_buf[y * gfx_w + x])))
            x += 1
        y += 1
    n = (gfx_dx1 - gfx_dx0) * (gfx_dy1 - gfx_dy0)
    gfx_presented += n
    gfx_dx0 = gfx_w
    gfx_dy0 = gfx_h
    gfx_dx1 = 0
    gfx_dy1 = 0
    return n

# 0xRRGGBB colour bars of the test pattern: white, yellow, cyan, green, magenta, red, blue, grey
gfx_bars: u64[8] = [0xFFFFFF, 0xFFFF00, 0x00FFFF, 0x00FF00, 0xFF00FF, 0xFF0000, 0x0000FF, 0x808080]
GFX_DESK = 0x1E2A3C

def gfx_selftest() -> u64:
# back-buffer checks before anything reaches the screen: fills land where asked, clipping keeps
# off-screen parts out, glyph pixels match the font, and the format conversion swaps only for RGBX
    if gfx_fill(10, 10, 4, 3, 0x123456) != 12 or gfx_get(13, 12) != 0x123456 or gfx_get(14, 12) != 0:
        return 0
    if gfx_fill(gfx_w - 2, gfx_h - 1, 10, 10, 0xABCDEF) != 2 or gfx_get(gfx_w - 1, gfx_h - 1) != 0xABCDEF:
        return 0
    if gfx_fill(gfx_w, 0, 5, 5, 1) != 0 or gfx_fill(0, gfx_h + 3, 5, 5, 1) != 0:
        return 0
    gfx_char(40, 40, 65, 0xFFFFFF, 0x000000)
    r: u64 = 0
    while r < 16:
        bits = u64(font8x16[(65 - 32) * 16 + r])
        k: u64 = 0
        while k < 8:
            want: u64 = 0
            if (bits >> (7 - k)) & 1:
                want = 0xFFFFFF
            if gfx_get(40 + k, 40 + r) != want:
                return 0
            k += 1
        r += 1
    if gfx_bgrx != 0 and gfx_px(0x112233) != u32(0x112233):
        return 0
    if gfx_bgrx == 0 and gfx_px(0x112233) != u32(0x332211):
        return 0
    return 1

def desktop_wanted() -> u64:
# `forge.desktop` on the boot command line
    i: u64 = 0
    while i < 255 and bi.cmdline[i] != 0:
        while i < 255 and bi.cmdline[i] == 32:
            i += 1
        s = i
        while i < 255 and bi.cmdline[i] != 0 and bi.cmdline[i] != 32:
            i += 1
        if i > s and cmd_tok_eq(s, i - s, "forge.desktop"):
            return 1
    return 0

def desktop_show():
# screen step 1: the desktop background, the colour-bar test pattern, two labelled rows right under the bars
# (white on the dark background, on the 8x16 grid from column 0, so the boot harness reads them back from a QEMU
# screendump as whole rows) and a framed area below them
    if gfx_init(bi.fb_format) == 0:
        puts("desktop: no linear framebuffer, staying on the text console\n")
        return
    if gfx_selftest() == 0:
        panic("gfx: back-buffer self-test failed")
    gfx_fill(0, 0, gfx_w, gfx_h, GFX_DESK)
    bw = gfx_w / 8
    i: u64 = 0
    while i < 8:
        gfx_fill(i * bw, 0, bw, 96, gfx_bars[i])
        i += 1
    gfx_frame(32, 128, gfx_w - 64, gfx_h - 192, 2, 0x8899AA)
    gfx_text(0, 96, "Forge desktop (screen step 1: framebuffer graphics)", 0xFFFFFF, GFX_DESK)
    x = gfx_text(0, 112, "gfx: ", 0xFFFFFF, GFX_DESK)
    x = gfx_num(x, 112, gfx_w, 0xFFFFFF, GFX_DESK)
    x = gfx_text(x, 112, "x", 0xFFFFFF, GFX_DESK)
    x = gfx_num(x, 112, gfx_h, 0xFFFFFF, GFX_DESK)
    gfx_text(x, 112, " back buffer, 8 colour bars, self-test OK", 0xFFFFFF, GFX_DESK)
    n = gfx_present()
    gfx_on = 1
    puts("desktop: gfx ")
    put_u64(gfx_w)
    puts("x")
    put_u64(gfx_h)
    if gfx_bgrx != 0:
        puts(" BGRX")
    else:
        puts(" RGBX")
    puts(", back-buffer self-test OK, ")
    put_u64(n)
    puts(" pixels presented, console text now serial only\n")
