# Phase 10.1: encryption at rest. Self-contained, fully safe Forge crypto shared by the kernel (objd.fsi
# encrypts every object-store log block) and the hosted test tests/fs/crypt.forge:
#   ChaCha20 (RFC 8439 block function; 32-bit words held in u64 slots) for confidentiality,
#   SHA-256 (FIPS 180-4) for key derivation and the per-block keyed tag.
# Keys are 8 little-endian 32-bit words. Nothing here touches memory outside these arrays.
cc_key: u64[8]  # ChaCha20 key (32-bit words)
cc_x: u64[16]  # working state
cc_ks: u64[16]  # last keystream block (32-bit words)

def cc_rotl(v: u64, n: u64) -> u64:
    return ((v << n) | (v >> (32 - n))) & 0xFFFFFFFF

def cc_qr(a: u64, b: u64, c: u64, d: u64):
    cc_x[a] = (cc_x[a] + cc_x[b]) & 0xFFFFFFFF
    cc_x[d] = cc_rotl(cc_x[d] ^ cc_x[a], 16)
    cc_x[c] = (cc_x[c] + cc_x[d]) & 0xFFFFFFFF
    cc_x[b] = cc_rotl(cc_x[b] ^ cc_x[c], 12)
    cc_x[a] = (cc_x[a] + cc_x[b]) & 0xFFFFFFFF
    cc_x[d] = cc_rotl(cc_x[d] ^ cc_x[a], 8)
    cc_x[c] = (cc_x[c] + cc_x[d]) & 0xFFFFFFFF
    cc_x[b] = cc_rotl(cc_x[b] ^ cc_x[c], 7)

def cc_block(ctr: u64, n0: u64, n1: u64, n2: u64):
# one 64-byte keystream block for (cc_key, counter, 96-bit nonce n0..n2) into cc_ks
    cc_ks[0] = 0x61707865
    cc_ks[1] = 0x3320646e
    cc_ks[2] = 0x79622d32
    cc_ks[3] = 0x6b206574
    i: u64 = 0
    while i < 8:
        cc_ks[4 + i] = cc_key[i]
        i += 1
    cc_ks[12] = ctr & 0xFFFFFFFF
    cc_ks[13] = n0 & 0xFFFFFFFF
    cc_ks[14] = n1 & 0xFFFFFFFF
    cc_ks[15] = n2 & 0xFFFFFFFF
    i = 0
    while i < 16:
        cc_x[i] = cc_ks[i]
        i += 1
    r: u64 = 0
    while r < 10:
        cc_qr(0, 4, 8, 12)
        cc_qr(1, 5, 9, 13)
        cc_qr(2, 6, 10, 14)
        cc_qr(3, 7, 11, 15)
        cc_qr(0, 5, 10, 15)
        cc_qr(1, 6, 11, 12)
        cc_qr(2, 7, 8, 13)
        cc_qr(3, 4, 9, 14)
        r += 1
    i = 0
    while i < 16:
        cc_ks[i] = (cc_ks[i] + cc_x[i]) & 0xFFFFFFFF
        i += 1

def cc_word(i: u64) -> u64:
# keystream bytes 8i..8i+7 of the last block as one little-endian u64 (i < 8)
    return cc_ks[i * 2] | (cc_ks[i * 2 + 1] << 32)

# ---- SHA-256 ----
sh_k: u64[64] = [
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0xfc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x6ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2]
sh_h: u64[8]  # state; after sh_fin the digest (big-endian 32-bit words)
sh_w: u64[64]
sh_buf: u8[64]
sh_n: u64
sh_tot: u64

def sh_ror(x: u64, n: u64) -> u64:
    return ((x >> n) | (x << (32 - n))) & 0xFFFFFFFF

def sh_init():
    sh_h[0] = 0x6a09e667
    sh_h[1] = 0xbb67ae85
    sh_h[2] = 0x3c6ef372
    sh_h[3] = 0xa54ff53a
    sh_h[4] = 0x510e527f
    sh_h[5] = 0x9b05688c
    sh_h[6] = 0x1f83d9ab
    sh_h[7] = 0x5be0cd19
    sh_n = 0
    sh_tot = 0

def sh_comp():
    i: u64 = 0
    while i < 16:
        sh_w[i] = (u64(sh_buf[i * 4]) << 24) | (u64(sh_buf[i * 4 + 1]) << 16) | (u64(sh_buf[i * 4 + 2]) << 8) | u64(sh_buf[i * 4 + 3])
        i += 1
    while i < 64:
        s0 = sh_ror(sh_w[i - 15], 7) ^ sh_ror(sh_w[i - 15], 18) ^ (sh_w[i - 15] >> 3)
        s1 = sh_ror(sh_w[i - 2], 17) ^ sh_ror(sh_w[i - 2], 19) ^ (sh_w[i - 2] >> 10)
        sh_w[i] = (sh_w[i - 16] + s0 + sh_w[i - 7] + s1) & 0xFFFFFFFF
        i += 1
    a = sh_h[0]
    b = sh_h[1]
    c = sh_h[2]
    d = sh_h[3]
    e = sh_h[4]
    f = sh_h[5]
    g = sh_h[6]
    h = sh_h[7]
    i = 0
    while i < 64:
        t1 = (h + (sh_ror(e, 6) ^ sh_ror(e, 11) ^ sh_ror(e, 25)) + ((e & f) ^ ((e ^ 0xFFFFFFFF) & g)) + sh_k[i] + sh_w[i]) & 0xFFFFFFFF
        t2 = ((sh_ror(a, 2) ^ sh_ror(a, 13) ^ sh_ror(a, 22)) + ((a & b) ^ (a & c) ^ (b & c))) & 0xFFFFFFFF
        h = g
        g = f
        f = e
        e = (d + t1) & 0xFFFFFFFF
        d = c
        c = b
        b = a
        a = (t1 + t2) & 0xFFFFFFFF
        i += 1
    sh_h[0] = (sh_h[0] + a) & 0xFFFFFFFF
    sh_h[1] = (sh_h[1] + b) & 0xFFFFFFFF
    sh_h[2] = (sh_h[2] + c) & 0xFFFFFFFF
    sh_h[3] = (sh_h[3] + d) & 0xFFFFFFFF
    sh_h[4] = (sh_h[4] + e) & 0xFFFFFFFF
    sh_h[5] = (sh_h[5] + f) & 0xFFFFFFFF
    sh_h[6] = (sh_h[6] + g) & 0xFFFFFFFF
    sh_h[7] = (sh_h[7] + h) & 0xFFFFFFFF

def sh_byte(v: u64):
    sh_buf[sh_n] = u8(v & 255)
    sh_n += 1
    sh_tot += 1
    if sh_n == 64:
        sh_comp()
        sh_n = 0

def sh_u64(v: u64):
# 8 bytes, little-endian
    i: u64 = 0
    while i < 8:
        sh_byte((v >> (i * 8)) & 255)
        i += 1

def sh_fin():
    bits = sh_tot * 8
    sh_byte(0x80)
    while sh_n != 56:
        sh_byte(0)
    i: u64 = 0
    while i < 8:
        sh_byte((bits >> (56 - i * 8)) & 255)
        i += 1

def sh_key(k: u64[8]):
# hash key words (cc-key format, 32-bit little-endian words)
    i: u64 = 0
    while i < 8:
        sh_byte(k[i] & 255)
        sh_byte((k[i] >> 8) & 255)
        sh_byte((k[i] >> 16) & 255)
        sh_byte((k[i] >> 24) & 255)
        i += 1
