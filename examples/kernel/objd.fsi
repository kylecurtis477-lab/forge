# Phase 9.2b: the object store's write-ahead log persisted on the Forge partition. The log is one Forge FS
# file, objects.log (OD_NB blocks, reserved zero-filled); each commit writes the log blocks it dirtied and
# flushes before returning, so a returned commit is durable. Mount replays the log (obj.fsi ob_replay),
# drops a torn or corrupt tail and zeroes it on disk so later appends can never revive stale records.
# Only Forge FS calls are used, so every write stays inside the explicitly created Forge partition.
# Phase 10.1: every log block is encrypted at rest and authenticated, encrypt-then-MAC: ChaCha20 under the
# enc key with nonce = (64-bit write counter, block number), then a full 256-bit HMAC-SHA256 under the
# separate MAC key over (block, counter, ciphertext), compared in constant time (crypt.fsi ct_eq4). Enc and
# MAC keys are HMAC-derived from the random per-user data key, which is stored only wrapped by the user
# key (objects.key). The write counter is never reused, across reboots, torn writes, rewinds and
# tampering: it only grows, and a high-water mark reserved ahead of use is made durable in objects.ctr
# (two HMAC-tagged slots) before any counter beyond it is used. Each logical block alternates between two
# physical slots so a torn write never destroys the previous contents. See docs/privacy.md.
OD_PW = 507  # payload words per logical log block; word 507 = write counter, 508..511 = HMAC-SHA256 tag
OD_WRES = 4096  # counters reserved per objects.ctr update
OD_CMAGIC = 0x52544345474F46  # "FOGECTR"
OD_NB = (OB_NL + OD_PW - 1) / OD_PW  # logical log blocks; each has two physical slots (A/B)
OD_UID = 1000  # the store owner (one user per store)
OD_KMAGIC = 0x59454B4547524F46  # "FORGEKEY"
od_st: u64
od_synced: u64
od_torn: u64
od_commits: u64
od_wc: u64  # last write counter used (never reused: it is part of every block's nonce)
od_cs: u8[OD_NB]  # current valid slot per logical block
od_kek: u64[8]  # key-encryption key (the platform key; TPM sealing is not implemented; never stored)
od_dk: u64[8]  # the store's data key (random, stored only wrapped by od_kek)
od_ek: u64[8]  # ChaCha20 key derived from od_dk
od_mk: u64[8]  # tag key derived from od_dk
od_t: u64[8]  # kdf output
od_ksrc: u64  # 0 none, 1 TPM, 2 fw_cfg platform key
od_badtag: u64  # slots rejected (torn, never written, or tampered) at the last open
od_hwm: u64  # durable counter high-water mark: no counter above it has been used
od_cst: u64  # objects.ctr first block
od_hs: u64  # current objects.ctr slot
od_rawmax: u64  # highest counter seen in any slot at open, verified or not
od_tv: u64[4]  # computed tag
od_tr: u64[4]  # stored tag

# ---- user key: TPM-sealed when a TPM is present, else QEMU fw_cfg "opt/forge/userkey" ----
def od_tpm_present() -> u64:
# TPM 2.0 sealing is not implemented: no TPM device is attached in any test configuration
    return 0

