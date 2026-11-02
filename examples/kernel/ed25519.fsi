# Ed25519 signature verification (RFC 8032), freestanding and fully safe Forge.
# A port of TweetNaCl's crypto_sign_open: field elements are 16 signed 16-bit limbs (i64),
# points are extended coordinates (x, y, z, t). Verification only: no secret data is handled,
# so constant-time behaviour is not required. Used by seal.fsi (kernel) and tests/ed25519.

struct Gf:
    v: i64[16]

struct Gf31:
    v: i64[31]

struct Ge:
    x: Gf
    y: Gf
    z: Gf
    t: Gf

struct B32:
    b: u8[32]

struct B64:
    b: u8[64]

struct I64x64:
    v: i64[64]

struct Sha5:
    h: u64[8]
    w: u64[80]
    buf: u8[128]
    nbuf: u64
    total: u64

# D, 2D, base X, base Y, sqrt(-1) as 16-bit limbs
ed_c: i64[80] = [0x78a3, 0x1359, 0x4dca, 0x75eb, 0xd8ab, 0x4141, 0xa4d, 0x70, 0xe898, 0x7779, 0x4079, 0x8cc7, 0xfe73, 0x2b6f, 0x6cee, 0x5203, 0xf159, 0x26b2, 0x9b94, 0xebd6, 0xb156, 0x8283, 0x149a, 0xe0, 0xd130, 0xeef3, 0x80f2, 0x198e, 0xfce7, 0x56df, 0xd9dc, 0x2406, 0xd51a, 0x8f25, 0x2d60, 0xc956, 0xa7b2, 0x9525, 0xc760, 0x692c, 0xdc5c, 0xfdd6, 0xe231, 0xc0a4, 0x53fe, 0xcd6e, 0x36d3, 0x2169, 0x6658, 0x6666, 0x6666, 0x6666, 0x6666, 0x6666, 0x6666, 0x6666, 0x6666, 0x6666, 0x6666, 0x6666, 0x6666, 0x6666, 0x6666, 0x6666, 0xa0b0, 0x4a0e, 0x1b27, 0xc4ee, 0xe478, 0xad2f, 0x1806, 0x2f43, 0xd7a7, 0x3dfb, 0x99, 0x2b4d, 0xdf0b, 0x4fc1, 0x2480, 0x2b83]
ED_D = 0
ED_D2 = 16
ED_X = 32
ED_Y = 48
ED_I = 64

# the group order L, little-endian bytes
ed_l: i64[32] = [0xed, 0xd3, 0xf5, 0x5c, 0x1a, 0x63, 0x12, 0x58, 0xd6, 0x9c, 0xf7, 0xa2, 0xde, 0xf9, 0xde, 0x14, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x10]

s5_k: u64[80] = [0x428a2f98d728ae22, 0x7137449123ef65cd, 0xb5c0fbcfec4d3b2f, 0xe9b5dba58189dbbc, 0x3956c25bf348b538, 0x59f111f1b605d019, 0x923f82a4af194f9b, 0xab1c5ed5da6d8118, 0xd807aa98a3030242, 0x12835b0145706fbe, 0x243185be4ee4b28c, 0x550c7dc3d5ffb4e2, 0x72be5d74f27b896f, 0x80deb1fe3b1696b1, 0x9bdc06a725c71235, 0xc19bf174cf692694, 0xe49b69c19ef14ad2, 0xefbe4786384f25e3, 0xfc19dc68b8cd5b5, 0x240ca1cc77ac9c65, 0x2de92c6f592b0275, 0x4a7484aa6ea6e483, 0x5cb0a9dcbd41fbd4, 0x76f988da831153b5, 0x983e5152ee66dfab, 0xa831c66d2db43210, 0xb00327c898fb213f, 0xbf597fc7beef0ee4, 0xc6e00bf33da88fc2, 0xd5a79147930aa725, 0x6ca6351e003826f, 0x142929670a0e6e70, 0x27b70a8546d22ffc, 0x2e1b21385c26c926, 0x4d2c6dfc5ac42aed, 0x53380d139d95b3df, 0x650a73548baf63de, 0x766a0abb3c77b2a8, 0x81c2c92e47edaee6, 0x92722c851482353b, 0xa2bfe8a14cf10364, 0xa81a664bbc423001, 0xc24b8b70d0f89791, 0xc76c51a30654be30, 0xd192e819d6ef5218, 0xd69906245565a910, 0xf40e35855771202a, 0x106aa07032bbd1b8, 0x19a4c116b8d2d0c8, 0x1e376c085141ab53, 0x2748774cdf8eeb99, 0x34b0bcb5e19b48a8, 0x391c0cb3c5c95a63, 0x4ed8aa4ae3418acb, 0x5b9cca4f7763e373, 0x682e6ff3d6b2b8a3, 0x748f82ee5defb2fc, 0x78a5636f43172f60, 0x84c87814a1f0ab72, 0x8cc702081a6439ec, 0x90befffa23631e28, 0xa4506cebde82bde9, 0xbef9a3f7b2c67915, 0xc67178f2e372532b, 0xca273eceea26619c, 0xd186b8c721c0c207, 0xeada7dd6cde0eb1e, 0xf57d4f7fee6ed178, 0x6f067aa72176fba, 0xa637dc5a2c898a6, 0x113f9804bef90dae, 0x1b710b35131c471b, 0x28db77f523047d84, 0x32caab7b40c72493, 0x3c9ebe0a15c9bebc, 0x431d67c49c100d4c, 0x4cc5d4becb3e42b6, 0x597f299cfc657e2a, 0x5fcb6fab3ad6faec, 0x6c44198c4a475817]
s5_iv: u64[8] = [0x6a09e667f3bcc908, 0xbb67ae8584caa73b, 0x3c6ef372fe94f82b, 0xa54ff53a5f1d36f1, 0x510e527fade682d1, 0x9b05688c2b3e6c1f, 0x1f83d9abfb41bd6b, 0x5be0cd19137e2179]

