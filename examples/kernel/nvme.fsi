# Phase 8: NVMe, read-only. Admin queue + one I/O queue pair, polled completions. The driver has no
# write path: nvme_write (end of file) only writes inside the range Forge FS claimed (its validated GPT partition).
nv_regs: u64
nv_dstrd: u64
nv_asq: u64
nv_asq_pa: u64
nv_acq: u64
nv_acq_pa: u64
nv_iosq: u64
nv_iosq_pa: u64
nv_iocq: u64
nv_iocq_pa: u64
nv_buf: u64
nv_buf_pa: u64
nv_sq_tail: u64[2]
nv_cq_head: u64[2]
nv_phase: u64[2]
nv_cid: u64
nv_lba_sz: u64
nv_nsze: u64
nv_reads: u64
NV_QD = 16

def nv_db(q: u64, cq: u64) -> u64:
    return nv_regs + 0x1000 + (2 * q + cq) * (u64(4) << nv_dstrd)

nv_d12: u64  # command dword 12 for the next nv_cmd (read: block count - 1)

def nv_cmd(q: u64, opc: u64, nsid: u64, prp: u64, d10: u64, d11: u64) -> u64:
# submit one command on queue q (0 admin, 1 I/O) and poll for its completion; returns status (0 = ok)
    sq = nv_asq
    cq = nv_acq
    if q == 1:
        sq = nv_iosq
        cq = nv_iocq
    e = sq + nv_sq_tail[q] * 64
    i: u64 = 0
    while i < 64:
        mmio_wr(e + i, 8, 0)
        i += 8
    nv_cid = (nv_cid + 1) & 0xFFFF
    mmio_wr(e, 4, opc | (nv_cid << 16))
    mmio_wr(e + 4, 4, nsid)
    mmio_wr(e + 24, 8, prp)
    mmio_wr(e + 40, 4, d10)
    mmio_wr(e + 44, 4, d11)
    mmio_wr(e + 48, 4, nv_d12)
    nv_d12 = 0
    mfence()
    nv_sq_tail[q] = (nv_sq_tail[q] + 1) % NV_QD
    mmio_wr(nv_db(q, 0), 4, nv_sq_tail[q])
    c = cq + nv_cq_head[q] * 16
    t: u64 = 0
    while ((mmio_rd(c + 12, 4) >> 16) & 1) != nv_phase[q]:
        t += 1
        if t > 2000000:
            return 0xFFFF
        tsc_delay_us(1)
    st = (mmio_rd(c + 12, 4) >> 17) & 0x7FFF
    nv_cq_head[q] += 1
    if nv_cq_head[q] == NV_QD:
        nv_cq_head[q] = 0
        nv_phase[q] ^= 1
    mmio_wr(nv_db(q, 1), 4, nv_cq_head[q])
    return st

def nv_wait_rdy(want: u64) -> u64:
    t: u64 = 0
    while (mmio_rd(nv_regs + 0x1C, 4) & 1) != want:
        t += 1
        if t > 500000:
            return 0
        tsc_delay_us(10)
    return 1

def nv_page(pa_out: ptr[u64]) -> u64:
    return dma_alloc(1, pa_out)

