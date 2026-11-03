# Supervised ring-3 driver services (no-restart gaps 1, 2 and 4)

The kernel keeps device state and journals. Decoding and command building run in ring-3 drivers.
A driver can crash or be hot-swapped for a different implementation without losing input or I/O, and its state survives kexec.

## Input: kbdd (gap 1)
Sources: `examples/kernel/kbd.fsi`, `examples/user/kbdd.forge` (v1), and `examples/user/kbdd2.forge` (v2).
- **Kernel side.** The PS/2 and COM1 IRQ handlers only append raw bytes to a 256-entry journal. Each entry is `kind<<8 | byte`, where kind 0 is a PS/2 scan code and kind 1 is a serial byte.
- **Driver side.** The driver (DRV_KBD, caps(drv), no root fd) decodes scan-code set 1, shift and E0 prefixes, and CR→LF.
- **Syscalls.** All three need CAPB_DRV and only work for the registered pid.
  - 411 recv: takes a batch of up to 32 entries. A return of 0 means quiesce.
  - 412 commit: takes the end seq, the decoded chars, and the decoder state word. It applies them to the tty in one step under rq_lock.
  - 413 get-state.
- **State format.** v1 and v2 share one state word. v1 uses one shift bit; v2 uses left and right shift in bits 0 and 2.
- **Recovery.** Entries that weren't committed stay in the journal, and the next driver replays them.
  - If a driver crashes (exit ≠ 0), the supervisor restarts it, up to 5 times. After that, the kernel decodes the input itself.
  - A swapper asks the running driver to quiesce every 10 ticks and then starts the other version.
- **Crash hook.** `crash k N` (syscall 408 b=1) arms a fault on the batch that holds the N-th entry. It is gated by ktest_hooks.

## NVMe: nvmed (gap 2)
Sources: `examples/kernel/nvsvc.fsi`, `examples/user/nvmed.forge`, and `examples/user/nvmed2.forge`.
- **Kernel side.** `nvme_read`, `nvme_write` and `nvme_flush` post requests through `nv_io` and `nvs_call`.
- **Syscalls.** All need CAPB_DRV and only work for the registered pid.
  - 414 recv: takes a request. A return of 0 means quiesce.
  - 415 submit: the driver's SQE has to match the posted request exactly (opcode, namespace 1, the kernel's DMA buffer, LBA, and block count). Writes are allowed only inside the claimed Forge partition. Anything else is refused and counted.
  - 416 reap.
  - 417 reply: returns the status and a state word (a magic value in bits 63:32, plus the served count).
  - 418 get-state.
- **Recovery.** If a driver dies, the kernel drains its in-flight completion and replays the request to the next driver.
- **No DMA access.** There is no IOMMU, so a driver never gets the BAR or a DMA address.

## Driver state across kexec (gap 4)
With the other CPUs halted and input IRQs off, `kxd_save` writes a driver-state section into the handoff header page, past KXH_BYTES.
- The section has its own magic and hash, so handoff format v3 doesn't change.
- It holds the driver kind and version, the decoder state, the uncommitted journal entries, and the service counters.
- In the new kernel, `kxd_adopt` validates the section. Then `kbd_service_start_from(1)` starts the same driver version on the carried state.

## Testing
`tests/os/run.sh` covers all three boot paths and checks these markers:
- `input service hot-swap + supervised restart OK`
- `supervised driver restart OK`
- `driver state carried across kexec OK`
- `lost 0`

`tests/os/forgefs.sh` checks the nvmed restart, replay, swaps, and the refused forged SQEs.

## Known limits
- The echo path and the line discipline stay in the kernel.
- The journal holds 256 entries; an overflow counts as dropped.
- The NVMe admin queue, interrupts, and queue memory stay in the kernel.
- Only the input service carries state across kexec. Driver processes are restarted, not carried.
