# SHA-256 for build tools (tools/forgeseal.forge): the same code as the kernel's seal verifier in
# examples/kernel/seal.fsi (struct Sha, sha_init/sha_byte/sha_final), without its kernel-memory reader.

struct Dig:
    b: u8[32]

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