def od_fwcfg_key() -> u64:
# read fw_cfg file opt/forge/userkey (8..64 bytes) and hash it into od_kek; 0 if absent
    pio_add(0x510, 2)
    pio_wr(0x510, 2, 0)
    if pio_rd(0x511, 1) != 81 or pio_rd(0x511, 1) != 69 or pio_rd(0x511, 1) != 77 or pio_rd(0x511, 1) != 85:
        return 0
    pio_wr(0x510, 2, 0x19)
    n: u64 = 0
    i: u64 = 0
    while i < 4:
        n = (n << 8) | pio_rd(0x511, 1)
        i += 1
    nm: u8[56]
    sel: u64 = 0
    sz: u64 = 0
    e: u64 = 0
    while e < n and e < 256:
        es: u64 = 0
        i = 0
        while i < 4:
            es = (es << 8) | pio_rd(0x511, 1)
            i += 1
        ec = (pio_rd(0x511, 1) << 8) | pio_rd(0x511, 1)
        pio_rd(0x511, 1)
        pio_rd(0x511, 1)
        i = 0
        while i < 56:
            nm[i] = u8(pio_rd(0x511, 1))
            i += 1
        v: u64[3]
        v[0] = 0
        v[1] = 0
        v[2] = 0
        i = 0
        while i < 24:
            v[i / 8] = v[i / 8] | (u64(nm[i]) << ((i % 8) * 8))
            i += 1
        same: u64 = 0
        if v[0] == 0x67726F662F74706F and v[1] == 0x656B726573752F65 and v[2] == 0x79:
            same = 1
        if same == 1 and sel == 0:
            sel = ec
            sz = es
        e += 1
    if sel == 0 or sz < 8 or sz > 64:
        return 0
    pio_wr(0x510, 2, sel)
    sh_init()
    sh_u64(0x6B656B2D65677266)  # "frge-kek" domain label
    i = 0
    while i < sz:
        sh_byte(pio_rd(0x511, 1))
        i += 1
    sh_fin()
    i = 0
    while i < 8:
        od_kek[i] = od_be(sh_h[i])
        i += 1
    return 1

def od_be(v: u64) -> u64:
# a SHA-256 output word (big-endian) as a little-endian key word
    return ((v >> 24) & 255) | (((v >> 16) & 255) << 8) | (((v >> 8) & 255) << 16) | ((v & 255) << 24)

def od_kdf(k: u64[8], label: u64, salt0: u64, salt1: u64):
# od_t = HMAC-SHA256(k, label || salt) (an HKDF-Expand style PRF; distinct labels give independent keys)
    hm_setkey32(k)
    hm_begin()
    sh_u64(label)
    sh_u64(salt0)
    sh_u64(salt1)
    hm_end()
    i: u64 = 0
    while i < 8:
        od_t[i] = od_be(sh_h[i])
        i += 1

def od_keys_from_dk():
    od_kdf(od_dk, 0x636E65, 0, 0)  # "enc"
    i: u64 = 0
    while i < 8:
        od_ek[i] = od_t[i]
        i += 1
    od_kdf(od_dk, 0x63616D, 0, 0)  # "mac"
    i = 0
    while i < 8:
        od_mk[i] = od_t[i]
        i += 1

def od_mkfile(name: ptr[u8], nb: u64) -> u64:
# reserve a zero-filled nb-block Forge FS file; its first block (0 on failure)
    if ffs_ok == 0 or ffs_find(name) != FFS_NI:
        return 0
    slot: u64 = 0
    while slot < FFS_NI and ffs_it[slot * 8] != 0:
        slot += 1
    if slot == FFS_NI:
        return 0
    st = ffs_alloc(nb)
    if st == 0:
        return 0
    i: u64 = 0
    while i < 512:
        ffs_b[i] = 0
        i += 1
    b: u64 = 0
    while b < nb:
        if ffs_put(st + b, 0) == 0:
            return 0
        b += 1
    if nvme_flush() == 0:
        return 0
    ffs_pack(name)
    k = slot * 8
    i = 0
    while i < 5:
        ffs_it[k + i] = ffs_nm[i]
        i += 1
    ffs_it[k + 5] = nb * 4096
    ffs_it[k + 6] = st
    ffs_it[k + 7] = nb
    if ffs_commit() == 0:
        return 0
    return st