# ---- SHA-512 ----
def ror64(x: u64, n: u64) -> u64:
    return (x >> n) | (x << (64 - n))

def s5_init(s: ptr[Sha5]):
    i: u64 = 0
    while i < 8:
        s.h[i] = s5_iv[i]
        i += 1
    s.nbuf = 0
    s.total = 0

def s5_compress(s: ptr[Sha5]):
    i: u64 = 0
    while i < 16:
        x: u64 = 0
        j: u64 = 0
        while j < 8:
            x = (x << 8) | u64(s.buf[i * 8 + j])
            j += 1
        s.w[i] = x
        i += 1
    while i < 80:
        x = s.w[i - 15]
        y = s.w[i - 2]
        s0 = ror64(x, 1) ^ ror64(x, 8) ^ (x >> 7)
        s1 = ror64(y, 19) ^ ror64(y, 61) ^ (y >> 6)
        s.w[i] = s.w[i - 16] + s0 + s.w[i - 7] + s1
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
    while i < 80:
        t1 = h + (ror64(e, 14) ^ ror64(e, 18) ^ ror64(e, 41)) + ((e & f) ^ ((~e) & g)) + s5_k[i] + s.w[i]
        t2 = (ror64(a, 28) ^ ror64(a, 34) ^ ror64(a, 39)) + ((a & b) ^ (a & c) ^ (b & c))
        h = g
        g = f
        f = e
        e = d + t1
        d = c
        c = b
        b = a
        a = t1 + t2
        i += 1
    s.h[0] = s.h[0] + a
    s.h[1] = s.h[1] + b
    s.h[2] = s.h[2] + c
    s.h[3] = s.h[3] + d
    s.h[4] = s.h[4] + e
    s.h[5] = s.h[5] + f
    s.h[6] = s.h[6] + g
    s.h[7] = s.h[7] + h

def s5_byte(s: ptr[Sha5], v: u64):
    s.buf[s.nbuf & 127] = u8(v & 255)
    s.nbuf += 1
    s.total += 1
    if s.nbuf == 128:
        s5_compress(s)
        s.nbuf = 0

def s5_final(s: ptr[Sha5], out: ptr[B64]):
    bits = s.total * 8
    s5_byte(s, 0x80)
    while s.nbuf != 112:
        s5_byte(s, 0)
    i: u64 = 0
    while i < 8:
        s5_byte(s, 0)
        i += 1
    i = 0
    while i < 8:
        s5_byte(s, (bits >> (56 - i * 8)) & 255)
        i += 1
    i = 0
    while i < 64:
        out.b[i] = u8((s.h[i / 8] >> (56 - (i % 8) * 8)) & 255)
        i += 1

# ---- field arithmetic mod 2^255 - 19 ----
def gf_zero(o: ptr[Gf]):
    i: u64 = 0
    while i < 16:
        o.v[i] = 0
        i += 1

def gf_one(o: ptr[Gf]):
    gf_zero(o)
    o.v[0] = 1

