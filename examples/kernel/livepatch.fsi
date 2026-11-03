# ---- Live patching (Phase 2.5) ----
# A @patchable function starts with the 5-byte nop 0f 1f 44 00 00 (emitted by forgec). Patching replaces
# it with `jmp rel32` to the replacement (normally inside a loaded module); reverting writes the nop back.
# Both writes happen under stop-machine: every other CPU is parked with interrupts off and serialises
# (cpuid) before it runs again, so no CPU fetches a half-written instruction. The function is 16-byte
# aligned, so the 5 bytes sit in one aligned 8-byte word and are written with a single store through
# the writable HHDM alias (kernel text itself stays read-only). Before patching, the first 32 bytes must
# hash to the value recorded at registration; after a revert they must hash to it again.
LP_ENOTPATCHABLE = 1
LP_ERANGE = 2
LP_EHASH = 3
LP_ESELFTEST = 4
LP_EFULL = 5
LP_MAX = 8
# site table: each registered @patchable entry keeps its own text hash and patch state
lp_sites: u64[8]
lp_hashes: u64[8]
lp_on: u64[8]
lp_nsites: u64
lp_applied: u64
lp_reverted: u64
lp_rollbacks: u64

@unsafe
def lp_fnv(va: u64, n: u64) -> u64:
    h: u64 = 0xcbf29ce484222325
    i: u64 = 0
    while i < n:
        h = (h ^ vload8(va + i)) * 0x100000001b3
        i += 1
    return h

@unsafe
def lp_is_nop(va: u64) -> u64:
    return u64(vload8(va) == 0x0F and vload8(va + 1) == 0x1F and vload8(va + 2) == 0x44 and vload8(va + 3) == 0 and vload8(va + 4) == 0)

def lp_find(target: u64) -> u64:
# index of the site for target, or LP_MAX
    i: u64 = 0
    while i < lp_nsites:
        if lp_sites[i] == target:
            return i
        i += 1
    return LP_MAX

def lp_site_hash(target: u64) -> u64:
    i = lp_find(target)
    if i == LP_MAX:
        return 0
    return lp_hashes[i]

def lp_register(target: u64) -> u64:
    if lp_find(target) != LP_MAX:
        return 0
    if (target & 15) != 0 or lp_is_nop(target) == 0:
        return LP_ENOTPATCHABLE
    if lp_nsites >= LP_MAX:
        return LP_EFULL
    lp_sites[lp_nsites] = target
    lp_hashes[lp_nsites] = lp_fnv(target, 32)
    lp_nsites += 1
    return 0

def lp_npatched() -> u64:
    n: u64 = 0
    i: u64 = 0
    while i < lp_nsites:
        n += lp_on[i]
        i += 1
    return n

@unsafe
def lp_write5(target: u64, b: u64):
# b: the new low 5 bytes; the other 3 bytes of the aligned word are kept
    w = p2v(virt_to_phys(kernel_pml4, target))
    f = stop_machine_begin()
    old = vload64(w)
    vstore64(w, (old & 0xFFFFFF0000000000) | (b & 0xFFFFFFFFFF))
    stop_machine_end(f)

def lp_apply(target: u64, newfn: u64) -> u64:
    i = lp_find(target)
    if i == LP_MAX or lp_is_nop(target) == 0:
        return LP_ENOTPATCHABLE
    if lp_fnv(target, 32) != lp_hashes[i]:
        return LP_EHASH
    d = newfn - (target + 5)
    if d + 0x80000000 > 0xFFFFFFFF:
        return LP_ERANGE
    lp_write5(target, 0xE9 | ((d & 0xFFFFFFFF) << 8))
    lp_on[i] = 1
    lp_applied += 1
    return 0

@unsafe
def lp_revert(target: u64) -> u64:
    i = lp_find(target)
    if i == LP_MAX or vload8(target) != 0xE9:
        return LP_ENOTPATCHABLE
    lp_write5(target, 0x0000441F0F)
    lp_on[i] = 0
    lp_reverted += 1
    if lp_fnv(target, 32) != lp_hashes[i]:
        return LP_EHASH
    return 0

lp_rejects: u64

@unsafe
def lp_apply_checked(target: u64, newfn: u64, check: u64) -> u64:
# check(fn) -> 1 if fn behaves. It runs on the replacement before it is installed (a failing
# replacement is never reachable), then on the live entry point; failing there rolls back.
    if call_ptr(check, newfn) != 1:
        lp_rejects += 1
        return LP_ESELFTEST
    r = lp_apply(target, newfn)
    if r != 0:
        return r
    if call_ptr(check, target) != 1:
        if lp_revert(target) != 0:
            panic("livepatch rollback failed")
        lp_rollbacks += 1
        return LP_ESELFTEST
    return 0
