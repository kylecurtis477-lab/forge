# Phase 6.1: verifier-produced proof manifests. tools/forgeseal.py appends a 160-byte trailer to
# every user ELF (see struct Seal) carrying the safety level, the unsafe-op count and the declared
# capabilities, the SHA-256 of the ELF bytes, and (v2, gap closed after 6.4) an Ed25519 signature
# over the trailer by the build's private seal key. The kernel embeds only the public key
# (seal.pub), so holding the kernel image no longer lets anyone forge a seal. The kernel refuses
# to start a user image whose trailer is missing, malformed, or fails the hash or signature.
SEAL_MAGIC = 0x4652504547524F46
SEAL_SIZE = 160
SEAL_OK = 0x8000000000000000
SEAL_USER = 0
SEAL_MODULE = 1  # trailer word +56: what the image is for (forgeseal.py --module)

struct Blk64:
    b: u8[64]

struct Dig:
    b: u8[32]

struct Seal:
    magic: u64
    version: u64
    level: u64
    unsafe_ops: u64
    caps: u64
    elf_len: u64
    own_ops: u64
    rsvd: u64
    hash: u8[32]
    sig: EdSig

struct Sha:
    h: u64[8]
    w: u64[64]
    buf: u8[64]
    nbuf: u64
    total: u64

sha_k: u64[64] = [
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0xfc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x6ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2]

def ror32(x: u64, n: u64) -> u64:
    return ((x >> n) | (x << (32 - n))) & 0xFFFFFFFF

def sha_init(s: ptr[Sha]):
    s.h[0] = 0x6a09e667
    s.h[1] = 0xbb67ae85
    s.h[2] = 0x3c6ef372
    s.h[3] = 0xa54ff53a
    s.h[4] = 0x510e527f
    s.h[5] = 0x9b05688c
    s.h[6] = 0x1f83d9ab
    s.h[7] = 0x5be0cd19
    s.nbuf = 0
    s.total = 0

def sha_compress(s: ptr[Sha]):
    i: u64 = 0
    while i < 16:
        s.w[i] = (u64(s.buf[i * 4]) << 24) | (u64(s.buf[i * 4 + 1]) << 16) | (u64(s.buf[i * 4 + 2]) << 8) | u64(s.buf[i * 4 + 3])
        i += 1
    while i < 64:
        x = s.w[i - 15]
        y = s.w[i - 2]
        s0 = ror32(x, 7) ^ ror32(x, 18) ^ (x >> 3)
        s1 = ror32(y, 17) ^ ror32(y, 19) ^ (y >> 10)
        s.w[i] = (s.w[i - 16] + s0 + s.w[i - 7] + s1) & 0xFFFFFFFF
        i += 1
    a = s.h[0]
    b = s.h[1]
    c = s.h[2]
    d = s.h[3]
    e = s.h[4]
    f = s.h[5]
    g = s.h[6]
    h = s.h[7]
    i = 0
    while i < 64:
        t1 = (h + (ror32(e, 6) ^ ror32(e, 11) ^ ror32(e, 25)) + ((e & f) ^ ((~e) & g)) + sha_k[i] + s.w[i]) & 0xFFFFFFFF
        t2 = ((ror32(a, 2) ^ ror32(a, 13) ^ ror32(a, 22)) + ((a & b) ^ (a & c) ^ (b & c))) & 0xFFFFFFFF
        h = g
        g = f
        f = e
        e = (d + t1) & 0xFFFFFFFF
        d = c
        c = b
        b = a
        a = (t1 + t2) & 0xFFFFFFFF
        i += 1
    s.h[0] = (s.h[0] + a) & 0xFFFFFFFF
    s.h[1] = (s.h[1] + b) & 0xFFFFFFFF
    s.h[2] = (s.h[2] + c) & 0xFFFFFFFF
    s.h[3] = (s.h[3] + d) & 0xFFFFFFFF
    s.h[4] = (s.h[4] + e) & 0xFFFFFFFF
    s.h[5] = (s.h[5] + f) & 0xFFFFFFFF
    s.h[6] = (s.h[6] + g) & 0xFFFFFFFF
    s.h[7] = (s.h[7] + h) & 0xFFFFFFFF

def sha_byte(s: ptr[Sha], v: u64):
    s.buf[s.nbuf & 63] = u8(v & 255)
    s.nbuf += 1
    s.total += 1
    if s.nbuf == 64:
        sha_compress(s)
        s.nbuf = 0

