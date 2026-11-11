# Forge for systems / OS work (freestanding mode)

Enable with `# forge: no-runtime` at the top of the file or `forgec --freestanding in.forge out.s`.
The output is plain GAS assembly for x86-64 that can be assembled with `as` and linked with `ld` and a linker script.
It has no GC, no libc, no syscall runtime, no bigints, and no prelude. The entry point is whatever symbol you export.

## Language subset
- **Types:** `i8 i16 i32 i64 u8 u16 u32 u64 bool void`, plus the aliases `int isize uint usize addr byte char`. Also `ptr[T]`, fixed arrays `T[N]`, and `struct`/`class`.
- **Integers:** ints wrap at their width. `# forge: checked-ints` traps (`ud2`) on overflow instead. `/` and `%` truncate toward zero like C. Signedness follows the operand types, and comparisons and shifts use unsigned or arithmetic forms accordingly.
- **Structs:** C ABI layout and alignment. `@packed` removes padding and `@align(N)` raises alignment. Fields work through values and pointers (`p.field` auto-derefs).
- **Memory:** `addr(lvalue)`, pointer arithmetic scaled by `sizeof(T)`, pointer differences, `p[i]`, `sizeof(T)`, and `offsetof(S, f)`.
  - Loads and stores: `load8..64`/`store8..64`, plus volatile `vload*`/`vstore*`.
  - `memset`/`memcpy` use `rep stos`/`rep movs`.
- **Intrinsics:**
  - `asm("...", ...)` at statement or top level
  - `cli sti hlt pause int3 ud2 mfence lfence sfence wbinvd`
  - `inb/inw/inl outb/outw/outl`
  - `lgdt lidt ltr invlpg read_crN write_crN rdmsr wrmsr rdtsc`
  - `call_ptr(f, ...)` (SysV, up to 6 args) and `call_efi(f, ...)` (Microsoft x64: shadow space, args 5–9 on the stack)
  - Atomics (all `lock`-prefixed or implicitly locked, sequentially consistent; the address is a `u64`):
    `atomic_load32/64`, `atomic_store32/64`, `atomic_xchg32/64`, `atomic_add32/64` (returns the old value),
    `atomic_cas32/64(addr, expected, new)` (returns the old value), `atomic_or32/64`, `atomic_and32/64`.
    tests/fs/atomics runs 4 `clone` threads hammering one counter.
- **ABI:**
  - `extern def` declares C functions. `@export` makes a symbol global.
  - Narrow arguments and return values are normalized at the boundary, so mixed Forge/C code is safe (tests/fs/cabi).
  - Up to 16 parameter words: words 7+ go on the stack per SysV (word 7 lowest, rsp 16-aligned at the call), for extern, @export and Forge-to-Forge calls (tests/fs/cabi8). A slice parameter must fit in the first 6 registers; @efiapi/@interrupt functions are still limited to 6.
  - Structs pass by value per SysV (all functions): up to 16 bytes in one or two registers (or two stack words if registers run out), larger ones on the stack. A function may return a struct: up to 16 bytes in rax:rdx, larger through a hidden pointer in rdi. The result can initialize or be assigned to a struct local (`r = f()`), or be passed straight on as an argument; whole-struct assignment otherwise still needs memcpy (tests/fs/cabi_struct). Not with @efiapi/@interrupt.
  - Floats: `f64` and `f32` (values held as IEEE bits; arithmetic in SSE). Arguments go in xmm0-7 independently of the integer registers, the rest on the stack; returns in xmm0; `%al` holds the xmm count. Conversions are explicit (`f64(n)`, `f32(x)`, `i64(x)` truncates) except integer literals. Structs with float fields pass by value per SysV: each eightbyte of a struct up to 16 bytes is SSE (only floats: next xmm) or INTEGER (next GPR), returned in xmm0/xmm1 and rax/rdx; larger structs use memory (tests/fs/cabi_floatstruct). Floats aren't allowed with @efiapi (tests/fs/cabi_float).
  - `@interrupt` and `@interrupt_err` generate a register-saving `iretq` wrapper. The body gets `frame: ptr[u64]`, which points at the error code or RIP.
  - `@efiapi` generates an MS-ABI entry (up to 6 params, 5th/6th from the stack). `@naked` emits no prologue. `@section("...")` places the function in a named section.
- **Globals:** `name: T = init` goes in .data, otherwise .bss. Initializers can be constants, arrays `[..]`, strings, or symbols. `NAME = constexpr` defines a constant. `include "file"` does textual inclusion (relative to the including file, or absolute).

