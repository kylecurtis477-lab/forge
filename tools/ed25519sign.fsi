# Ed25519 signing helpers shared by tools/ed25519.forge and tools/forgeseal.forge (RFC 8032, the
# sign side of tools/ed25519.py); needs examples/kernel/ed25519.fsi (SHA-512, field, group, sc_reduce).

def ed_key(hk: ptr[B64], a: ptr[B32], pre: ptr[B32], pk: ptr[B32]):
# keypair from h = SHA-512(seed): a = clamp(h[0:32]), prefix = h[32:64], A = enc(a * B)
    i: u64 = 0
    while i < 32:
        a.b[i] = hk.b[i]
        pre.b[i] = hk.b[i + 32]
        i += 1
    a.b[0] = u8(u64(a.b[0]) & 248)
    a.b[31] = u8((u64(a.b[31]) & 127) | 64)
    pt: Ge
    ge_scalarbase(addr(pt), a)
    ge_pack(pk, addr(pt))

def sc_muladd(out: ptr[B32], r: ptr[B32], h: ptr[B32], a: ptr[B32]):
# out = (r + h * a) mod L  (TweetNaCl crypto_sign + modL)
    x: I64x64
    i: u64 = 0
    while i < 64:
        x.v[i] = 0
        i += 1
    i = 0
    while i < 32:
        x.v[i] = i64(r.b[i])
        i += 1
    i = 0
    while i < 32:
        j: u64 = 0
        while j < 32:
            x.v[i + j] = x.v[i + j] + i64(h.b[i]) * i64(a.b[j])
            j += 1
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
        out.b[i] = u8(x.v[i] & 255)
        i += 1
