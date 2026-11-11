# Phase 8: network interfaces. e1000 (82540EM, MMIO BAR0) and virtio-net (legacy transitional, I/O
# BAR0), polled. Each sends one ARP request for the QEMU user-network gateway 10.0.2.2 and waits
# for the reply.
NET_IP = 0x0A00020F  # 10.0.2.15
NET_GW = 0x0A000202  # 10.0.2.2
net_mac: u64
net_frame: u64  # one DMA page: frame to send at 0, scratch at 2048
net_frame_pa: u64

def net_put_mac(m: u64):
    i: u64 = 0
    while i < 6:
        if i != 0:
            putc(58)
        put_hex8((m >> (8 * i)) & 0xFF)
        i += 1

def net_be(a: u64, v: u64, n: u64):
# store n bytes of v big-endian at a
    i: u64 = 0
    while i < n:
        mmio_wr(a + i, 1, (v >> (8 * (n - 1 - i))) & 0xFF)
        i += 1

def net_rd_be(a: u64, n: u64) -> u64:
    v: u64 = 0
    i: u64 = 0
    while i < n:
        v = (v << 8) | mmio_rd(a + i, 1)
        i += 1
    return v

def net_mac_wr(a: u64, m: u64):
    i: u64 = 0
    while i < 6:
        mmio_wr(a + i, 1, (m >> (8 * i)) & 0xFF)
        i += 1

def net_mac_rd(a: u64) -> u64:
    m: u64 = 0
    i: u64 = 0
    while i < 6:
        m = m | (mmio_rd(a + i, 1) << (8 * i))
        i += 1
    return m

def net_arp_build(a: u64, mac: u64) -> u64:
# broadcast ARP who-has NET_GW tell NET_IP; returns the frame length (60, padded)
    i: u64 = 0
    while i < 64:
        mmio_wr(a + i, 8, 0)
        i += 8
    net_mac_wr(a, 0xFFFFFFFFFFFF)
    net_mac_wr(a + 6, mac)
    net_be(a + 12, 0x0806, 2)
    net_be(a + 14, 0x0001080006040001, 8)  # Ethernet, IPv4, hlen 6, plen 4, request
    net_mac_wr(a + 22, mac)
    net_be(a + 28, NET_IP, 4)
    net_be(a + 38, NET_GW, 4)
    return 60

def net_arp_reply(a: u64, len: u64, mac: u64) -> u64:
# the gateway's MAC if frame a is an ARP reply from NET_GW to us, else 0
    if len < 42 or net_rd_be(a + 12, 2) != 0x0806 or net_rd_be(a + 20, 2) != 2:
        return 0
    if net_rd_be(a + 28, 4) != NET_GW or net_rd_be(a + 38, 4) != NET_IP:
        return 0
    if net_mac_rd(a + 32) != mac:
        return 0
    return net_mac_rd(a + 22)

# ---- e1000 ----
E1_RX_N = 32
e1_regs: u64
e1_rx: u64
e1_rx_pa: u64
e1_tx: u64
e1_tx_pa: u64
e1_bufs: u64  # 32 x 2 KiB receive buffers (16 pages)
e1_bufs_pa: u64
e1_rx_i: u64
e1_tx_i: u64

def e1_wr(r: u64, v: u64):
    mmio_wr(e1_regs + r, 4, v)

def e1_rd(r: u64) -> u64:
    return mmio_rd(e1_regs + r, 4)

