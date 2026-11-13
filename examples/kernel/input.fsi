# input.fsi: one input event queue for the desktop (screen step 3). The PS/2 interrupt handler (proc.fsi on_kbd,
# shared by IRQ 1 and IRQ 12) sorts bytes by the controller's aux bit: keyboard scan codes keep their path into
# the tty / kbdd journal and, while the desktop runs, are also queued as EV_KEY; mouse bytes go to mouse_byte,
# which assembles the 3-byte PS/2 packets into EV_MOVE and EV_BUTTON events. The desktop takes events with
# ev_pop and moves an arrow cursor drawn over the back buffer (the pixels under it are saved and put back).
# Events are one u64: kind in bits 7:0, a (scan code or button mask) in 31:8, dx in 47:32, dy in 63:48
# (signed 16-bit, screen direction: dy > 0 is down).
EV_N = 256
EV_KEY = 1
EV_MOVE = 2
EV_BUTTON = 3
ev_q: u64[256]
ev_head: u64
ev_tail: u64
ev_lock: u64
ev_on: u64  # 1 while the desktop takes events
ev_dropped: u64
ev_keys: u64
ev_moves: u64
ev_buttons: u64
ms_b: u64[3]
ms_n: u64
ms_btn: u64
ms_resync: u64

def ev_push(kind: u64, a: u64, dx: u64, dy: u64):
# from the interrupt handler (interrupts off)
    spin_lock(addr(ev_lock))
    if ev_head - ev_tail >= EV_N:
        ev_dropped += 1
    else:
        ev_q[ev_head % EV_N] = kind | (a << 8) | ((dx & 0xFFFF) << 32) | ((dy & 0xFFFF) << 48)
        ev_head += 1
    spin_unlock(addr(ev_lock))

def ev_pop() -> u64:
# the oldest event, or 0 when the queue is empty
    f = irq_save()
    spin_lock(addr(ev_lock))
    e: u64 = 0
    if ev_tail != ev_head:
        e = ev_q[ev_tail % EV_N]
        ev_tail += 1
    spin_unlock(addr(ev_lock))
    irq_restore(f)
    return e

def ev_s16(v: u64) -> u64:
# sign-extend a 16-bit field (two's complement in u64)
    v = v & 0xFFFF
    if v & 0x8000:
        return v | 0xFFFFFFFFFFFF0000
    return v

def kbd_key_event(sc: u64):
# a keyboard scan code while the desktop takes events
    if ev_on != 0:
        ev_keys += 1
        ev_push(EV_KEY, sc, 0, 0)

def mouse_byte(b: u64):
# PS/2 mouse packet: flags (bit 3 always set; bits 0-2 buttons L/R/M; 4/5 X/Y sign; 6/7 overflow), dx, dy
    if ms_n == 0 and (b & 8) == 0:
        ms_resync += 1
        return
    ms_b[ms_n] = b
    ms_n += 1
    if ms_n < 3:
        return
    ms_n = 0
    fl = ms_b[0]
    if (fl & 0xC0) != 0:
        return
    dx = ms_b[1]
    if fl & 0x10:
        dx = dx | 0xFFFFFFFFFFFFFF00
    dy = ms_b[2]
    if fl & 0x20:
        dy = dy | 0xFFFFFFFFFFFFFF00
    dy = 0 - dy
    if ev_on == 0:
        return
    if dx != 0 or dy != 0:
        ev_moves += 1
        ev_push(EV_MOVE, fl & 7, dx, dy)
    if (fl & 7) != ms_btn:
        ms_btn = fl & 7
        ev_buttons += 1
        ev_push(EV_BUTTON, ms_btn, 0, 0)

# the arrow cursor: 12 rows of an 8-pixel mask (1 = outline, 2 = fill), drawn over the back buffer
CUR_W = 8
CUR_H = 12
cur_shape: u8[96] = [1, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 1, 2, 1, 0, 0, 0, 0, 0, 1, 2, 2, 1, 0, 0, 0, 0, 1, 2, 2, 2, 1, 0, 0, 0, 1, 2, 2, 2, 2, 1, 0, 0, 1, 2, 2, 2, 2, 2, 1, 0, 1, 2, 2, 2, 2, 2, 2, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 2, 1, 2, 1, 0, 0, 0, 1, 1, 0, 1, 2, 1, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0]
cur_save: u32[96]
cur_x: u64
cur_y: u64
cur_shown: u64

