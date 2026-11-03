# Private by design (Phase 10)

## 10.1 Per-user keys, objects encrypted at rest
Everything the object store puts on disk (`objects.log`, so every object, version and blob, including objfs
file names and contents) is encrypted and authenticated. The code is in `examples/kernel/crypt.fsi`
(primitives) and `examples/kernel/objd.fsi` (keys and the on-disk format). Both are safe Forge: no new
`@unsafe` code.

### Primitives (crypt.fsi)
- ChaCha20 block function (RFC 8439): `cc_key` (8 x 32-bit words), `cc_block(ctr, n0, n1, n2)`, `cc_word(i)`.
- SHA-256: `sh_init`, `sh_byte`, `sh_u64`, `sh_key`, `sh_fin` (result in `sh_h`).
- Known-answer tests: tests/fs/crypt.forge (RFC 8439 2.3.2 block, SHA-256 of "abc", "" and 1M x "a"), at O2 and O0.

### Keys
- **User key (KEK).** Taken from the TPM when one is present (`od_tpm_present`). Otherwise it is the platform
  key: SHA-256("frge-kek" || passphrase), where the passphrase (8..64 bytes) arrives as the QEMU fw_cfg file
  `opt/forge/userkey` (I/O ports 0x510/0x511, read through the `pio_add`/`pio_rd`/`pio_wr` safe wrappers).
  The user key itself is never written anywhere.
- **Data key (DK).** Made at first mount from 512 TSC jitter samples hashed with SHA-256. It is stored only
  wrapped: `objects.key` (one Forge FS block) holds magic `FORGEKEY`, uid 1000, a 128-bit salt,
  DK XOR SHA-256(KEK || "wrap" || salt) and a check word SHA-256(KEK || "chk" || salt).
- **Working keys.** K_enc = SHA-256(DK || "enc") is the ChaCha20 key, and K_mac = SHA-256(DK || "mac") keys the tags.
- **No key** (no TPM and no fw_cfg file): the store stays locked. Nothing is read, created or written, and
  the boot continues. **Wrong key** (the check word doesn't match): the mount is refused the same way, and the
  on-disk bytes are left untouched.

### On-disk format
- `objects.log` has `OD_NB` = 65 logical blocks (510 payload words each, enough for `OB_NL` = 32768 log words).
  Each logical block has two physical 4 KiB slots, A and B, so the file is 130 blocks.
- A slot holds words 0..509 of ciphertext, word 510 the write counter `wc`, and word 511 the tag: the first
  64 bits of SHA-256(K_mac || block || wc || ciphertext).
- ChaCha20 nonce = (wc low 32 bits, wc high 32 bits, logical block); the block counter runs over the 64
  ChaCha blocks of a slot. `wc` increases with every slot write and resumes from the highest value found at
  mount, so a (key, nonce) pair is never reused.
- A write goes to the slot that is *not* current, so a torn write can never destroy the previous contents.
  At mount, each block uses the slot with a valid tag and the higher `wc`. If neither slot verifies, the block
  reads as zeros, and the log replay then truncates there exactly as for a torn tail (Phase 9.2).

### Tests (tests/os/forgefs.sh, QEMU)
- **os/objstore-key-wrap:** boot 1 creates and wraps the data key; boot 3 unwraps it with the platform key.
- **os/objstore-no-plaintext:** the Forge partition contains no object plaintext (`motd\0`, `notes\0`),
  and not the passphrase either. The written log slots do not compress with zlib.
- **os/objstore-wrong-key-locked:** a boot with a wrong passphrase and a boot with no key both report the
  store locked, and `objects.log`/`objects.key` are byte-identical afterwards.
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
- **64-bit tags.** Tags are truncated to 64 bits.
