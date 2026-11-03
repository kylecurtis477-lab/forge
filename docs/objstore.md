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
- **Search (9.3)**: `obj_find(t, field, value, start)`, `obj_text_find(term, start)` and `obj_type_first`/`obj_type_next` go through indexes (below); `obj_find_scan` is the linear reference.

Capacities are consts that the includer defines before `include "obj.fsi"` (a missing one is a compile
error): `OB_NT` types, `OB_NO` objects, `OB_NV` versions, `OB_NB` blobs, `OB_NW` blob words, `OB_NL` log words,
`OB_HB` blob-hash slots (power of two, at least 2 x `OB_NB`), `OB_NX` index entries and `OB_HX` index-hash slots
(power of two). The kernel uses 32 / 256 / 2048 / 1024 / 16384 / 32768 / 2048 / 8192 / 16384; tests/fs/objindex.forge
sizes the store for 100k objects. `OB_MAXW` (64 words per payload) is fixed. When a table is full, the call returns `OB_ERR`.

## Indexes (9.3)
All indexes are derived state, maintained in `ob_add_ver`, so writes, rewinds and log replay keep them current.
- **Blob hash**: `ob_bht` maps a payload hash to its blob (linear probing), so dedup is O(1) instead of a scan.
- **By type**: objects of each type are linked in id order (`ob_tfirst`, `ob_tnext`): `obj_type_first(t)`, then
  `obj_type_next(o)` until `OB_ERR`.
- **By field**: one hash index (`ob_hx` buckets, `ix_*` entries) keyed by (type, field, value). A version adds an
  entry for every field whose value differs from the previous version. Lookups check each candidate against the
  object's current version, so entries left behind by later changes are skipped; `obj_find` returns the smallest
  matching id >= `start`, exactly like the scan.
- **Full text**: `ob_type_text(t, f0)` declares fields `f0..` of type `t` as text (8 bytes per word, little-endian,
  zero padded); it is logged as record `OB_RX` `0x4F42580000000005` (type, f0). Terms are runs of ASCII letters,
  digits and `_`, case-folded, hashed with FNV-1a (`ob_term_start`, `ob_term_byte`), and indexed under the pseudo
  field `OB_TXF`. `obj_text_find(h, start)` re-scans each candidate's current text, so it never returns a stale match.
- **Overflow**: if `OB_NX` entries run out, `ob_ixfull` is set and lookups fall back to scanning, so answers stay correct.
- **Latency** (tests/fs/objindex.forge, 100,100 objects, rdtsc cycles per lookup on the build box): indexed field
  lookup about 50, full-text lookup about 21,000 (about 100 candidates rechecked), linear scan about 1,500,000. The
  test requires the index to be at least 50x and full-text at least 20x faster than the scan.

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

## System rewind (9.4)
`objd.fsi` keeps a small "system configuration" as type 2 objects (`OD_NCFG` 8 slots; field 0 = slot,
field 1 = 100 + slot, field 2 = 1 when healthy) and `od_health()` counts broken slots.
- Boot 2 (after crash recovery) creates the slots, notes the last good clock and state fingerprint (`od_sfp`, the
  payload fingerprint without the clock), then commits a bad change durably: every slot broken and object 1
  overwritten. The health check reports 8 broken slots, `sys_rewind(good)` restores all 9 changed objects, and the
  rewind is committed. The state fingerprint must equal the pre-change one; the bad versions stay in history
  (a rewind is a new version). The boot ends by printing `boot done, state fingerprint X`.
- Boot 3 sees type 2 in the replayed store, runs the health check (0 broken) and prints the state fingerprint,
  which must equal boot 2's `boot done` fingerprint.

## Testing
- `tests/fs/objstore.forge` covers versions, dedup, rewind, diff and find.
- `tests/fs/objindex.forge` builds 100k objects and checks field, type and text lookups against the scan,
  across an update, a rewind and a log replay, plus the latency ratios.
- `tests/fs/objlog.forge` replays every possible cut and every single-bit flip, then checks that writes after
  recovery work.
- `os/objstore-system-rewind` and `os/objstore-rewind-persists` (tests/os/forgefs.sh, boots 2 and 3) cover 9.4.
- `os/objstore-crash-recover` runs two QEMU boots:
  - Boot 1 makes 62 durable commits, prints a fingerprint, and then writes a torn record.
  - Boot 2 recovers, drops the torn tail, verifies all 50 versions of object 0, and must print the same fingerprint.

## Known limits
- The tables are fixed-size and in memory, and the log holds at most `OD_NB` blocks. There's no compaction or checkpoint yet.
- The store is single-writer and has no locking. In the kernel it runs from the boot thread only.
- Index entries are never removed (stale ones are filtered at lookup), so heavy rewriting of indexed fields grows
  the index until `OB_NX`, after which lookups scan. A text hash collision can only return a false match if two
  terms share a 64-bit FNV hash.