def od_key_new() -> u64:
# first mount: a random data key, stored only wrapped by the user key, in objects.key
    st = od_mkfile("objects.key", 1)
    if st == 0:
        return 0
    sh_init()
    i: u64 = 0
    while i < 512:
        t0 = rdtsc()
        j: u64 = 0
        while j < (t0 & 15):
            j += 1
        sh_u64(nd_rand(4, rdtsc() - t0))
        sh_u64(t0)
        i += 1
    sh_fin()
    s0 = sh_h[0] | (sh_h[1] << 32)
    s1 = sh_h[2] | (sh_h[3] << 32)
    i = 0
    while i < 8:
        od_dk[i] = od_be(sh_h[i]) ^ sh_h[7 - i]
        i += 1
    od_kdf(od_kek, 0x70617277, s0, s1)  # "wrap"
    i = 0
    while i < 512:
        ffs_b[i] = 0
        i += 1
    ffs_b[0] = OD_KMAGIC
    ffs_b[1] = OD_UID
    ffs_b[2] = s0
    ffs_b[3] = s1
    i = 0
    while i < 8:
        ffs_b[4 + i] = od_dk[i] ^ od_t[i]
        i += 1
    od_kdf(od_kek, 0x6B6863, s0, s1)  # "chk": a 256-bit key check value
    i = 0
    while i < 4:
        ffs_b[12 + i] = od_t[i * 2] | (od_t[i * 2 + 1] << 32)
        i += 1
    if ffs_put(st, 0) == 0 or nvme_flush() == 0:
        return 0
    od_keys_from_dk()
    return 1

def od_key_load() -> u64:
# 1 = data key unwrapped, 3 = wrong user key / bad keyslot
    i = ffs_find("objects.key")
    if i == FFS_NI or ffs_get(ffs_it[i * 8 + 6], 0) == 0:
        return 3
    if ffs_b[0] != OD_KMAGIC or ffs_b[1] != OD_UID:
        return 3
    s0 = ffs_b[2]
    s1 = ffs_b[3]
    od_kdf(od_kek, 0x6B6863, s0, s1)
    k: u64 = 0
    while k < 4:
        od_tv[k] = od_t[k * 2] | (od_t[k * 2 + 1] << 32)
        od_tr[k] = ffs_b[12 + k]
        k += 1
    if ct_eq4(od_tv, od_tr) == 0:
        return 3
    od_kdf(od_kek, 0x70617277, s0, s1)
    k = 0
    while k < 8:
        od_dk[k] = ffs_b[4 + k] ^ od_t[k]
        k += 1
    od_keys_from_dk()
    return 1

# ---- encrypted, tagged log blocks ----
def od_mac_end():
# finish the HMAC and leave the 256-bit tag in od_tv
    hm_end()
    i: u64 = 0
    while i < 4:
        od_tv[i] = (sh_h[i * 2] << 32) | sh_h[i * 2 + 1]
        i += 1

def od_tag(b: u64, wc: u64):
# od_tv = HMAC-SHA256(od_mk, block || counter || ciphertext words 0..OD_PW-1 in ffs_b)
    hm_setkey32(od_mk)
    hm_begin()
    sh_u64(b)
    sh_u64(wc)
    i: u64 = 0
    while i < OD_PW:
        sh_u64(ffs_b[i])
        i += 1
    od_mac_end()

def od_tag_ok(at: u64) -> u64:
# constant-time compare of od_tv with the tag stored at ffs_b[at..at+3]
    i: u64 = 0
    while i < 4:
        od_tr[i] = ffs_b[at + i]
        i += 1
    return ct_eq4(od_tv, od_tr)

# ---- objects.ctr: the durable counter high-water mark (two slots, HMAC-tagged, higher valid one wins) ----
def od_ctr_tag(slot: u64, v: u64):
    hm_setkey32(od_mk)
    hm_begin()
    sh_u64(OD_CMAGIC)
    sh_u64(slot)
    sh_u64(v)
    od_mac_end()

def od_ctr_put(v: u64) -> u64:
# make "no counter above v is used" durable (flushed) before any counter up to v is used
    i: u64 = 0
    while i < 512:
        ffs_b[i] = 0
        i += 1
    slot = 1 - od_hs
    od_ctr_tag(slot, v)
    ffs_b[0] = OD_CMAGIC
    ffs_b[1] = v
    i = 0
    while i < 4:
        ffs_b[2 + i] = od_tv[i]
        i += 1
    if ffs_put(od_cst + slot, 0) == 0 or nvme_flush() == 0:
        return 0
    od_hs = slot
    od_hwm = v
    return 1

