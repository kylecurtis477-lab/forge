# std/map.fsi -- Phase 4.5c generic hash map Map[K, V] (freestanding).
# Open addressing, linear probing, tombstones; K is an integer-like type (hashed as u64(k)).
# Same allocator contract as std/vec.fsi: forge_alloc(bytes) -> u64, forge_free(p, bytes).
# state bytes: 0 empty, 1 used, 2 deleted.

@generic(K, V)
struct Map:
    keys: u64
    vals: u64
    st: u64
    cap: u64
    len: u64
    used: u64

@generic(K, V)
def map_init(m: ptr[Map[K, V]]):
    m.keys = 0
    m.vals = 0
    m.st = 0
    m.cap = 0
    m.len = 0
    m.used = 0

@generic(K)
def map_hash(k: K, cap: u64) -> u64:
    h: u64 = u64(k) * 2654435761
    h = h ^ (h >> 29)
    return h % cap

@generic(K, V)
@unsafe
def map_find(m: ptr[Map[K, V]], k: K) -> u64:
# slot index holding k, or cap
    if m.cap == 0:
        return 0
    ks = ptr[K](m.keys)
    st = ptr[u8](m.st)
    i = map_hash[K](k, m.cap)
    n: u64 = 0
    while n < m.cap:
        if st[i] == 0:
            return m.cap
        if st[i] == 1 and ks[i] == k:
            return i
        i = (i + 1) % m.cap
        n += 1
    return m.cap

@generic(K, V)
@unsafe
def map_grow(m: ptr[Map[K, V]]) -> u64:
    nc: u64 = m.cap * 2
    if nc < 8:
        nc = 8
    nk = forge_alloc(nc * sizeof(K))
    nv = forge_alloc(nc * sizeof(V))
    ns = forge_alloc(nc)
    if nk == 0 or nv == 0 or ns == 0:
        return 0
    nks = ptr[K](nk)
    nvs = ptr[V](nv)
    nst = ptr[u8](ns)
    i: u64 = 0
    while i < nc:
        nst[i] = 0
        i += 1
    if m.cap > 0:
        ks = ptr[K](m.keys)
        vs = ptr[V](m.vals)
        st = ptr[u8](m.st)
        i = 0
        while i < m.cap:
            if st[i] == 1:
                j = map_hash[K](ks[i], nc)
                while nst[j] == 1:
                    j = (j + 1) % nc
                nst[j] = 1
                nks[j] = ks[i]
                nvs[j] = vs[i]
            i += 1
        forge_free(m.keys, m.cap * sizeof(K))
        forge_free(m.vals, m.cap * sizeof(V))
        forge_free(m.st, m.cap)
    m.keys = nk
    m.vals = nv
    m.st = ns
    m.cap = nc
    m.used = m.len
    return 1

@generic(K, V)
@unsafe
def map_put(m: ptr[Map[K, V]], k: K, v: V) -> u64:
    i = map_find[K, V](m, k)
    if i < m.cap:
        vs = ptr[V](m.vals)
        vs[i] = v
        return 1
    if (m.used + 1) * 4 > m.cap * 3:
        if map_grow[K, V](m) == 0:
            return 0
    ks = ptr[K](m.keys)
    vs2 = ptr[V](m.vals)
    st = ptr[u8](m.st)
    j = map_hash[K](k, m.cap)
    while st[j] == 1:
        j = (j + 1) % m.cap
    if st[j] == 0:
        m.used += 1
    st[j] = 1
    ks[j] = k
    vs2[j] = v
    m.len += 1
    return 1

@generic(K, V)
@unsafe
def map_get(m: ptr[Map[K, V]], k: K, out: ptr[V]) -> u64:
    i = map_find[K, V](m, k)
    if i >= m.cap:
        return 0
    vs = ptr[V](m.vals)
    out[0] = vs[i]
    return 1

@generic(K, V)
def map_has(m: ptr[Map[K, V]], k: K) -> u64:
    if map_find[K, V](m, k) < m.cap:
        return 1
    return 0

@generic(K, V)
@unsafe
def map_del(m: ptr[Map[K, V]], k: K) -> u64:
    i = map_find[K, V](m, k)
    if i >= m.cap:
        return 0
    st = ptr[u8](m.st)
    st[i] = 2
    m.len -= 1
    return 1

@generic(K, V)
def map_len(m: ptr[Map[K, V]]) -> u64:
    return m.len

@generic(K, V)
def map_free(m: ptr[Map[K, V]]):
    if m.cap > 0:
        forge_free(m.keys, m.cap * sizeof(K))
        forge_free(m.vals, m.cap * sizeof(V))
        forge_free(m.st, m.cap)
    map_init[K, V](m)
