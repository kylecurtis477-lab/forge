/* Higher-half layout: low identity-linked boot stub at 1 MiB, everything else at -2 GiB + phys. */
ENTRY(_start)
KBASE = 0xFFFFFFFF80000000;
SECTIONS
{
    . = 1M;
    __kernel_phys_start = .;
    .boot : {
        KEEP(*(.multiboot2))
        *(.text.boot)
        *(.data.boot)
    }
    .bootbss (NOLOAD) : { *(.bss.boot) }
    . = ALIGN(4K);
    . += KBASE;
    __text_start = .;
    .text : AT(ADDR(.text) - KBASE) { *(.text .text.*) }
    . = ALIGN(4K);
    __text_end = .;
    __rodata_start = .;
    .rodata : AT(ADDR(.rodata) - KBASE) { *(.rodata .rodata.*) }
    . = ALIGN(4K);
    __rodata_end = .;
    __data_start = .;
    .data : AT(ADDR(.data) - KBASE) { *(.data .data.*) }
    .bss : AT(ADDR(.bss) - KBASE) { *(.bss .bss.*) *(COMMON) }
    . = ALIGN(4K);
    __data_end = .;
    __kernel_phys_end = . - KBASE;
    /DISCARD/ : { *(.note.GNU-stack) *(.comment) }
}