def od_ctr_load() -> u64:
# od_hwm = the higher verified slot (0 if neither verifies); 0 if objects.ctr is missing
    i = ffs_find("objects.ctr")
    if i == FFS_NI or ffs_it[i * 8 + 7] != 2:
        return 0
    od_cst = ffs_it[i * 8 + 6]
    od_hwm = 0
    od_hs = 1
    slot: u64 = 0
    while slot < 2:
        if ffs_get(od_cst + slot, 0) != 0 and ffs_b[0] == OD_CMAGIC:
            v = ffs_b[1]
            od_ctr_tag(slot, v)
            if od_tag_ok(2) != 0 and v >= od_hwm:
                od_hwm = v
                od_hs = slot
        slot += 1
    return 1

def od_xor(b: u64, wc: u64):
# ffs_b[0..509] ^= ChaCha20(od_ek, nonce = (wc, block b))
    i: u64 = 0
    while i < 8:
        cc_key[i] = od_ek[i]
        i += 1
    j: u64 = 0
    while j * 8 < OD_PW:
        cc_block(j, wc & 0xFFFFFFFF, wc >> 32, b)
        k: u64 = 0
        while k < 8 and j * 8 + k < OD_PW:
            ffs_b[j * 8 + k] = ffs_b[j * 8 + k] ^ cc_word(k)
            k += 1
        j += 1

def od_put(b: u64) -> u64:
# encrypt logical block b into the slot that is not current, so a torn write leaves the old slot intact
    i: u64 = 0
    while i < OD_PW:
        w = b * OD_PW + i
        ffs_b[i] = 0
        if w < OB_NL:
            ffs_b[i] = ob_log[w]
        i += 1
    if od_wc + 1 > od_hwm:
        if od_ctr_put(od_wc + OD_WRES) == 0:
            return 0
        i = 0
        while i < OD_PW:
            w = b * OD_PW + i
            ffs_b[i] = 0
            if w < OB_NL:
                ffs_b[i] = ob_log[w]
            i += 1
    od_wc += 1
    wc = od_wc
    od_xor(b, wc)
    ffs_b[OD_PW] = wc
    od_tag(b, wc)
    i = 0
    while i < 4:
        ffs_b[OD_PW + 1 + i] = od_tv[i]
        i += 1
    slot = 1 - u64(od_cs[b])
    if ffs_put(od_st + b * 2 + slot, 0) == 0:
        return 0
    od_cs[b] = u8(slot)
    return 1

def od_slot_wc(b: u64, slot: u64) -> u64:
# the slot's write counter if its tag verifies, else 0 (ffs_b holds the slot afterwards)
    if ffs_get(od_st + b * 2 + slot, 0) == 0:
        return 0
    wc = ffs_b[OD_PW]
    if wc > od_rawmax and wc < (1 << 62):
        od_rawmax = wc  # even an unverified counter is never reused
    if wc == 0:
        return 0
    od_tag(b, wc)
    if od_tag_ok(OD_PW + 1) == 0:
        return 0
    return wc

def od_sync() -> u64:
# write every log block touched since the last sync, then flush: the commit is durable on return
    if ob_lp == od_synced:
        return 1
    b = od_synced / OD_PW
    e = (ob_lp - 1) / OD_PW
    while b <= e:
        if od_put(b) == 0:
            return 0
        b += 1
    if nvme_flush() == 0:
        return 0
    od_synced = ob_lp
    od_commits += 1
    return 1

