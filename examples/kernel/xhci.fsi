# Phase 8: xHCI host controller. Reset and run with a command ring and one event ring (polled), scan
# the root ports, reset connected USB2 ports, then for the first device: Enable Slot, Address Device
# and a GET_DESCRIPTOR(device) control transfer on endpoint 0. Then (HID step 1) the configuration
# descriptor is read and walked for a HID boot keyboard interface and its interrupt IN endpoint, and the
# device is configured: SET_CONFIGURATION, SET_PROTOCOL(boot) and SET_IDLE(0). HID step 2: Configure Endpoint
# adds the interrupt IN endpoint with its own transfer ring of Normal TRBs (8-byte boot reports), and
# xh_kbd_poll reads the reports (polled, bounded), decoding newly pressed keys to text; tests/os/drivers.sh
# types into QEMU's usb-kbd through the monitor. HID step 3: the controller then switches to MSI-X (vector
# 0x52, cpu0): on_xhci drains the event ring under rq_lock and pushes each new key into the tty input path
# (tty_push_l, like the PS/2 and COM1 handlers), so a USB keyboard types into the shell.
xh_cap: u64
xh_op: u64
xh_rt: u64
xh_db: u64
xh_csz: u64  # context size, 32 or 64
xh_slots: u64
xh_ports: u64
xh_dcbaa: u64
xh_dcbaa_pa: u64
xh_cr: u64  # command ring (one page, 255 TRBs + link)
xh_cr_pa: u64
xh_er: u64  # event ring (one page, 256 TRBs)
xh_er_pa: u64
xh_er_i: u64
xh_er_c: u64
xh_erst: u64
xh_erst_pa: u64
xh_ev: u64[4]  # last event TRB
xh_connected: u64
xh_bdf: u64

xh_rb: u64[3]  # producer rings: 0 = command ring, 1 = EP0 transfer ring of the probed device, 2 = its keyboard IN endpoint
xh_rpa: u64[3]
xh_ri: u64[3]
xh_rc: u64[3]

def xh_ring_put(r: u64, p: u64, s: u64, c: u64):
# write one TRB; wraps through a link TRB in the last slot (toggle cycle)
    ring = xh_rb[r]
    t = ring + xh_ri[r] * 16
    mmio_wr(t, 8, p)
    mmio_wr(t + 8, 4, s)
    mfence()
    mmio_wr(t + 12, 4, c | xh_rc[r])
    xh_ri[r] += 1
    if xh_ri[r] == 255:
        l = ring + 255 * 16
        mmio_wr(l, 8, xh_rpa[r])
        mmio_wr(l + 8, 4, 0)
        mfence()
        mmio_wr(l + 12, 4, (6 << 10) | 2 | xh_rc[r])
        xh_ri[r] = 0
        xh_rc[r] ^= 1

def xh_event(want: u64) -> u64:
    return xh_event_us(want, 1000000)

def xh_event_us(want: u64, us: u64) -> u64:
# poll the event ring for a TRB of type want for about us microseconds; returns its completion code (0xFF on timeout)
    t: u64 = 0
    while t < us:
        e = xh_er + xh_er_i * 16
        c = mmio_rd(e + 12, 4)
        if (c & 1) == xh_er_c:
            xh_ev[0] = mmio_rd(e, 4)
            xh_ev[1] = mmio_rd(e + 4, 4)
            xh_ev[2] = mmio_rd(e + 8, 4)
            xh_ev[3] = c
            xh_er_i += 1
            if xh_er_i == 256:
                xh_er_i = 0
                xh_er_c ^= 1
            mmio_wr(xh_rt + 0x38, 8, (xh_er_pa + xh_er_i * 16) | 8)  # ERDP, clear EHB
            if ((c >> 10) & 0x3F) == want:
                return xh_ev[2] >> 24
        else:
            t += 1
            tsc_delay_us(1)
    return 0xFF

def xh_command(p: u64, s: u64, c: u64) -> u64:
    xh_ring_put(0, p, s, c)
    mmio_wr(xh_db, 4, 0)
    return xh_event(33)

def xh_wait(r: u64, mask: u64, want: u64) -> u64:
    t: u64 = 0
    while (mmio_rd(r, 4) & mask) != want:
        t += 1
        if t > 200000:
            return 0
        tsc_delay_us(10)
    return 1

