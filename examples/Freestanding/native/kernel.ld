ENTRY(_start)

PHDRS
{
    text PT_LOAD FLAGS(5);
    data PT_LOAD FLAGS(6);
}

SECTIONS
{
    . = 0x400000;
    .text.boot : ALIGN(16)
    {
        KEEP(*(.text.boot))
    } :text
    .text : ALIGN(16)
    {
        *(.text .text.*)
    } :text
    .rodata : ALIGN(16) { *(.rodata .rodata.*) } :text
    . = ALIGN(0x1000);
    .data : ALIGN(16) { *(.data .data.*) } :data
    .bss : ALIGN(16) { *(.bss .bss.* COMMON) } :data
    /DISCARD/ : { *(.comment) *(.note*) *(.eh_frame*) }
}
