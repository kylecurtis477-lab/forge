# BootInfo: the one structure the kernel consumes, filled either from multiboot2 tags (by the kernel)
# or by the native UEFI loader (examples/efi/loader.forge) right before ExitBootServices.
BI_MAGIC = 0x4F464E49544F4F42
BOOT_MB2_BIOS = 1
BOOT_MB2_UEFI = 2
BOOT_FORGE_UEFI = 3
BOOT_KEXEC = 4
MEM_USABLE = 1
MEM_RESERVED = 2
MEM_ACPI_RECLAIM = 3
MEM_ACPI_NVS = 4
MEM_LOADER = 5
MEM_BAD = 6
MEM_HANDOFF = 7
MAX_REGIONS = 256
FB_RGBX = 0
FB_BGRX = 1

struct MemRegion:
    base: u64
    len: u64
    typ: u64

struct BootInfo:
    magic: u64
    method: u64
    kdelta: u64
    fb_addr: u64
    fb_width: u64
    fb_height: u64
    fb_pitch: u64
    fb_bpp: u64
    fb_format: u64
    rsdp: u64
    info_base: u64
    info_len: u64
    handoff: u64
    kx_gen: u64
    nregions: u64
    regions: MemRegion[256]
    # boot command line, NUL-terminated: the multiboot2 tag (type 1) or empty (native loader); kexec copies
    # the whole BootInfo, so a successor kernel sees the same line (bootpol.fsi)
    cmdline: u8[256]
