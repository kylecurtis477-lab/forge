<!-- CHANGELOG.md: release notes for Forge from 0.17.1 (5b8e7af) on. Each entry names its commit; the
     version-to-commit mapping comes from the project plan. There are no git tags: the versions are
     groupings of commits, not contiguous ranges of history (for example the 0.17.4 fixes 0bbe40c, 4ecde04,
     f5c66be and 2aba6e2 come before the 0.17.2 commits in the log). Newest version first. -->
# Changelog

## Unreleased (0.18.1)

- `01beb01` fix(test): the boot harness checks the framebuffer only after two consecutive screendumps
  decode identically (inv128 O0 efi-loader-gpt had caught a scroll redraw mid-row).
- `be09366` feat(fs): Phase 12.1 step 4a, `--fs-opt` promotes scalar locals in safe functions that also
  take frame addresses (arrays, structs, `addr()`); kernel frame accesses -4.0%. Reseed.
- `7a6d513` feat(xhci): Phase 8 USB HID step 1, the HID boot keyboard interface and its interrupt IN
  endpoint are found and configured (SET_CONFIGURATION, SET_PROTOCOL(boot), SET_IDLE(0)).
- `6091098` feat(bench): record integrity, `bench run` records the full HEAD sha and refuses an uncommitted
  tree (`--allow-dirty` marks `+dirty`); `bench tree`; orphan records annotated.
- `e72e5d3` feat(tools): fsfuzz, a differential fuzzer for `--fs-opt` (O2, -O0, `--fs-opt`, -O0 `--fs-opt`
  must agree).
- `6dd80b0` docs(systems): the `--fs-opt` asm audit as a checked list, kept complete by tests/asm-audit.

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