## Demos (tested by `tests/os/run.sh`, also called from run_tests.sh)
- **`examples/kernel/`:** a higher-half x86-64 kernel (`boot bootinfo console sync mm cpu acpi apic sched smp user pci virtio fat .fsi` + `kernel.forge`).
  - **Layout:** linked at `0xFFFFFFFF80000000 + phys` with a small identity-linked `.boot` section at 1 MiB (Multiboot2 header, 32→64-bit trampoline, `FORGEKRN` header for the UEFI loader). The image is position-independent in physical memory: the loader may put it anywhere 2 MiB-congruent (`kdelta`).
  - **Two entries, one BootInfo:** `kmain_mb2` parses Multiboot2 tags (memory map, framebuffer, RSDP, EFI marker); `kmain_efi` copies the BootInfo filled by the Forge UEFI loader. Both continue in the same `kernel_start`.
  - **Physical memory:** bitmap frame allocator (up to 16 GiB) over usable regions, reserving the low 1 MiB, the kernel image and the boot info.
  - **Paging:** the kernel builds its own 4-level tables: HHDM at `0xFFFF800000000000` (2 MiB pages, NX, PCD on the IOAPIC/LAPIC pages), kernel `.text` RX / `.rodata` R+NX / `.data/.bss` RW+NX in 4 KiB global pages, a 64 KiB kernel stack plus two IST stacks each with an unmapped guard page. The identity map is dropped (verified by a page walk). EFER.NXE and CR4.PGE are enabled.
  - **Heap:** `kmalloc/kfree` with 8 slab size classes (32–4096 bytes incl. header) and page-backed large blocks in their own virtual range; frees return frames (the test checks for leaks over three rounds).
  - **Demand paging:** #PF on the `0xFFFF900000000000` region maps a zeroed frame and resumes.
  - **Console:** COM1 + framebuffer text with an 8×16 font (`tools/mkfont.forge`, byte-identical to the reference `tools/mkfont.py` → `font.fsi`, rendered from DejaVu Sans Mono), from the GOP (Forge loader) or the Multiboot2 framebuffer tag (GRUB, BIOS VBE or UEFI GOP); VGA text as fallback.
  - **CPU:** GDT with a 64-bit TSS (`ltr`), IST1 for #DF and IST2 for NMI/#MC. The test overflows the kernel stack into its guard page; the #DF arrives on IST1, prints, and `iretq`s into a recovery continuation on a fresh stack.
  - **ACPI/APIC:** RSDP → XSDT/RSDT (checksums) → MADT (CPUs, LAPIC, IOAPIC, interrupt source overrides). The 8259 PIC is remapped and masked; the LAPIC timer is calibrated against PIT channel 2 and runs periodically at 100 Hz; the PIT's IRQ0 is routed through the IOAPIC using the override (GSI 2) and delivered once.
  - **Memory reclaim:** after ACPI is parsed, loader-owned (EFI loader code/data) and ACPI-reclaimable regions plus the boot-info copy are returned to the frame allocator (e.g. 6.2 MiB under the Forge loader).
  - **W^X:** separate PHDRS for boot RX/RW, `.text` RX, `.rodata` R, `.data/.bss` RW; no RWX segment (checked by `os/kernel-wx`; the Forge loader refuses W+X segments).
  - **SMP:** CPUs from the MADT are started with INIT-SIPI-SIPI through a position-independent real-mode trampoline copied into a free page below 640 KiB (mapped only during bring-up). Each CPU gets its own TSS, guarded kernel/IST stacks, LAPIC timer and syscall MSRs.
  - **Scheduler:** preemptive kernel threads (100 Hz LAPIC tick on every CPU), per-CPU run queues (shortest-queue placement, local-first pop, work stealing) under one run-queue lock, `xchg` spinlocks with IRQ save/restore. The run-queue lock is held across `ctx_switch` and released by the resumed thread, so a thread can never run on two CPUs at once; dead threads are reaped after a lock round-trip. Heap and frame allocator are locked. Test: 8 threads hammer a lock-protected counter on 4 CPUs (counter == sum of per-thread counts, every thread preempted, all CPUs used).
  - **User mode:** per-process PML4 (kernel half shared, all 256 kernel PML4 entries pre-created), ELF64 loader (bounds-checked, W^X, NX for non-exec segments), 64 KiB user stack, `iretq` into ring 3, `syscall`/`sysret` (STAR/LSTAR/FMASK; `swapgs` only to find the per-CPU kernel stack), syscalls write/yield/getpid/exit/ticks/cpu with user-pointer validation by page walk. Faults from ring 3 kill the process (exit code 0x100+vector), and its address space is freed (leak-checked). User programs are Forge (`examples/user/`, `ulib.fsi`).
  - **Drivers / FS:** PCI config mechanism #1 scan; legacy (transitional) virtio-blk over I/O BAR0 with one polled split virtqueue in contiguous DMA frames; GPT parsing (ESP, Microsoft basic data, Linux partitions recognized); read-only FAT32 (8.3 names, cluster chains). The kernel reads `\EFI\Forge\init.elf` from the ESP, checks it, and runs it in ring 3.
  - **Kernel modules (Phase 2.1):** `module.fsi` loads ELF64 ET_REL objects (plain forgec + `as` output) from `\EFI\Forge\mod\` into 1 MiB slots of a module area inside the top 2 GiB (page tables pre-built, so loads never leak table frames). Sections are grouped text RX / rodata R+NX / data+bss RW+NX. Imports resolve only against a versioned kernel export table (KABI 1.0; each export has a since-minor). Modules export `mod_abi/mod_init/mod_exit` and optionally `mod_save_state/mod_restore_state`. The boot test does 1000 load/call/unload cycles with zero leaks, a state transfer across reload, and rejects a truncated image, a wrong-major ABI, an unresolved import and an import newer than the module's ABI.
  - **Driver services (Phase 2.2):** `drv.fsi` runs the virtio-blk driver as the ring-3 process `blkd` (`\EFI\Forge\bin\blkd`) with granted capabilities only: port I/O inside the device's I/O window, waiting on its MSI-X vector, one DMA region; requests arrive over a recv/reply IPC slot. A crashing driver is contained: the device is reset before its DMA frames are freed, the in-flight request fails with EIO, and the client keeps running. When a VT-d remapping unit is present, translation is enabled on every remapping unit after PCI scan (`iommu.fsi`) with default deny: only devices the kernel drives itself (pass-through) and DMA grants have context entries, every other requester ID is blocked, and an audit of all 65536 requester IDs checks that; a DMA grant is remapped to a non-identity IOVA whose second-level page table maps only those pages, and drv 403 returns that IOVA. The kernel-iommu boot uses a modern (non-transitional) virtio-blk with ACCESS_PLATFORM so device DMA actually goes through the IOMMU; transitional virtio in QEMU bypasses it. Without a remapping unit, 403 still returns the host physical address. Broader device grants stay gated.
  - **Supervisor (Phase 2.3):** a kernel thread restarts a dead `blkd` from its cached image, and the in-flight read is replayed from the kernel's request journal. The boot test crashes the driver in the middle of a 96 KiB read; the bytes still hash identically and the restart count is reported.
  - **tty hot-swap (Phase 2.4):** the line discipline is a module (`tty1.ko` / `tty2.ko`) that can be swapped while typing is in progress. Queued input moves across in a versioned state blob under rq_lock, and the boot test checks that no keystroke is lost across about 120 swaps.
  - **Live patching (Phase 2.5):** `@patchable` functions start with a 5-byte nop that is swapped for a `jmp` under stop-machine on all CPUs, with a hash check before the patch and after a revert, and a pre-install and post-install self-test with automatic rollback. `sys_getpid` is patched and reverted 25 times on 4 CPUs while it is being called, with 0 wrong results.
  - **kexec handoff (Phase 2.6):** syscall 409 (test hooks only) loads `\EFI\Forge\kernel.elf` into free RAM, serialises the calling process (user pages, brk, args, FPU, syscall frame, console fds) into a hashed handoff region with a new `BOOT_KEXEC` BootInfo and page tables, quiesces devices and APs from the BSP, and jumps. The successor boots fully, restores the process, frees the region and resumes it with return value 1. Nothing is written to disk (checked by disk-image hash).
  - **kexec v2 hardening:** `kexec next` boots `kernel-next.elf` (version `forge-kernel 0.2.6-next`); every non-driver user process is parked at a syscall boundary and carried with its pages, FPU, pid, parent links, pending syscall and open fds (shared Files deduped); versioned 448-byte header with data+metadata hashes; drivers restart in the new kernel. Tested with background `bg` reading a file across the swap.
- **`examples/efi/loader.forge` → `forgex64.efi`:** the native UEFI boot loader, written in Forge.
  - LoadedImage → SimpleFileSystem reads `\EFI\Forge\kernel.elf` from its own volume. It then loads the PT_LOAD segments at a 2 MiB-congruent base below 1 GiB and finds the entry through the `FORGEKRN` header.
  - It picks GOP 1024×768 when offered, finds the ACPI 2.0 RSDP in the configuration table, and builds identity (0–4 GiB) plus −2 GiB tables.
  - It runs GetMemoryMap → ExitBootServices (retried when the map key is stale), converts the map into BootInfo, switches CR3 and jumps to `kentry64`.
  - It is linked directly by `ld -m i386pep --subsystem 10`. `mkgpt.forge` (Forge port of `mkgpt.py`, byte-identical) builds a GPT disk with an ESP plus placeholder Microsoft basic data and Linux partitions.
- **Reproducible ESP and ISO (plan v1.8 item 7):** With `SOURCE_DATE_EPOCH` set, `examples/efi/build.sh` / `build.forge` pin the FAT volume serial (`tools/repro-mformat`, low 32 bits of the epoch) so `esp.fat` and `disk.img` are byte-identical across clean builds (`FORGE_GUID_SEED` already pins the GPT GUIDs). The BIOS+UEFI GRUB ISO is built through `tools/repro-mkrescue`, which substitutes an epoch-derived UUID for grub-mkrescue's wall-clock one and rebuilds with `xorriso --modification-date` / `--set_all_file_dates` and a pinned El Torito FAT serial. `tests/efi-build/esp-reproducible` and `tests/kernel-build/iso-reproducible` build twice and compare sha256. ESP contents are unchanged: the loader still writes only under `\EFI\Forge\` (the test disk also has `\EFI\BOOT\BOOTX64.EFI` as a removable-media fallback).
- **Tests:** `tests/os/boot.py` boots each path headless, checks the serial markers, takes a QEMU monitor `screendump` after `KERNEL OK`, decodes every framebuffer text row against the font (KERNEL OK must be on screen, so console scrolling is checked), and then sends `q` on COM1 so the kernel exits. The three paths are BIOS+GRUB, OVMF+GRUB, and OVMF+forgex64.efi on GPT, all with `-smp 4` and a read-only virtio-blk copy of the GPT disk (its removable-media fallback loader removed so firmware never boots it).

### Boot timeouts (tests/os/boot.py)
Every boot line reports its wall time (`[76s]`). A boot that reaches its timeout is classified instead of
being reported as a bare `qemu rc=-9` (which was just the harness's own SIGKILL):
- **SLOW**: serial output was still arriving, so the deadline is extended (up to 2x the timeout) and the
  line says SLOW with the time used.
- **HANG**: no serial output for 60 s (`FORGE_BOOT_STALL`). The vCPU state (`info cpus`,
  `info registers -a`, 64 words at each vCPU's stack: RSP (ESP before long mode) is parsed from the register dump and read with `cpu N` + `x/64gx 0xADDR`, because QEMU's HMP has no `$rsp`, so the old `x/64gx $rsp` only printed "unknown register") is saved to `LOG.hangN`. A HANG is always a FAIL: the boot
  is retried once only for information (`FLAKY os/NAME ... informational retry PASS`, `[retry PASS; a HANG
  always fails, dump kept ...]`), so a real hang can never be hidden by a lucky second boot.
  `FORGE_BOOT_NO_RETRY=1` skips the retry.
The kills seen so far (g11 bios, g52 bios-smp1, g64 uefi) were all of this second kind: the serial log
stops mid-boot at different points (after the blkd restart, after the Linux personality line, inside the
module load/unload cycles) and nothing follows for minutes, while the same boot alone takes 65-85 s and
no OOM was logged. That is an intermittent guest stall, not slow emulation; the dumps are for finding it.
The g67 bios-smp1 HANG stopped after the kexec handoff: the shell printed `sh$ ` but `kexec_resume` never
printed "kexec: input service state carried", so the boot thread was stuck inside `kbd_service_start_from`
(two sleeping virtio-blk reads of kbdd/kbdd2, then `kbd_start_l`). On the carried path it now prints
`kexec: input service: reading kbdd` / `reading kbdd2` / `starting kbdd` / `kbdd started`, so the next
such HANG's serial log plus its per-vCPU stacks show which step stalls (suspect: a lost virtio-blk completion).
The efi-loader-gpt 60 s stall after `KERNEL OK` (open after inv78) was the guest `hlt()`-ing for the
harness's `q` while the LAPIC timer was not firing and COM1 had no RX interrupt, so the CPU slept
through the character. The wait is now TSC-bounded and uses `cpu_relax()`; the kernel prints
`debug-exit N ms after KERNEL OK` and `os/efi-loader-gpt-post-ok` requires N < 8000.

## Compiler tooling (Phase 3)
- `--diagnostics=json`: one schema-v1 JSON object per error (file/line/col span, code, message, fix-its); goldens in `tests/diag`.
- Hosted runtime source: `selfhost/runtime.forge` (naked Forge functions, inline asm; v1.3 item 9). build.sh compiles it with the seed to `build/runtime.s`, which `build/embed` splices into forgec. `tests/rtport/run.sh` checks it assembles to the same instruction/data stream as the reference `selfhost/runtime.s`. To change the runtime, edit runtime.forge (and keep runtime.s in step or retire it).


### Slices and bounds checks (Phase 4.1)

- `slice[T]` (freestanding backend) is a 16-byte (ptr, len) header. Make one with `slice(p, n)` or `x[a:b]` (bounds optional) on an array or slice; `len(x)` reads it (arrays give their constant length). Slice params take two argument registers (ptr, len); slices copy on assignment. Array and struct params are passed by address and copied into the callee's frame on entry, so they are values: a callee's writes never reach the caller, and a parameter can be passed on to another function (tests/fs/aggparam.forge).
- Array and slice indexing is bounds-checked: `cmp len,%rcx; jb ok`, else `forge_bounds_fail(line, idx, len, file)` if the program defines it, then `ud2`. The line and file are the original source position, even through `include`. Constant out-of-range array indices are compile errors. `ptr[T]` indexing is unchecked.
- Checks are elided for `x[v]` inside `for v in range(len(x))`, `range(C, len(x))` or `range(K)` with K ≤ the array length, when the body never assigns v or x or takes their `addr()`. `forgec … --bounds-stats` prints the counts on stderr; `--no-bounds` or `# forge: no-bounds-checks` disables checks.

