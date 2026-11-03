# Private by design (Phase 10)

## 10.1 Per-user keys, objects encrypted at rest
Everything the object store puts on disk (`objects.log`, so every object, version and blob, including objfs
file names and contents) is encrypted and authenticated. The code is in `examples/kernel/crypt.fsi`
(primitives) and `examples/kernel/objd.fsi` (keys and the on-disk format). Both are safe Forge: no new
`@unsafe` code.

### Primitives (crypt.fsi)
- ChaCha20 block function (RFC 8439): `cc_key` (8 x 32-bit words), `cc_block(ctr, n0, n1, n2)`, `cc_word(i)`.
- SHA-256 (FIPS 180-4): `sh_init`, `sh_byte`, `sh_u64`, `sh_fin` (result in `sh_h`).
- HMAC-SHA256 (RFC 2104): `hm_setkey32` (or `hm_setkey_digest` for a hashed long key), `hm_begin`, feed the
  message with `sh_byte`/`sh_u64`, `hm_end` (MAC in `sh_h`). This is the real two-pass HMAC with the 0x36/0x5c
  padded key blocks, not SHA-256(key || message), which would be open to length extension. The first
  10.1 commit (2473aed) used that prefix-keyed form with 64-bit tags; it was replaced before 10.2.
- `ct_eq4(a, b)`: constant-time 256-bit compare (ORs the XOR of every word, then one branch-free reduction),
  used for every tag and the key check value.
- Known-answer tests, tests/fs/crypt.forge, at O2 and O0, every output word compared: RFC 8439 2.3.2
  (block function, all 16 words) and 2.4.2 (the 114-byte "Ladies and Gentlemen" encryption); SHA-256 of
  "abc", the 448-bit two-block FIPS message, "" and 1M x "a"; RFC 4231 HMAC-SHA256 cases 1, 2 and 6 (131-byte
  key, hashed first); `ct_eq4` equal and one-bit-different.

### Keys
- **User key (KEK).** The platform key (`od_tpm_present` is a placeholder for TPM sealing and always
  returns 0; see Limits): SHA-256("frge-kek" || passphrase), where the passphrase (8..64 bytes) arrives as the QEMU fw_cfg file
  `opt/forge/userkey` (I/O ports 0x510/0x511, read through the `pio_add`/`pio_rd`/`pio_wr` safe wrappers).
  The user key itself is never written anywhere.
- **Data key (DK).** Made at first mount from 512 TSC jitter samples hashed with SHA-256. It is stored only
  wrapped: `objects.key` (one Forge FS block) holds magic `FORGEKEY`, uid 1000, a 128-bit salt,
  DK XOR HMAC(KEK, "wrap" || salt) and a 256-bit check value HMAC(KEK, "chk" || salt), compared in constant time.
- **Working keys, separate for each job.** K_enc = HMAC(DK, "enc") is the ChaCha20 key and K_mac = HMAC(DK, "mac")
  the HMAC key; neither is stored, and they are independent PRF outputs, so the cipher and the MAC never share a key.