def xhci_init() -> u64:
    f = pci_find(0x0C0330, 0)
    if f == 0:
        puts("xHCI: not present\n")
        return 0
    xh_bdf = f - 1
    xh_cap = pci_map_bar(f - 1, 0, 0x10000)
    if xh_cap == 0:
        puts("xHCI: BAR0 not mappable\n")
        return 0
    c0 = mmio_rd(xh_cap, 4)  # CAPLENGTH + HCIVERSION, read as one dword
    caplen = c0 & 0xFF
    ver = c0 >> 16
    hcs1 = mmio_rd(xh_cap + 4, 4)
    hcs2 = mmio_rd(xh_cap + 8, 4)
    hcc1 = mmio_rd(xh_cap + 0x10, 4)
    xh_op = xh_cap + caplen
    xh_db = xh_cap + (mmio_rd(xh_cap + 0x14, 4) & ~u64(3))
    xh_rt = xh_cap + (mmio_rd(xh_cap + 0x18, 4) & ~u64(0x1F))
    xh_slots = hcs1 & 0xFF
    xh_ports = hcs1 >> 24
    xh_csz = 32
    if (hcc1 & 4) != 0:
        xh_csz = 64
    nsp = ((hcs2 >> 27) & 0x1F) | (((hcs2 >> 21) & 0x1F) << 5)
    # halt, then reset
    mmio_wr(xh_op, 4, mmio_rd(xh_op, 4) & ~u64(1))
    if xh_wait(xh_op + 4, 1, 1) == 0:
        puts("xHCI: did not halt\n")
        return 0
    mmio_wr(xh_op, 4, 2)
    if xh_wait(xh_op, 2, 0) == 0 or xh_wait(xh_op + 4, 0x800, 0) == 0:
        puts("xHCI: reset timed out\n")
        return 0
    if xh_slots > 8:
        xh_slots = 8
    mmio_wr(xh_op + 0x38, 4, xh_slots)
    xh_dcbaa = dma_alloc(1, addr(xh_dcbaa_pa))
    xh_cr = dma_alloc(1, addr(xh_cr_pa))
    xh_er = dma_alloc(1, addr(xh_er_pa))
    xh_erst = dma_alloc(1, addr(xh_erst_pa))
    if xh_dcbaa == 0 or xh_cr == 0 or xh_er == 0 or xh_erst == 0:
        panic("xHCI: out of DMA memory")
    if nsp != 0:
    # scratchpad buffers: an array of page pointers in DCBAA[0]
        if nsp > 64:
            puts("xHCI: too many scratchpad buffers\n")
            return 0
        sa_pa: u64 = 0
        sa = dma_alloc(1, addr(sa_pa))
        i: u64 = 0
        while i < nsp:
            pg: u64 = 0
            if dma_alloc(1, addr(pg)) == 0:
                panic("xHCI: out of DMA memory")
            mmio_wr(sa + i * 8, 8, pg)
            i += 1
        mmio_wr(xh_dcbaa, 8, sa_pa)
    mmio_wr(xh_op + 0x30, 8, xh_dcbaa_pa)
    xh_rb[0] = xh_cr
    xh_rpa[0] = xh_cr_pa
    xh_ri[0] = 0
    xh_rc[0] = 1
    mmio_wr(xh_op + 0x18, 8, xh_cr_pa | 1)
    xh_er_i = 0
    xh_er_c = 1
    mmio_wr(xh_erst, 8, xh_er_pa)
    mmio_wr(xh_erst + 8, 4, 256)
    ir = xh_rt + 0x20
    mmio_wr(ir + 8, 4, 1)  # ERSTSZ
    mmio_wr(ir + 0x18, 8, xh_er_pa)  # ERDP
    mmio_wr(ir + 0x10, 8, xh_erst_pa)  # ERSTBA
    mmio_wr(xh_op, 4, 1)  # run, no interrupts
    if xh_wait(xh_op + 4, 1, 0) == 0:
        puts("xHCI: did not start\n")
        return 0
    puts("xHCI: version ")
    put_hex16(ver)
    puts(", ")
    put_u64(xh_ports)
    puts(" ports, ")
    put_u64(hcs1 & 0xFF)
    puts(" slots, ")
    put_u64(xh_csz)
    puts("-byte contexts, ")
    put_u64(nsp)
    puts(" scratchpad pages; running\n")
    # Command ring sanity: a No Op command must complete with success
    if xh_command(0, 0, 23 << 10) != 1:
        puts("xHCI: no-op command failed\n")
        return 0
    puts("xHCI: command ring OK (No Op completed)\n")
    return 1

