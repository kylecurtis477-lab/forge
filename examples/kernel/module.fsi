# Kernel modules: relocatable ELF64 objects (ET_REL, straight out of forgec + as) loaded and unloaded
# at runtime. Each module gets a 1 MiB slot of the module area, which sits inside the top 2 GiB with the
# kernel so PC32/PLT32 calls into kernel exports reach. Sections are laid out text | rodata | data+bss,
# each group page aligned, and mapped W^X: text RX, rodata R+NX, data RW+NX.
#
# ABI contract (all module-side symbols are plain @export functions):
#   mod_abi() -> u64            ABI the module was built against: (major << 16) | minor
#   mod_init() -> u64           0 = ok; anything else aborts the load (the module is freed)
#   mod_exit()                  called before unload
#   mod_save_state(buf, cap) -> u64       optional: serialise typed state, returns its length (0 = none)
#   mod_restore_state(buf, len) -> u64    optional: adopt state written by save_state; 1 = ok
# Imports are resolved only against the kernel's versioned export table: a symbol exported "since"
# ABI minor m is invisible to a module built against an older minor, and the major must match exactly.
MOD_BASE = 0xFFFFFFFFD0000000
MOD_SLOT = 0x100000
MOD_SLOTS = 32
KABI_MAJOR = 1
KABI_MINOR = 0
MOD_MAXSEC = 64
MOD_MAXSYM = 32

struct KExport:
    name: u64
    addr: u64
    since: u64

kexports: KExport[160]
nkexports: u64

struct ModSym:
    name: u8[32]
    addr: u64

struct Module:
    slot: u64
    base: u64
    npages: u64
    abi: u64
    init: u64
    exit: u64
    save: u64
    restore: u64
    nsyms: u64
    syms: ModSym[32]
    name: u8[32]

mod_slots: u64[32]
mod_lock: u64
mod_loads: u64
mod_unloads: u64
mod_rejects: u64
mod_err: nptr[u8]
mod_err_sym: nptr[u8]

def mod_load_ok(img: u64, sz: u64) -> ptr[Module]:
# kernel self-tests: load a module that must be accepted
    m = mod_load(img, sz)
    if m == 0:
        panic("mod_load: module rejected")
    return m

def mod_err_str() -> ptr[u8]:
# last rejection reason, for messages
    e = mod_err
    if e != 0:
        return e
    return "(none)"

def kstreq(a: u64, b: u64) -> u64:
    i: u64 = 0
    while vload8(a + i) == vload8(b + i):
        if vload8(a + i) == 0:
            return 1
        i += 1
    return 0

def kexport(name: ptr[u8], a: u64, since: u64):
    if nkexports >= 160:
        panic("kexport table full")
    e = addr(kexports[nkexports])
    e.name = u64(name)
    e.addr = a
    e.since = since
    nkexports += 1

def kexport_find(name: u64, abi: u64) -> u64:
    i: u64 = 0
    while i < nkexports:
        e = addr(kexports[i])
        if kstreq(e.name, name):
            if e.since > (abi & 0xFFFF):
                return 0
            return e.addr
        i += 1
    return 0

def mod_area_init():
# pre-build the page tables of the whole module area so loads never allocate (or leak) table frames
    va = MOD_BASE
    while va < MOD_BASE + MOD_SLOT * MOD_SLOTS:
        pte_addr(kernel_pml4, va, 1)
        va += 0x200000

def mod_fail(msg: ptr[u8]) -> nptr[Module]:
    mod_err = msg
    atomic_add64(u64(addr(mod_rejects)), 1)
    return ptr[Module](0)

def mod_free_pages(base: u64, n: u64):
    i: u64 = 0
    while i < n:
        pa = unmap_page(kernel_pml4, base + i * PAGE)
        if pa != 0:
            frame_free(pa)
        i += 1
    tlb_shootdown(0, base, n)

def mod_slot_get() -> u64:
    f = irq_save()
    spin_lock(u64(addr(mod_lock)))
    i: u64 = 0
    while i < MOD_SLOTS:
        if mod_slots[i] == 0:
            mod_slots[i] = 1
            spin_unlock(u64(addr(mod_lock)))
            irq_restore(f)
            return i
        i += 1
    spin_unlock(u64(addr(mod_lock)))
    irq_restore(f)
    return MOD_SLOTS

def mod_slot_put(s: u64):
    f = irq_save()
    spin_lock(u64(addr(mod_lock)))
    mod_slots[s] = 0
    spin_unlock(u64(addr(mod_lock)))
    irq_restore(f)

def mod_sym(m: ptr[Module], name: ptr[u8]) -> u64:
    i: u64 = 0
    while i < m.nsyms:
        if kstreq(u64(addr(m.syms[i].name[0])), u64(name)):
            return m.syms[i].addr
        i += 1
    return 0

