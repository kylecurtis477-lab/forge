# wm.fsi: the windowed compositor (screen step 4). Windows are kept as a list with a z-order (wm_z[0] is the
# bottom, wm_z[wm_n - 1] the top); nothing is drawn into a window directly. Any change marks a rectangle, and
# wm_paint repaints it from the models: the desktop background, then every window that intersects it, bottom to
# top (frame, title bar, content), then the mouse cursor, all clipped to the rectangle (gfx_clip). The terminal
# (gfx.fsi) becomes window 0's content: its cells are drawn from term_cell, so text under another window stays
# hidden. A sandboxed program's canvas (appwin.fsi, step 5) is one more window. A left click raises and focuses the topmost window under the cursor; pressing on a floating window's
# title bar and moving drags it. The desktop shell (panel.fsi, step 6) draws a taskbar and launcher above the windows.
WM_N = 6
WM_TB = 16
WM_TERM = 1
WM_ABOUT = 2
WM_HELP = 3
WM_APP = 4  # screen step 5: the canvas a sandboxed program drew (appwin.fsi)
WM_FOCUS = 0x3B6EA8
WM_BLUR = 0x3A4250
WM_EDGE = 0x8899AA
WM_BODY = 0x2A3446
wm_on: u64
wm_n: u64
wm_x: u64[6]
wm_y: u64[6]
wm_w: u64[6]
wm_h: u64[6]
wm_kind: u64[6]
wm_z: u64[6]
wm_focus: u64
wm_drag: u64  # 1 + the window being dragged, 0 when none
wm_ox: u64
wm_oy: u64
wm_btn: u64
wm_paints: u64
wm_raises: u64
wm_moves: u64
wm_cur: u64  # 1 once the input window has placed the cursor
wm_hid: u64[6]  # screen step 6: 1 while minimized from the taskbar (panel.fsi): not drawn, not hit

def wm_add(kind: u64, x: u64, y: u64, w: u64, h: u64) -> u64:
    i = wm_n
    wm_x[i] = x
    wm_y[i] = y
    wm_w[i] = w
    wm_h[i] = h
    wm_kind[i] = kind
    wm_hid[i] = 0
    wm_z[i] = i
    wm_n += 1
    wm_focus = i
    return i

def wm_title(w: u64, x: u64, y: u64, fg: u64, bg: u64) -> u64:
    k = wm_kind[w]
    if k == WM_TERM:
        return gfx_text(x, y, "Terminal: kernel console", fg, bg)
    if k == WM_ABOUT:
        return gfx_text(x, y, "About Forge", fg, bg)
    if k == WM_APP:
        return gfx_text(x, y, addr(app_title[0]), fg, bg)
    return gfx_text(x, y, "Help", fg, bg)

def wm_name(w: u64):
    k = wm_kind[w]
    if k == WM_TERM:
        puts("Terminal")
    elif k == WM_ABOUT:
        puts("About Forge")
    elif k == WM_APP:
        puts(addr(app_title[0]))
    else:
        puts("Help")

def wm_hits(w: u64, x: u64, y: u64) -> u64:
    if wm_hid[w] != 0:
        return 0
    if x >= wm_x[w] and x < wm_x[w] + wm_w[w] and y >= wm_y[w] and y < wm_y[w] + wm_h[w]:
        return 1
    return 0

def wm_meets(w: u64, x0: u64, y0: u64, x1: u64, y1: u64) -> u64:
    if wm_hid[w] != 0:
        return 0
    if wm_x[w] < x1 and wm_x[w] + wm_w[w] > x0 and wm_y[w] < y1 and wm_y[w] + wm_h[w] > y0:
        return 1
    return 0

def wm_draw(w: u64):
# one window inside the current clip: title bar (focused or not), a 1-pixel edge for floating windows, content
    x = wm_x[w]
    y = wm_y[w]
    ww = wm_w[w]
    hh = wm_h[w]
    tb = WM_BLUR
    if w == wm_focus:
        tb = WM_FOCUS
    if wm_kind[w] == WM_TERM:
        tb = GFX_TITLE
        if w == wm_focus:
            tb = WM_FOCUS
    gfx_fill(x, y, ww, WM_TB, tb)
    tx: u64 = x + 8
    if wm_kind[w] == WM_TERM:
        tx = x
    wm_title(w, tx, y, 0xFFFFFF, tb)
    if wm_kind[w] == WM_TERM:
        gfx_fill(x, y + WM_TB, ww, hh - WM_TB, GFX_TERM)
        term_draw(gfx_cx0, gfx_cy0, gfx_cx1, gfx_cy1)
        return
    gfx_fill(x, y + WM_TB, ww, hh - WM_TB, WM_BODY)
    gfx_frame(x, y, ww, hh, 1, WM_EDGE)
    if wm_kind[w] == WM_APP:
        app_blit(x + 1, y + WM_TB)
        return
    cx = x + 16
    cy = y + WM_TB + 16
    if wm_kind[w] == WM_ABOUT:
        gfx_text(cx, cy, "Forge OS: kernel, language and tools", 0xFFFFFF, WM_BODY)
        gfx_text(cx, cy + 16, "written in Forge.", 0xFFFFFF, WM_BODY)
        gfx_text(cx, cy + 48, "Screen step 6: desktop shell", 0xC8D0E0, WM_BODY)
        gfx_text(cx, cy + 64, "(taskbar, launcher, minimize)", 0xC8D0E0, WM_BODY)
    else:
        gfx_text(cx, cy, "Click a window to focus it.", 0xFFFFFF, WM_BODY)
        gfx_text(cx, cy + 16, "Drag a title bar to move it.", 0xFFFFFF, WM_BODY)
        gfx_text(cx, cy + 48, "Keys go to the focused window.", 0xC8D0E0, WM_BODY)