def od_open() -> u64:
# 0 = unavailable, 1 = recovered an existing log, 2 = created an empty one, 3 = locked (no or wrong key)
    od_torn = 0
    od_commits = 0
    od_badtag = 0
    od_ksrc = 0
    b: u64 = 0
    if od_tpm_present() != 0:
        od_ksrc = 1
    elif od_fwcfg_key() != 0:
        od_ksrc = 2
    if od_ksrc == 0:
        return 3
    i = ffs_find("objects.log")
    if i == FFS_NI:
        if ffs_find("objects.key") != FFS_NI:
            return 3
        if od_key_new() == 0:
            return 0
        od_st = od_mkfile("objects.log", OD_NB * 2)
        od_cst = od_mkfile("objects.ctr", 2)
        if od_st == 0 or od_cst == 0:
            return 0
        od_hs = 1
        od_hwm = 0
        ob_reset()
        ob_log_clear()
        w: u64 = 0
        while w < OB_NL:
            ob_log[w] = 0
            w += 1
        while b < OD_NB:
            od_cs[b] = 1
            b += 1
        od_wc = 0
        od_synced = 0
        return 2
    if ffs_it[i * 8 + 7] != OD_NB * 2:
        return 0
    od_st = ffs_it[i * 8 + 6]
    if od_key_load() != 1:
        return 3
    if od_ctr_load() == 0:
        return 0
    od_wc = 0
    od_rawmax = 0
    b = 0
    while b < OD_NB:
        w0 = od_slot_wc(b, 0)
        w1 = od_slot_wc(b, 1)
        if w0 == 0 and w1 == 0:
            od_cs[b] = 1
            od_badtag += 2
            k: u64 = 0
            while k < OD_PW:
                if b * OD_PW + k < OB_NL:
                    ob_log[b * OD_PW + k] = 0
                k += 1
        else:
            slot: u64 = 1
            wc = w1
            if w0 > w1:
                slot = 0
                wc = w0
                od_slot_wc(b, 0)
            if w0 == 0 or w1 == 0:
                od_badtag += 1
            od_cs[b] = u8(slot)
            if wc > od_wc:
                od_wc = wc
            od_xor(b, wc)
            k = 0
            while k < OD_PW:
                if b * OD_PW + k < OB_NL:
                    ob_log[b * OD_PW + k] = ffs_b[k]
                k += 1
        b += 1
    # the next counter is above every counter ever made durable or seen on disk
    if od_hwm > od_wc:
        od_wc = od_hwm
    if od_rawmax > od_wc:
        od_wc = od_rawmax
    ob_replay(OB_NL)
    # anything after the last good record is a torn/corrupt tail: zero it in memory and on disk
    last: u64 = 0
    p = ob_lp
    while p < OB_NL:
        if ob_log[p] != 0:
            od_torn += 1
            ob_log[p] = 0
            last = p
        p += 1
    if od_torn != 0:
        b = ob_lp / OD_PW
        while b <= last / OD_PW:
            if od_put(b) == 0:
                return 0
            b += 1
        if nvme_flush() == 0:
            return 0
    od_synced = ob_lp
    return 1

def od_fp() -> u64:
    h: u64 = 0xcbf29ce484222325
    o: u64 = 0
    while o < ob_nobj:
        n = obj_read(o)
        if n != OB_ERR:
            i: u64 = 0
            while i < n:
                h = (h ^ ob_out[i]) * 0x100000001b3
                i += 1
        h = (h ^ o) * 0x100000001b3
        o += 1
    return h ^ ob_clock

def od_put3(a: u64, b: u64, c: u64):
    ob_in[0] = a
    ob_in[1] = b
    ob_in[2] = c

def od_commit(o: u64) -> u64:
    if obj_write(o, 3) == OB_ERR or od_sync() == 0:
        panic("objstore: commit failed")
    return ob_clock

# Phase 9.4: whole-system rewind after a bad change. Type 2 objects are the "system configuration"
# (field 0 = slot, field 1 = 100 + slot, field 2 = 1 when healthy). A bad change flips every slot to
# broken, durably; the health check notices, sys_rewind takes every object back to the last good
# clock (each rewind is a new version, so the bad change stays inspectable in history), and the next
# boot finds the rewound state on disk.
OD_NCFG = 8