def gf_const(o: ptr[Gf], k: u64):
    i: u64 = 0
    while i < 16:
        o.v[i] = ed_c[k + i]
        i += 1

def gf_copy(o: ptr[Gf], a: ptr[Gf]):
    i: u64 = 0
    while i < 16:
        o.v[i] = a.v[i]
        i += 1

def gf_car(o: ptr[Gf]):
    i: u64 = 0
    while i < 16:
        o.v[i] = o.v[i] + 65536
        c = o.v[i] >> 16
        if i < 15:
            o.v[i + 1] = o.v[i + 1] + c - 1
        else:
            o.v[0] = o.v[0] + 38 * (c - 1)
        o.v[i] = o.v[i] - (c << 16)
        i += 1

def gf_sel(p: ptr[Gf], q: ptr[Gf], b: i64):
    c: i64 = ~(b - 1)
    i: u64 = 0
    while i < 16:
        t = c & (p.v[i] ^ q.v[i])
        p.v[i] = p.v[i] ^ t
        q.v[i] = q.v[i] ^ t
        i += 1

def gf_pack(o: ptr[B32], n: ptr[Gf]):
    m: Gf
    t: Gf
    gf_copy(addr(t), n)
    gf_car(addr(t))
    gf_car(addr(t))
    gf_car(addr(t))
    j: u64 = 0
    while j < 2:
        m.v[0] = t.v[0] - 0xffed
        i: u64 = 1
        while i < 15:
            m.v[i] = t.v[i] - 0xffff - ((m.v[i - 1] >> 16) & 1)
            m.v[i - 1] = m.v[i - 1] & 0xffff
            i += 1
        m.v[15] = t.v[15] - 0x7fff - ((m.v[14] >> 16) & 1)
        b = (m.v[15] >> 16) & 1
        m.v[14] = m.v[14] & 0xffff
        gf_sel(addr(t), addr(m), 1 - b)
        j += 1
    i = 0
    while i < 16:
        o.b[2 * i] = u8(t.v[i] & 0xff)
        o.b[2 * i + 1] = u8((t.v[i] >> 8) & 0xff)
        i += 1

def gf_neq(a: ptr[Gf], b: ptr[Gf]) -> u64:
    x: B32
    y: B32
    gf_pack(addr(x), a)
    gf_pack(addr(y), b)
    d: u64 = 0
    i: u64 = 0
    while i < 32:
        d = d | (u64(x.b[i]) ^ u64(y.b[i]))
        i += 1
    return d != 0

def gf_par(a: ptr[Gf]) -> i64:
    x: B32
    gf_pack(addr(x), a)
    return i64(x.b[0]) & 1

def gf_unpack(o: ptr[Gf], n: ptr[B32]):
    i: u64 = 0
    while i < 16:
        o.v[i] = i64(n.b[2 * i]) + (i64(n.b[2 * i + 1]) << 8)
        i += 1
    o.v[15] = o.v[15] & 0x7fff

def gf_add(o: ptr[Gf], a: ptr[Gf], b: ptr[Gf]):
    i: u64 = 0
    while i < 16:
        o.v[i] = a.v[i] + b.v[i]
        i += 1

def gf_sub(o: ptr[Gf], a: ptr[Gf], b: ptr[Gf]):
    i: u64 = 0
    while i < 16:
        o.v[i] = a.v[i] - b.v[i]
        i += 1

def gf_mul(o: ptr[Gf], a: ptr[Gf], b: ptr[Gf]):
    t: Gf31
    i: u64 = 0
    while i < 31:
        t.v[i] = 0
        i += 1
    i = 0
    while i < 16:
        j: u64 = 0
        while j < 16:
            t.v[i + j] = t.v[i + j] + a.v[i] * b.v[j]
            j += 1
        i += 1
    i = 0
    while i < 15:
        t.v[i] = t.v[i] + 38 * t.v[i + 16]
        i += 1
    i = 0
    while i < 16:
        o.v[i] = t.v[i]
        i += 1
    gf_car(o)
    gf_car(o)

def gf_sq(o: ptr[Gf], a: ptr[Gf]):
    gf_mul(o, a, a)

def gf_inv(o: ptr[Gf], x: ptr[Gf]):
    c: Gf
    gf_copy(addr(c), x)
    a: i64 = 253
    while a >= 0:
        gf_sq(addr(c), addr(c))
        if a != 2 and a != 4:
            gf_mul(addr(c), addr(c), x)
        a -= 1
    gf_copy(o, addr(c))