def xh_portsc(p: u64) -> u64:
    return xh_op + 0x400 + p * 0x10

def xhci_scan() -> u64:
# reset each connected port and talk to the first device that comes up enabled
    if xh_op == 0:
        return 0
    found: u64 = 0
    p: u64 = 0
    tsc_delay_us(20000)
    while p < xh_ports:
        r = xh_portsc(p)
        v = mmio_rd(r, 4)
        if (v & 1) != 0:
            xh_connected += 1
            if (v & 2) == 0:
                mmio_wr(r, 4, (v & 0x0E00C200) | 0x10)  # PR (port reset)
                xh_wait(r, 0x200000, 0x200000)
                v = mmio_rd(r, 4)
                mmio_wr(r, 4, (v & 0x0E00C200) | 0x00FE0000)  # clear change bits
            v = mmio_rd(r, 4)
            sp = (v >> 10) & 0xF
            puts("xHCI: port ")
            put_u64(p + 1)
            puts(": device connected, speed ")
            put_u64(sp)
            if (v & 2) != 0:
                puts(", enabled\n")
                if found == 0 and xh_device(p + 1, sp) != 0:
                    found = 1
            else:
                puts(", not enabled\n")
        p += 1
    puts("xHCI: ")
    put_u64(xh_connected)
    puts(" connected ports\n")
    return found

def xh_device(port: u64, speed: u64) -> u64:
    cc = xh_command(0, 0, 9 << 10)  # Enable Slot
    slot = xh_ev[3] >> 24
    if cc != 1 or slot == 0 or slot > xh_slots:
        puts("xHCI: Enable Slot failed\n")
        return 0
    puts("xHCI: Enable Slot -> slot ")
    put_u64(slot)
    putc(10)
    out_pa: u64 = 0
    in_pa: u64 = 0
    tr_pa: u64 = 0
    buf_pa: u64 = 0
    outc = dma_alloc(1, addr(out_pa))
    inc = dma_alloc(1, addr(in_pa))
    tr = dma_alloc(1, addr(tr_pa))
    buf = dma_alloc(1, addr(buf_pa))
    if outc == 0 or inc == 0 or tr == 0 or buf == 0:
        panic("xHCI: out of DMA memory")
    mmio_wr(xh_dcbaa + slot * 8, 8, out_pa)
    mps: u64 = 8
    if speed == 3:
        mps = 64
    if speed >= 4:
        mps = 512
    mmio_wr(inc + 4, 4, 3)  # add slot + EP0
    sc = inc + xh_csz
    mmio_wr(sc, 4, (speed << 20) | (1 << 27))
    mmio_wr(sc + 4, 4, port << 16)
    ep = inc + 2 * xh_csz
    mmio_wr(ep + 4, 4, (3 << 1) | (4 << 3) | (mps << 16))
    mmio_wr(ep + 8, 8, tr_pa | 1)
    mmio_wr(ep + 16, 4, 8)
    if xh_command(in_pa, 0, (11 << 10) | (slot << 24)) != 1:
        puts("xHCI: Address Device failed\n")
        return 0
    dev_addr = mmio_rd(outc + 12, 4) & 0xFF
    puts("xHCI: Address Device OK, USB address ")
    put_u64(dev_addr)
    putc(10)
    # GET_DESCRIPTOR(DEVICE): setup (IN data), 18-byte data stage, status (OUT, interrupt on completion)
    xh_rb[1] = tr
    xh_rpa[1] = tr_pa
    xh_ri[1] = 0
    xh_rc[1] = 1
    n: u64 = 18
    xh_ring_put(1, 0x80 | (6 << 8) | (0x0100 << 16) | (n << 48), 8, (2 << 10) | (1 << 6) | (3 << 16))
    xh_ring_put(1, buf_pa, n, (3 << 10) | (1 << 16))
    xh_ring_put(1, 0, 0, (4 << 10) | (1 << 5))
    mmio_wr(xh_db + slot * 4, 4, 1)
    cc = xh_event(32)
    if cc != 1 and cc != 13:
        puts("xHCI: GET_DESCRIPTOR failed, code ")
        put_u64(cc)
        putc(10)
        return 0
    puts("xHCI: device descriptor: USB ")
    put_hex16(mmio_rd(buf + 2, 2))
    puts(", class ")
    put_u64(mmio_rd(buf + 4, 1))
    puts(", max packet ")
    put_u64(mmio_rd(buf + 7, 1))
    puts(", id ")
    put_hex16(mmio_rd(buf + 8, 2))
    putc(58)
    put_hex16(mmio_rd(buf + 10, 2))
    putc(10)
    if xh_hid_setup(slot, buf, buf_pa) != 0 and xh_hid_endpoint(slot, inc, in_pa, outc, speed, port) != 0:
        xh_kbd_poll(8000)
        xh_irq_init()
    return 1