@unsafe
def sha_mem(s: ptr[Sha], a: u64, n: u64):
# hash n bytes at kernel address a, viewed 64 bytes at a time (only the first n are read)
    o: u64 = 0
    while o < n:
        q = ptr[Blk64](a + o)
        j: u64 = 0
        while j < 64 and o + j < n:
            sha_byte(s, u64(q.b[j]))
            j += 1
        o += 64

def sha_final(s: ptr[Sha], out: ptr[Dig]):
    bits = s.total * 8
    sha_byte(s, 0x80)
    while s.nbuf != 56:
        sha_byte(s, 0)
    i: u64 = 0
    while i < 8:
        sha_byte(s, (bits >> (56 - i * 8)) & 255)
        i += 1
    i = 0
    while i < 32:
        out.b[i] = u8((s.h[i / 4] >> (24 - (i % 4) * 8)) & 255)
        i += 1

def seal_eq(a: ptr[Dig], t: ptr[Seal]) -> u64:
# compare a digest against the trailer's image hash
    d: u64 = 0
    i: u64 = 0
    while i < 32:
        d = d | (u64(a.b[i]) ^ u64(t.hash[i]))
        i += 1
    return d == 0

seal_ok_count: u64
seal_refused: u64
compat_sandbox_ready: u64  # 6.4: set by the Phase 11 Linux ABI layer; 0 = no compat sandbox
seal_why: u64  # reason code of the last refusal

def seal_reason(code: u64) -> ptr[u8]:
    if code == 1:
        return "no proof manifest"
    if code == 2:
        return "unknown manifest version"
    if code == 3:
        return "manifest length mismatch"
    if code == 4:
        return "bad manifest signature"
    if code == 5:
        return "image hash mismatch"
    if code == 7:
        return "wrong image kind (user program vs kernel module)"
    if code == 6:
        return "Linux ABI image needs the compatibility sandbox (Phase 11, not built)"
    return "?"

# cache of signed trailer contents that passed Ed25519 verification (round robin)
struct SealCache:
    m: EdMsg[8]
    n: u64

seal_cache: SealCache
seal_cache_hits: u64

def seal_cache_hit(msg: ptr[EdMsg]) -> u64:
    k: u64 = 0
    while k < seal_cache.n and k < 8:
        d: u64 = 0
        i: u64 = 0
        while i < 96:
            d = d | (u64(seal_cache.m[k].b[i]) ^ u64(msg.b[i]))
            i += 1
        if d == 0:
            seal_cache_hits += 1
            return 1
        k += 1
    return 0

def seal_cache_put(msg: ptr[EdMsg]):
    k = seal_cache.n % 8
    i: u64 = 0
    while i < 96:
        seal_cache.m[k].b[i] = msg.b[i]
        i += 1
    seal_cache.n += 1

def seal_fail(code: u64) -> u64:
    seal_why = code
    seal_refused += 1
    return 0

@unsafe
def image_verify(img: u64, len: u64, kind: u64) -> u64:
# returns SEAL_OK | declared caps, or 0 (reason in seal_why); kind = SEAL_USER or SEAL_MODULE
    if len < SEAL_SIZE + 64:
        return seal_fail(1)
    t = ptr[Seal](img + len - SEAL_SIZE)
    if t.magic != SEAL_MAGIC:
        return seal_fail(1)
    if t.version != 2:
        return seal_fail(2)
    if t.elf_len != len - SEAL_SIZE:
        return seal_fail(3)
    # signed message = trailer[0:96]; public key embedded in the kernel image. A trailer whose
    # signed bytes equal one already verified is authentic, so the Ed25519 step is skipped for it
    # (the image hash below is still recomputed every time).
    msg = ptr[EdMsg](t)
    if seal_cache_hit(msg) == 0:
        if ed25519_verify(addr(t.sig), ptr[B32](uimg(4)), msg, 96) == 0:
            return seal_fail(4)
        seal_cache_put(msg)
    if t.rsvd != kind:
        return seal_fail(7)
    ctx: Sha
    s = addr(ctx)
    inner: Dig
    sha_init(s)
    sha_mem(s, img, len - SEAL_SIZE)
    sha_final(s, addr(inner))
    if seal_eq(addr(inner), t) == 0:
        return seal_fail(5)
    # 6.4: Linux-ABI images (EI_OSABI = 3) run only inside the explicit compat sandbox;
    # checked after the seal so a sealed Linux image is still refused
    if compat_sandbox_ready == 0 and ((ptr[Ehdr](img).w0 >> 56) & 0xFF) == 3:
        return seal_fail(6)
    seal_ok_count += 1
    return SEAL_OK | (t.caps & 0xFFFFFFFF)