def gf_pow2523(o: ptr[Gf], x: ptr[Gf]):
    c: Gf
    gf_copy(addr(c), x)
    a: i64 = 250
    while a >= 0:
        gf_sq(addr(c), addr(c))
        if a != 1:
            gf_mul(addr(c), addr(c), x)
        a -= 1
    gf_copy(o, addr(c))

# ---- group operations ----
def ge_add(p: ptr[Ge], q: ptr[Ge]):
    a: Gf
    b: Gf
    c: Gf
    d: Gf
    t: Gf
    e: Gf
    f: Gf
    g: Gf
    h: Gf
    k: Gf
    gf_sub(addr(a), addr(p.y), addr(p.x))
    gf_sub(addr(t), addr(q.y), addr(q.x))
    gf_mul(addr(a), addr(a), addr(t))
    gf_add(addr(b), addr(p.x), addr(p.y))
    gf_add(addr(t), addr(q.x), addr(q.y))
    gf_mul(addr(b), addr(b), addr(t))
    gf_mul(addr(c), addr(p.t), addr(q.t))
    gf_const(addr(k), ED_D2)
    gf_mul(addr(c), addr(c), addr(k))
    gf_mul(addr(d), addr(p.z), addr(q.z))
    gf_add(addr(d), addr(d), addr(d))
    gf_sub(addr(e), addr(b), addr(a))
    gf_sub(addr(f), addr(d), addr(c))
    gf_add(addr(g), addr(d), addr(c))
    gf_add(addr(h), addr(b), addr(a))
    gf_mul(addr(p.x), addr(e), addr(f))
    gf_mul(addr(p.y), addr(h), addr(g))
    gf_mul(addr(p.z), addr(g), addr(f))
    gf_mul(addr(p.t), addr(e), addr(h))

def ge_cswap(p: ptr[Ge], q: ptr[Ge], b: i64):
    gf_sel(addr(p.x), addr(q.x), b)
    gf_sel(addr(p.y), addr(q.y), b)
    gf_sel(addr(p.z), addr(q.z), b)
    gf_sel(addr(p.t), addr(q.t), b)

def ge_copy(p: ptr[Ge], q: ptr[Ge]):
    gf_copy(addr(p.x), addr(q.x))
    gf_copy(addr(p.y), addr(q.y))
    gf_copy(addr(p.z), addr(q.z))
    gf_copy(addr(p.t), addr(q.t))

def ge_pack(r: ptr[B32], p: ptr[Ge]):
    zi: Gf
    tx: Gf
    ty: Gf
    gf_inv(addr(zi), addr(p.z))
    gf_mul(addr(tx), addr(p.x), addr(zi))
    gf_mul(addr(ty), addr(p.y), addr(zi))
    gf_pack(r, addr(ty))
    r.b[31] = u8(u64(r.b[31]) ^ (u64(gf_par(addr(tx))) << 7))

def ge_scalarmult(p: ptr[Ge], q0: ptr[Ge], s: ptr[B32]):
# p = s * q0 (q0 is not modified)
    q: Ge
    ge_copy(addr(q), q0)
    gf_zero(addr(p.x))
    gf_one(addr(p.y))
    gf_one(addr(p.z))
    gf_zero(addr(p.t))
    i: i64 = 255
    while i >= 0:
        b = (i64(s.b[u64(i) / 8]) >> (i & 7)) & 1
        ge_cswap(p, addr(q), b)
        ge_add(addr(q), p)
        ge_add(p, p)
        ge_cswap(p, addr(q), b)
        i -= 1

def ge_scalarbase(p: ptr[Ge], s: ptr[B32]):
    q: Ge
    gf_const(addr(q.x), ED_X)
    gf_const(addr(q.y), ED_Y)
    gf_one(addr(q.z))
    gf_mul(addr(q.t), addr(q.x), addr(q.y))
    ge_scalarmult(p, addr(q), s)

