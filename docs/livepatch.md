# Live patching

Sources: `examples/kernel/livepatch.fsi`, the `@patchable` decorator in forgec, and the module `getpid2.ko`.

## Design
`@patchable` emits a 5-byte nop (`0f 1f 44 00 00`) at the function entry, which is 16-byte aligned.
- **Registering.** `lp_register(target)` records the site and an FNV hash of its first 32 bytes. The site table holds up to `LP_MAX` 8 entries (gap 3). Registering the same site twice does nothing, and a 9th site gets `LP_EFULL`.
- **Applying.** `lp_apply(target, newfn)` checks the nop and the hash, then writes `jmp rel32` to the replacement.
  - The write is one aligned 8-byte store through the HHDM alias, so kernel text stays read-only.
  - It runs under stop-machine: the other CPUs are parked with interrupts off, and each serialises with cpuid before it continues.
- **Reverting.** `lp_revert` writes the nop back and checks the hash again.
- **Checked apply.** `lp_apply_checked(target, newfn, check)` tests the replacement before installing it. After installing, it tests the live entry point and rolls back on failure.
- **Errors.** `LP_ENOTPATCHABLE`, `LP_ERANGE`, `LP_EHASH`, `LP_ESELFTEST`, and `LP_EFULL`.
- **Queries.** `lp_find`, `lp_site_hash`, and `lp_npatched`.

The sites are `sys_getpid` and `sys_getppid` (syscall 110, over `kcur_ppid`).

## Testing
The boot test runs hammer threads on 3 CPUs that call both syscalls while 25 rounds patch and revert each site independently.
- The wrong replacements are rejected (rejects 2), and one failed patch is rolled back.
- The text hashes are restored and there are 0 wrong results.
- Markers: `live patching OK`, and `livepatch: ... wrong 0`.

## Known limits
- The quiescence check counts known callers; it is not a stack walk.
- Patches on several sites are applied one site at a time, not in one stop-machine transaction.
