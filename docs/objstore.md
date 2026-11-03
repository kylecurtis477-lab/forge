# The living object store (Phase 9)

Sources: `examples/kernel/obj.fsi` (the store core and write-ahead log, in fully safe Forge and shared by the
kernel and hosted tests) and `examples/kernel/objd.fsi` (persistence on the Forge partition, kernel only).
Tests: `tests/fs/objstore.forge`, `tests/fs/objlog.forge`, and `os/objstore-crash-recover` in `tests/os/forgefs.sh`.

## Model
- **Types**: `ob_type_def(t, nfields)` fixes the field count of type `t`, with `t` below `OB_NT`.
- **Objects**: `obj_new(t)` returns an id. `obj_write(o, n)` takes `n` payload words staged in `ob_in` and
  adds a new immutable version. It rejects a payload of the wrong shape.
- **Versions**: each version has a logical timestamp `ov_ts`, taken from `ob_clock`, and points back to the previous version.
  Old versions are never modified (copy-on-write).
- **Blobs**: payloads are content-addressed (FNV-1a with a word-by-word compare), so identical payloads share
  one blob. `ob_dedup` counts the shared ones.
- **Time travel**:
  - `obj_read_at(o, ts)` and `obj_read(o)` put the payload in `ob_out`; `obj_field(o, f)` reads one field.
  - `obj_rewind(o, ts)` and `sys_rewind(ts)` write the old payload back as a new version, so history is never lost.
  - `obj_diff(o, t1, t2)` compares an object at two times, and `obj_nversions(o)` counts its versions.
- **Search**: `obj_find(t, field, value, start)` is a linear scan for now; Phase 9.3 adds indexes.

Capacities are `OB_NT` 32 types, `OB_NO` 256 objects, `OB_NV` 2048 versions, `OB_NB` 1024 blobs,
`OB_NW` 16384 blob words, and `OB_MAXW` 64 words per payload. When a table is full, the call returns `OB_ERR`.

## Write-ahead log (9.2)
Every mutation appends one record to `ob_log`, which holds up to `OB_NL` 32768 words. A record is a tag word, its fields, and then the
FNV of the record:

| Tag | Fields |
|---|---|
| `OB_RT` `0x4F42540000000001` | type `t`, `nfields` |
| `OB_RO` `0x4F424F0000000002` | object id, type |
| `OB_RB` `0x4F42420000000003` | `n`, then `n` blob words |
| `OB_RV` `0x4F42560000000004` | object, blob, timestamp |

Records only refer to earlier ones, so any prefix of the log is a consistent store.
`ob_replay(len)` rebuilds the tables. It stops at the first record that is torn, has a bad checksum, or
arrives out of order, and truncates the log there.

## On disk (9.2b)
`objd.fsi` keeps the log in a single Forge FS file, `objects.log`. The file is `OD_NB` 64 blocks, reserved and zero-filled
up front. It lives on the explicitly created Forge-owned partition (see the Forge FS) and is never written anywhere else.
- `od_open()` replays an existing log. If the log ends in a torn tail, it zeroes the tail both in memory and on disk, so later appends
  can't revive stale records. If there's no log, it creates one.
- `od_commit(o)` writes every log block that changed since the last sync, then issues an NVMe flush
  before returning. Once a commit has returned, it is durable.

## Testing
- `tests/fs/objstore.forge` covers versions, dedup, rewind, diff and find.
- `tests/fs/objlog.forge` replays every possible cut and every single-bit flip, then checks that writes after
  recovery work.
- `os/objstore-crash-recover` runs two QEMU boots:
  - Boot 1 makes 62 durable commits, prints a fingerprint, and then writes a torn record.
  - Boot 2 recovers, drops the torn tail, verifies all 50 versions of object 0, and must print the same fingerprint.

## Known limits
- The tables are fixed-size and in memory, and the log holds at most `OD_NB` blocks. There's no compaction or checkpoint yet.
- The store is single-writer and has no locking. In the kernel it runs from the boot thread only.
- Search is linear until 9.3.
