# mkfont (tools/mkfont.forge)

This is the Forge port of `tools/mkfont.py`, which builds the kernel's 8×16 console font.

    mkfont FONT.ttf OUT.fsi      writes OUT.fsi (u8[1520], ASCII 32..126) and previews 'F' and 'g' on stdout

The output is byte-identical to the PIL version: `mkfont /usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf`
reproduces `examples/kernel/font.fsi` exactly. Exit codes: 2 with a usage line on stderr for bad arguments, 1 if the
font can't be read or uses something outside the supported subset.

## How it works

PIL renders with FreeType: hinting is on by default and it uses the smooth (anti-aliasing) rasterizer. mkfont.py then
thresholds the gray values at 110. Matching that bit for bit takes the same three stages, all in integers:

1. **Parsing.** sfnt tables head, hhea, OS/2, hmtx, maxp, cmap format 4, loca, glyf (simple glyphs only), fpgm,
   prep and cvt.
2. **Hinting.** A TrueType bytecode interpreter that follows FreeType 2.14's v40 interpreter in backward-compatibility
   mode:
   - x moves only set the touch flag;
   - SHPIX and DELTAP move only y;
   - y is frozen once both IUPs have run.
   It also matches FreeType's rounding in MulFix, MulDiv, DivFix and FT_Vector_NormLen, the default graphics state, the
   CVT scaling (13 px, scale 26624) and the phantom points.
3. **Rasterizing.** A port of ftgrays: line and DDA conic rendering into cells with cover and area, then the sweep
   to 0..255 coverage. A pixel is set at coverage ≥ 110, using the same baseline and offset as mkfont.py.

The program is hosted and contains no `@unsafe` code.

## Status and tests

`tools/mkfont.py` stays as the reference. Nothing ran it at build time, because `font.fsi` is committed, so no
script needed switching.

tests/mkfont/run.sh builds the tool and checks:

- that its output is identical to `examples/kernel/font.fsi`;
- when PIL is installed, that the `.fsi` and stdout match `mkfont.py` on DejaVu Sans Mono, Mono-Bold, Mono-Oblique and
  Sans-Bold;
- that a font needing an unimplemented opcode (DejaVu Serif) fails with exit 1.

Gaps: only the opcodes those fonts use are implemented. Composite glyphs and sizes other than 13 px are not supported.