def e1000_init() -> u64:
    f = pci_find_id(0x100E8086)
    if f == 0:
        puts("e1000: not present\n")
        return 0
    e1_regs = pci_map_bar(f - 1, 0, 0x20000)
    if e1_regs == 0:
        puts("e1000: BAR0 not mappable\n")
        return 0
    e1_wr(0xD8, 0xFFFFFFFF)  # mask all interrupts
    e1_wr(0, e1_rd(0) | 0x04000000)  # CTRL.RST
    tsc_delay_us(10000)
    t: u64 = 0
    while (e1_rd(0) & 0x04000000) != 0 and t < 1000:
        tsc_delay_us(100)
        t += 1
    e1_wr(0xD8, 0xFFFFFFFF)
    e1_wr(0, (e1_rd(0) | 0x60) & ~u64(0x80000008))  # SLU | ASDE, no PHY reset, no LRST
    net_mac = e1_rd(0x5400) | ((e1_rd(0x5404) & 0xFFFF) << 32)
    i: u64 = 0
    while i < 128:
        e1_wr(0x5200 + i * 4, 0)  # multicast table
        i += 1
    e1_rx = dma_alloc(1, addr(e1_rx_pa))
    e1_tx = dma_alloc(1, addr(e1_tx_pa))
    e1_bufs = dma_alloc(16, addr(e1_bufs_pa))
    if e1_rx == 0 or e1_tx == 0 or e1_bufs == 0 or net_frame == 0:
        panic("e1000: out of DMA memory")
    i = 0
    while i < E1_RX_N:
        mmio_wr(e1_rx + i * 16, 8, e1_bufs_pa + i * 2048)
        i += 1
    e1_wr(0x2800, e1_rx_pa & 0xFFFFFFFF)
    e1_wr(0x2804, e1_rx_pa >> 32)
    e1_wr(0x2808, E1_RX_N * 16)
    e1_wr(0x2810, 0)
    e1_wr(0x2818, E1_RX_N - 1)
    e1_wr(0x100, 2 | 0x8000 | 0x04000000)  # RCTL: EN, BAM, SECRC, 2 KiB buffers
    e1_wr(0x3800, e1_tx_pa & 0xFFFFFFFF)
    e1_wr(0x3804, e1_tx_pa >> 32)
    e1_wr(0x3808, 16 * 16)
    e1_wr(0x3810, 0)
    e1_wr(0x3818, 0)
    e1_wr(0x400, 2 | 8 | (0x10 << 4) | (0x40 << 12))  # TCTL: EN, PSP, CT, COLD
    e1_wr(0x410, 0x0060200A)
    e1_rx_i = 0
    e1_tx_i = 0
    puts("e1000: MAC ")
    net_put_mac(net_mac)
    puts(", link ")
    if (e1_rd(8) & 2) != 0:
        puts("up\n")
    else:
        puts("down\n")
    return 1

def e1_send(pa: u64, len: u64) -> u64:
    d = e1_tx + e1_tx_i * 16
    mmio_wr(d, 8, pa)
    mmio_wr(d + 8, 8, len | (u64(0x0B) << 24))  # EOP | IFCS | RS
    e1_tx_i = (e1_tx_i + 1) % 16
    mfence()
    e1_wr(0x3818, e1_tx_i)
    t: u64 = 0
    while (mmio_rd(d + 12, 1) & 1) == 0:
        t += 1
        if t > 100000:
            return 0
        tsc_delay_us(10)
    return 1

def e1_recv(len_out: ptr[u64]) -> u64:
# next received frame's buffer address, 0 if none; the descriptor goes back to the NIC on the next call
    d = e1_rx + e1_rx_i * 16
    if (mmio_rd(d + 12, 1) & 1) == 0:
        return 0
    len_out[0] = mmio_rd(d + 8, 2)
    a = e1_bufs + e1_rx_i * 2048
    mmio_wr(d + 8, 8, 0)
    e1_wr(0x2818, e1_rx_i)
    e1_rx_i = (e1_rx_i + 1) % E1_RX_N
    return a

def e1000_arp() -> u64:
    if e1_regs == 0:
        return 0
    n = net_arp_build(net_frame, net_mac)
    tries: u64 = 0
    while tries < 3:
        if e1_send(net_frame_pa, n) == 0:
            puts("e1000: transmit timed out\n")
            return 0
        t: u64 = 0
        while t < 200000:
            len: u64 = 0
            a = e1_recv(addr(len))
            if a != 0:
                gw = net_arp_reply(a, len, net_mac)
                if gw != 0:
                    puts("e1000: ARP reply: 10.0.2.2 is at ")
                    net_put_mac(gw)
                    puts(" (")
                    put_u64(e1_rd(0x40D0))
                    puts(" frames received)\n")
                    return 1
            else:
                t += 1
                tsc_delay_us(10)
        tries += 1
    puts("e1000: no ARP reply\n")
    return 0

# ---- virtio-net (legacy) ----
vn_io: u64
vn_q: u64[2]
vn_qpa: u64[2]
vn_qsz: u64[2]
vn_used: u64[2]  # used ring offset
vn_last: u64[2]
vn_rxb: u64  # 8 x 2 KiB receive buffers
vn_rxb_pa: u64
vn_mac: u64

def vn_setup_q(q: u64) -> u64:
    io = vn_io
    pio_wr(io + VIO_QSEL, 2, q)
    n = pio_rd(io + VIO_QSIZE, 2)
    if n == 0 or n > 1024:
        return 0
    used = (16 * n + 6 + 2 * n + PAGE - 1) & ~u64(PAGE - 1)
    total = used + ((6 + 8 * n + PAGE - 1) & ~u64(PAGE - 1))
    pa: u64 = 0
    va = dma_alloc(total / PAGE, addr(pa))
    if va == 0:
        return 0
    vn_q[q] = va
    vn_qpa[q] = pa
    vn_qsz[q] = n
    vn_used[q] = used
    vn_last[q] = 0
    pio_wr(io + VIO_QPFN, 4, pa >> 12)
    return 1