def xh_ctrl(slot: u64, req: u64, n: u64, pa: u64) -> u64:
# one control transfer on EP0: req is the 8-byte setup packet without wLength; n > 0 reads n bytes to pa
    if n > 0:
        xh_ring_put(1, req | (n << 48), 8, (2 << 10) | (1 << 6) | (3 << 16))
        xh_ring_put(1, pa, n, (3 << 10) | (1 << 16))
        xh_ring_put(1, 0, 0, (4 << 10) | (1 << 5))
    else:
        xh_ring_put(1, req, 8, (2 << 10) | (1 << 6))
        xh_ring_put(1, 0, 0, (4 << 10) | (1 << 5) | (1 << 16))
    mmio_wr(xh_db + slot * 4, 4, 1)
    cc = xh_event(32)
    if cc == 13:
        cc = 1
    return cc

xh_kbd_if: u64  # HID boot keyboard found on the first device: interface number, IN endpoint address, max packet, interval
xh_kbd_ep: u64
xh_kbd_mps: u64
xh_kbd_ival: u64

def xh_hid_setup(slot: u64, buf: u64, buf_pa: u64) -> u64:
# GET_DESCRIPTOR(CONFIGURATION): 9 bytes for wTotalLength, then the whole set (at most 1024 bytes, at buf+1024)
    cfg = buf + 1024
    cfg_pa = buf_pa + 1024
    if xh_ctrl(slot, 0x80 | (6 << 8) | (0x0200 << 16), 9, cfg_pa) != 1:
        puts("xHCI: GET_DESCRIPTOR(configuration) failed\n")
        return 0
    tot = mmio_rd(cfg + 2, 2)
    if tot < 9 or tot > 1024:
        puts("xHCI: configuration descriptor length out of range\n")
        return 0
    if xh_ctrl(slot, 0x80 | (6 << 8) | (0x0200 << 16), tot, cfg_pa) != 1:
        puts("xHCI: GET_DESCRIPTOR(configuration) failed\n")
        return 0
    cv = mmio_rd(cfg + 5, 1)
    nif = mmio_rd(cfg + 4, 1)
    # walk the descriptors: interface (4) and endpoint (5); the endpoint that follows a boot keyboard
    # interface (class 3, subclass 1, protocol 1) and is interrupt IN is the keyboard's report endpoint
    o: u64 = 0
    cur: u64 = 0xFF
    kif: u64 = 0xFF
    kep: u64 = 0
    while o + 2 <= tot:
        ln = mmio_rd(cfg + o, 1)
        ty = mmio_rd(cfg + o + 1, 1)
        if ln < 2 or o + ln > tot:
            break
        if ty == 4 and ln >= 9:
            cur = 0xFF
            if mmio_rd(cfg + o + 5, 1) == 3 and mmio_rd(cfg + o + 6, 1) == 1 and mmio_rd(cfg + o + 7, 1) == 1:
                cur = mmio_rd(cfg + o + 2, 1)
        if ty == 5 and ln >= 7 and cur != 0xFF and kep == 0:
            ea = mmio_rd(cfg + o + 2, 1)
            if (ea & 0x80) != 0 and (mmio_rd(cfg + o + 3, 1) & 3) == 3:
                kif = cur
                kep = ea
                # descriptors are byte-packed: wMaxPacketSize may be unaligned (mmio_rd requires alignment)
                xh_kbd_mps = (mmio_rd(cfg + o + 4, 1) | (mmio_rd(cfg + o + 5, 1) << 8)) & 0x7FF
                xh_kbd_ival = mmio_rd(cfg + o + 6, 1)
        o += ln
    puts("xHCI: configuration ")
    put_u64(cv)
    puts(", ")
    put_u64(tot)
    puts(" bytes, ")
    put_u64(nif)
    puts(" interface(s)\n")
    if kep == 0:
        puts("xHCI: no HID boot keyboard interface\n")
        return 0
    xh_kbd_if = kif
    xh_kbd_ep = kep
    puts("xHCI: HID boot keyboard: interface ")
    put_u64(kif)
    puts(", interrupt IN endpoint 0x")
    put_hex8(kep)
    puts(", max packet ")
    put_u64(xh_kbd_mps)
    puts(", interval ")
    put_u64(xh_kbd_ival)
    putc(10)
    # SET_CONFIGURATION(cv); class requests to the interface: SET_PROTOCOL(0 = boot), SET_IDLE(0)
    c1 = xh_ctrl(slot, (9 << 8) | (cv << 16), 0, 0)
    c2 = xh_ctrl(slot, 0x21 | (0x0B << 8) | (kif << 32), 0, 0)
    c3 = xh_ctrl(slot, 0x21 | (0x0A << 8) | (kif << 32), 0, 0)
    if c1 != 1 or c2 != 1 or c3 != 1:
        puts("xHCI: HID setup failed, codes ")
        put_u64(c1)
        putc(32)
        put_u64(c2)
        putc(32)
        put_u64(c3)
        putc(10)
        return 0
    puts("xHCI: SET_CONFIGURATION ")
    put_u64(cv)
    puts(" OK, SET_PROTOCOL(boot) OK, SET_IDLE(0) OK\n")
    return 1

