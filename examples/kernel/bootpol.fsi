# bootpol.fsi: boot-time DMA policy switch (Phase 8, A.2 gap 7 follow-up).
# iommu_strict (iommu.fsi) decides whether drv 402 DMA grants are refused on a machine without an active
# remapping unit. Three inputs, applied in this order by boot_policy (called from fs_test once the ESP is
# mounted, before any user-mode driver starts):
#   1. the build default, IOMMU_STRICT_DEFAULT (unchanged: permissive);
#   2. \EFI\Forge\boot.cfg, only when it carries a valid kind-2 seal (forgeseal --config, checked by
#      image_verify with SEAL_CONFIG): `iommu=strict` or `iommu=permissive` may set the policy either way.
#      An unsealed, tampered or malformed config is refused whole and logged; the policy stays as it was;
#   3. the boot command line (bi.cmdline: the multiboot2 tag, kept across kexec): `forge.iommu=strict`
#      tightens; `forge.iommu=permissive` is refused (loosening needs the sealed config), so an unsigned
#      line can never weaken the policy.
# pol_resolve is the pure rule; pol_selftest checks its whole truth table, including a strict build default.
# Safe code only: file bytes are read through le16 (fat.fsi), seal checks through image_verify.
PCFG_NONE = 0
PCFG_PERMISSIVE = 1
PCFG_STRICT = 2
PCMD_NONE = 0
PCMD_PERMISSIVE = 1
PCMD_STRICT = 2
POL_CFG_MAX = 4096  # longest boot.cfg body accepted

def pol_resolve(dflt: u64, cfg: u64, cmd: u64) -> u64:
# returns 1 for strict, 0 for permissive
    r = dflt
    if cfg == PCFG_PERMISSIVE:
        r = 0
    elif cfg == PCFG_STRICT:
        r = 1
    if cmd == PCMD_STRICT:
        r = 1
    return r

def pol_selftest():
# all 18 (default, config, command line) combinations; bit n of WANT is the expected result of row n
# (n = 9 * default + 3 * config + cmd). Rows 9-11: a strict default stays strict whatever the command
# line says; row 12: only a sealed iommu=permissive loosens it.
    want: u64 = 0x3cfe4
    n: u64 = 0
    d: u64 = 0
    while d < 2:
        c: u64 = 0
        while c < 3:
            m: u64 = 0
            while m < 3:
                if pol_resolve(d, c, m) != ((want >> n) & 1):
                    panic("boot policy: resolver truth table")
                n += 1
                m += 1
            c += 1
        d += 1

def cmd_tok_eq(s: u64, n: u64, lit: ptr[u8]) -> u64:
# bi.cmdline[s..s+n) equals the NUL-terminated literal
    i: u64 = 0
    while i < n:
        if ffs_ch(lit, i) == 0 or u64(bi.cmdline[s + i]) != ffs_ch(lit, i):
            return 0
        i += 1
    return ffs_ch(lit, n) == 0

def cmd_tok_pre(s: u64, n: u64, lit: ptr[u8]) -> u64:
# the token bi.cmdline[s..s+n) starts with the literal
    i: u64 = 0
    while ffs_ch(lit, i) != 0:
        if i >= n or u64(bi.cmdline[s + i]) != ffs_ch(lit, i):
            return 0
        i += 1
    return 1

def pol_cmdline() -> u64:
# PCMD_* from bi.cmdline; the last forge.iommu= token wins; other tokens are ignored
    r = PCMD_NONE
    i: u64 = 0
    while i < 255 and bi.cmdline[i] != 0:
        while i < 255 and bi.cmdline[i] == 32:
            i += 1
        s = i
        while i < 255 and bi.cmdline[i] != 0 and bi.cmdline[i] != 32:
            i += 1
        n = i - s
        if n > 0 and cmd_tok_pre(s, n, "forge.iommu="):
            if cmd_tok_eq(s, n, "forge.iommu=strict"):
                r = PCMD_STRICT
            elif cmd_tok_eq(s, n, "forge.iommu=permissive"):
                r = PCMD_PERMISSIVE
            else:
                puts("boot policy: unknown forge.iommu= value on the command line, ignored\n")
    return r

