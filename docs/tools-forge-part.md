# forge-part (tools/forge-part.forge)

Forge port of `tools/forge-part.py`. The images, stdout, stderr and exit codes are byte-identical (tests/forge-part/run.sh).

    forge-part create DISK SIZE_MIB --yes-add-forge-partition
    forge-part read DISK NAME          (print a Forge FS file to stdout)

`create` adds one partition with the Forge type GUID D0C2E3A1-6B7F-4F0A-9E11-466F72676546 in the largest free gap
(start aligned to 1 MiB). It rewrites only the primary and backup GPT headers and entry arrays, and zeroes the first
4 KiB of the new partition so the kernel will format it. It refuses to run without the flag, on a disk without a
valid GPT (bad signature, header CRC or entry CRC), when a Forge partition already exists, when no GPT entry is free,
or when no gap is large enough. `read` looks up NAME in the 64 64-byte entries after the FORGEFS1 superblock and
writes the file's bytes to stdout.

The unique partition GUID is random (uuid4 / getrandom). With `FORGE_GUID_SEED=N` it becomes
bytes_le = pack('<QQ', N, 0) in both the Python and Forge versions. That setting is for tests only.

Implementation: the image is read whole, patched in memory and written back. This works for the 64-128 MiB test
disks; a seek/pwrite version can follow once the runtime has those calls.

Used by tests/os/forgefs.sh, which builds it with build/forgec, build/fas and build/fld.