# ---- HID step 2: the interrupt IN endpoint ----
XH_KBD_NTRB = 8  # Normal TRBs kept queued, one 8-byte report buffer each
xh_rep: u64  # report buffers (one DMA page)
xh_rep_pa: u64
xh_kbd_slot: u64
xh_kbd_dci: u64
xh_kbd_last: u64[6]  # key usages of the previous report

def xh_kbd_queue(pa: u64):
# one Normal TRB for an 8-byte report: interrupt on completion and on a short packet
    xh_ring_put(2, pa, 8, (1 << 10) | (1 << 5) | (1 << 2))

def xh_hid_endpoint(slot: u64, inc: u64, in_pa: u64, outc: u64, speed: u64, port: u64) -> u64:
# Configure Endpoint: input context adds the slot context (Context Entries = DCI) and the endpoint context
    dci = 2 * (xh_kbd_ep & 0xF) + 1
    tr_pa: u64 = 0
    tr = dma_alloc(1, addr(tr_pa))
    xh_rep = dma_alloc(1, addr(xh_rep_pa))
    if tr == 0 or xh_rep == 0:
        panic("xHCI: out of DMA memory")
    # bInterval is in frames (ms) at full/low speed, an exponent at high speed and above; the endpoint
    # context wants 125 us units as an exponent: 2^e * 125 us
    e: u64 = 3
    if speed >= 3:
        e = xh_kbd_ival - 1
    else:
        while e < 10 and (u64(1) << (e + 1)) <= xh_kbd_ival * 8:
            e += 1
    if e > 15:
        e = 15
    mmio_wr(inc, 4, 0)
    mmio_wr(inc + 4, 4, 1 | (u64(1) << dci))
    sc = inc + xh_csz
    mmio_wr(sc, 4, (speed << 20) | (dci << 27))
    mmio_wr(sc + 4, 4, port << 16)
    ep = inc + (dci + 1) * xh_csz
    mmio_wr(ep, 4, e << 16)
    mmio_wr(ep + 4, 4, (3 << 1) | (7 << 3) | (xh_kbd_mps << 16))  # CErr 3, Interrupt IN
    mmio_wr(ep + 8, 8, tr_pa | 1)
    mmio_wr(ep + 16, 4, 8 | (xh_kbd_mps << 16))  # average TRB length, max ESIT payload
    xh_rb[2] = tr
    xh_rpa[2] = tr_pa
    xh_ri[2] = 0
    xh_rc[2] = 1
    cc = xh_command(in_pa, 0, (12 << 10) | (slot << 24))
    st = mmio_rd(outc + dci * xh_csz, 4) & 7
    if cc != 1 or st != 1:
        puts("xHCI: Configure Endpoint failed, code ")
        put_u64(cc)
        puts(", endpoint state ")
        put_u64(st)
        putc(10)
        return 0
    xh_kbd_slot = slot
    xh_kbd_dci = dci
    i: u64 = 0
    while i < XH_KBD_NTRB:
        xh_kbd_queue(xh_rep_pa + i * 8)
        i += 1
    mmio_wr(xh_db + slot * 4, 4, dci)
    puts("xHCI: Configure Endpoint OK: endpoint 0x")
    put_hex8(xh_kbd_ep)
    puts(" -> DCI ")
    put_u64(dci)
    puts(", interrupt IN, interval 2^")
    put_u64(e)
    puts(" x 125 us, state running; ")
    put_u64(XH_KBD_NTRB)
    puts(" report TRBs queued\n")
    return 1