def cur_hide():
    if cur_shown == 0:
        return
    r: u64 = 0
    while r < CUR_H:
        k: u64 = 0
        while k < CUR_W:
            if cur_x + k < gfx_w and cur_y + r < gfx_h:
                gfx_buf[(cur_y + r) * gfx_w + cur_x + k] = cur_save[r * CUR_W + k]
            k += 1
        r += 1
    gfx_damage(cur_x, cur_y, cur_x + CUR_W, cur_y + CUR_H)
    cur_shown = 0

def cur_show():
    r: u64 = 0
    while r < CUR_H:
        k: u64 = 0
        while k < CUR_W:
            if cur_x + k < gfx_w and cur_y + r < gfx_h:
                i = (cur_y + r) * gfx_w + cur_x + k
                cur_save[r * CUR_W + k] = gfx_buf[i]
                m = cur_shape[r * CUR_W + k]
                if m == 1:
                    gfx_buf[i] = u32(0x000000)
                elif m == 2:
                    gfx_buf[i] = u32(0xFFFFFF)
            k += 1
        r += 1
    gfx_damage(cur_x, cur_y, cur_x + CUR_W, cur_y + CUR_H)
    cur_shown = 1

def cur_move(dx: u64, dy: u64):
# relative move, clamped to the screen (dx, dy are two's complement)
    cur_hide()
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
    cur_show()

def ps2_poll():
# with interrupts off (the desktop's input window at the end of boot): take every waiting controller byte the
# way on_kbd does, but only into the event queue
    while 1:
        st = ps2_io(0x64, 0, 0)
        if (st & 1) == 0:
            return
        sc = ps2_io(0x60, 0, 0)
        if (st & 0x20) != 0:
            mouse_byte(sc)
        else:
            kbd_key_event(sc)

def desktop_input():
# screen step 3: mouse moves move the cursor, button changes and key presses are echoed in the terminal. The boot
# test sends them through QEMU's monitor once it sees "desktop: input ready"; this returns once a left press and
# its release and a key press have arrived, or after 30 s
    mouse_init()
    cur_x = gfx_w / 2
    cur_y = gfx_h / 2
    if wm_on != 0:
        wm_cur = 1
        wm_paint(cur_x, cur_y, cur_x + CUR_W, cur_y + CUR_H)
    else:
        cur_show()
    gfx_present()
    ev_on = 1
    puts("desktop: input ready, mouse ")
    if ms_ready != 0:
        puts("on\n")
    else:
        puts("not answering\n")
    t0 = rdtsc()
    pressed: u64 = 0
    released: u64 = 0
    key: u64 = 0
    while rdtsc() - t0 < tsc_per_ms * 30000:
        ps2_poll()
        e = ev_pop()
        if e == 0:
            cpu_relax()
            continue
        kind = e & 0xFF
        a = (e >> 8) & 0xFFFFFF
        if kind == EV_MOVE:
            if wm_on != 0:
                wm_cursor(ev_s16(e >> 32), ev_s16(e >> 48))
            else:
                cur_move(ev_s16(e >> 32), ev_s16(e >> 48))
            gfx_present()
        elif kind == EV_BUTTON:
            puts("mouse: buttons ")
            put_u64(a)
            puts(" at ")
            put_u64(cur_x)
            puts(",")
            put_u64(cur_y)
            putc(10)
            if wm_on != 0:
                wm_button(a)
                gfx_present()
            if (a & 1) != 0:
                pressed = 1
            elif pressed != 0:
                released = 1
        elif kind == EV_KEY and a < 0x80:
            puts("key: scan code ")
            put_hex8(a)
            putc(10)
            key = 1
        if released != 0 and key != 0:
            break
    ev_on = 0
    puts("desktop: input events: ")
    put_u64(ev_moves)
    puts(" moves, ")
    put_u64(ev_buttons)
    puts(" button changes, ")
    put_u64(ev_keys)
    puts(" key codes, cursor at ")
    put_u64(cur_x)
    puts(",")
    put_u64(cur_y)
    puts(", dropped ")
    put_u64(ev_dropped)
    putc(10)
    if wm_on != 0:
        puts("wm: ")
        put_u64(wm_raises)
        puts(" raises, ")
        put_u64(wm_moves)
        puts(" drag steps, ")
        put_u64(wm_paints)
        puts(" damage repaints\n")
