#!/usr/bin/env python3
# Minimal pure-Python Ed25519 (RFC 8032 reference algorithm) for build-time signing.
# Sign-side only; the kernel verifies with examples/kernel/ed25519.fsi.
import hashlib
p = 2**255 - 19
L = 2**252 + 27742317777372353535851937790883648493
d = -121665 * pow(121666, p - 2, p) % p
I = pow(2, (p - 1) // 4, p)

def inv(x): return pow(x, p - 2, p)

def xrecover(y):
    xx = (y * y - 1) * inv(d * y * y + 1)
    x = pow(xx, (p + 3) // 8, p)
    if (x * x - xx) % p: x = x * I % p
    if x % 2: x = p - x
    return x

By = 4 * inv(5) % p
B = (xrecover(By), By, 1, xrecover(By) * By % p)

def add(P, Q):
    x1, y1, z1, t1 = P; x2, y2, z2, t2 = Q
    a = (y1 - x1) * (y2 - x2) % p; b = (y1 + x1) * (y2 + x2) % p
    c = t1 * 2 * d * t2 % p; dd = z1 * 2 * z2 % p
    e, f, g, h = b - a, dd - c, dd + c, b + a
    return (e * f % p, g * h % p, f * g % p, e * h % p)

def mul(s, P):
    Q = (0, 1, 1, 0)
    while s:
        if s & 1: Q = add(Q, P)
        P = add(P, P); s >>= 1
    return Q

def enc(P):
    x, y, z, _ = P; zi = inv(z); x, y = x * zi % p, y * zi % p
    return (y | ((x & 1) << 255)).to_bytes(32, 'little')

def H(m): return int.from_bytes(hashlib.sha512(m).digest(), 'little')

def keypair(seed):
    h = hashlib.sha512(seed).digest()
    a = int.from_bytes(h[:32], 'little'); a &= (1 << 254) - 8; a |= 1 << 254
    return a, h[32:], enc(mul(a, B))

def public(seed): return keypair(seed)[2]

def sign(seed, m):
    a, pre, A = keypair(seed)
    r = H(pre + m) % L
    R = enc(mul(r, B))
    S = (r + H(R + A + m) * a) % L
    return R + S.to_bytes(32, 'little')
