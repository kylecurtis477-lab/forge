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
  - **Console:** COM1 + framebuffer text with an 8×16 font (`tools/mkfont.py` → `font.fsi`, rendered from DejaVu Sans Mono), from the GOP (Forge loader) or the Multiboot2 framebuffer tag (GRUB, BIOS VBE or UEFI GOP); VGA text as fallback.
  - **CPU:** GDT with a 64-bit TSS (`ltr`), IST1 for #DF and IST2 for NMI/#MC. The test overflows the kernel stack into its guard page; the #DF arrives on IST1, prints, and `iretq`s into a recovery continuation on a fresh stack.
  - **ACPI/APIC:** RSDP → XSDT/RSDT (checksums) → MADT (CPUs, LAPIC, IOAPIC, interrupt source overrides). The 8259 PIC is remapped and masked; the LAPIC timer is calibrated against PIT channel 2 and runs periodically at 100 Hz; the PIT's IRQ0 is routed through the IOAPIC using the override (GSI 2) and delivered once.
  - **Memory reclaim:** after ACPI is parsed, loader-owned (EFI loader code/data) and ACPI-reclaimable regions plus the boot-info copy are returned to the frame allocator (e.g. 6.2 MiB under the Forge loader).
  - **W^X:** separate PHDRS for boot RX/RW, `.text` RX, `.rodata` R, `.data/.bss` RW; no RWX segment (checked by `os/kernel-wx`; the Forge loader refuses W+X segments).
  - **SMP:** CPUs from the MADT are started with INIT-SIPI-SIPI through a position-independent real-mode trampoline copied into a free page below 640 KiB (mapped only during bring-up). Each CPU gets its own TSS, guarded kernel/IST stacks, LAPIC timer and syscall MSRs.
  - **Scheduler:** preemptive kernel threads (100 Hz LAPIC tick on every CPU), one global run queue, `xchg` spinlocks with IRQ save/restore. The run-queue lock is held across `ctx_switch` and released by the resumed thread, so a thread can never run on two CPUs at once; dead threads are reaped after a lock round-trip. Heap and frame allocator are locked. Test: 8 threads hammer a lock-protected counter on 4 CPUs (counter == sum of per-thread counts, every thread preempted, all CPUs used).
  - **User mode:** per-process PML4 (kernel half shared, all 256 kernel PML4 entries pre-created), ELF64 loader (bounds-checked, W^X, NX for non-exec segments), 64 KiB user stack, `iretq` into ring 3, `syscall`/`sysret` (STAR/LSTAR/FMASK; `swapgs` only to find the per-CPU kernel stack), syscalls write/yield/getpid/exit/ticks/cpu with user-pointer validation by page walk. Faults from ring 3 kill the process (exit code 0x100+vector), and its address space is freed (leak-checked). User programs are Forge (`examples/user/`, `ulib.fsi`).
  - **Drivers / FS:** PCI config mechanism #1 scan; legacy (transitional) virtio-blk over I/O BAR0 with one polled split virtqueue in contiguous DMA frames; GPT parsing (ESP, Microsoft basic data, Linux partitions recognized); read-only FAT32 (8.3 names, cluster chains). The kernel reads `\EFI\Forge\init.elf` from the ESP, checks it, and runs it in ring 3.
  - **Kernel modules (Phase 2.1):** `module.fsi` loads ELF64 ET_REL objects (plain forgec + `as` output) from `\EFI\Forge\mod\` into 1 MiB slots of a module area inside the top 2 GiB (page tables pre-built, so loads never leak table frames). Sections are grouped text RX / rodata R+NX / data+bss RW+NX. Imports resolve only against a versioned kernel export table (KABI 1.0; each export has a since-minor). Modules export `mod_abi/mod_init/mod_exit` and optionally `mod_save_state/mod_restore_state`. The boot test does 1000 load/call/unload cycles with zero leaks, a state transfer across reload, and rejects a truncated image, a wrong-major ABI, an unresolved import and an import newer than the module's ABI.
  - **Driver services (Phase 2.2):** `drv.fsi` runs the virtio-blk driver as the ring-3 process `blkd` (`\EFI\Forge\bin\blkd`) with granted capabilities only: port I/O inside the device's I/O window, waiting on its MSI-X vector, one DMA region; requests arrive over a recv/reply IPC slot. A crashing driver is contained: the device is reset before its DMA frames are freed, the in-flight request fails with EIO, and the client keeps running. No IOMMU yet, so DMA itself is not confined.
  - **Supervisor (Phase 2.3):** a kernel thread restarts a dead `blkd` from its cached image, and the in-flight read is replayed from the kernel's request journal. The boot test crashes the driver in the middle of a 96 KiB read; the bytes still hash identically and the restart count is reported.
  - **tty hot-swap (Phase 2.4):** the line discipline is a module (`tty1.ko` / `tty2.ko`) that can be swapped while typing is in progress. Queued input moves across in a versioned state blob under rq_lock, and the boot test checks that no keystroke is lost across about 120 swaps.
  - **Live patching (Phase 2.5):** `@patchable` functions start with a 5-byte nop that is swapped for a `jmp` under stop-machine on all CPUs, with a hash check before the patch and after a revert, and a pre-install and post-install self-test with automatic rollback. `sys_getpid` is patched and reverted 25 times on 4 CPUs while it is being called, with 0 wrong results.
  - **kexec handoff (Phase 2.6):** syscall 409 (test hooks only) loads `\EFI\Forge\kernel.elf` into free RAM, serialises the calling process (user pages, brk, args, FPU, syscall frame, console fds) into a hashed handoff region with a new `BOOT_KEXEC` BootInfo and page tables, quiesces devices and APs from the BSP, and jumps. The successor boots fully, restores the process, frees the region and resumes it with return value 1. Nothing is written to disk (checked by disk-image hash).
  - **kexec v2 hardening:** `kexec next` boots `kernel-next.elf` (version `forge-kernel 0.2.6-next`); every non-driver user process is parked at a syscall boundary and carried with its pages, FPU, pid, parent links, pending syscall and open fds (shared Files deduped); versioned 448-byte header with data+metadata hashes; drivers restart in the new kernel. Tested with background `bg` reading a file across the swap.
- **`examples/efi/loader.forge` → `forgex64.efi`:** the native UEFI boot loader, written in Forge.
  - LoadedImage → SimpleFileSystem reads `\EFI\Forge\kernel.elf` from its own volume. It then loads the PT_LOAD segments at a 2 MiB-congruent base below 1 GiB and finds the entry through the `FORGEKRN` header.
  - It picks GOP 1024×768 when offered, finds the ACPI 2.0 RSDP in the configuration table, and builds identity (0–4 GiB) plus −2 GiB tables.
  - It runs GetMemoryMap → ExitBootServices (retried when the map key is stale), converts the map into BootInfo, switches CR3 and jumps to `kentry64`.
  - It is linked directly by `ld -m i386pep --subsystem 10`. `mkgpt.py` builds a GPT disk with an ESP plus placeholder Microsoft basic data and Linux partitions.
- **Tests:** `tests/os/boot.py` boots each path headless, checks the serial markers, takes a QEMU monitor `screendump` after `KERNEL OK`, decodes every framebuffer text row against the font (KERNEL OK must be on screen, so console scrolling is checked), and then sends `q` on COM1 so the kernel exits. The three paths are BIOS+GRUB, OVMF+GRUB, and OVMF+forgex64.efi on GPT, all with `-smp 4` and a read-only virtio-blk copy of the GPT disk (its removable-media fallback loader removed so firmware never boots it).

## Compiler tooling (Phase 3)
- `--diagnostics=json`: one schema-v1 JSON object per error (file/line/col span, code, message, fix-its); goldens in `tests/diag`.


### Test blocks, forge test, forge verify

- `test "name":` + block at top level (column 0) is a unit test. forgec rewrites each header line to `def __forge_test_N():` before lexing (line count unchanged, so diagnostics keep their line numbers); without `--test` they are uncalled functions and the program is unchanged. Test blocks in imported modules are renamed the same way (global counter) and never run from the importer.
- `forgec in out.s --test` appends a runner: `--forge-test-list` prints `--forge-tests--` then one name per line; `--forge-test-run N` runs test N; exit status is the result (a failed assert exits 1 with `error: line L: assertion failed`). The program's own top-level code runs first in both modes.
- `forge test [files]` (tools/forge_test.sh; default tests/unit/*.forge) builds with `--test`, runs each test in its own process with a 10 s timeout and prints `{"forge_test":1,"files":[{"file","build":"ok|fail","tests":[{"name","status":"pass|fail","exit","message","ms"}]}],"passed":N,"failed":M}`; exit 1 on any failure.
- `forge verify [files]` (tools/forge_verify.sh) runs, per file, parse (frozen grammar, ast-json), fmt --check, build (typecheck+codegen+as+ld) and test (if the file has test blocks), and prints `{"forge_verify":1,"files":[{"file","checks":{"parse","fmt","build","test":{"status":"ok|fail|skip"[,"message"]}}}],"ok":bool}`; exit 1 if any check fails.
- Tests: tests/testblocks/run.sh (in run_tests.sh).

### forge fmt

`forge fmt [--check] [files]` runs tools/fmt.forge (token-level, comment-preserving, AST- and line-number-preserving). `--check` exits 1 and prints `would reformat F` for any file not in canonical form. The CI file set is tools/fmt_files.sh; tests in tests/fmt/run.sh.

### Grammar freeze
- docs/GRAMMAR.md is the frozen grammar (v1); the strict `a_*` parser behind `--emit=ast-json` is its reference implementation, and changing either needs a version bump of both.
- tests/grammar/{valid,invalid} plus tools/grammar_fuzz.py (`--seed N --n COUNT`, writes to build/fuzz) check it; see BUILD_PLAN Phase 3 item 3.

### AST export (`--emit=ast-json`, `--emit=pretty`)
- Entry: `forge_main` sees the flag and calls `ast_main`, which lexes only the named file with `ast_raw=1` (`add_id` then skips std-module renaming), parses it with `ast_parse` into flat node arrays (`ak` kind, `av` value, `al` line, `acs`/`acn` child slice into `akid`), and writes JSON (`ast_json`) or source (`pp_s`/`pp_e`).
- Schema v1: `{"forge_ast":1,"file","pragmas":[lines],"root"}`, nodes `{"k","l"[,"v"][,"c"]}`. Kinds: Module Block Expr Assign AugAssign(v=op) AnnAssign Pass Break Continue Return Raise Global Nonlocal Import ImportFrom Alias Assert Del Include Def(Decorators, Params, ret|Empty, Block) ExternDef Class/Struct(Decorators, Bases, Block) Decorator If(v="elif" in chains) While For Try Handler Else Finally With WithItem Param(v=[*|**]name; type, default) Params Empty Name Const Num Float Str BinOp BoolOp Not Unary Compare(v=comma-joined ops) IfExp Lambda Call Kw Star DStar Attr Sub Slice Tuple List Set Dict Pair ListComp SetComp GenExp DictComp CompFor CompIf Yield YieldFrom Test(v=name; Block).
- Num values are decimal; values with 19+ digits print back as hex so the lexer reproduces the same 64-bit pattern. Parse errors in this mode use the normal diagnostic path (`expected ...` is code E0105).

## Dual boot
- The loader and kernel install into their own vendor directory on an existing ESP: `\EFI\Forge\forgex64.efi` and `\EFI\Forge\kernel.elf`. Add a firmware boot entry, e.g. `efibootmgr -c -d /dev/sdX -p N -L Forge -l '\EFI\Forge\forgex64.efi'`, or `bcdedit /copy {bootmgr}` + `/set {id} path \EFI\Forge\forgex64.efi` on Windows.
- Nothing else is touched: not `\EFI\Microsoft`, not `\EFI\<distro>`, not the MBR, and no repartitioning. The loader only reads its own kernel file. The kernel's disk access is read-only: it reads only GPT headers and files under `\\EFI\\Forge\\` on the ESP and never writes. `\EFI\BOOT\BOOTX64.EFI` is used only on the throwaway test disk.
- The kernel can also be started from an existing Linux GRUB with a `multiboot2 /boot/forge/kernel.elf` entry (see examples/kernel/grub.cfg, which loads `all_video` so the framebuffer tag works on both BIOS and UEFI GRUB). GRUB can also chainload `forgex64.efi` (`chainloader /EFI/Forge/forgex64.efi`).
- If `forgex64.efi` fails, it prints the reason and returns to the firmware boot manager, so the next boot entry (Windows/Linux) still works.

## Roadmap
The full phased plan is in docs/BUILD_PLAN.md; the items below are the kernel subset.

1. ~~**Memory:** memory map, frame allocator, 4-level higher-half paging with guard pages and NX, kernel heap, ExitBootServices with the UEFI app as the loader, GOP framebuffer console.~~ **Done.** Reclaim done. Still open: a buddy allocator (contiguous frames are a linear bitmap scan), a virtual address allocator for the large-object heap, and PCID.
2. ~~**Interrupts and time:** PIC off, LAPIC/IOAPIC via ACPI MADT, LAPIC timer, IST stacks for #DF/NMI.~~ **Done** (atomics landed in the language too). Still open: HPET/TSC-deadline, x2APIC, and MSI.
3. ~~**Scheduler + SMP:** preemptive kernel threads, spinlocks, INIT-SIPI AP bring-up.~~ **Done.** Since done: xsave/fxsave FPU state per thread, TLB shootdown IPIs, pinned threads, wait queues, sleeping mutexes, condvars, sleep_ticks. Still open: per-CPU run queues + load balancing, priorities.
4. ~~**User mode:** TSS, ring 3, `syscall`/`sysret`, ELF loader, per-process address spaces.~~ **Done.** Since done: SMEP/SMAP with copy-in/out #PF fixups; Proc objects with fork (eager copy), exec, wait4, exit, brk, anonymous mmap/munmap, getargs; an interactive `sh` from `\EFI\Forge\bin\`. Still open: copy-on-write fork, signals, env, file-backed mmap.
5. ~~**Drivers:** PCI, virtio-blk, read-only FAT32.~~ **Done** (legacy virtio). Since done: MSI-X completions with sleeping requests, FAT32 long names + case-insensitive lookup, a VFS with fds (open/read/close/stat/getdents), PS/2 keyboard and COM1 RX interrupts feeding a tty line queue. Still open: modern virtio 1.0 capability layout, multiple requests in flight, AHCI/NVMe, virtio-net, ext2.
6. **Language and compiler:**
   - Weaker memory orderings (acquire/release) and `unsafe`-scoped raw pointers.
   - Slices with bounds checks, generics for containers, `defer`, and a register allocator for fs mode (the IR backend currently covers hosted code only).
   - Debug info (DWARF line tables), so GDB works on QEMU's gdbstub.
7. **Toolchain independence:** a built-in assembler and an ELF64/PE32+ writer and linker in Forge, which removes `as`/`ld`/`objcopy`, plus linker-script equivalents (section placement, entry, image base).
8. **ARM64 target:** an AArch64 backend for fs mode, UEFI on ARM (AAVMF in QEMU `virt`), PL011 serial, GIC, and the generic timer.
9. **Secure Boot:** sign `forgex64.efi` (sbsign/MOK) or chain through shim so it coexists with distro and Windows Secure Boot setups.
10. **Never reboot (BUILD_PLAN Phase 2):** 1 module ABI, 2 isolated driver services, 3 supervisor restart, 4 tty hot-swap, 5 live patching, 6 kexec handoff — done.