def cfg_line_eq(buf: u64, s: u64, n: u64, lit: ptr[u8]) -> u64:
    i: u64 = 0
    while i < n:
        if ffs_ch(lit, i) == 0 or (le16(buf + s + i) & 0xFF) != ffs_ch(lit, i):
            return 0
        i += 1
    return ffs_ch(lit, n) == 0

def pol_config() -> u64:
# PCFG_* from a sealed \EFI\Forge\boot.cfg; PCFG_NONE when absent or refused (logged either way)
    sz: u64 = 0
    buf = fs_read_file("EFI/Forge/boot.cfg", addr(sz))
    if buf == 0:
        puts("boot policy: no \\EFI\\Forge\\boot.cfg\n")
        return PCFG_NONE
    if image_verify(buf, sz, SEAL_CONFIG) == 0:
        puts("boot policy: \\EFI\\Forge\\boot.cfg refused (")
        puts(seal_reason(seal_why))
        puts("), ignored\n")
        kfree(buf)
        return PCFG_NONE
    body = sz - seal_tlen
    r = PCFG_NONE
    bad: u64 = 0
    seen: u64 = 0
    if body > POL_CFG_MAX:
        bad = 1
    i: u64 = 0
    while bad == 0 and i < body:
        s = i
        while i < body and (le16(buf + i) & 0xFF) != 10:
            i += 1
        e = i
        i += 1
        # trim spaces, tabs and a CR
        while s < e and ((le16(buf + s) & 0xFF) == 32 or (le16(buf + s) & 0xFF) == 9):
            s += 1
        while e > s and ((le16(buf + e - 1) & 0xFF) == 32 or (le16(buf + e - 1) & 0xFF) == 9 or (le16(buf + e - 1) & 0xFF) == 13):
            e -= 1
        if e == s or (le16(buf + s) & 0xFF) == 35:
            continue
        if cfg_line_eq(buf, s, e - s, "iommu=strict") and seen == 0:
            r = PCFG_STRICT
            seen = 1
        elif cfg_line_eq(buf, s, e - s, "iommu=permissive") and seen == 0:
            r = PCFG_PERMISSIVE
            seen = 1
        else:
            bad = 1
    kfree(buf)
    if bad != 0:
        puts("boot policy: \\EFI\\Forge\\boot.cfg is sealed but malformed (unknown or repeated line), ignored\n")
        return PCFG_NONE
    puts("boot policy: sealed \\EFI\\Forge\\boot.cfg verified: ")
    if r == PCFG_STRICT:
        puts("iommu=strict\n")
    elif r == PCFG_PERMISSIVE:
        puts("iommu=permissive\n")
    else:
        puts("no iommu setting\n")
    return r

def boot_policy():
    pol_selftest()
    cfg = pol_config()
    cmd = pol_cmdline()
    if cmd == PCMD_STRICT:
        puts("boot policy: forge.iommu=strict on the command line (tightens)\n")
    elif cmd == PCMD_PERMISSIVE:
        puts("boot policy: forge.iommu=permissive on the command line refused: loosening needs the sealed boot config\n")
    iommu_strict = pol_resolve(IOMMU_STRICT_DEFAULT, cfg, cmd)
    iommu_pol_src = POL_DEFAULT
    if cfg != PCFG_NONE:
        iommu_pol_src = POL_CONFIG
    if cmd == PCMD_STRICT:
        iommu_pol_src = POL_CMDLINE
    puts("boot policy: DMA policy ")
    if iommu_strict != 0:
        puts("strict")
    else:
        puts("permissive")
    puts(" (")
    puts(iommu_pol_src_name())
    if bi.kx_gen != 0:
        puts(", kexec generation ")
        put_u64(bi.kx_gen)
    puts("); resolver truth table 18/18\n")