def xh_usage_char(u: u64, shift: u64) -> u64:
# HID boot keyboard usage -> ASCII (letters, digits, space, enter, a few marks); 0 if not printable here
    if u >= 4 and u <= 29:
        if shift != 0:
            return 65 + u - 4
        return 97 + u - 4
    if u >= 30 and u <= 38:
        return 49 + u - 30
    if u == 39:
        return 48
    if u == 40:
        return 10
    if u == 44:
        return 32
    if u == 45:
        return 45
    if u == 55:
        return 46
    return 0

xh_text: u8[64]  # text decoded by the polled check (sink 0)
xh_nt: u64

def xh_kbd_report(b: u64, tty: u64) -> u64:
# decode one 8-byte boot report at b: each key usage absent from the previous report is a new key press.
# Sink 0 appends to xh_text (Enter ends the polled check: returns 1); sink 1 pushes into the tty input path
# (rq_lock held, interrupt context). Then the report becomes the previous one.
    enter: u64 = 0
    shift = mmio_rd(b, 1) & 0x22
    k: u64 = 2
    while k < 8:
        u = mmio_rd(b + k, 1)
        if u >= 4:
            seen: u64 = 0
            j: u64 = 0
            while j < 6:
                if xh_kbd_last[j] == u:
                    seen = 1
                j += 1
            if seen == 0:
                ch = xh_usage_char(u, shift)
                if tty != 0 and ch != 0:
                    xh_keys += 1
                    tty_push_l(ch)
                elif ch == 10:
                    enter = 1
                elif ch != 0 and xh_nt < 63:
                    xh_text[xh_nt] = u8(ch)
                    xh_nt += 1
        k += 1
    k = 0
    while k < 6:
        xh_kbd_last[k] = mmio_rd(b + 2 + k, 1)
        k += 1
    return enter

def xh_kbd_poll(ms: u64):
# read reports for up to ms milliseconds or until Enter; each newly pressed key (absent from the previous
# report) is decoded; every completed TRB is re-queued with its own buffer
    puts("xHCI: HID boot keyboard: waiting for key reports\n")
    xh_nt = 0
    nrep: u64 = 0
    nerr: u64 = 0
    done: u64 = 0
    i: u64 = 0
    while i < 6:
        xh_kbd_last[i] = 0
        i += 1
    t: u64 = 0
    while t < ms and done == 0:
        cc = xh_event_us(32, 1000)
        if cc == 0xFF:
            t += 1
            continue
        tp = xh_ev[0] | (xh_ev[1] << 32)
        if tp < xh_rpa[2] or tp >= xh_rpa[2] + 4096:
            continue
        bpa = mmio_rd(xh_rb[2] + (tp - xh_rpa[2]), 8)
        if bpa < xh_rep_pa or bpa >= xh_rep_pa + XH_KBD_NTRB * 8:
            continue
        if cc == 1 or cc == 13:
            nrep += 1
            if xh_kbd_report(xh_rep + (bpa - xh_rep_pa), 0) != 0:
                done = 1
        else:
            nerr += 1
        xh_kbd_queue(bpa)
        mmio_wr(xh_db + xh_kbd_slot * 4, 4, xh_kbd_dci)
    xh_text[xh_nt] = 0
    puts("xHCI: HID boot keyboard: ")
    put_u64(nrep)
    puts(" reports, ")
    put_u64(nerr)
    puts(" errors, typed \"")
    puts(addr(xh_text[0]))
    puts("\"")
    if done != 0:
        puts(" + Enter")
    putc(10)

# ---- HID step 3: MSI-X and the tty ----
VEC_XHCI = 0x52
xh_irqs: u64
xh_keys: u64  # keys pushed into the tty from interrupts
xh_irq_on: u64

@interrupt
def on_xhci(frame: ptr[u64]):
    spin_lock(addr(rq_lock))
    xh_irqs += 1
    ir = xh_rt + 0x20
    mmio_wr(ir, 4, 3)  # IMAN: clear IP (write 1), keep IE
    mmio_wr(xh_op + 4, 4, 8)  # USBSTS.EINT (write 1 to clear)
    xh_irq_drain()
    spin_unlock(addr(rq_lock))
    lapic_eoi()