### No null (Phase 4.2)

- `ptr[T]` is non-null; `nptr[T]` may be null (same 8-byte representation, ftsg=1 on the ptr type). `fs_conv` rejects literal 0 and unnarrowed nptr → ptr; deref of nptr is rejected in `fs_postfix`.
- Narrowing is a per-local flag `fsl_nn` (set means "this nptr local is non-null here"). `fs_primary` gives a narrowed local the `ptr[T]` type. Sources: `fs_nnscan` (if/while conjuncts), `fs_andor` (rhs of `and` / `x == 0 or`), `fs_nulltest`+`fs_exits` (early-exit ifs), stores in `fs_lhs_store` / typed decls. `fs_suite` ANDs facts with the entry snapshot on exit; `fs_loopkill` clears facts for locals a loop assigns (reuses `fs_bodyok`).
- `fs_die` maps include-expanded lines back to file:line.

### Unsafe regions (Phase 4.3)

- State: `fs_unsafe` (depth; function-level from decorator bit 128 or naked bit 8 in the `fs_function` wrapper around `fs_function1`, block-level from `with unsafe:` in `fs_stmt`). `fs_unsafe_op(what)` counts an op when inside, else dies (or, with `--unsafe-fix`, prints `unsafe-fix: FILE:LINE fn` once per function via `fsf_ln`).
- Gate points: start of `fs_intrin` (list `FS_UNSAFE_FNS`), int→ptr in `fs_conv`, explicit pointer casts, non-`[0]` ptr indexing. `ptr[T](0)` is rejected before the unsafe check.
- Provenance (plan v1.3): `fs_provsite` marks every integer-to-pointer site (fs_conv and `ptr[T](n)` casts); `--provenance-lint` prints `prov: FILE:LINE fn int->ptr ok|OUTSIDE @unsafe` and a `provenance: N int->ptr sites, M outside @unsafe` summary, and fails when M > 0, so a `with unsafe:` block cannot mint pointers from integers. At an extern call a pointer argument for an integer parameter is rejected (`fsf_ext`): pointers given to C stay pointers. Run over all baseline programs by tests/unsafe/run.sh.
- Report: `--unsafe-report` per region + summary (`fs_ufn`, `fs_ublk`, `fs_utot`, `fs_ubare`); baseline in tests/unsafe/baseline.txt.

### Typed atomics (Phase 4.6)

`fs_atyped` (called from `fs_intrin` before the raw `atomic_*32/64` block) handles `atomic_load/store/swap/fetch_add/cas/fence`. `fs_aptr` checks the pointer (non-null, integer element, no writes through `ref[T]`), `fs_aord` reads the ordering word (`FS_ORDN`), `fs_asfx`/`fs_areg` pick the b/w/l/q suffix and sub-register. Mapping on x86-64 (TSO):

| op | relaxed | acquire | release | acq_rel | seq_cst |
|---|---|---|---|---|---|
| load | mov | mov | error | error | mov |
| store | mov | error | mov | error | xchg |
| swap / fetch_add / cas | xchg / lock xadd / lock cmpxchg (all orderings) |||||
| fence | error | (none) | (none) | (none) | mfence |

### Kernel use of typed atomics and std Vec/Map

- sync.fsi: `spin_lock(l: ptr[u64])` = test-and-test-and-set with `atomic_swap(l, 1, acquire)` / `atomic_load(l, relaxed)`; `spin_unlock` = `atomic_store(l, 0, release)` (a plain mov on x86). Callers pass `addr(lock)`.
- kernel.forge includes ../../std/vec.fsi and map.fsi; `forge_alloc` = kmalloc, `forge_free` = kfree (size ignored). Usable from any context that may take heap_lock (not from IRQ handlers that already hold it).

### Generics (Phase 4.5b)