def nvme_init() -> u64:
    f = pci_find(0x010802, 0)
    if f == 0:
        puts("NVMe: not present\n")
        return 0
    nv_regs = pci_map_bar(f - 1, 0, 0x4000)
    if nv_regs == 0:
        puts("NVMe: BAR0 not mappable\n")
        return 0
    cap = mmio_rd(nv_regs, 8)
    nv_dstrd = (cap >> 32) & 0xF
    mqes = (cap & 0xFFFF) + 1
    if mqes < NV_QD:
        puts("NVMe: queues too small\n")
        return 0
    mmio_wr(nv_regs + 0x14, 4, 0)
    if nv_wait_rdy(0) == 0:
        puts("NVMe: controller did not stop\n")
        return 0
    nv_asq = nv_page(addr(nv_asq_pa))
    nv_acq = nv_page(addr(nv_acq_pa))
    nv_iosq = nv_page(addr(nv_iosq_pa))
    nv_iocq = nv_page(addr(nv_iocq_pa))
    nv_buf = nv_page(addr(nv_buf_pa))
    if nv_asq == 0 or nv_acq == 0 or nv_iosq == 0 or nv_iocq == 0 or nv_buf == 0:
        panic("NVMe: out of DMA memory")
    i: u64 = 0
    while i < 2:
        nv_sq_tail[i] = 0
        nv_cq_head[i] = 0
        nv_phase[i] = 1
        i += 1
    mmio_wr(nv_regs + 0x24, 4, ((NV_QD - 1) << 16) | (NV_QD - 1))
    mmio_wr(nv_regs + 0x28, 8, nv_asq_pa)
    mmio_wr(nv_regs + 0x30, 8, nv_acq_pa)
    mmio_wr(nv_regs + 0x14, 4, 1 | (6 << 16) | (4 << 20))  # EN, 64-byte SQEs, 16-byte CQEs
    if nv_wait_rdy(1) == 0:
        puts("NVMe: controller not ready\n")
        return 0
    vs = mmio_rd(nv_regs + 8, 4)
    if nv_cmd(0, 6, 0, nv_buf_pa, 1, 0) != 0:
        puts("NVMe: identify controller failed\n")
        return 0
    puts("NVMe: version ")
    put_u64(vs >> 16)
    putc(46)
    put_u64((vs >> 8) & 0xFF)
    puts(", model '")
    nv_str(nv_buf + 24, 40)
    puts("', serial '")
    nv_str(nv_buf + 4, 20)
    puts("'\n")
    if nv_cmd(0, 6, 1, nv_buf_pa, 0, 0) != 0:
        puts("NVMe: identify namespace 1 failed\n")
        return 0
    nv_nsze = mmio_rd(nv_buf, 8)
    fl = mmio_rd(nv_buf + 26, 1) & 0xF
    nv_lba_sz = u64(1) << ((mmio_rd(nv_buf + 128 + fl * 4, 4) >> 16) & 0xFF)
    if nv_lba_sz != 512 and nv_lba_sz != 4096:
        puts("NVMe: unsupported LBA size\n")
        return 0
    if nv_cmd(0, 5, 0, nv_iocq_pa, ((NV_QD - 1) << 16) | 1, 1) != 0:
        puts("NVMe: create I/O CQ failed\n")
        return 0
    if nv_cmd(0, 1, 0, nv_iosq_pa, ((NV_QD - 1) << 16) | 1, (1 << 16) | 1) != 0:
        puts("NVMe: create I/O SQ failed\n")
        return 0
    puts("NVMe: namespace 1: ")
    put_u64(nv_nsze)
    puts(" blocks of ")
    put_u64(nv_lba_sz)
    puts(" bytes, I/O queue pair 1 (depth 16, polled)\n")
    return 1

def nv_str(a: u64, n: u64):
    e = n
    while e > 0 and (mmio_rd(a + e - 1, 1) == 32 or mmio_rd(a + e - 1, 1) == 0):
        e -= 1
    i: u64 = 0
    while i < e:
        c = mmio_rd(a + i, 1)
        if c < 32 or c > 126:
            c = 63
        putc(u8(c))
        i += 1

def nvme_read(lba: u64) -> u64:
# one 4 KiB read at lba (in device blocks); returns the (registered DMA) buffer, 0 on failure
    nb = 4096 / nv_lba_sz
    if nv_regs == 0 or lba + nb > nv_nsze:
        return 0
    nv_d12 = nb - 1
    if nv_cmd(1, 2, 1, nv_buf_pa, lba & 0xFFFFFFFF, lba >> 32) != 0:
        return 0
    nv_reads += 1
    return nv_buf

# Write path: only inside the LBA range claimed by Forge FS (nvme_claim), which only claims a validated
# Forge-type GPT partition. Everything else is refused and counted.
nv_own_lo: u64
nv_own_hi: u64  # exclusive; 0 = owns nothing, all writes refused
nv_writes: u64
nv_refused: u64
def nvme_claim(lo: u64, hi: u64):
    nv_own_lo = lo
    nv_own_hi = hi

def nvme_write(lba: u64) -> u64:
# one 4 KiB write of the DMA buffer at lba; 1 on success
    nb = 4096 / nv_lba_sz
    if nv_regs == 0 or nv_own_hi == 0 or lba < nv_own_lo or lba + nb > nv_own_hi or lba + nb > nv_nsze:
        nv_refused += 1
        return 0
    nv_d12 = nb - 1
    if nv_cmd(1, 1, 1, nv_buf_pa, lba & 0xFFFFFFFF, lba >> 32) != 0:
        return 0
    nv_writes += 1
    return 1

def nvme_flush() -> u64:
    if nv_regs == 0:
        return 0
    return u64(nv_cmd(1, 0, 1, 0, 0, 0) == 0)