def wm_cur_draw():
# the arrow cursor on top of everything, inside the current clip
    r: u64 = 0
    while r < CUR_H:
        k: u64 = 0
        while k < CUR_W:
            m = cur_shape[r * CUR_W + k]
            if m == 1:
                gfx_fill(cur_x + k, cur_y + r, 1, 1, 0x000000)
            elif m == 2:
                gfx_fill(cur_x + k, cur_y + r, 1, 1, 0xFFFFFF)
            k += 1
        r += 1

def wm_paint(x0: u64, y0: u64, x1: u64, y1: u64):
# repaint the rectangle [x0, x1) x [y0, y1) from the models, bottom to top
    if x1 > gfx_w:
        x1 = gfx_w
    if y1 > gfx_h:
        y1 = gfx_h
    if x0 >= x1 or y0 >= y1:
        return
    gfx_clip(x0, y0, x1, y1)
    desk_bg(x0, y0, x1, y1)
    i: u64 = 0
    while i < wm_n:
        w = wm_z[i]
        if wm_meets(w, x0, y0, x1, y1) != 0:
            wm_draw(w)
        i += 1
    if pn_meets(x0, y0, x1, y1) != 0:
        pn_draw()
    if wm_cur != 0 and cur_x < x1 and cur_x + CUR_W > x0 and cur_y < y1 and cur_y + CUR_H > y0:
        wm_cur_draw()
    gfx_clip(0, 0, gfx_w, gfx_h)
    wm_paints += 1

def wm_paint_win(w: u64):
    wm_paint(wm_x[w], wm_y[w], wm_x[w] + wm_w[w], wm_y[w] + wm_h[w])

def wm_top_at(x: u64, y: u64) -> u64:
# the topmost window under (x, y), or WM_N
    i = wm_n
    while i > 0:
        i -= 1
        if wm_hits(wm_z[i], x, y) != 0:
            return wm_z[i]
    return WM_N

def wm_raise(w: u64):
# move w to the top of the z-order and focus it; repaint it and the window that lost focus
    old = wm_focus
    i: u64 = 0
    while i < wm_n and wm_z[i] != w:
        i += 1
    while i + 1 < wm_n:
        wm_z[i] = wm_z[i + 1]
        i += 1
    wm_z[wm_n - 1] = w
    wm_focus = w
    wm_raises += 1
    if old != w and old < WM_N:
        wm_paint_win(old)
    wm_paint_win(w)
    pn_paint_bar()  # the focused window's taskbar button changes too

def wm_cursor(dx: u64, dy: u64):
# move the cursor (two's complement deltas, clamped to the screen); a dragged window follows it
    ox = cur_x
    oy = cur_y
    nx = cur_x + dx
    if dx & 0x8000000000000000:
        if 0 - dx > cur_x:
            nx = 0
    elif nx >= gfx_w:
        nx = gfx_w - 1
    ny = cur_y + dy
    if dy & 0x8000000000000000:
        if 0 - dy > cur_y:
            ny = 0
    elif ny >= gfx_h:
        ny = gfx_h - 1
    cur_x = nx
    cur_y = ny
    if wm_drag != 0:
        w = wm_drag - 1
        px = wm_x[w]
        py = wm_y[w]
        wx: u64 = 0
        if cur_x > wm_ox:
            wx = cur_x - wm_ox
        wy: u64 = 0
        if cur_y > wm_oy:
            wy = cur_y - wm_oy
        if wx + wm_w[w] > gfx_w:
            wx = gfx_w - wm_w[w]
        if wy + wm_h[w] > gfx_h - PN_H:
            wy = gfx_h - PN_H - wm_h[w]
        wm_x[w] = wx
        wm_y[w] = wy
        wm_moves += 1
        wm_paint(px, py, px + wm_w[w], py + wm_h[w])
        wm_paint_win(w)
    wm_paint(ox, oy, ox + CUR_W, oy + CUR_H)
    wm_paint(cur_x, cur_y, cur_x + CUR_W, cur_y + CUR_H)

def wm_button(b: u64):
# left press: raise and focus the window under the cursor; on a floating window's title bar, start a drag.
# left release: end the drag and report where the window went
    if (b & 1) != 0 and (wm_btn & 1) == 0 and pn_press(cur_x, cur_y) == 0:
        w = wm_top_at(cur_x, cur_y)
        if w < WM_N:
            wm_raise(w)
            puts("wm: focus ")
            wm_name(w)
            putc(10)
            if wm_kind[w] != WM_TERM and cur_y < wm_y[w] + WM_TB:
                wm_drag = w + 1
                wm_ox = cur_x - wm_x[w]
                wm_oy = cur_y - wm_y[w]
    if (b & 1) == 0 and wm_drag != 0:
        w = wm_drag - 1
        wm_drag = 0
        puts("wm: ")
        wm_name(w)
        puts(" moved to ")
        put_u64(wm_x[w])
        puts(",")
        put_u64(wm_y[w])
        putc(10)
    wm_btn = b

def wm_start():
# the desktop's windows: the terminal (window 0, full width under the labels, so the boot harness reads its rows
# whole, ending above the taskbar) and two floating windows that overlap, Help on top
    wm_n = 0
    pn_menu = 0
    wm_add(WM_TERM, 0, 128, gfx_w, gfx_h - 128 - PN_H)
    wm_add(WM_ABOUT, 100, 170, 420, 160)
    wm_add(WM_HELP, 380, 250, 380, 140)
    if app_pid != 0:
        wm_add(WM_APP, 600, 420, app_w + 2, app_h + WM_TB + 1)
    wm_on = 1
    term_wm = 1
    wm_paint(0, 0, gfx_w, gfx_h)