def mod_load(img: u64, sz: u64) -> nptr[Module]:
    secaddr: u64[64]
    # ---- ELF header ----
    if sz < 64 or vload32(img) != 0x464C457F or vload8(img + 4) != 2 or vload8(img + 5) != 1:
        return mod_fail("not an ELF64 LSB image")
    if vload16(img + 16) != 1 or vload16(img + 18) != 62:
        return mod_fail("not an x86-64 ET_REL object")
    shoff = vload64(img + 40)
    shentsize = u64(vload16(img + 58))
    shnum = u64(vload16(img + 60))
    if shentsize != 64 or shnum == 0 or shnum > MOD_MAXSEC or shoff > sz or shoff + shnum * 64 > sz:
        return mod_fail("bad section header table")
    # ---- layout: text | rodata | data+bss, each group page aligned ----
    off: u64 = 0
    grp: u64 = 0
    gend: u64[3]
    i: u64 = 0
    while grp < 3:
        i = 0
        while i < shnum:
            sh = img + shoff + i * 64
            fl = vload64(sh + 8)
            if grp == 0:
                secaddr[i] = 0xFFFFFFFFFFFFFFFF
            g = 3
            if (fl & 2) != 0:
                g = 1
                if (fl & 4) != 0:
                    g = 0
                elif (fl & 1) != 0:
                    g = 2
            if g == grp:
                al = vload64(sh + 48)
                if al == 0:
                    al = 1
                if al > PAGE or (al & (al - 1)) != 0:
                    return mod_fail("bad section alignment")
                off = (off + al - 1) & ~(al - 1)
                secaddr[i] = off
                ssz = vload64(sh + 32)
                typ = vload32(sh + 4)
                if typ != 8 and (vload64(sh + 24) > sz or vload64(sh + 24) + ssz > sz):
                    return mod_fail("section outside the image")
                off += ssz
            i += 1
        off = (off + PAGE - 1) & ~u64(PAGE - 1)
        gend[grp] = off
        grp += 1
    npages = off / PAGE
    if npages == 0 or off > MOD_SLOT:
        return mod_fail("module does not fit a slot")
    # ---- symbol table ----
    symsh: u64 = 0
    i = 0
    while i < shnum:
        if vload32(img + shoff + i * 64 + 4) == 2:
            symsh = img + shoff + i * 64
        i += 1
    if symsh == 0:
        return mod_fail("no symbol table")
    symoff = vload64(symsh + 24)
    nsym = vload64(symsh + 32) / 24
    strl = u64(vload32(symsh + 40))
    if strl >= shnum or symoff + nsym * 24 > sz:
        return mod_fail("bad symbol table")
    stroff = vload64(img + shoff + strl * 64 + 24)
    strsz = vload64(img + shoff + strl * 64 + 32)
    if stroff + strsz > sz or strsz == 0 or vload8(img + stroff + strsz - 1) != 0:
        return mod_fail("bad string table")
    # ---- map the slot RW, copy sections ----
    slot = mod_slot_get()
    if slot == MOD_SLOTS:
        return mod_fail("no free module slot")
    base = MOD_BASE + slot * MOD_SLOT
    i = 0
    while i < npages:
        map_page(kernel_pml4, base + i * PAGE, frame_alloc(), PTE_W | PTE_G | PTE_NX)
        i += 1
    i = 0
    while i < shnum:
        sh = img + shoff + i * 64
        if secaddr[i] != 0xFFFFFFFFFFFFFFFF and vload32(sh + 4) != 8:
            memcpy(base + secaddr[i], img + vload64(sh + 24), vload64(sh + 32))
        i += 1
    m = ptr[Module](kmalloc(sizeof(Module)))
    memset(u64(m), 0, sizeof(Module))
    m.slot = slot
    m.base = base
    m.npages = npages
    # ---- relocations ----
    err: ptr[u8] = ptr[u8](0)
    r: u64 = 0
    while r < shnum and u64(err) == 0:
        rsh = img + shoff + r * 64
        tgt = u64(vload32(rsh + 44))
        if vload32(rsh + 4) == 4 and tgt < shnum and secaddr[tgt] != 0xFFFFFFFFFFFFFFFF:
            roff = vload64(rsh + 24)
            nrel = vload64(rsh + 32) / 24
            if roff + nrel * 24 > sz:
                err = "relocations outside the image"
            k: u64 = 0
            while k < nrel and u64(err) == 0:
                re = img + roff + k * 24
                ro = vload64(re)
                info = vload64(re + 8)
                add = vload64(re + 16)
                rt = info & 0xFFFFFFFF
                si = info >> 32
                if si >= nsym or ro + 8 > vload64(img + shoff + tgt * 64 + 32) + 4:
                    err = "bad relocation"
                    break
                s = img + symoff + si * 24
                shx = u64(vload16(s + 6))
                S: u64 = 0
                if shx == 0:
                    if vload32(s) >= strsz:
                        err = "bad symbol name"
                        break
                    S = kexport_find(img + stroff + vload32(s), 0xFFFF)
                    if S == 0:
                        mod_err_sym = ptr[u8](img + stroff + vload32(s))
                        err = "unresolved symbol"
                        break
                elif shx == 0xFFF1:
                    S = vload64(s + 8)
                elif shx < shnum and secaddr[shx] != 0xFFFFFFFFFFFFFFFF:
                    S = base + secaddr[shx] + vload64(s + 8)
                else:
                    err = "symbol in an unloaded section"
                    break
                P = base + secaddr[tgt] + ro
                v = S + add
                if rt == 1:
                    vstore64(P, v)
                elif rt == 2 or rt == 4:
                    d = v - P
                    if d + 0x80000000 >= 0x100000000:
                        err = "PC32 relocation out of range"
                        break
                    vstore32(P, u32(d & 0xFFFFFFFF))
                elif rt == 11:
                    if v + 0x80000000 >= 0x100000000:
                        err = "32S relocation out of range"
                        break
                    vstore32(P, u32(v & 0xFFFFFFFF))
                else:
                    err = "unsupported relocation type"
                    break
                k += 1
        r += 1
    # ---- exported symbols ----
    i = 0
    while i < nsym and u64(err) == 0:
        s = img + symoff + i * 24
        shx = u64(vload16(s + 6))
        if (vload8(s + 4) >> 4) == 1 and shx != 0 and shx < shnum and secaddr[shx] != 0xFFFFFFFFFFFFFFFF and vload32(s) < strsz:
            nm = img + stroff + vload32(s)
            l: u64 = 0
            while vload8(nm + l) != 0:
                l += 1
            if m.nsyms < MOD_MAXSYM and l < 32:
                memcpy(u64(addr(m.syms[m.nsyms].name[0])), nm, l + 1)
                m.syms[m.nsyms].addr = base + secaddr[shx] + vload64(s + 8)
                m.nsyms += 1
        i += 1
    if u64(err) == 0:
        m.init = mod_sym(m, "mod_init")
        m.exit = mod_sym(m, "mod_exit")
        m.save = mod_sym(m, "mod_save_state")
        m.restore = mod_sym(m, "mod_restore_state")
        if mod_sym(m, "mod_abi") == 0 or m.init == 0 or m.exit == 0:
            err = "missing mod_abi/mod_init/mod_exit"
    # ---- final protections: text RX, rodata R, data RW (all NX except text) ----
    if u64(err) == 0:
        i = 0
        while i < npages:
            va = base + i * PAGE
            e = pte_addr(kernel_pml4, va, 0)
            pa = vload64(e) & ADDR_MASK
            pfl: u64 = PTE_P | PTE_G | PTE_NX | PTE_W
            if va < base + gend[0]:
                pfl = PTE_P | PTE_G
            elif va < base + gend[1]:
                pfl = PTE_P | PTE_G | PTE_NX
            vstore64(e, pa | pfl)
            i += 1
        tlb_shootdown(0, base, npages)
        m.abi = call_ptr(mod_sym(m, "mod_abi"))
        if (m.abi >> 16) != KABI_MAJOR or (m.abi & 0xFFFF) > KABI_MINOR:
            err = "ABI version mismatch"
    if u64(err) == 0:
    # second pass for imports newer than the module's ABI minor (resolution above allowed all):
    # reject the module if it imports a symbol exported only since a later minor
        i = 0
        while i < nsym:
            s = img + symoff + i * 24
            if vload16(s + 6) == 0 and vload32(s) != 0 and vload32(s) < strsz:
                if kexport_find(img + stroff + vload32(s), m.abi) == 0:
                    mod_err_sym = ptr[u8](img + stroff + vload32(s))
                    err = "import newer than the module ABI"
                    break
            i += 1
    if u64(err) == 0:
        if call_ptr(m.init) != 0:
            err = "mod_init failed"
    if u64(err) != 0:
        mod_free_pages(base, npages)
        mod_slot_put(slot)
        kfree(u64(m))
        return mod_fail(err)
    atomic_add64(u64(addr(mod_loads)), 1)
    return m

def mod_unload(m: ptr[Module]):
    call_ptr(m.exit)
    mod_free_pages(m.base, m.npages)
    mod_slot_put(m.slot)
    kfree(u64(m))
    atomic_add64(u64(addr(mod_unloads)), 1)