def od_health() -> u64:
# number of broken configuration slots (OB_ERR if the configuration is missing)
    bad: u64 = 0
    k: u64 = 0
    o = obj_type_first(2)
    while o != OB_ERR:
        if obj_read(o) == OB_ERR or ob_out[1] != 100 + ob_out[0] or ob_out[2] != 1:
            bad += 1
        k += 1
        o = obj_type_next(o)
    if k != OD_NCFG:
        return OB_ERR
    return bad

def od_sfp() -> u64:
# state fingerprint: current payloads of every object (no clock, so a rewind can reproduce it)
    return od_fp() ^ ob_clock

def objrw_test():
    if ob_type_def(2, 3) != 0:
        panic("objstore: config type failed")
    k: u64 = 0
    while k < OD_NCFG:
        o = obj_new(2)
        od_put3(k, 100 + k, 1)
        od_commit(o)
        k += 1
    tg = ob_clock
    fg = od_sfp()
    if od_health() != 0:
        panic("objstore: config unhealthy before the change")
    # the bad change: every slot broken, and object 1 overwritten, each write durable
    o = obj_type_first(2)
    k = 0
    while o != OB_ERR:
        od_put3(k, 7, 0)
        od_commit(o)
        k += 1
        o = obj_type_next(o)
    od_put3(9, 9, 9)
    od_commit(1)
    hb = od_health()
    puts("objstore: bad change committed, health check: ")
    put_u64(hb)
    puts(" broken slots\n")
    if hb != OD_NCFG:
        panic("objstore: bad change not detected")
    n = sys_rewind(tg)
    if od_sync() == 0:
        panic("objstore: rewind commit failed")
    if od_health() != 0 or od_sfp() != fg or n != OD_NCFG + 1:
        panic("objstore: system rewind did not restore the good state")
    puts("objstore: system rewound to clock ")
    put_u64(tg)
    puts(" (")
    put_u64(n)
    puts(" objects restored, bad change still in history), state fingerprint ")
    put_hex(fg)
    putc(10)
    puts("objstore system rewind OK\n")

def objrw_verify():
# boot after the rewind: the rewound state must be what the disk holds
    puts("objstore: after reboot, health check: ")
    put_u64(od_health())
    puts(" broken slots, state fingerprint ")
    put_hex(od_sfp())
    putc(10)
    if od_health() != 0:
        panic("objstore: rewound state lost")
    puts("objstore system rewind persisted OK\n")

def od_keymsg(r: u64):
    puts("objstore: encrypted at rest (ChaCha20, encrypt-then-MAC HMAC-SHA256), uid 1000 data key ")
    if r == 2:
        puts("created, wrapped by the ")
    else:
        puts("unwrapped with the ")
    if od_ksrc == 1:
        puts("TPM-sealed key\n")
    else:
        puts("platform key (no TPM)\n")
    if r == 1:
        puts("objstore: ")
        put_u64(OD_NB * 2)
        puts(" log slots read, ")
        put_u64(od_badtag)
        puts(" never written or superseded-and-torn slots rejected by tag\n")

