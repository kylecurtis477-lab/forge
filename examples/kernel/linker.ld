/* Higher-half layout: low identity-linked boot stub at 1 MiB, everything else at -2 GiB + phys. */
ENTRY(_start)
KBASE = 0xFFFFFFFF80000000;
/* W^X at the ELF level: every PT_LOAD is RX, R or RW, never RWX. */
PHDRS
{
    bootrx PT_LOAD FLAGS(5);
    bootrw PT_LOAD FLAGS(6);
    text   PT_LOAD FLAGS(5);
    rodata PT_LOAD FLAGS(4);
    data   PT_LOAD FLAGS(6);
}
SECTIONS
{
    . = 1M;
    __kernel_phys_start = .;
    .boot : {
        KEEP(*(.multiboot2))
        *(.text.boot)
    } :bootrx
    . = ALIGN(4K);
    .bootdata : { *(.data.boot) } :bootrw
    .bootbss (NOLOAD) : { *(.bss.boot) } :bootrw
    . = ALIGN(4K);
    . += KBASE;
    __text_start = .;
    .text : AT(ADDR(.text) - KBASE) { *(.text .text.*) } :text
    . = ALIGN(4K);
    __text_end = .;
    __rodata_start = .;
    .rodata : AT(ADDR(.rodata) - KBASE) { *(.rodata .rodata.*) } :rodata
    . = ALIGN(4K);
    __rodata_end = .;
    __data_start = .;
    .data : AT(ADDR(.data) - KBASE) { *(.data .data.*) } :data
    .bss : AT(ADDR(.bss) - KBASE) { *(.bss .bss.*) *(COMMON) } :data
    . = ALIGN(4K);
    __data_end = .;
    __kernel_phys_end = . - KBASE;
    /DISCARD/ : { *(.note.GNU-stack) *(.comment) }
}
