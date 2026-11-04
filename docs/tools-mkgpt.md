# mkgpt (examples/efi/mkgpt.forge)

Forge port of `examples/efi/mkgpt.py`. With the same `FORGE_GUID_SEED`, the two write byte-identical images (tests/mkgpt/run.sh).

    mkgpt out.img esp.fat [size_mib]      (default 128)

It writes a protective MBR, the primary and backup GPT, and three partitions starting at LBA 2048, each aligned to
1 MiB:

1. EFI System Partition, filled with esp.fat
2. Microsoft basic data, 16 MiB, empty
3. Linux filesystem `linux-root`, 16 MiB, empty

It's used by examples/efi/build.sh to test that the Forge loader and kernel boot from a shared ESP.

GUIDs are random (uuid4 / getrandom). With `FORGE_GUID_SEED=N` (0..2^64-1, tests only), GUID k is
bytes_le = pack('<QQ', N, k), where k = 0..2 are the partitions and k = 3 is the disk.

If the partitions don't fit, it exits 1 (the Python version fails an assert).
The image is built front to back in memory and written in one go.
