# std/vec.fsi -- Phase 4.5c generic growable array Vec[T] (freestanding).
# Trusted library code (@unsafe inside); callers stay safe: pass addr(v).
# The includer provides:  forge_alloc(bytes: u64) -> u64   (0 = out of memory)
#                         forge_free(p: u64, bytes: u64)
# vec_get/vec_set are bounds-checked (forge_bounds_fail on a bad index).

@generic(T)
struct Vec:
    data: u64
    len: u64
    cap: u64

@generic(T)
def vec_init(v: ptr[Vec[T]]):
    v.data = 0
    v.len = 0
    v.cap = 0

@generic(T)
@unsafe
def vec_reserve(v: ptr[Vec[T]], n: u64) -> u64:
    if n <= v.cap:
        return 1
    nc: u64 = v.cap * 2
    if nc < 4:
        nc = 4
    if nc < n:
        nc = n
    nd = forge_alloc(nc * sizeof(T))
    if nd == 0:
        return 0
    if v.cap > 0:
        src = ptr[T](v.data)
        dst = ptr[T](nd)
        i: u64 = 0
        while i < v.len:
            dst[i] = src[i]
            i += 1
        forge_free(v.data, v.cap * sizeof(T))
    v.data = nd
    v.cap = nc
    return 1

@generic(T)
@unsafe
def vec_push(v: ptr[Vec[T]], x: T) -> u64:
    if vec_reserve[T](v, v.len + 1) == 0:
        return 0
    d = ptr[T](v.data)
    d[v.len] = x
    v.len += 1
    return 1

@generic(T)
def vec_len(v: ptr[Vec[T]]) -> u64:
    return v.len

@generic(T)
@unsafe
def vec_get(v: ptr[Vec[T]], i: u64) -> T:
    s = slice(ptr[T](v.data), v.len)
    return s[i]

@generic(T)
@unsafe
def vec_set(v: ptr[Vec[T]], i: u64, x: T):
    s = slice(ptr[T](v.data), v.len)
    s[i] = x

@generic(T)
@unsafe
def vec_pop(v: ptr[Vec[T]], out: ptr[T]) -> u64:
    if v.len == 0:
        return 0
    v.len -= 1
    d = ptr[T](v.data)
    out[0] = d[v.len]
    return 1

@generic(T)
def vec_free(v: ptr[Vec[T]]):
    if v.cap > 0:
        forge_free(v.data, v.cap * sizeof(T))
    v.data = 0
    v.len = 0
    v.cap = 0