def objstore_test():
    r = od_open()
    if r == 0:
        panic("objstore: objects.log unavailable")
    if r == 3:
        if od_ksrc == 0:
            puts("objstore: locked: no user key (no TPM, no platform key), nothing read or written\n")
        else:
            puts("objstore: locked: wrong user key for uid 1000, nothing decrypted or written\n")
        return
    od_keymsg(r)
    if r == 1 and ob_tf[2] != 0:
        objrw_verify()
        objfs_verify()
        return
    if r == 2:
        if ob_type_def(1, 3) != 0 or od_sync() == 0:
            panic("objstore: type def failed")
        a = obj_new(1)
        b = obj_new(1)
        od_sync()
        j: u64 = 0
        while j < 50:
            od_put3(j, j * j, 7)
            od_commit(a)
            if j % 5 == 0:
                od_put3(j, 1000 + j, 8)
                od_commit(b)
            j += 1
        puts("objstore: objects.log created on the Forge partition, ")
        put_u64(od_commits)
        puts(" durable commits, clock ")
        put_u64(ob_clock)
        puts(", fingerprint ")
        put_hex(od_fp())
        putc(10)
        # crash: a version record header reaches the disk without the rest of the record
        p = ob_lp
        ob_log[p] = OB_RV
        ob_log[p + 1] = a
        ob_log[p + 2] = 0
        if od_put(p / 512) == 0 or nvme_flush() == 0:
            panic("objstore: torn write failed")
        puts("objstore: crash simulated (torn record after the last commit)\n")
        return
    puts("objstore: recovered ")
    put_u64(ob_nver)
    puts(" versions of ")
    put_u64(ob_nobj)
    puts(" objects, clock ")
    put_u64(ob_clock)
    puts(", torn tail dropped (")
    put_u64(od_torn)
    puts(" words), fingerprint ")
    put_hex(od_fp())
    putc(10)
    # every committed version must be readable at its timestamp with the values that were written
    bad: u64 = 0
    i: u64 = 0
    t0: u64 = 0
    v: u64 = 0
    while v < ob_nver:
        if ov_obj[v] == 0:
            if i == 0:
                t0 = ov_ts[v]
            obj_read_at(0, ov_ts[v])
            if ob_out[0] != i or ob_out[1] != i * i or ob_out[2] != 7:
                bad += 1
            i += 1
        v += 1
    if bad != 0 or i != 50 or obj_field(1, 1) != 1045:
        panic("objstore: recovered history wrong")
    # keep working after recovery: rewind a to its first version, then new commits
    if obj_rewind(0, t0) == OB_ERR or od_sync() == 0 or obj_field(0, 1) != 0:
        panic("objstore: rewind failed")
    od_put3(1, 2, 3)
    od_commit(1)
    puts("objstore: all 50 committed versions of object 0 read back, rewind + new commit durable\n")
    puts("objstore crash recovery OK\n")
    objrw_test()
    objfs_test()
    puts("objstore: boot done, state fingerprint ")
    put_hex(od_sfp())
    putc(10)

# ---- Phase 9.5: files as objects, read back through the VFS (/obj in proc.fsi) ----
ofs_pth: u8[64]
ofs_pl: u64

def ofs_pa(w: u64):
# append the bytes of a little-endian packed word (up to its first NUL) to ofs_pth
    k: u64 = 0
    while k < 8 and ((w >> (k * 8)) & 255) != 0 and ofs_pl < 63:
        ofs_pth[ofs_pl] = u8((w >> (k * 8)) & 255)
        ofs_pl += 1
        k += 1
    ofs_pth[ofs_pl] = 0

def ofs_pn(v: u64):
    d: u64 = 1
    while d * 10 <= v:
        d *= 10
    while d > 0 and ofs_pl < 63:
        ofs_pth[ofs_pl] = u8(48 + (v / d) % 10)
        ofs_pl += 1
        d /= 10
    ofs_pth[ofs_pl] = 0

def ofs_p0():
    ofs_pl = 0
    ofs_pth[0] = 0

def ofs_fill(n: u64, seed: u64):
    i: u64 = 0
    while i < n:
        ofs_buf[i] = u8((i * 7 + seed) & 255)
        i += 1

def ofs_check(n: u64, seed: u64) -> u64:
    i: u64 = 0
    while i < n:
        if ofs_buf[i] != u8((i * 7 + seed) & 255):
            return 0
        i += 1
    return 1

def ofs_kname(w: u64):
    k: u64 = 0
    while k < 32:
        ofs_nm[k] = 0
        k += 1
    k = 0
    while k < 8:
        ofs_nm[k] = u8((w >> (k * 8)) & 255)
        k += 1

