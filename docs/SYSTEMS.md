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
  - `call_ptr(f, ...)` (SysV) and `call_efi(f, ...)` (Microsoft x64, with shadow space)
- **ABI:**
  - `extern def` declares C functions. `@export` makes a symbol global.
  - Narrow arguments and return values are normalized at the boundary, so mixed Forge/C code is safe (tests/fs/cabi).
  - `@interrupt` and `@interrupt_err` generate a register-saving `iretq` wrapper. The body gets `frame: ptr[u64]`, which points at the error code or RIP.
  - `@efiapi` generates an MS-ABI entry. `@naked` emits no prologue. `@section("...")` places the function in a named section.
- **Globals:** `name: T = init` goes in .data, otherwise .bss. Initializers can be constants, arrays `[..]`, strings, or symbols. `NAME = constexpr` defines a constant. `include "file"` does textual inclusion.

## Demos (tested by `tests/os/run.sh`, also called from run_tests.sh)
- **`examples/kernel/`:** an x86-64 kernel.
  - It has a Multiboot2 header and a 32→64-bit trampoline, then `kmain` in Forge.
  - kmain drives COM1 serial and the VGA console, parses Multiboot2 tags, loads its own GDT/IDT, and handles #BP, #PF (with CR2), and #GP.
  - It is built into a BIOS+UEFI hybrid GRUB ISO and boots under both SeaBIOS and OVMF.
- **`examples/efi/`:** a native PE32+ UEFI application written in Forge.
  - It is linked directly by `ld -m i386pep --subsystem 10` and writes through `ConOut->OutputString`.
  - `mkgpt.py` builds a GPT disk with an ESP plus placeholder Microsoft basic data and Linux partitions, and OVMF boots the app from it.

## Dual boot
- The EFI app installs into its own vendor directory, `\EFI\Forge\forgex64.efi`, on an existing ESP. Add a firmware boot entry for it, e.g. `efibootmgr -c -d /dev/sdX -p N -L Forge -l '\EFI\Forge\forgex64.efi'`, or `bcdedit` on Windows.
- It never touches `\EFI\Microsoft`, `\EFI\<distro>`, or the MBR, and does not repartition. `\EFI\BOOT\BOOTX64.EFI` is used only on the throwaway test disk.
- The kernel can also be started from an existing Linux GRUB with a `multiboot2 /boot/forge/kernel.elf` entry (see examples/kernel/grub.cfg).
- The kernel identity-maps 1 GiB with 2 MiB pages and touches no disks.

## Roadmap
1. **Memory:**
   - Parse the Multiboot2/UEFI memory map, then build a physical frame allocator (bitmap or buddy).
   - Use 4-level paging with a higher-half kernel, guard pages, and NX.
   - Add a kernel heap (slab plus a large-object allocator).
   - Exit UEFI boot services so the native EFI path can become the kernel loader (GetMemoryMap → ExitBootServices → jump). This makes GRUB optional.
2. **Interrupts and time:** remap or disable the PIC, then add the LAPIC/IOAPIC via ACPI MADT, the HPET/LAPIC timer, and IST stacks for #DF/NMI.
3. **Scheduler:** kernel threads with context switching (asm stubs), preemptive round-robin then priorities, spinlocks/mutexes (needs atomics intrinsics: `lock cmpxchg`, `xchg`), and SMP bring-up via INIT-SIPI.
4. **User mode:** TSS/`ltr`, ring 3, `syscall`/`sysret`, an ELF loader, and per-process address spaces.
5. **Drivers:** PS/2 keyboard, framebuffer via GOP (UEFI) or the multiboot2 framebuffer tag, PCI enumeration, AHCI/NVMe, virtio-blk/net, and a read-only FAT/ext2 to start.
6. **Language and compiler:**
   - Atomics and memory-ordering intrinsics, and `unsafe`-scoped raw pointers.
   - Slices with bounds checks, generics for containers, `defer`, and a register allocator for fs mode (the IR backend currently covers hosted code only).
   - Debug info (DWARF line tables), so GDB works on QEMU's gdbstub.
7. **Toolchain independence:** a built-in assembler and an ELF64/PE32+ writer and linker in Forge, which removes `as`/`ld`/`objcopy`, plus linker-script equivalents (section placement, entry, image base).
8. **ARM64 target:** an AArch64 backend for fs mode, UEFI on ARM (AAVMF in QEMU `virt`), PL011 serial, GIC, and the generic timer.
9. **Secure Boot:** sign `forgex64.efi` (sbsign/MOK) or chain through shim so it coexists with distro and Windows Secure Boot setups.
