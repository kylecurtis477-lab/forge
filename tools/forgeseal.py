#!/usr/bin/env python3
"""Phase 6.1 proof manifest: seal a Forge user ELF with a verifier-produced trailer.

usage: forgeseal.py [--module] SRC.forge IMAGE KEYFILE BASELINE
       forgeseal.py --foreign IMAGE KEYFILE   (11.1e: a foreign Linux-ABI program, e.g. BusyBox)

Runs `forgec --unsafe-report` on SRC (the verifier: type/unsafe checking happens there) and refuses
to seal unless the unsafe counts are zero or within the audited entry in BASELINE. Declared
capabilities come from a `# forge: caps(a, b, ...)` line in SRC. KEYFILE is the 32-byte Ed25519
private seed (build-only; the kernel embeds just the public key). Appends a 160-byte trailer:
  +0  "FORGEPRF"   +8 version (2)   +16 safety level   +24 unsafe ops (whole program)
  +32 caps mask    +40 ELF length   +48 main-file unsafe ops   +56 kind (0 user program, 1 kernel module: --module)
  +64 SHA-256(ELF bytes)            +96 Ed25519 signature (R || S) over trailer[0:96]
--foreign: level 3, no unsafe counts (the Forge verifier never saw the program), caps 0; refused
unless the ELF has EI_OSABI = 3, so the kernel runs it only in the compat sandbox (it re-checks both).
Safety level: 2 = no unsafe code at all in the program's own file (only the audited ulib),
1 = unsafe counts match the audited baseline. 0 is reserved for "no unsafe anywhere"."""
import hashlib, os, re, struct, subprocess, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ed25519

CAPS = {'fs': 1, 'spawn': 2, 'ipc': 4, 'drv': 8, 'kexec': 16, 'probe': 32, 'ktest': 64, 'grant': 128}
MAGIC = b'FORGEPRF'

def foreign(img, keyf):
    elf = open(img, 'rb').read()
    if elf[:4] != b'\x7fELF' or elf[7] != 3:
        sys.exit(f'forgeseal: {img}: --foreign needs a Linux ABI ELF (EI_OSABI = 3)')
    if elf[-160:-152] == MAGIC:
        sys.exit(f'forgeseal: {img}: already sealed')
    t = MAGIC + struct.pack('<7Q', 2, 3, 0, 0, len(elf), 0, 0) + hashlib.sha256(elf).digest()
    t += ed25519.sign(open(keyf, 'rb').read(), t)
    open(img, 'wb').write(elf + t)
    print(f'sealed {os.path.basename(img)}: level 3 (foreign), caps 0x0')

def main():
    kind = 0
    if sys.argv[1] == '--module':
        kind = 1
        del sys.argv[1]
    src, img, keyf, base = sys.argv[1:5]
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    fc = os.path.join(root, 'build/forgec')
    d, f = os.path.dirname(os.path.abspath(src)), os.path.basename(src)
    r = subprocess.run([fc, f, '/dev/null', '--unsafe-report'], cwd=d, capture_output=True, text=True)
    if r.returncode != 0:
        sys.exit(f'forgeseal: {src}: verifier rejected the program:\n{r.stderr}')
    lines = (r.stdout + r.stderr).splitlines()
    m = [re.match(r'unsafe: (\d+) functions, (\d+) blocks, (\d+) ops, (\d+) top-level', l) for l in lines]
    m = [x for x in m if x]
    if not m:
        sys.exit(f'forgeseal: {src}: no unsafe report')
    cnt = tuple(int(x) for x in m[-1].groups())
    own = sum(int(x.group(1)) for x in (re.match(rf'unsafe: \S*{re.escape(f)}:\d+ .*ops=(\d+)', l) for l in lines) if x)
    rel = os.path.relpath(d, root)
    audited = None
    for l in open(base):
        p = l.split()
        if len(p) == 6 and p[0] == rel and p[1] == f:
            audited = tuple(int(x) for x in p[2:])
    if cnt == (0, 0, 0, 0):
        level = 0
    elif audited and all(a <= b for a, b in zip(cnt, audited)):
        level = 2 if own == 0 else 1
    else:
        sys.exit(f'forgeseal: {src}: unsafe counts {cnt} are not in the audited baseline; refusing to seal')
    caps = 0
    for l in open(src):
        mm = re.match(r'#\s*forge:\s*caps\((.*)\)', l)
        if mm:
            for c in filter(None, (x.strip() for x in mm.group(1).split(','))):
                if c not in CAPS:
                    sys.exit(f'forgeseal: {src}: unknown capability {c!r}')
                caps |= CAPS[c]
    elf = open(img, 'rb').read()
    if elf[-160:-152] == MAGIC:
        sys.exit(f'forgeseal: {img}: already sealed')
    key = open(keyf, 'rb').read()
    t = MAGIC + struct.pack('<7Q', 2, level, cnt[2], caps, len(elf), own, kind) + hashlib.sha256(elf).digest()
    t += ed25519.sign(key, t)
    assert len(t) == 160
    open(img, 'wb').write(elf + t)
    print(f'sealed {os.path.basename(img)}: level {level}, {cnt[2]} unsafe ops, caps 0x{caps:x}')

if len(sys.argv) == 3 and sys.argv[1] == '--pub':
    sys.stdout.buffer.write(ed25519.public(open(sys.argv[2], 'rb').read()))
elif len(sys.argv) == 4 and sys.argv[1] == '--foreign':
    foreign(sys.argv[2], sys.argv[3])
else:
    main()