```
@generic(T)
struct Pair:
    a: T
    b: T

@generic(T)
def psum(p: ptr[Pair[T]]) -> T:
    return p.a + p.b

p: Pair[u64]
psum(addr(p))        # inferred: psum__g<N> with T = u64
width[u16]()         # explicit type arguments
```

Generic defs/structs are recorded at prescan and skipped; an instance is created the first time a type tuple is used (struct layout computed then; function signature registered and the body emitted by the normal function loop with a binding frame T -> concrete type). Inference dry-runs the argument expressions (no code kept, move/borrow/unsafe state rolled back) and unifies them with the declared parameter types. There are no trait bounds; errors in a body are reported per instance.

### Vec / Map stdlib (Phase 4.5c)

- `include "std/vec.fsi"` / `"std/map.fsi"`; the program defines `forge_alloc(bytes: u64) -> u64` (0 on failure) and `forge_free(p: u64, bytes: u64)`.
- Storage is held as u64 addresses inside the struct and reinterpreted as `ptr[T]` inside `@unsafe` library functions; the public calls take `ptr[Vec[T]]` / `ptr[Map[K, V]]` (from `addr(x)`, safe) and are themselves callable from `# forge: safe` code.
- Vec grows x2 (min 4); Map grows x2 (min 8) at 75% occupancy counting tombstones; hash is `u64(k) * 2654435761` folded with `>> 29`, mod capacity.

### defer (Phase 4.5a)

- `with defer:` in `fs_stmt1` records the suite position in `fs_dfp` (and the unsafe depth in `fs_dfu`) and skips it; only allowed when `fs_sdep` (suite depth, maintained by the `fs_suite` wrapper around `fs_suite1`) is 1. `fs_rundefers` (called by `return` and by `fs_function1` when the body falls through) pushes %rax, ends block borrows (`fs_bkill(0)`, state restored afterwards), re-parses each block newest first with the move state snapshotted around it, then pops %rax.

### Ownership and borrows (Phase 4.4)

- Types: `ft_bptr(el, sg)` with `ftsg` 2 = own, 3 = ref, 4 = mut (all `fsk` 1, non-null; `ft_isnn` is `ftsg != 1`). Parsed in `fs_type`/`fs_istype` only when followed by `[` (the kernel has a field named `ref`).
- Per-local state set in `fs_newlocal`: `fsl_mv` (move line, 0 = live), `fsl_bf` (owner a borrow local borrows from), `fsl_bk` (1 shared, 2 mut), `fsl_sh`/`fsl_mu` (live borrows of this owner), `fsl_bl` (line of the last borrow), `fsl_dead` (borrow outlived its block).
- `fs_lchk` (from `fs_primary` local resolution) does the move/use/reassign checks; whole value = next token is not `.` or `[`. `fs_borrow` handles borrow()/borrow_mut(): in call arguments (`fs_argd` > 0) it pushes a temporary onto `fs_tb`/`fs_tbk`, released at the end of `fs_args`; otherwise it sets `fs_bpend`/`fs_bpk`, which `fs_battach` attaches to the new local (typed decl or `fs_lhs_store` new-local path). `fs_wstore` checks stores through ref/borrowed own. `fs_bconv` handles conversions.
- Flow: `fs_stmt` wraps `fs_stmt1` and resets `fs_term` (set by return/break/continue/panic). `fs_mvsnap`/`fs_mvset`/`fs_mvor` join moved states over if branches; loops keep `fs_lb0` (entry) and `fs_lbm` (break union) and `fs_mvloop` rejects moves of outer values. `fs_suite` ends borrows declared inside it (`fs_bkill`).
- Safe mode: `fs_safe` from `# forge: safe`; `fs_safechk` uses `fsx_f` so only the main file is restricted.

### Test blocks, forge test, forge verify

