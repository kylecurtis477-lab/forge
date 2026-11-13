# ostools (tools/ostools.forge)

Checks the OS-test toolchain before a gate, and reinstalls it if a box reset removed it.

    ostools [--check] [--force]

The toolchain is QEMU, GRUB (BIOS and EFI), xorriso, mtools and OVMF. The tool checks for:

- the commands `qemu-system-x86_64`, `grub-file`, `grub-mkrescue`, `xorriso` and `mformat`;
- `/usr/share/OVMF/OVMF_CODE_4M.fd`, `/usr/lib/grub/i386-pc` and `/usr/lib/grub/x86_64-efi`.

| Invocation | Behaviour |
|---|---|
| `ostools --check` | Reports what is missing; exits 1 if anything is. |
| `ostools` | Does nothing if everything is present. Otherwise it reinstalls the Debian packages (`qemu-system-x86`, `grub-pc-bin`, `grub-efi-amd64-bin`, `grub-common`, `mtools`, `xorriso`, `ovmf`) and checks again. If anything is still missing it prints `PREFLIGHT FAIL: still missing ...` and exits 1. |
| `ostools --force` | Reinstalls even when everything is present. |

How the restore works:

- apt's own downloads fail through the box's network egress, but curl works.
- So the tool fetches the signed trixie `InRelease` and `Packages.xz` with curl and puts them in apt's lists. apt checks the index signature.
- `apt-get install --print-uris` names the .debs. curl fetches them into `/tmp/forge-ostools/debs`, and `apt-get install --no-download` installs them from the cache. apt checks every package's hash against the signed index.
- It needs sudo.

## Why

At about 04:20 CST on 2026-11-13 the box was reset. Untracked `build/` folders and the apt-installed QEMU/GRUB
packages were gone. The next gate failed `os-artifacts-build` on a missing `grub-file`, which is a setup failure
and not a code failure.

## Where it runs

The gate harness (`/workspace/forge-harness/inv.sh`, outside the repo) builds this tool right after `./build.sh`
and runs it before any test. It logs to `forge-logs/preflightN.log` and prints `PREFLIGHT EXIT=N`. A failed
preflight stops the gate with `PREFLIGHT FAIL` instead of producing a wall of OS-test failures. Trees older than
this tool fall back to the harness's `restore-tools.sh`.

## Tested

The tool was checked on the box in four ways:

- `--check` with everything present.
- `--force` with everything present: it fetched 0 packages, as expected.
- With `mtools` and `xorriso` removed: `--check` listed `xorriso mformat` and exited 1. A plain run fetched 2
  packages, reinstalled them, and reported "restored, all present".
- The harness block itself, in both the new path and the old-tree fallback.
