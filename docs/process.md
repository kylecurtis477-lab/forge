# Processes, ELF loading, fork, brk and mmap

Kernel sources: `examples/kernel/user.fsi` (address spaces, ELF64 loader), `examples/kernel/proc.fsi`
(process table, `sys_fork`, `sys_exec`, `sys_brk`, `sys_mmap`), user test `examples/user/forktest.forge`.

## What it does
Every user process has its own lower-half address space (PML4 slots 0..255; the kernel half is shared).
Images are sealed ELF64 executables (see `image_verify`); `elf_load` maps each `PT_LOAD` with per-page
W^X (`RWX` segments and mixed RX/RW pages are refused) and caps total mapped memory at 64 MiB.

## Layout of a process
| Region | Where |
|---|---|
| image (`PT_LOAD`s) | from 0x10000 up, as linked (normally 0x401000) |
| brk heap | from `page_up(image end)` up to `BRK_MAX` |
| mmap | `MMAP_BASE` 0x7000_0000_0000 bump allocator up to `MMAP_TOP` |
| user stack | `USTACK_PAGES` pages below `USTACK_TOP` 0x7FFF_FFFF_F000 |

## APIs and syscalls
- `elf_load(pml4, img, len, endp) -> entry`: returns 0 on rejection; writes the highest `va + memsz`
  to `*endp` when `endp != 0`. The end is a per-call value: CPUs load images concurrently.
- `as_new`, `as_clone` (fork: full copy of every user page, no COW), `as_free` (frees pages, tables,
  PML4, and forgets its PCID on every CPU).
- Syscalls: `fork`, `exec`, `wait`, `exit`, `brk` (`brk(0)` returns the current break;
  growing maps zero pages, shrinking unmaps), `mmap(len)`, `munmap(va, len)`.

- VFS: `open`/`stat`/`read`/`getdents` also serve the object-store files under `/obj` (file types `F_OBJ` = 5 and
  `F_ODIR` = 6, read-only, see docs/objstore.md 9.5); every other path goes to FAT32 as before.

## Known bug fixed (Nov 2, 2026)
`elf_load` used to record the image end in a global `elf_end` that callers read after it
returned. When two CPUs loaded images at once (a spawn racing an exec), one process could get
the other image's end as its brk base. forktest saw `brk(0) == 0x401000` (its own text page).
`user_map_zero` skips pages that are already present, so `brk` "grew" without mapping anything,
and the first write killed the process with `#PF at rip=0x401df0 addr=0x401000` (exit 270). This was
the intermittent `os/efi-loader-gpt` failure. The fix passes the end back through `endp`. forktest
now also checks that `brk(0)` lies above its own last segment.

## Testing
`tests/os/run.sh` boots three paths (BIOS, UEFI-GRUB, native UEFI loader). Each one runs forktest from
the shell, and the run needs `forktest: PASS` and `mmap 16KiB rw ok`.

## Known limits
- Fork copies every page; there is no copy-on-write.
- Processes are single-threaded. `munmap` invalidates locally and forgets the PCID elsewhere.
- `brk` does not check whether new pages overlap pages that are already mapped. It relies on a correct brk base.

## SMP note: single-CPU boots
With `-smp 1` no APs come online, so no IPIs are sent; the x2APIC self-check
("x2APIC sent no IPIs") now only fires when `ncpu_online > 1`. Regression test:
`kernel-bios-smp1` in tests/os/run.sh boots the BIOS ISO with `-smp 1`.
