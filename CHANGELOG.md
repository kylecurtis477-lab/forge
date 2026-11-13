<!-- CHANGELOG.md: release notes for Forge from 0.17.1 (5b8e7af) on. Each entry names its commit; the
     version-to-commit mapping comes from the project plan. There are no git tags: the versions are
     groupings of commits, not contiguous ranges of history (for example the 0.17.4 fixes 0bbe40c, 4ecde04,
     f5c66be and 2aba6e2 come before the 0.17.2 commits in the log). Newest version first. 0.18.1 is cut as
     a grouping only; 0.19.0 and 0.20.0 are open. -->
# Changelog

## 0.20.0 (unreleased)

### Ownership-typed struct fields (build-plan item 2)
- `c081619` Stage 1 (designed with ChatGPT): `own[T]` fields are laid out (8 bytes, 8-aligned) but every
  access is refused until Stage 2.
- `4f9aec0` Stage 2 (designed by ChatGPT, reviewed here): direct `own[T]` fields of local structs get
  per-field move, initialisation and borrow state, joined across branches and loops.
- `05df144` fix: only the outermost assignment target takes the field-store path (double move and a write
  under a shared borrow inside targets).
- `9d057e6` Stage 3a (designed by ChatGPT, reviewed here): an `own[T]` field behind a local
  `own`/`mut`/`ref` pointer is usable as a place (`p.head.id`); moving it out waits for `take`/`replace`.
- `c2d57f3` fix: Stage 3a review fixes (named-base guard, a real runtime test, the raw-pointer message,
  seven regression tests).
- `6e19afc` fix: the Stage 3a comment block in `fs_postfix` reformatted with `build/fmt` (gate inv152
  failed fmt-repo-clean on it).

### Optimizer: Phase 12.1 steps 4a-4c
- `be09366` step 4a, `--fs-opt` promotes scalar locals in safe functions that also take frame addresses;
  kernel frame accesses -4.0%.
- `604811f` step 4b, liveness-based register sharing under `--fs-live` (byte-identical without the flag).
- `447067b` the kernel is built with `--fs-live` (frame accesses 11,975 to 11,125).
- `3b69d8a` step 4c, stack temporaries into registers (`--fs-temps`).
- `6b43bdd` the kernel is built with `--fs-temps`.
- `e72e5d3` tools/fsfuzz, a differential fuzzer for `--fs-opt`.
- `6dd80b0` docs: the `--fs-opt` asm audit as a checked list, kept complete by tests/asm-audit.
- `6091098` bench record integrity (`bench run` records the full HEAD sha and refuses a dirty tree).

### Optimizer correctness gate (build-plan item 3)
- `f5a044b` part a, tools/optcov: block coverage of the freestanding optimizer passes (252/284).
- `d2cc610` part b, `fsfuzz --combos`: every optimizer flag combination must give the same output.
- `2324477` part c, `optcov --check`: the 2 reachable never-hit blocks get tests, the other 30 are an
  allow-list with reasons.
- `1d4e26e` part d, `optcov --registry` and the landing rule: a new pass lands only with `optcov --check`
  and `fs-opt-combos` green.
- `4985232` fix: `-O0` turns off bounds-check elision in freestanding code (the O0 pass now runs a
  different code path for kernel and user code).
- `221b90f` fix(tests): efi-build/esp-reproducible builds both ESPs with the same FORGEC_FLAGS.

### v1 reservations 1-4
- `48ec0a6` 1: AI-provenance commit trailers, checked by `forge provenance`.
- `36786ed` 2: `noalloc`/`noio`/`noreclaim` effect names reserved (grammar v1.3).
- `b56affe` 3: an address-space ID in the device-grant ABI (drv 402); only 0 exists.
- `43b38fa` 4: process-state v2 records carry a checksum next to their version.

### Plan and docs
- `ce37b64` CHANGELOG.md for 0.17.2 through 0.18.0 and the start of 0.18.1.
- `e21ccf8` current execution order, the six v1 reservations and a Version 2 section.
- `e287417` working screen first, then the reviewed core order and three never-reboot tiers.
- docs for the gate's whole-repo `build/fmt --check` preflight (the commit after 9c31307; the harness lives
  outside the repo).

## 0.19.0 (unreleased)

### Working screen (build-plan item 1)
- `caf405d` step 1, framebuffer graphics and a desktop boot (gfx.fsi, `forge.desktop`).
- `cfaa6b6` step 2, a terminal window that receives console text.
- `f2c39a8` step 3, PS/2 mouse and keyboard events with an on-screen cursor.
- `e0e40cd` step 4, a windowed compositor (z-order, damage repaint, click focus, drag).
- `be48d2a` step 5, a sandboxed program gets a window through the gfx capability.
- `6f29153` step 6, a desktop shell (taskbar, launcher, minimize); the working screen is complete.

### Interrupt-driven drivers (Phase 8)
- `7a6d513` USB HID step 1: the HID boot keyboard interface and its interrupt IN endpoint are configured.
- `d6abc5d` USB HID step 2: boot reports read and decoded from an interrupt IN ring.
- `2309455` USB HID step 3: the keyboard moves to MSI-X vector 0x52 and feeds the tty.
- `4358997` NVMe I/O completions on MSI-X vector 0x53 wake sleeping readers.
- `822db56` AHCI command completions on PCI MSI vector 0x54; `hw_quiesce` clears interrupt enables
  before kexec.

### DMA policy
- `ce02f98` boot-time DMA policy switch: a sealed `\EFI\Forge\boot.cfg` sets `iommu=strict|permissive`;
  `forge.iommu=strict` on the command line can only tighten; the default is unchanged.

## 0.18.1 (cut; no tag)