def ofs_kput(w: u64, n: u64, seed: u64):
    mutex_lock(addr(ofs_m))
    ofs_kname(w)
    ofs_fill(n, seed)
    if ofs_put(n) == OB_ERR or od_sync() == 0:
        panic("objfs: write failed")
    mutex_unlock(addr(ofs_m))

def ofs_kopen_p() -> u64:
# what open("<ofs_pth>") would give, read back the way the read/getdents syscalls do it: a file's
# contents go into ofs_buf and its size is returned; a listing returns 1000 + its entry count;
# OB_ERR = not under /obj, 0x7FFF = no such file
    mutex_lock(addr(ofs_m))
    r = ovfs_resolve(ofs_pth[:])
    v: u64 = 0x7FFF
    if r == OB_ERR:
        v = OB_ERR
    if r == 1:
        v = ofs_read_at(ovfs_o, ovfs_ts)
    if r == 2:
        ovfs_pos = 0
        n: u64 = 0
        while odir_next(ovfs_ts) != OB_ERR:
            n += 1
        v = 1000 + n
    mutex_unlock(addr(ofs_m))
    return v

OFS_MOTD = 0x64746F6D  # "motd"
OFS_NOTES = 0x7365746F6E  # "notes"
OFS_PRE = 0x2F6A626F2F  # "/obj/"

def ofs_kopen(w: u64) -> u64:
    ofs_p0()
    ofs_pa(OFS_PRE)
    ofs_pa(w)
    return ofs_kopen_p()

def ofs_klist(ts: u64) -> u64:
    ofs_p0()
    ofs_pa(0x6A626F2F)  # "/obj"
    if ts != OB_ERR:
        ofs_pa(0x402F)  # "/@"
        ofs_pn(ts)
    return ofs_kopen_p()

def objfs_test():
    if ofs_init() != 0 or od_sync() == 0:
        panic("objfs: init failed")
    ofs_kput(OFS_MOTD, 300, 1)
    ofs_kput(OFS_NOTES, 50, 3)
    t1 = ob_clock
    ofs_kput(OFS_MOTD, 200, 2)
    ok: u64 = 1
    if ofs_kopen(OFS_MOTD) != 200 or ofs_check(200, 2) == 0:
        ok = 0
    ofs_p0()
    ofs_pa(OFS_PRE)
    ofs_pa(0x40)
    ofs_pn(t1)
    ofs_pa(0x2F)
    ofs_pa(OFS_MOTD)
    if ofs_kopen_p() != 300 or ofs_check(300, 1) == 0:
        ok = 0
    if ofs_kopen(OFS_NOTES) != 50 or ofs_check(50, 3) == 0:
        ok = 0
    nd = ofs_klist(OB_ERR)
    if nd != 1002 or ofs_klist(0) != 1000 or ofs_kopen(0x68637573) != 0x7FFF:
        ok = 0
    ofs_p0()
    ofs_pa(0x786A626F2F)  # "/objx" is not under /obj
    if ofs_kopen_p() != OB_ERR:
        ok = 0
    puts("objfs: /obj lists ")
    put_u64(nd - 1000)
    puts(" files; /obj/motd reads v2 (200 bytes), /obj/@")
    put_u64(t1)
    puts("/motd reads v1 (300 bytes), /obj/@0 lists 0\n")
    if ok == 0:
        panic("objfs: VFS view wrong")
    puts("objstore VFS view OK\n")

def objfs_verify():
# next boot: the files came back from the log
    if ofs_init() != 0:
        panic("objfs: no file type after reboot")
    sz = ofs_kopen(OFS_MOTD)
    ok = ofs_check(200, 2)
    nd = ofs_klist(OB_ERR) - 1000
    puts("objfs: after reboot /obj lists ")
    put_u64(nd)
    puts(" files, /obj/motd is ")
    put_u64(sz)
    puts(" bytes\n")
    if sz != 200 or ok == 0 or nd != 2:
        panic("objfs: files lost")
    puts("objstore VFS view persisted OK\n")