- **No key** (no TPM and no fw_cfg file): the store stays locked. Nothing is read, created or written, and
  the boot continues. **Wrong key** (the check value doesn't match): the mount is refused the same way, and the
  on-disk bytes are left untouched.

### On-disk format
- `objects.log` has `OD_NB` = 65 logical blocks (507 payload words each, enough for `OB_NL` = 32768 log words).
  Each logical block has two physical 4 KiB slots, A and B, so the file is 130 blocks.
- A slot holds words 0..506 of ciphertext, word 507 the write counter `wc`, and words 508..511 the full
  256-bit tag HMAC(K_mac, block || wc || ciphertext): **encrypt-then-MAC**. A slot is decrypted only after its
  tag verifies, so unauthenticated ciphertext is never processed.
- ChaCha20 nonce = (wc low 32 bits, wc high 32 bits, logical block); the ChaCha block counter runs over the 64
  keystream blocks of a slot. Every slot write takes a fresh `wc`, so a (K_enc, nonce) pair is never reused.
- **Counters are never reused**, including across reboots, torn writes, rewinds and tampering:
  - `objects.ctr` (two slots, each HMAC-tagged) holds a high-water mark. Before any counter above it is used,
    a new mark `wc + 4096` is written to the other slot and flushed; at mount the higher verified slot wins.
  - At mount `wc` resumes from the maximum of that mark and every counter word found in any log slot,
    verified or not, so even a torn or tampered slot's counter is skipped.
  - A rewind (Phase 9.4) or an objfs rewrite appends new log records, which go out in new slot writes with
    new counters; nothing is ever rewritten under an old counter.
- A write goes to the slot that is *not* current, so a torn write can never destroy the previous contents.
  At mount, each block uses the slot with a valid tag and the higher `wc`. If neither slot verifies, the block
  reads as zeros, and the log replay then truncates there exactly as for a torn tail (Phase 9.2).

### Threat model
Protected against: someone who gets the disk (or the image) while Forge is not running and does not know the
user key. They learn no object contents, file names inside the object store, or key material, and any change
they make to a log slot, the counter file or the key slot is detected (HMAC / check value) instead of being
decrypted or trusted: a changed slot is treated like a torn one, a changed key slot locks the store.
Not protected against:
- **Rollback.** Each slot is authenticated on its own, so an attacker can put back an older copy of the
  whole image, or destroy the newer slot of a block so the older authentic one is used. Detecting that needs
  a monotonic counter outside the disk (a TPM NV counter), which the test setup does not have.
- **Weak passphrases.** The platform key is one SHA-256 of the passphrase, with no salt or work factor, so a
  stolen disk allows offline guessing; a TPM-sealed key or a memory-hard KDF would fix this.
- **A running, compromised kernel**, physical memory attacks (keys live in kernel memory while mounted), and
  side channels other than tag comparison timing.
- **Denial of service.** Corrupting the disk can always make data unreadable; it cannot make forged data be accepted.

### Tests (tests/os/forgefs.sh, QEMU)
- **os/objstore-key-wrap:** boot 1 creates and wraps the data key; boot 3 unwraps it with the platform key.
- **os/objstore-no-plaintext:** the Forge partition contains no object plaintext (`motd\0`, `notes\0`),
  and not the passphrase either. The written log slots do not compress with zlib.
- **os/objstore-wrong-key-locked:** a boot with a wrong passphrase and a boot with no key both report the
  store locked, and `objects.log`/`objects.key` are byte-identical afterwards.
- **os/objstore-nonce-unique:** every written slot in the image has a distinct counter, and the durable
  high-water mark in `objects.ctr` is at least the highest of them.
- **os/objstore-tamper-rejected:** boot 6 runs on a copy of the image with one bit of the newest slot's
  ciphertext flipped; the HMAC rejects exactly one more slot than on the clean boot.
- The Phase 9 crash-recovery, rewind and /obj tests all run on the encrypted store.

### Limits
- **TPM sealing is not implemented.** No TPM device exists in the test setup (no swtpm), so `od_tpm_present`
  returns 0 and the fw_cfg platform key is used. Without a TPM, the passphrase protects the data.
- **No swap.** Forge has no swap, so there are no swap pages to encrypt.
- **One user per store.** Each store has one user key (uid 1000); there are no separate per-user stores yet.
- **Plaintext metadata.** Forge FS metadata (file names, sizes, extents, and the plaintext `hello.txt`/`persist.dat`
  Forge FS self-test files) is not encrypted. Only the object store is.
- **Weak DK entropy.** TSC jitter at boot is a weak entropy source; RDRAND/RDSEED are not used yet.
- **No rotation.** Neither the passphrase nor the data key can be rotated yet.
- **Rollback and weak-passphrase guessing** are possible, see the threat model.
- **Key wrap.** The wrapped DK is XORed with a PRF output and checked through the KEK check value, not a
  standard AEAD key wrap; tampering with it only makes every tag fail (the store reads as empty / locked).
