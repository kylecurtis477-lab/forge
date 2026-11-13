# panel.fsi: the desktop shell (screen step 6), a taskbar along the bottom of the screen and a launcher menu,
# drawn by the compositor (wm.fsi) above every window. The taskbar holds a "Forge" button that opens the
# launcher, then one button per window (the focused window's button is highlighted). Pressing a window's button
# raises and focuses it; pressing the focused window's button minimizes it (hidden from the screen and from
# clicks, focus moves to the topmost visible window). The launcher lists every window; choosing one restores it
# if it was minimized, raises it and closes the menu. A press anywhere else closes an open menu.
PN_H = 32  # taskbar height (two 16-pixel rows, so the terminal above it stays on the boot harness's 8x16 grid)
PN_BG = 0x0C1220
PN_START = 0x2D7D46
PN_BW = 200  # window button width
PN_X0 = 96  # first window button
PN_MW = 200  # launcher width
PN_MH = 24  # launcher entry height
pn_menu: u64  # 1 while the launcher is open
pn_launches: u64
pn_minimized: u64
pn_restored: u64

def pn_y() -> u64:
    return gfx_h - PN_H

def pn_my() -> u64:
# top of the launcher (entries stacked above the taskbar)
    return pn_y() - 4 - wm_n * PN_MH

def pn_text(x: u64, y: u64, w: u64, win: u64, fg: u64, bg: u64):
# a window's title inside [x, x + w), clipped to that box and to the current clip
    sx0 = gfx_cx0
    sy0 = gfx_cy0
    sx1 = gfx_cx1
    sy1 = gfx_cy1
    a = x
    if a < sx0:
        a = sx0
    b = x + w
    if b > sx1:
        b = sx1
    if a < b:
        gfx_clip(a, sy0, b, sy1)
        wm_title(win, x, y, fg, bg)
    gfx_clip(sx0, sy0, sx1, sy1)

def pn_draw():
# the taskbar (and the launcher when open) inside the current clip
    y = pn_y()
    gfx_fill(0, y, gfx_w, PN_H, PN_BG)
    gfx_fill(4, y + 4, 80, PN_H - 8, PN_START)
    gfx_text(24, y + 8, "Forge", 0xFFFFFF, PN_START)
    i: u64 = 0
    while i < wm_n:
        bx = PN_X0 + i * (PN_BW + 8)
        if bx + PN_BW <= gfx_w:
            c = WM_BLUR
            if wm_hid[i] != 0:
                c = PN_BG
            elif i == wm_focus:
                c = WM_FOCUS
            gfx_fill(bx, y + 4, PN_BW, PN_H - 8, c)
            gfx_frame(bx, y + 4, PN_BW, PN_H - 8, 1, WM_EDGE)
            pn_text(bx + 8, y + 8, PN_BW - 16, i, 0xFFFFFF, c)
        i += 1
    if pn_menu != 0:
        my = pn_my()
        gfx_fill(4, my, PN_MW, wm_n * PN_MH, WM_BODY)
        gfx_frame(4, my, PN_MW, wm_n * PN_MH, 1, WM_EDGE)
        i = 0
        while i < wm_n:
            pn_text(16, my + i * PN_MH + 4, PN_MW - 24, i, 0xFFFFFF, WM_BODY)
            i += 1

def pn_meets(x0: u64, y0: u64, x1: u64, y1: u64) -> u64:
    if y1 > pn_y():
        return 1
    if pn_menu != 0 and x0 < 4 + PN_MW and y1 > pn_my():
        return 1
    return 0

def pn_paint_menu():
    wm_paint(4, pn_my(), 4 + PN_MW, pn_y())

def pn_paint_bar():
    wm_paint(0, pn_y(), gfx_w, gfx_h)

def pn_show(w: u64):
# restore w if minimized, raise and focus it
    if wm_hid[w] != 0:
        wm_hid[w] = 0
        pn_restored += 1
    wm_raise(w)
    pn_paint_bar()

def pn_minimize(w: u64):
    wm_hid[w] = 1
    pn_minimized += 1
    wm_paint_win(w)
    i = wm_n
    wm_focus = WM_N
    while i > 0:
        i -= 1
        if wm_hid[wm_z[i]] == 0:
            wm_focus = wm_z[i]
            wm_paint_win(wm_focus)
            break
    pn_paint_bar()

def pn_press(x: u64, y: u64) -> u64:
# a left press: 1 when the shell took it (taskbar or open launcher), 0 to let the compositor have it
    was = pn_menu
    if pn_menu != 0:
        my = pn_my()
        pn_menu = 0
        pn_paint_menu()
        if x >= 4 and x < 4 + PN_MW and y >= my and y < pn_y():
            w = (y - my) / PN_MH
            if w < wm_n:
                pn_launches += 1
                puts("shell: launch ")
                wm_name(w)
                putc(10)
                pn_show(w)
            return 1
    if y < pn_y():
        return 0
    if x >= 4 and x < 84:
        if was != 0:
            return 1
        pn_menu = 1
        pn_paint_menu()
        puts("shell: launcher open, ")
        put_u64(wm_n)
        puts(" entries\n")
        return 1
    if x >= PN_X0:
        w = (x - PN_X0) / (PN_BW + 8)
        if w < wm_n and x - PN_X0 - w * (PN_BW + 8) < PN_BW:
            if w == wm_focus and wm_hid[w] == 0:
                pn_minimize(w)
                puts("shell: ")
                wm_name(w)
                puts(" minimized\n")
            else:
                pn_show(w)
                puts("shell: taskbar raise ")
                wm_name(w)
                putc(10)
    return 1