### Fixes
- `01beb01` the boot harness checks the framebuffer only after two consecutive identical screendumps.
- `2829140` kbd hot-swap waits until the running input driver has entered `recv` once (inv134 hang).
- `1a04c95` the boot harness does not count time the box itself was frozen as guest silence.
- `80641f6` the fail-fast stall checks skip a clock jump over 1 s (box suspended whole).
- `3b13416` bounds-bench-overhead takes the best of 21 interleaved runs.
- `a7f2084` AP start-up waits 1 s and parks an AP that misses it before the trampoline is unmapped.
- `27aed3d` multiboot2 framebuffer byte order (red and blue swapped on GRUB boots).
- `1b4a017` more than 4096 locals in one freestanding function is a diagnostic, not an internal index error.

### Ownership checker holes
- `44d594d` arrays and slices of own/ref/mut refused as struct fields (two owners from `q.a[0]`).
- `cd512f0` `addr()` of an own/ref/mut value is an unsafe operation.
- `357500e` hosted code refuses own/ref/mut in every type position, not only parameters.
- `817848d` arrays and slices of own/ref/mut refused in every position (globals, locals, parameters,
  `slice(p, n)`), not only struct fields.
- `66edf73` `addr()` of a place reached through a `ref[T]` is an unsafe operation.

### Test and debug infrastructure
- `4e8e48b` stall dumps record QEMU's threads and the box from the host side (tests/os/hostcap.sh).
- `e7bc71f` fail-fast lost-lock and stalled-tick checks print PANIC-STALL instead of hanging.
- `f421b86` tools/ostools, the OS-test toolchain preflight that reinstalls QEMU/GRUB/xorriso/OVMF.

## 0.18.0 (cut at 1fa2883)

### c2forge (C subset to Forge)
- `515ff04` file-scope variables with integer constant initializers, wrapped as C converts them.
- `6f20a1a` integer constant expressions folded for array sizes.
- `2f75b57` `++`/`--` inside expressions.
- `8099896` string literals and char array string initializers.
- `25c273a` fix: `/` and `%` in constant expressions truncate toward zero as in C.
- `e688155` integer global initializers may be constant expressions (shifts, bitwise ops, `sizeof`, C division).
- `ab1aa5a` 1-D integer brace lists at file scope; array sizes taken from the initializer.
- `a84048e` tentative definitions and extern redeclarations merge into one definition.
- `4b8dd49` float and double global initializers (scalars and 1-D brace lists).

All of them match gcc at O2 and O0 on their tests.

### Optimizer and bench (Phase 12.1, 12.4)
- `879f794` tools/bench.forge, the Phase 12.4 tracker (self-compile and bench-vs-native numbers, regression check).
- `67b7f8f` perf(compiler): reused name buffers; self-compile 2354 ms to 1560 ms.
- `ce066fa` step 1, opt-in `--fs-opt`: leaf-function scalars in caller-saved registers, push/pop peephole.
- `e22d2c1` `--fs-opt` divides by literal constants with multiply-and-shift (fsloop 297 ms to 184 ms).
- `79e8282` fix(bench): `bench check` takes each value as its best over the newest 3 records.
- `6e764dd` step 2: functions with calls promote slots into callee-saved registers.
- `8471ed7` step 3: the kernel is built with `--fs-opt` after a SysV audit of every asm routine
  (`smep_probe` fixed; kernel code 534,272 to 495,157 bytes).
- `dd5c0d3` step 3b: the UEFI loader (-8.7%) and kernel modules (-14%) are built with `--fs-opt`.
- `b6d5906` step 3c: the user programs are built with `--fs-opt` (code -4.2%, frame accesses -20%).
- `d0e42b9` f64/f32 globals and arrays take float or integer literals, stored as IEEE bits. Reseed.

### Networking (Phase 8)
- `c969e25` ICMP echo: e1000 and virtio-net ping the QEMU gateway after ARP and check the reply.
- `1fa2883` UDP echo on port 7 (IP and UDP checksums checked, ARP answered), tested through a QEMU hostfwd port.

## 0.17.4 (fixes)

- `4ecde04` fix(kernel): no `hlt` in the post-KERNEL-OK wait, so efi-loader-gpt cannot stall 60 s.
- `f5c66be` fix(build): ESP volume serial and ISO UUID pinned, so two clean builds match.
- `43b821c` fix(kernel): TSC-bound THRE wait in `serial_putc`, so a backed-up serial pipe cannot wedge the kernel.
- `4f84fac` fix(test): QEMU stdio drained before HMP hang dumps; the 60 s stall checked every loop iteration.
- `45fd6a5` fix(kernel): Linux `rt_sigreturn` leaves by iretq and restores rcx and r11, so a resumed fault
  keeps every register.
- `0bbe40c` docs(kernel): verification-scope appendix in kernel-core.md.
- `2aba6e2` chore(unsafe): tests/unsafe baseline ratcheted from 716 to the live 709.

## 0.17.3 (security: VT-d)

- `a1c4c5b` VT-d default deny, every DRHD programmed, and a 65,536-entry context-table audit.
- `643d72e` VT-d fault recording and requester-ID aliasing, tested with edu behind a PCIe-to-PCI bridge.
- `a4dbaa6` a written DMA policy for machines without a remapping unit; strict mode refuses drv 402.
- `3f64450` VT-d interrupt remapping with queued invalidation and source-validated IRTEs.
- `385bdaf` docs(plan): BUILD_PLAN no longer lists the seven closed DMA/IOMMU gaps as open.

## 0.17.2

- `bc37abd` fix(c2forge): static locals and same-name inner-block shadowing are rejected with clear errors.
- `877f206` feat(ffi): PyConfig/PyStatus/PyMethodDef layouts generated from the pinned CPython headers; a
  non-pinned or free-threaded libpython is refused.

## 0.17.1

Base: `5b8e7af`.
