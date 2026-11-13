# appwin.fsi: screen step 5, a window drawn by a sandboxed user program. A program whose sealed manifest declares
# the gfx capability (CAPB_GFX; seal.fsi sys_need) may open one window (sys 431, at most APP_MAXW x APP_MAXH) and
# fill rectangles (432) and draw text (433) in it. The pixels live in a kernel canvas (app_buf), clipped to the
# window by the kernel; only the process that opened the window may draw (any other handle or process: EBADF).
# The compositor (wm.fsi) shows the canvas as an ordinary window (WM_APP) once the desktop starts; the window
# outlives the program, keeping what it drew. Programs without gfx never reach this code (EACCES at the syscall
# boundary, cap_sys_deny).
APP_MAXW = 320
APP_MAXH = 160
app_buf: u32[51200]
app_w: u64
app_h: u64
app_pid: u64
app_title: u8[64]
app_draws: u64
app_badh: u64

def sys_win_open(w: u64, h: u64, title: u64) -> u64:
    if app_pid != 0:
        return EBUSY
    if w == 0 or h == 0 or w > APP_MAXW or h > APP_MAXH:
        return EINVAL
    if copy_str_user(u64(addr(app_title[0])), title, 48) > 47:
        return EFAULT
    app_w = w
    app_h = h
    app_pid = cur_proc().pid
    i: u64 = 0
    while i < w * h:
        app_buf[i] = u32(WM_BODY)
        i += 1
    return 1

def app_owner(win: u64) -> u64:
    if win == 1 and app_pid != 0 and cur_proc().pid == app_pid:
        return 1
    app_badh += 1
    return 0

def app_fill(x: u64, y: u64, w: u64, h: u64, c: u64):
# clipped to the canvas
    if x >= app_w or y >= app_h:
        return
    x1 = x + w
    if x1 > app_w:
        x1 = app_w
    y1 = y + h
    if y1 > app_h:
        y1 = app_h
    yy = y
    while yy < y1:
        xx = x
        while xx < x1:
            app_buf[yy * app_w + xx] = u32(c & 0xFFFFFF)
            xx += 1
        yy += 1

def sys_win_fill(win: u64, rect: u64, c: u64) -> u64:
    if app_owner(win) == 0:
        return EBADF
    app_fill(rect & 0xFFFF, (rect >> 16) & 0xFFFF, (rect >> 32) & 0xFFFF, rect >> 48, c)
    app_draws += 1
    return 0

def sys_win_text(win: u64, xyc: u64, s: u64) -> u64:
# a NUL-terminated user string (at most 63 bytes), 8x16 glyphs on a transparent background
    if app_owner(win) == 0:
        return EBADF
    t: u8[64]
    if copy_str_user(u64(addr(t[0])), s, 64) > 63:
        return EFAULT
    x = xyc & 0xFFFF
    y = (xyc >> 16) & 0xFFFF
    c = (xyc >> 32) & 0xFFFFFF
    i: u64 = 0
    while t[i] != 0:
        ch = u64(t[i])
        g: u64 = 0
        if ch >= 32 and ch <= 126:
            g = (ch - 32) * 16
        r: u64 = 0
        while r < 16:
            bits = u64(font8x16[g + r])
            k: u64 = 0
            while k < 8:
                if (bits >> (7 - k)) & 1:
                    app_fill(x + i * 8 + k, y + r, 1, 1, c)
                k += 1
            r += 1
        i += 1
    app_draws += 1
    return i

def app_blit(x: u64, y: u64):
# the canvas at screen (x, y), inside the current clip (wm_draw of the WM_APP window)
    y0 = y
    if y0 < gfx_cy0:
        y0 = gfx_cy0
    y1 = y + app_h
    if y1 > gfx_cy1:
        y1 = gfx_cy1
    x0 = x
    if x0 < gfx_cx0:
        x0 = gfx_cx0
    x1 = x + app_w
    if x1 > gfx_cx1:
        x1 = gfx_cx1
    if x0 >= x1 or y0 >= y1:
        return
    yy = y0
    while yy < y1:
        xx = x0
        while xx < x1:
            gfx_buf[yy * gfx_w + xx] = app_buf[(yy - y) * app_w + xx - x]
            xx += 1
        yy += 1
    gfx_damage(x0, y0, x1, y1)

def app_run(name: ptr[u8]) -> u64:
# spawn one sealed program from the ESP and wait for it; its exit code
    sz: u64 = 0
    buf = fs_read_file(name, addr(sz))
    if buf == 0:
        panic("desktop app: program missing")
    t = proc_spawn(buf, sz)
    kfree(buf)
    if u64(t) == 0:
        panic("desktop app: program refused")
    return proc_reap(t)

def desktop_app():
# before the desktop starts (scheduler running): gfxdeny (caps(fs), no gfx) must be refused a window, then gfxapp
# (caps(gfx) only) opens one and draws in it, and is refused a file; the compositor shows its window
    d0 = cap_sys_denials
    if app_run("EFI/Forge/bin/gfxdeny") != 0:
        panic("desktop app: a program without gfx got a window")
    if app_run("EFI/Forge/bin/gfxapp") != 0 or app_pid == 0:
        panic("desktop app: gfxapp failed")
    if cap_sys_denials - d0 != 2 or app_badh != 1:
        panic("desktop app: undeclared capabilities not denied")
    puts("desktop app: gfxapp (pid ")
    put_u64(app_pid)
    puts(") drew ")
    put_u64(app_draws)
    puts(" times into its ")
    put_u64(app_w)
    puts("x")
    put_u64(app_h)
    puts(" window; gfx without the cap, fs without the cap and another handle refused\n")
