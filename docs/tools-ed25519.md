# ed25519 (tools/ed25519.forge)

This is the Forge port of the signing side of `tools/ed25519.py` (RFC 8032 Ed25519).

    ed25519 public SEEDFILE          32-byte public key on stdout   (= ed25519.public(seed))
    ed25519 sign SEEDFILE MSGFILE    64-byte signature on stdout    (= ed25519.sign(seed, msg))

- The seed file is hashed as-is, so any length works, as in the Python version. A standard key is 32 bytes.
- The message file is streamed twice: once for r and once for the challenge. Its size has no limit.
- Exit codes: 2 with a usage line on stderr for bad arguments, 1 when a file can't be read.

## How it works

It's a freestanding (`# forge: no-runtime`) Linux program with its own `_start` and raw syscalls. It includes
`examples/kernel/ed25519.fsi`, the kernel's verifier (a TweetNaCl port), and reuses that file's SHA-512,
GF(2^255-19) and point code. On top of that it adds:

- key clamping
- `sc_muladd`, which computes S = (r + h·a) mod L

The only `@unsafe` code reads argv and makes the syscalls.

## Status and tests

`tools/ed25519.py` stays as the reference. The keypair step (`ed_key`) and `sc_muladd` live in `tools/ed25519sign.fsi`,
shared with `tools/forgeseal.forge`, which signs seal trailers with the same code.

tests/ed25519-tool/run.sh compares the output byte for byte with the Python version on 26 vectors and checks the
RFC 8032 known answers. tests/ed25519 still checks the kernel verifier against `cryptography`.