- `test "name":` + block at top level (column 0) is a unit test. forgec rewrites each header line to `def __forge_test_N():` before lexing (line count unchanged, so diagnostics keep their line numbers); without `--test` they are uncalled functions and the program is unchanged. Test blocks in imported modules are renamed the same way (global counter) and never run from the importer.
- `forgec in out.s --test` appends a runner: `--forge-test-list` prints `--forge-tests--` then one name per line; `--forge-test-run N` runs test N; exit status is the result (a failed assert exits 1 with `error: line L: assertion failed`). The program's own top-level code runs first in both modes.
- `forge test [files]` (tools/forge_test.forge, built to build/forge_test; tools/forge_test.sh is the reference and tests/testblocks/run.sh checks they match; default tests/unit/*.forge) builds with `--test`, runs each test in its own process with a 10 s timeout and prints `{"forge_test":1,"files":[{"file","build":"ok|fail","tests":[{"name","status":"pass|fail","exit","message","ms"}]}],"passed":N,"failed":M}`; exit 1 on any failure.
- `forge verify [files]` (tools/forge_verify.forge, built to build/forge_verify; tools/forge_verify.sh is the reference and tests/testblocks/run.sh checks they match, except that fas/fld stdout no longer leaks into the JSON) runs, per file, parse (frozen grammar, ast-json), fmt --check, build (typecheck+codegen+as+ld) and test (if the file has test blocks), and prints `{"forge_verify":1,"files":[{"file","checks":{"parse","fmt","build","test":{"status":"ok|fail|skip"[,"message"]}}}],"ok":bool}`; exit 1 if any check fails.
- Tests: tests/testblocks/run.sh (in run_tests.sh).

### forge fmt

`forge fmt [--check] [files]` runs tools/fmt.forge (token-level, comment-preserving, AST- and line-number-preserving). `--check` exits 1 and prints `would reformat F` for any file not in canonical form. The CI file set is `build/fmt --list` (a Forge walk of the repo; tools/fmt_files.sh is kept as the reference and tests/fmt/run.sh checks the two lists are identical); tests in tests/fmt/run.sh.

### Grammar freeze
- docs/GRAMMAR.md is the frozen grammar (v1); the strict `a_*` parser behind `--emit=ast-json` is its reference implementation, and changing either needs a version bump of both.
- tests/grammar/{valid,invalid} plus tools/grammar_fuzz.forge (reference: grammar_fuzz.py; `--seed N --n COUNT`, writes to build/fuzz) check it; see BUILD_PLAN Phase 3 item 3.

### AST export (`--emit=ast-json`, `--emit=pretty`)
- Entry: `forge_main` sees the flag and calls `ast_main`, which lexes only the named file with `ast_raw=1` (`add_id` then skips std-module renaming), parses it with `ast_parse` into flat node arrays (`ak` kind, `av` value, `al` line, `acs`/`acn` child slice into `akid`), and writes JSON (`ast_json`) or source (`pp_s`/`pp_e`).
- Schema v1: `{"forge_ast":1,"file","pragmas":[lines],"root"}`, nodes `{"k","l"[,"v"][,"c"]}`. Kinds: Module Block Expr Assign AugAssign(v=op) AnnAssign Pass Break Continue Return Raise Global Nonlocal Import ImportFrom Alias Assert Del Include Def(Decorators, Params, ret|Empty, [Effects(Effect v=name ...)], Block; Effects only with a v1.2 `uses` clause, rejected by the checker with E0106) ExternDef Class/Struct(Decorators, Bases, Block) Decorator If(v="elif" in chains) While For Try Handler Else Finally With WithItem Param(v=[*|**]name; type, default) Params Empty Name Const Num Float Str BinOp BoolOp Not Unary Compare(v=comma-joined ops) IfExp Lambda Call Kw Star DStar Attr Sub Slice Tuple List Set Dict Pair ListComp SetComp GenExp DictComp CompFor CompIf Yield YieldFrom Test(v=name; Block).
- Num values are decimal; values with 19+ digits print back as hex so the lexer reproduces the same 64-bit pattern. Parse errors in this mode use the normal diagnostic path (`expected ...` is code E0105). A `uses` effect clause (grammar v1.2, reserved) parses and pretty-prints, but compiling it fails with E0106.

## Capabilities (Phase 5.1)
- File descriptors are handles with rights: `fdr[16]` in `Proc`, next to `fds`. The bits are CAP_READ 1, CAP_WRITE 2, CAP_LOOKUP 4, CAP_DUP 8 and CAP_XFER 16, and CAP_ALL is 31.
- Spawned processes get console fds 0–2 with CAP_ALL. When the FAT is mounted, non-driver processes also get a root-directory handle at fd 3 (CAP_ROOT_FD). Driver processes get no root capability.
- Checks:
  - read and getdents need READ, and write needs WRITE (EPERM otherwise).
  - open, stat and exec work only if fd 3 is a directory handle on `fs_root` with LOOKUP. If not, they return EACCES and `cap_deny` logs `cap: pid N denied open|stat|exec (no root capability)`.
  - `path_ok` (safe, slice-based) rejects `.` and `..` components on every path.
- Syscalls: 420 `dup(fd, rights)` needs DUP, and rights must be a subset of the old ones. 421 `openat(dirfd, path)` is a relative lookup from the directory's cluster through `fs_lookup_at`; it needs LOOKUP, takes no absolute paths, and the new handle gets the directory's rights minus WRITE. 422 `rights(fd)`.
- fork copies the rights, and close and exit clear them. kexec stores `(file index + 1) | rights << 32` in each fd slot of the v2 handoff record.
- Test: `examples/user/captest.forge` (safe mode) checks a read-only dup, widening, DUP-less dup, relative and escaping openat, and a no-LOOKUP directory. It also forks a child that closes every handle except a read-only one; that child's write, open, openat(3) and exec are all denied.

## IPC channels (Phase 5.2, `examples/kernel/chan.fsi`)
- An endpoint is a `File` of type F_CHAN. `clu` is an index into the static `chans: Chan[16]` table and `pos` is the end (0 or 1). Each `Chan` has per-direction rings: `head`, `cnt`, `mlen`, `mh`/`mr` (moved File and its rights) and `mdata` (64 bytes per slot). It also has `open[2]` and the wait queues `rq0`/`rq1` (receivers) and `sq0`/`sq1` (senders blocked on a full ring). Everything is guarded by `rq_lock`, and blocking goes through `wq_block_l`.
- Syscalls:
  - 423 `chan(u64[2])` returns two fds, both with READ|WRITE|DUP|XFER.
  - 424 `send(fd, buf, n, h)` takes n in 1..64. h is `NO_HANDLE` (~0) or an fd with XFER; it cannot be the endpoint itself. The handle is moved, not copied. Sending to a closed peer returns EPIPE.
  - 425 `recv(fd, buf, n, &h)` blocks while the ring is empty. It returns 0 when the peer is closed and nothing is queued. A moved handle is installed in the lowest free fd; if there is none, the handle is dropped and EMFILE is returned.
- The user-side 4th argument travels in r10 (`syscall4` in ulib; `syscall3` now wraps it, so ulib's unsafe count is unchanged).
- Closing: when an endpoint File's last reference goes, `file_put` calls `chan_end_close`. That wakes the peer and, once both ends are closed, drops any queued handles and frees the table slot. `chan_live` counts open channels, and `sys_kexec` fails while it is nonzero.
- Test: `examples/user/chantest.forge` (safe mode).
- Fix found by chantest: `cur_thread()` now reads the CPU id and `current[]` with irqs off. Before, a preemption that migrated the thread between the two loads returned another CPU's (idle) thread, so `cur_proc()` was 0 and caused a #PF in `fd_get`.

## App sandboxes (Phase 5.3)
- Manifests live in `\EFI\Forge\apps\<app>.mf` (read-only ESP, inside the Forge directory). The format is one grant per line: `read PATH` or `dir PATH`. `#` lines and unknown lines are ignored.
- Launch (`sh.forge` `run_app`): fork. The child opens each grant (fds 4.. in order), narrows each with `restrict(fd, rights)` (427; it can only drop rights) and calls `exec_sandboxed(path, args)` (426). If a grant fails to open, the child exits 126 before exec.
- Kernel (`sandbox_enter` in proc.fsi): after a successful exec the root capability at fd 3 is closed, and the pid is recorded in `sb_pid[]`/`sb_name[]` beside the Proc table. That table is kept outside Proc so Proc stays in its 512-byte slab class. Fork inherits the record, `proc_destroy` clears it, and `sb_live` counts live records. `cap_deny` logs denials for sandboxed processes with the app name, operation and path. `sys_kexec` fails while `sb_live != 0`.
- Leak check fix: the shell-session check now leaves out slab pages (`slab_pages`) and kernel page-table pages (`kpt_pages`, counted in `pt_next` for `kernel_pml4`). Both are kept by design. Adding the apps directory moved allocation peaks and showed them as 1–2 false 'leaked frames'. Objects are still checked exactly through `heap_live`.

## Dual boot
- The loader and kernel install into their own vendor directory on an existing ESP: `\EFI\Forge\forgex64.efi` and `\EFI\Forge\kernel.elf`. Add a firmware boot entry, e.g. `efibootmgr -c -d /dev/sdX -p N -L Forge -l '\EFI\Forge\forgex64.efi'`, or `bcdedit /copy {bootmgr}` + `/set {id} path \EFI\Forge\forgex64.efi` on Windows.
- Nothing else is touched: not `\EFI\Microsoft`, not `\EFI\<distro>`, not the MBR, and no repartitioning. The loader only reads its own kernel file. The kernel's disk access is read-only: it reads only GPT headers and files under `\\EFI\\Forge\\` on the ESP and never writes. `\EFI\BOOT\BOOTX64.EFI` is used only on the throwaway test disk.
- The kernel can also be started from an existing Linux GRUB with a `multiboot2 /boot/forge/kernel.elf` entry (see examples/kernel/grub.cfg, which loads `all_video` so the framebuffer tag works on both BIOS and UEFI GRUB). GRUB can also chainload `forgex64.efi` (`chainloader /EFI/Forge/forgex64.efi`).
- If `forgex64.efi` fails, it prints the reason and returns to the firmware boot manager, so the next boot entry (Windows/Linux) still works.

## Roadmap
The full phased plan is in docs/BUILD_PLAN.md; the items below are the kernel subset.

1. ~~**Memory:** memory map, frame allocator, 4-level higher-half paging with guard pages and NX, kernel heap, ExitBootServices with the UEFI app as the loader, GOP framebuffer console.~~ **Done.** Reclaim done. Buddy allocator done (Phase 8). Virtual range allocator and PCID done (Phase 8).
2. ~~**Interrupts and time:** PIC off, LAPIC/IOAPIC via ACPI MADT, LAPIC timer, IST stacks for #DF/NMI.~~ **Done** (atomics landed in the language too). x2APIC done (Phase 8). HPET calibration cross-check and TSC-deadline timer done (Phase 8; TSC-deadline not offered by QEMU TCG). Still open: MSI.
3. ~~**Scheduler + SMP:** preemptive kernel threads, spinlocks, INIT-SIPI AP bring-up.~~ **Done.** Since done: xsave/fxsave FPU state per thread, TLB shootdown IPIs, pinned threads, wait queues, sleeping mutexes, condvars, sleep_ticks. Since done: per-CPU run queues with work stealing. Still open: per-queue locks, priorities.
4. ~~**User mode:** TSS, ring 3, `syscall`/`sysret`, ELF loader, per-process address spaces.~~ **Done.** Since done: SMEP/SMAP with copy-in/out #PF fixups; Proc objects with fork (eager copy), exec, wait4, exit, brk, anonymous mmap/munmap, getargs; an interactive `sh` from `\EFI\Forge\bin\`. Still open: copy-on-write fork, signals, env, file-backed mmap.
5. ~~**Drivers:** PCI, virtio-blk, read-only FAT32.~~ **Done** (legacy virtio). Since done: MSI-X completions with sleeping requests, FAT32 long names + case-insensitive lookup, a VFS with fds (open/read/close/stat/getdents), PS/2 keyboard and COM1 RX interrupts feeding a tty line queue. Still open: richer modern virtio 1.0 (multiple queues / packed rings), multiple requests in flight, AHCI/NVMe, virtio-net, ext2. A minimal modern 1.0 path (one split queue, ACCESS_PLATFORM) is used for virtio-blk when the device has no legacy I/O BAR, so VT-d can confine blkd DMA.
6. **Language and compiler:**
   - Weaker memory orderings (acquire/release) and `unsafe`-scoped raw pointers.
   - Slices with bounds checks, generics for containers, `defer`, and a register allocator for fs mode (the IR backend currently covers hosted code only).
   - Debug info (DWARF line tables), so GDB works on QEMU's gdbstub.
7. **Toolchain independence:** a built-in assembler and an ELF64/PE32+ writer and linker in Forge, which removes `as`/`ld`/`objcopy`, plus linker-script equivalents (section placement, entry, image base). Assembler done (Phase 7.1, `tools/fas.forge`); linker done (Phase 7.2, `tools/fld.forge`): the build no longer uses as/ld/objcopy past the bootstrap seed.
8. **ARM64 target:** an AArch64 backend for fs mode, UEFI on ARM (AAVMF in QEMU `virt`), PL011 serial, GIC, and the generic timer.
9. **Secure Boot:** sign `forgex64.efi` (sbsign/MOK) or chain through shim so it coexists with distro and Windows Secure Boot setups.
10. **Never reboot (BUILD_PLAN Phase 2):** 1 module ABI, 2 isolated driver services, 3 supervisor restart, 4 tty hot-swap, 5 live patching, 6 kexec handoff — done.

## User W^X (Phase 5.4a)
- `elf_load` (user.fsi) rejects a PT_LOAD with PF_W|PF_X, and rejects a segment that lands on an already-mapped page whose W/NX bits differ from its own, so no page is ever writable and executable or silently gets weaker permissions. Rejections bump `elf_wx_rejects`.
- Text pages are mapped read-only + executable; everything else (data, bss, stack, brk, mmap) is NX. User programs are linked with `-z separate-code -z noexecstack`.
- Tests: `user_wx_test` in kernel.forge (crafted images built in a static buffer, safe code) and `wxtest` from the boot shell (four forked children; each must exit 0x10e from #PF).

## Kernel stack guard pages (Phase 5.4b)
- `kstack_alloc(np)` / `kstack_free` (mm.fsi) map np pages at `KSTACKS_BASE` + bump offset with one never-mapped page below; VA is never reused, so a stale pointer into a freed stack faults too. The range shares the big heap's PML4 slot, so every user address space already sees it.
- Overflow: push into the guard page -> #PF cannot deliver -> #DF on IST1. `thread_guard_hit(cr2)` (sched.fsi) matches the current thread's guard page; the handler iretqs to `thread_overflow_exit` with rsp = the thread's stack top, and the thread exits. Any other #DF without a recovery point still panics.
- Test: `guard_test` (kernel.forge) after the scheduler test.

## Stack canaries (Phase 5.4c)
- Compiler (selfhost/compiler.forge, freestanding backend): pragma `# forge: stack-protector` sets `fs_sp`; each frame reserves -8(%rbp). Functions with a buffer local (`fs_hasbuf`) get `mov stack_canary(%rip),%r11; mov %r11,-8(%rbp)` after the frame setup and `mov -8(%rbp),%r11; xor stack_canary(%rip),%r11; jz ok; call stack_smashed` at the single return label. `stack_smashed` itself is never instrumented. Programs without the pragma compile byte-for-byte as before (FIXPOINT unaffected).
- Kernel: `stack_canary` global, `canary_init` (no buffer locals, so it is not instrumented) seeds it first thing in `kmain_high`; `stack_smashed` panics, except in the armed self-test, where it kills the thread.
- Not covered: user programs and modules (no pragma yet); the canary is per boot, not per thread.

## KASLR, partial (Phase 5.4d)
- `canary_init` (kernel.forge) also sets `heap_big_next` = HEAP_BIG_BASE + rand16 * 2 MiB and `kstack_next` = KSTACKS_BASE + rand16 * 2 MiB, before the first big allocation or thread. Both stay in PML4 slot 384, so user address spaces created later share them. Logged as `KASLR: big heap at ..., thread stacks at ...`.
- Fixed: KSTACK_TOP/IST stacks, the HHDM, DEMAND_BASE. Entropy is the TSC (no RDRAND yet).
## Image KASLR (Phase 5.4e)
- Build (examples/kernel/build.sh `klink`): `tools/fld --format kernel` writes `.krel` (u64 entries `offset_from_KBASE | kind << 56`; kind 1 u64 += s, 2 u32 += s, 3 i32 -= s) from the relocations it applies, as the last PT_LOAD (R); the shipped ELF has no .rela sections. (Until Phase 7.2: `ld -q` + tools/krel.py + a second link; tests/fld/run.sh still checks fld's table equals that reference.) Currently 15 entries (ksyms, the boot stub's high jump target, rodata function tables).
- Boot (kernel_start): `kaslr_pick` -> `paging_init` (both aliases, same frames; `kv2p` subtracts kslide for slid addresses) -> `paging_activate` -> `krel_apply(HHDM + kdelta, kslide)` (asm in boot.fsi) -> `switch_stack(KSTACK_TOP, kmain_high + kslide)` -> `kaslr_unmap` in kmain_high. `check_mapping("unslid .text")` must report unmapped.
- FORGEKRN header: entry, phys start, phys end, krel start, krel end. The Forge UEFI loader and kexec refuse a kernel whose table is empty; kexec'd kernels pick a fresh slide.
- Limits: slide granularity 2 MiB, about 9 bits of entropy; the low boot stub at 1 MiB (phys) is not randomized; physical placement (kdelta) only varies under kexec.

## Kernel core interface (plan v1.3 item 8)
The contract of the scheduler, IPC channels and the two capability layers is specified in
[kernel-core.md](kernel-core.md) (spec v1); changing any of it bumps that version.
Appendix A of that file is the verification scope (what the tests pin, the known DMA/IOMMU gaps, verifier-ready conventions).

## Process-state format (plan v1.3 item 7)
`examples/kernel/pstate.fsi` defines the single versioned record of one user process, **process-state
v1**. The kexec handoff (format v5) writes one record per process and adopts nothing else; checkpointing,
migration or record/replay must reuse it and bump `PS_VERSION` instead of defining another format.
- Two pages: the record page and the FPU page (fxsave/xsave image; the container records its size).
- Record words: pid, ppid, state, exit code, brk_base, brk, mmap_next, flags (is-caller | caps << 8 |
  abi << 40), user page count and first page, the syscall in progress (5 words), FS base, 16 fd slots
  ((file index + 1) | rights << 32), args (128 B), syscall frame (128 B). Offsets are the `PS_*` constants.
- Header word at +512: `PS_MAGIC << 32 | PS_VERSION` ("FPST", v1). `ps_hdr_ok` accepts
  `PS_MIN_VERSION..PS_VERSION`; kexec panics on a record that fails it rather than adopting a misread
  process (the container header itself is still versioned separately: `KXH_VERSION` 5).
- User pages, the page index and the shared file table belong to the container (the handoff region) and
  are referenced by index.
- Boot check: `ps_selftest` (run before the kexec session) prints `process-state: format v1 (adopts
  v1..v1), header checks OK` after checking that its own header passes and wrong-magic, version-0 and
  newer-version headers are refused.

## Nondeterminism hook (plan v1.3 item 6)
`examples/kernel/nondet.fsi`. Every nondeterministic input the kernel takes goes through one function,
`nd_hook(kind, a, v) -> v`:
- `ND_SYSCALL`: `syscall_dispatch` is a thin wrapper around `syscall_do`, so every syscall result
  (native and Linux personality) passes the hook (a = number, v = result).
- `ND_TIMER`: `on_lapic_timer`, on every CPU (a = cpu, v = `lapic_ticks`).
- `ND_SCHED`: `schedule_l`, after the next thread is chosen (a = cpu, v = the thread).
- `ND_RNG`: `nd_rand(site, v)` at every random site: KASLR slide (1), stack canary and heap/stack
  KASLR (2), Linux AT_RANDOM (3), object-store key entropy (4).

The hook returns v unchanged; it is the single point where a later replay mode can substitute recorded
values. Recording (`nd_rec`) is **off by default**: the hook then reads one global and returns. When on, it
counts events per kind and appends (kind, a, v) to a fixed 256-entry ring (slot taken with an atomic
fetch-add, so CPUs never share a slot). Nothing is persisted or exported yet.

Boot self-test (`shell_test`): nothing may be recorded before the shell session (`nondet: recording off by
default`); the shell session (which also runs the Linux-ABI programs) is recorded and must contain all
four kinds, the ring may only hold known kinds, and nothing is recorded after recording is switched off
(`nondet hook OK`). No unsafe code was added (the new file is safe Forge; unsafe counts unchanged).

## Proof manifests (Phase 6.1)
Every user ELF is sealed at build time by `tools/forgeseal.py`, which is the verifier gate: it compiles the program with `--unsafe-report` and refuses to seal unless the program has no unsafe code or its counts are within the audited `tests/unsafe/baseline.txt` entry. It appends a 128-byte trailer: `FORGEPRF`, version 1, safety level (0 none, 1 audited, 2 audited ulib only), unsafe ops, declared caps (`# forge: caps(fs, spawn, ipc, drv, kexec, probe, ktest)`), ELF length, own-file unsafe ops, SHA-256 of the ELF and HMAC-SHA256(key, trailer[0:96]). The key is 32 random bytes per build tree (`build/user/seal.key`, never committed) and is `.incbin`'d into the kernel. The kernel (`seal.fsi`, safe SHA-256/HMAC; 3 new casts offset by moving `elf_load` onto `Ehdr`/`Phdr` struct views) refuses any image that fails, in `sys_exec` (ENOEXEC, logged `verify: PATH refused: REASON`) and `proc_spawn_args`. The W^X self-test loads its crafted images with `elf_try` directly. Limits: MAC not signature; modules unsealed.

## Capability grants (Phase 6.2)
A process holds exactly the capability bits its sealed manifest declares (fs=1, spawn=2, ipc=4, drv=8, kexec=16, probe=32, ktest=64, grant=128 for the 10.2 grant syscalls 428/429 and the 10.3 audit syscall 430); exec can only narrow them (declared ∩ held), fork copies them, kexec hands them over (handoff v3). Base syscalls (read/write/close/mmap/munmap/brk/yield/getpid/exit/ticks/cpu/getppid/getargs/dup/rights/restrict/kx_gen) need none. The check sits at the top of `syscall_dispatch`, before the handle-rights and sandbox checks, which still apply.

## Refusal paths and the Linux ABI gate (Phase 6.3, 6.4)
- Reason codes from `image_verify`: 1 no manifest, 2 bad version, 3 length mismatch, 4 bad signature, 5 hash mismatch, 6 Linux ABI image outside the compat sandbox.
- Reason 6 is checked after the seal succeeds: an image whose ELF EI_OSABI byte is 3 is refused while `compat_sandbox_ready == 0`. Nothing sets it today; Phase 11 will set it only for processes launched in the compat sandbox.
- The shell test runs unsealed, tampered, forged, linuxabi (all refused, exit 127) and capdeny (fork and probe get EACCES).

## Ed25519 seal (Phase 6 gap 1)
- Trailer v2, 160 bytes: +0 "FORGEPRF", +8 version 2, +16 level, +24 unsafe ops, +32 caps, +40 ELF length, +48 own ops, +56 reserved, +64 SHA-256(ELF), +96 Ed25519 signature over trailer[0:96].
- Build: `forgeseal --pub seal.key > seal.pub` (`build/forgeseal` from tools/forgeseal.forge; forgeseal.py is the reference); the kernel `.incbin`s seal.pub (uimg(4)). The private seed never leaves build/user.
- Kernel: `ed25519_verify(sig, pk, msg, n)` in ed25519.fsi; rejects S >= L and keys that are not curve points. One verification costs two 256-bit scalar multiplications (it adds about 15 s to the whole OS test run, which execs dozens of images).

## Seal v3 (plan v1.3)
- Trailer v3, 384 bytes, the default from `tools/forgeseal.forge` / `tools/forgeseal.py` (`--v2` still writes the 160-byte v2 trailer, which the kernel still accepts): +0 "FORGEPRF", +8 version 3, +16..64 as v2 (level, unsafe ops, caps, ELF length, own ops), +64 SHA-256(ELF), +96..128 reserved proof slot (must be zero, else reason 10), +128 signature count 1..3, +136 zero, then 3 slots of 80 bytes at +144: algorithm ID (1 = Ed25519, the only one implemented; anything else is reason 9), 8-byte key ID, 64-byte signature.
- Signed message (96 bytes, the verifier's limit): trailer[0:64] || SHA-256(trailer[64:144]). The kernel accepts the image if a slot whose key ID matches the built-in key verifies. One `SealTail` view covers v2 (+224) and v3 (+0); net unsafe ops go down by 1.
- seal_test also checks: a v2 image accepted, an unknown algorithm ID refused (9), a set proof slot refused (10); cat carries a second signature by another key.

## Sealed kernel modules (Phase 6 gap 2)
- examples/modules/build.sh seals each .ko (`--module`, kind word 1). `image_verify(img, len, kind)` checks kind (reason 7, "wrong image kind") after the signature; `mod_load` calls it with SEAL_MODULE and parses only the first `len - 160` bytes.
- Seal cache: up to 8 signed trailer prefixes (96 bytes) that already passed Ed25519; a hit skips the signature check only, never the image hash.
- `tty_swap_to` now returns 0 on a refused module instead of panicking, and the current line discipline stays installed.

## Built-in assembler (Phase 7.1)
`build/fas` (tools/fas.forge): `fas [-I dir] [-o out.o] in.s`. Byte-identical to GNU as on everything the toolchain emits (compiler output, kernel incl. asm blocks, krel stubs, EFI loader, modules, user programs); tested by tests/fas/run.sh. Layout rules it mirrors: file offsets aligned to min(sh_addralign, 8); NOBITS takes an aligned offset without advancing; no symtab/strtab when there are no symbols; locals relocated against section symbols; PLT32 for calls unless the target was a defined local when the call was assembled; GNU relax_segment jump relaxation with alignment regions. Unsupported directives/instructions fail with `fas: file:line: ...`.

## Language server (Phase 7.5)
`forge lsp` runs an LSP server on stdin/stdout (point the editor's client at the command `forge lsp`). It provides diagnostics from forgec (parse first, then the full compile), go-to-definition and hover. Both come from the `--emit=ast-json` tree and cover locals, parameters, module globals and sibling-module imports. Unsaved buffers are compiled from a hidden `.<name>.lsp<pid>.forge` file next to the original, which is deleted after each run. Positions are ASCII-only. See tests/lsp for a scripted session.

## Packages (Phase 7.4)
`forge pkg lock` / `forge pkg build` in a directory with a forge.toml. Dependencies come from a local registry directory (`<name>/versions`, `<name>/<version>/forge.toml` and sources) and are pinned with SHA-256 content hashes in forge.lock; `build` verifies every hash before staging the sources into target/stage and compiling, so the same lock gives the same binary hash. See tests/pkg for a fixture registry and project.

## Debug info (Phase 7.3)
`forgec -g` emits DWARF 4 line tables and subprogram DIEs for the kernel; `.Lfsd<n>` labels mark each statement and the line program uses `fixed_advance_pc` label differences, so fas and GNU as produce the same object. fld keeps `.debug_*` as non-alloc sections after the loaded segments. To debug: boot with `-gdb tcp::PORT`, read the slide from the `KASLR: kernel image at ... (slide 0x...)` serial line, then in gdb `symbol-file -o 0x<slide> build/dwarf/kernel.elf`, `target remote :PORT`, `hbreak schedule`. Line info only so far.

## Built-in linker (Phase 7.2)
`build/fld` (tools/fld.forge): `fld [--format exec|kernel|pe] [--base ADDR] [-e SYM] [--subsystem N] [--image-base ADDR] -o OUT in.o...`.
- `exec`: GNU ld's default static layout (headers R at base, .text RX at base+0x1000, .rodata R on the next page, .data/.bss RW at next page + offset remainder, .bss end aligned to 8, PT_GNU_STACK RW); hosted programs use `--base 0x100000000000`. Images are laid out at 0 and the base is added only while bytes are written (fld itself runs at the hosted base, whose runtime would read integers in its own heap range as objects).
- `kernel`: the linker.ld layout built in (linker.ld and tools/krel.py stay as the reference that tests/fld/run.sh compares against); the .krel table (u64 `offset_from_KBASE | kind << 56`, kinds as in the KASLR section) is collected during relocation of high-half sections and appended as the last R PT_LOAD; its size is counted first, so `__krel_end`/`__kernel_phys_end` are final before any byte is written. No ELF relocation sections are emitted.
- `pe`: PE32+ EFI application, SectionAlignment 0x1000, FileAlignment 0x200, image base 0x10000000, subsystem 10, DllCharacteristics 0x160; .text/.rdata/.data(+bss), a .reloc DIR64 table when there are R_X86_64_64 sites (the loader has none).
- Build bootstrap: binutils links only the seed (stage2, and stage2-built fas/fld); build.sh then checks self-built fas/fld reproduce stage3 byte-for-byte.

## Buddy allocator (Phase 8)
Per-order free bitmaps (4 KiB..4 MiB) on top of the authoritative `frame_bits` map in `examples/kernel/mm.fsi`: allocation takes the lowest valid block of the smallest sufficient order and splits it; frees coalesce with free buddies. Stale entries (frames grabbed by direct `frame_bits` edits) are detected on take and dropped, so a double allocation is impossible; an empty map triggers one rebuild from `frame_bits`. `buddy_test` checks alignment, odd-size tails, coalescing back to the starting 4 MiB block count and zero leaks on every boot. Unsafe counts unchanged.

## vmalloc (Phase 8)
Big-heap VA comes from a coalescing first-fit free list of ranges (each with a top guard page); freed ranges are reused. `vmalloc_test` runs 2000 alloc/free cycles with bounded VA growth and no frame leaks. The bounds-overhead benchmark in `tests/bounds/run.sh` now interleaves checked/unchecked runs (best of 7) so load drift cannot fail it.

## PCID (Phase 8)
The shootdown vector (VEC_TLB 0xF0) gets its IDT gate before the first `sti`, together with the timer, PIT and spurious gates: after kexec the successor's LAPIC can still hold a VEC_TLB latched while the previous kernel had interrupts off (answered by `tlb_poll`, never delivered). `sys_kexec` latches one on purpose so the boot tests check that the new kernel handles it (`TLB IPI: 1 stale shootdown IPI(s) ...`).

Per-CPU PCID slots (1..63, from the PML4 frame) let a context switch keep TLB entries when the slot still belongs to the incoming address space; any user shootdown, `user_unmap` or address-space teardown forgets the slot on all CPUs. Enabled only when CPUID reports PCID; not exercised under QEMU TCG (no PCID), so only the bookkeeping is boot-tested.

## x2APIC (Phase 8)
When CPUID offers it, every CPU runs its local APIC in x2APIC mode. Register access goes through MSRs behind the old xAPIC offset interface; IPIs are one 64-bit ICR write to MSR 0x830 with the destination taken from a per-CPU copy of the ICR-high value. Tested on every boot under QEMU (BIOS, UEFI, kexec).

## Hardware drivers (Phase 8)
All device register access goes through checked accessors in `mmio.fsi` that only allow registered MMIO/DMA windows and port ranges (others panic). NVMe and AHCI are read-only (identify + read), xHCI enumerates one device up to its device descriptor, e1000 and virtio-net exchange an ARP request/reply. Tested by `tests/os/drivers.sh`, which checks the disk images are unchanged.

## Framebuffer console (Phase 8)
The loader sets a 1024x768 GOP mode when offered. The kernel keeps two cell grids (wanted and drawn) and draws only changed cells; scrolling never reads the framebuffer. The boot tests decode the screen and check the console's consistency line above KERNEL OK.

## Security fix: user-copy validation (after 8.8)

`copy_user` / `copy_str_user` (cpux.fsi) now enforce the user range themselves: every operand below
USER_TOP must lie wholly below it (overflow-safe `va + n`), at least one operand must be a user
address (a kernel/HHDM pointer passed as the user side returns -14), and `copy_str_user` re-checks
each byte address so a string cannot run past USER_TOP. Call sites in sys_wait, sys_getargs,
sys_stat and drv recv/reply also check `user_ptr_ok` explicitly. sys_mmap maps before consuming VA
and rolls back partial maps on ENOMEM; sys_chan_create drops both fds and the channel when the fd
copy faults; sys_chan_recv touches the user buffer and handle slot (with zeros) before dequeuing.
Boot self-test `uaccess_test` probes the helpers, sys_open and sys_stat with kernel pointers and
expects EFAULT ("uaccess: 9 kernel-pointer probes -> EFAULT").

## Security fix 2: ELF bounds, wait/recv/munmap error paths

elf_load checks `off > len or fsz > len - off` and `va >= USER_TOP or msz > USER_TOP - va` (no
wrapping sums), caps PT_LOAD memory at 64 MiB per image (ELF_MAX_MAP) and allocates with
frame_try_alloc so exhaustion fails the load instead of panicking. sys_wait copies the status before
proc_destroy and puts the zombie back on EFAULT. drv recv (405) returns a TAKEN request to POSTED when
the copy faults. munmap refuses (EPERM) ranges overlapping the driver's DMA region, which stays pinned
until the process exits. chan_recv reserves its fd slot before dequeuing. Boot test
`user_elf_bounds_test` feeds 5 malformed headers and checks refusal, the reject counter and no frame leak.
drv 403 returns a non-identity IOVA when VT-d translation is on, so the user-mode virtio driver programs a confined bus address; without a remapping unit it still returns the host physical address.
## Forge FS (Phase 8)
The only writable filesystem. It exists only on a GPT partition with the Forge type GUID, created explicitly with `tools/forge-part.forge` (`create ... --yes-add-forge-partition`; forge-part.py is the reference). The kernel validates the GPT, claims only that LBA range on NVMe, and every write outside it is refused. Tested by `tests/os/forgefs.sh`: no partition means no writes; with one, files persist across reboots and every byte outside the partition stays identical.

**Syscall pointer validation (9.0):** every syscall taking a user pointer returns EFAULT for pointers outside [0, USER_TOP) or straddling USER_TOP, without consuming state (messages, zombies). `chan_send` returns EINVAL for n==0 or n>CH_MSG and EFAULT for a bad buffer. Covered by `badptr` in tests/os.