def xh_irq_drain():
# rq_lock held: consume every pending event; keyboard reports go to the tty, their TRBs are re-queued
    n: u64 = 0
    while n < 256:
        e = xh_er + xh_er_i * 16
        c = mmio_rd(e + 12, 4)
        if (c & 1) != xh_er_c:
            break
        tp = mmio_rd(e, 8)
        cc = mmio_rd(e + 8, 4) >> 24
        xh_er_i += 1
        if xh_er_i == 256:
            xh_er_i = 0
            xh_er_c ^= 1
        n += 1
        if ((c >> 10) & 0x3F) == 32 and tp >= xh_rpa[2] and tp < xh_rpa[2] + 4096:
            bpa = mmio_rd(xh_rb[2] + (tp - xh_rpa[2]), 8)
            if bpa >= xh_rep_pa and bpa < xh_rep_pa + XH_KBD_NTRB * 8:
                if cc == 1 or cc == 13:
                    xh_kbd_report(xh_rep + (bpa - xh_rep_pa), 1)
                xh_kbd_queue(bpa)
                mmio_wr(xh_db + xh_kbd_slot * 4, 4, xh_kbd_dci)
    mmio_wr(xh_rt + 0x38, 8, (xh_er_pa + xh_er_i * 16) | 8)  # ERDP, clear EHB

def xh_irq_init():
# after the polled check: MSI-X table entry 0 -> vector 0x52 on cpu0, interrupter 0 enabled, USBCMD.INTE.
# The event ring has a single consumer from here on (on_xhci); no command is issued after this.
    if iommu_ir != 0:
        puts("xHCI: interrupt remapping on and no IRTE for xHCI yet: keyboard stays polled\n")
        return
    c = pci_rd32(xh_bdf, 0x34) & 0xFC
    g: u64 = 0
    while c != 0 and g < 48:
        h = pci_rd32(xh_bdf, c)
        if (h & 0xFF) == 0x11:
            break
        c = (h >> 8) & 0xFC
        g += 1
    if c == 0 or g == 48:
        puts("xHCI: no MSI-X, keyboard stays polled\n")
        return
    tb = pci_rd32(xh_bdf, c + 4)
    bir = tb & 7
    bar = pci_rd32(xh_bdf, 0x10 + bir * 4)
    base = bar & 0xFFFFFFF0
    if (bar & 6) == 4:
        base = base | (pci_rd32(xh_bdf, 0x14 + bir * 4) << 32)
    if (bar & 1) != 0 or base == 0 or base >= 0x100000000:
        puts("xHCI: MSI-X table not reachable, keyboard stays polled\n")
        return
    t = base + (tb & 0xFFFFFFF8)
    # the table usually sits inside BAR0, which is mapped already; otherwise map its 16 bytes
    v = xh_cap + (t - (pci_rd32(xh_bdf, 0x10) & 0xFFFFFFF0))
    if bir != 0 or t - (pci_rd32(xh_bdf, 0x10) & 0xFFFFFFF0) >= 0x10000:
        v = mmio_add(p2v(t), 16)
    idt_set(VEC_XHCI, u64(on_xhci), 0)
    mmio_wr(v, 4, 0xFEE00000 | (cpu_to_apic[0] << 12))
    mmio_wr(v + 4, 4, 0)
    mmio_wr(v + 8, 4, VEC_XHCI)
    mmio_wr(v + 12, 4, 0)  # vector control: unmasked
    pci_wr32(xh_bdf, c, (pci_rd32(xh_bdf, c) | 0x80000000) & ~u64(0x40000000))
    f = irq_save()
    spin_lock(addr(rq_lock))
    xh_irq_drain()  # anything left from the polled phase
    ir = xh_rt + 0x20
    mmio_wr(ir + 4, 4, 4000)  # IMOD: 1 ms moderation (250 ns units)
    mmio_wr(ir, 4, 3)  # IMAN: IE, clear IP
    mmio_wr(xh_op, 4, mmio_rd(xh_op, 4) | 4)  # USBCMD.INTE
    xh_irq_on = 1
    spin_unlock(addr(rq_lock))
    irq_restore(f)
    puts("xHCI: HID boot keyboard on MSI-X vector 0x52 -> cpu0; key presses go to the tty\n")