def seal_log(what: ptr[u8]):
    puts("verify: ")
    puts(what)
    puts(" refused: ")
    puts(seal_reason(seal_why))
    putc(10)

def seal_check(path: ptr[u8], want: u64) -> u64:
# Phase 6.1 self-test: verify an image read from the ESP; want = expected result (0 = refused)
    sz: u64 = 0
    buf = fs_read_file(path, addr(sz))
    if buf == 0:
        panic("seal test: image missing on the ESP")
    r = image_verify(buf, sz, SEAL_USER)
    kfree(buf)
    if r != want:
        puts(path)
        panic(": unexpected verification result")
    if r == 0:
        return seal_why
    return 0

def seal_test():
    n0 = seal_refused
    seal_check("EFI/Forge/bin/hello", SEAL_OK)
    seal_check("EFI/Forge/bin/cat", SEAL_OK | 1)
    seal_check("EFI/Forge/bin/fault", SEAL_OK | 32)
    if seal_check("EFI/Forge/bin/unsealed", 0) != 1:
        panic("unsealed image refused for the wrong reason")
    if seal_check("EFI/Forge/bin/tampered", 0) != 5:
        panic("tampered image refused for the wrong reason")
    if seal_check("EFI/Forge/bin/forged", 0) != 4:
        panic("forged manifest refused for the wrong reason")
    if seal_check("EFI/Forge/bin/linuxabi", 0) != 6:
        panic("Linux ABI image refused for the wrong reason")
    if seal_refused - n0 != 4:
        panic("seal refusals not counted")
    puts("proof manifest (Ed25519): 3 sealed images accepted with their declared caps; unsealed, tampered and forged images refused; Linux ABI image confined to compat sandbox\n")

# ---- Phase 6.2: the kernel grants exactly the declared capabilities ----
CAPB_FS = 1
CAPB_SPAWN = 2
CAPB_IPC = 4
CAPB_DRV = 8
CAPB_KEXEC = 16
CAPB_PROBE = 32
CAPB_KTEST = 64
CAPB_ALL = 127
cap_sys_denials: u64

def sys_need(nr: u64) -> u64:
# the capability a syscall needs; 0 = base syscall (read/write/close/mmap/brk/exit/getpid/...)
    if nr == 2 or nr == 4 or nr == 78 or nr == 421:
        return CAPB_FS
    if nr == 57 or nr == 59 or nr == 61 or nr == 426:
        return CAPB_SPAWN
    if nr >= 423 and nr <= 425:
        return CAPB_IPC
    if nr >= 400 and nr <= 407:
        return CAPB_DRV
    if nr == 408:
        return CAPB_KTEST
    if nr == 409:
        return CAPB_KEXEC
    if nr == 200:
        return CAPB_PROBE
    return 0

def cap_name(c: u64) -> ptr[u8]:
    if c == CAPB_FS:
        return "fs"
    if c == CAPB_SPAWN:
        return "spawn"
    if c == CAPB_IPC:
        return "ipc"
    if c == CAPB_DRV:
        return "drv"
    if c == CAPB_KEXEC:
        return "kexec"
    if c == CAPB_PROBE:
        return "probe"
    return "ktest"

def cap_sys_deny(p: ptr[Proc], nr: u64, need: u64) -> u64:
    atomic_fetch_add(addr(cap_sys_denials), 1, acq_rel)
    puts("caps: pid ")
    put_u64(p.pid)
    puts(" denied syscall ")
    put_u64(nr)
    puts(": capability '")
    puts(cap_name(need))
    puts("' not declared in its manifest\n")
    return EACCES

def cap_test():
# a sealed program declaring only fs: open works, fork and probe are refused by syscall_dispatch
    sz: u64 = 0
    buf = fs_read_file("EFI/Forge/bin/capdeny", addr(sz))
    if buf == 0:
        panic("cap test: capdeny missing")
    d0 = cap_sys_denials
    t = proc_spawn(buf, sz)
    kfree(buf)
    if u64(t) == 0:
        panic("cap test: capdeny refused")
    code = proc_reap(t)
    if code != 0 or cap_sys_denials - d0 != 2:
        panic("cap test: undeclared capability not denied")
    puts("capability gate: undeclared spawn and probe denied at the syscall boundary\n")