def ge_unpackneg(r: ptr[Ge], p: ptr[B32]) -> u64:
# decode -P from its 32-byte encoding; 0 = not a curve point
    t: Gf
    chk: Gf
    num: Gf
    den: Gf
    den2: Gf
    den4: Gf
    den6: Gf
    k: Gf
    gf_one(addr(r.z))
    gf_unpack(addr(r.y), p)
    gf_sq(addr(num), addr(r.y))
    gf_const(addr(k), ED_D)
    gf_mul(addr(den), addr(num), addr(k))
    gf_sub(addr(num), addr(num), addr(r.z))
    gf_add(addr(den), addr(r.z), addr(den))
    gf_sq(addr(den2), addr(den))
    gf_sq(addr(den4), addr(den2))
    gf_mul(addr(den6), addr(den4), addr(den2))
    gf_mul(addr(t), addr(den6), addr(num))
    gf_mul(addr(t), addr(t), addr(den))
    gf_pow2523(addr(t), addr(t))
    gf_mul(addr(t), addr(t), addr(num))
    gf_mul(addr(t), addr(t), addr(den))
    gf_mul(addr(t), addr(t), addr(den))
    gf_mul(addr(r.x), addr(t), addr(den))
    gf_sq(addr(chk), addr(r.x))
    gf_mul(addr(chk), addr(chk), addr(den))
    if gf_neq(addr(chk), addr(num)):
        gf_const(addr(k), ED_I)
        gf_mul(addr(r.x), addr(r.x), addr(k))
    gf_sq(addr(chk), addr(r.x))
    gf_mul(addr(chk), addr(chk), addr(den))
    if gf_neq(addr(chk), addr(num)):
        return 0
    if gf_par(addr(r.x)) == (i64(p.b[31]) >> 7):
        gf_zero(addr(k))
        gf_sub(addr(r.x), addr(k), addr(r.x))
    gf_mul(addr(r.t), addr(r.x), addr(r.y))
    return 1

# ---- scalars mod L ----
def sc_reduce(r: ptr[B32], h: ptr[B64]):
    x: I64x64
    i: u64 = 0
    while i < 64:
        x.v[i] = i64(h.b[i])
        i += 1
    i = 63
    while i >= 32:
        carry: i64 = 0
        j = i - 32
        while j < i - 12:
            x.v[j] = x.v[j] + carry - 16 * x.v[i] * ed_l[j - (i - 32)]
            carry = (x.v[j] + 128) >> 8
            x.v[j] = x.v[j] - (carry << 8)
            j += 1
        x.v[j] = x.v[j] + carry
        x.v[i] = 0
        i -= 1
    carry2: i64 = 0
    j2: u64 = 0
    while j2 < 32:
        x.v[j2] = x.v[j2] + carry2 - (x.v[31] >> 4) * ed_l[j2]
        carry2 = x.v[j2] >> 8
        x.v[j2] = x.v[j2] & 255
        j2 += 1
    j2 = 0
    while j2 < 32:
        x.v[j2] = x.v[j2] - carry2 * ed_l[j2]
        j2 += 1
    i = 0
    while i < 32:
        x.v[i + 1] = x.v[i + 1] + (x.v[i] >> 8)
        r.b[i] = u8(x.v[i] & 255)
        i += 1

def sc_canonical(s: ptr[B32]) -> u64:
# RFC 8032: reject S >= L
    i: i64 = 31
    while i >= 0:
        a = i64(s.b[u64(i)])
        if a < ed_l[u64(i)]:
            return 1
        if a > ed_l[u64(i)]:
            return 0
        i -= 1
    return 0

# ---- verification ----
struct EdMsg:
    b: u8[96]

struct EdSig:
    r: B32
    s: B32

def ed25519_verify(sig: ptr[EdSig], pk: ptr[B32], msg: ptr[EdMsg], n: u64) -> u64:
# 1 if sig is a valid Ed25519 signature by pk over msg.b[0..n), n <= 96
    if sc_canonical(addr(sig.s)) == 0:
        return 0
    q: Ge
    if ge_unpackneg(addr(q), pk) == 0:
        return 0
    st: Sha5
    s = addr(st)
    s5_init(s)
    i: u64 = 0
    while i < 32:
        s5_byte(s, u64(sig.r.b[i]))
        i += 1
    i = 0
    while i < 32:
        s5_byte(s, u64(pk.b[i]))
        i += 1
    i = 0
    while i < n:
        s5_byte(s, u64(msg.b[i]))
        i += 1
    hb: B64
    s5_final(s, addr(hb))
    h: B32
    sc_reduce(addr(h), addr(hb))
    p: Ge
    ge_scalarmult(addr(p), addr(q), addr(h))
    ge_scalarbase(addr(q), addr(sig.s))
    ge_add(addr(p), addr(q))
    t: B32
    ge_pack(addr(t), addr(p))
    d: u64 = 0
    i = 0
    while i < 32:
        d = d | (u64(t.b[i]) ^ u64(sig.r.b[i]))
        i += 1
    return d == 0
