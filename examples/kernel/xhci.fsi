# Phase 8: xHCI host controller. Reset and run with a command ring and one event ring (polled), scan
# the root ports, reset connected USB2 ports, then for the first device: Enable Slot, Address Device
# and a GET_DESCRIPTOR(device) control transfer on endpoint 0.
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

xh_rb: u64[2]  # producer rings: 0 = command ring, 1 = EP0 transfer ring of the probed device
xh_rpa: u64[2]
xh_ri: u64[2]
xh_rc: u64[2]

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
# poll the event ring for a TRB of type want; returns its completion code (0xFF on timeout)
    t: u64 = 0
    while t < 1000000:
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
    return 1
