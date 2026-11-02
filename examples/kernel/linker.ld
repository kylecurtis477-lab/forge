ENTRY(_start)
SECTIONS
{
    . = 1M;
    .boot : { KEEP(*(.multiboot2)) }
    .text : { *(.text.boot) *(.text .text.*) }
    .rodata : { *(.rodata .rodata.*) }
    .data : { *(.data .data.*) }
    .bss : { *(.bss .bss.*) *(COMMON) }
    /DISCARD/ : { *(.note.GNU-stack) *(.comment) }
}