def vn_add(q: u64, d: u64, pa: u64, len: u64, flags: u64):
# put descriptor d (pa/len/flags) on queue q's available ring and notify the device
    b = vn_q[q]
    n = vn_qsz[q]
    mmio_wr(b + d * 16, 8, pa)
    mmio_wr(b + d * 16 + 8, 8, (len & 0xFFFFFFFF) | (flags << 32))
    av = b + 16 * n
    idx = mmio_rd(av + 2, 2)
    mmio_wr(av + 4 + (idx % n) * 2, 2, d)
    mfence()
    mmio_wr(av + 2, 2, (idx + 1) & 0xFFFF)
    mfence()
    pio_wr(vn_io + VIO_QNOTIFY, 2, q)

def vn_used_pending(q: u64) -> u64:
    return mmio_rd(vn_q[q] + vn_used[q] + 2, 2) != vn_last[q]

def virtio_net_init() -> u64:
    f = pci_find_id(0x10001AF4)
    if f == 0:
        puts("virtio-net: not present\n")
        return 0
    bdf = f - 1
    bar = pci_rd32(bdf, 0x10)
    if (bar & 1) == 0:
        puts("virtio-net: no I/O BAR\n")
        return 0
    vn_io = bar & 0xFFFC
    pio_add(vn_io, 0x20)
    pci_busmaster(bdf, 5)
    io = vn_io
    pio_wr(io + VIO_STATUS, 1, 0)
    pio_wr(io + VIO_STATUS, 1, 1)
    pio_wr(io + VIO_STATUS, 1, 3)
    hf = pio_rd(io + VIO_HOSTF, 4)
    if (hf & 0x20) == 0:
        puts("virtio-net: no MAC feature\n")
        return 0
    pio_wr(io + VIO_GUESTF, 4, 0x20)  # VIRTIO_NET_F_MAC only: 10-byte header, no offloads
    if vn_setup_q(0) == 0 or vn_setup_q(1) == 0:
        puts("virtio-net: queue setup failed\n")
        return 0
    vn_rxb = dma_alloc(4, addr(vn_rxb_pa))
    if vn_rxb == 0:
        panic("virtio-net: out of DMA memory")
    pio_wr(io + VIO_STATUS, 1, 7)
    m: u64 = 0
    i: u64 = 0
    while i < 6:
        m = m | (pio_rd(io + 0x14 + i, 1) << (8 * i))
        i += 1
    vn_mac = m
    i = 0
    while i < 8:
        vn_add(0, i, vn_rxb_pa + i * 2048, 2048, 2)
        i += 1
    puts("virtio-net: io 0x")
    put_hex16(vn_io)
    puts(", MAC ")
    net_put_mac(vn_mac)
    puts(", rx queue ")
    put_u64(vn_qsz[0])
    puts(", tx queue ")
    put_u64(vn_qsz[1])
    putc(10)
    return 1

def virtio_net_arp() -> u64:
    if vn_io == 0:
        return 0
    tx = net_frame + 2048
    txpa = net_frame_pa + 2048
    i: u64 = 0
    while i < 16:
        mmio_wr(tx + i, 1, 0)
        i += 1
    n = net_arp_build(net_frame, vn_mac)
    i = 0
    while i < n:
        mmio_wr(tx + 10 + i, 1, mmio_rd(net_frame + i, 1))
        i += 1
    tries: u64 = 0
    while tries < 3:
        vn_add(1, 0, txpa, 10 + n, 0)
        t: u64 = 0
        while vn_used_pending(1) == 0 and t < 100000:
            tsc_delay_us(10)
            t += 1
        if t >= 100000:
            puts("virtio-net: transmit timed out\n")
            return 0
        vn_last[1] = (vn_last[1] + 1) & 0xFFFF
        t = 0
        while t < 200000:
            if vn_used_pending(0):
                e = vn_q[0] + vn_used[0] + 4 + (vn_last[0] % vn_qsz[0]) * 8
                d = mmio_rd(e, 4)
                len = mmio_rd(e + 4, 4)
                vn_last[0] = (vn_last[0] + 1) & 0xFFFF
                gw: u64 = 0
                if d < 8 and len > 10:
                    gw = net_arp_reply(vn_rxb + d * 2048 + 10, len - 10, vn_mac)
                    vn_add(0, d, vn_rxb_pa + d * 2048, 2048, 2)
                if gw != 0:
                    puts("virtio-net: ARP reply: 10.0.2.2 is at ")
                    net_put_mac(gw)
                    putc(10)
                    return 1
            else:
                t += 1
                tsc_delay_us(10)
        tries += 1
    puts("virtio-net: no ARP reply\n")
    return 0

def net_init():
    if net_frame == 0:
        net_frame = dma_alloc(1, addr(net_frame_pa))
