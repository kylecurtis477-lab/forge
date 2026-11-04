# Process-state format (plan v1.3 item 7): the single versioned, self-describing record of one user
# process. The kexec handoff writes and adopts exactly these records (one per process, two pages each),
# and any later user of process state (checkpoint, migration, record/replay) must use this format and
# bump PS_VERSION rather than define its own.
# Record page 0 (offsets from the record start; all words little-endian u64):
#   +0 pid +8 ppid +16 state +24 exit code +32 brk_base +40 brk +48 mmap_next
#   +56 flags: is-caller (bit 0) | caps << 8 | abi << 40
#   +64 user page count +72 first page in the container's page index/data
#   +80 syscall in progress (nr, a, b, c, d) +120 FS base
#   +128 fds x16: (file index + 1) | rights << 32 (0 = closed)
#   +256 args (128 bytes) +384 syscall frame (SF_SIZE = 128 bytes)
#   +512 header word: PS_MAGIC << 32 | PS_VERSION (adopters refuse a record whose header fails ps_hdr_ok)
# Record page 1: FPU state (fxsave/xsave image, the container records its size).
# User pages, the page index and the shared file table belong to the container (the kexec handoff
# region, see kexec.fsi); the record refers to them by index.
PS_MAGIC = 0x46505354  # "TSPF" little-endian: Forge Process STate
PS_VERSION = 1  # written by this kernel
PS_MIN_VERSION = 1  # oldest record this kernel adopts
PS_PID = 0
PS_PPID = 8
PS_STATE = 16
PS_EXIT = 24
PS_BRK_BASE = 32
PS_BRK = 40
PS_MMAP_NEXT = 48
PS_FLAGS = 56
PS_NPG = 64
PS_FIRST_PG = 72
PS_SYSCALL = 80
PS_FSBASE = 120
PS_FDS = 128
PS_ARGS = 256
PS_FRAME = 384
PS_HDR = 512
PS_PAGES = 2

def ps_hdr_word() -> u64:
    return (PS_MAGIC << 32) | PS_VERSION

def ps_hdr_ok(w: u64) -> u64:
# 1 if w is the header word of a record this kernel can adopt
    if (w >> 32) != PS_MAGIC:
        return 0
    v = w & 0xFFFFFFFF
    if v < PS_MIN_VERSION or v > PS_VERSION:
        return 0
    return 1

def ps_selftest():
# boot check of the header rules: own word accepted; wrong magic, version 0 and a newer version refused
    if ps_hdr_ok(ps_hdr_word()) == 0 or ps_hdr_ok(ps_hdr_word() ^ (u64(1) << 40)) != 0:
        panic("process-state: header check")
    if ps_hdr_ok(PS_MAGIC << 32) != 0 or ps_hdr_ok(ps_hdr_word() + 1) != 0:
        panic("process-state: version check")
    puts("process-state: format v")
    put_u64(PS_VERSION)
    puts(" (adopts v")
    put_u64(PS_MIN_VERSION)
    puts("..v")
    put_u64(PS_VERSION)
    puts("), header checks OK\n")
