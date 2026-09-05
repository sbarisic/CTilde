/* Resident layout for packaged CTilde managed modules.

   Xtensa's default shared-object script places the dynamic symbol and
   relocation tables in the executable PT_LOAD.  Espressif's ELF loader then
   has to reserve scarce executable RAM for data that is never executed.  Keep
   loader metadata, code, immutable data, and writable data in separate load
   segments so only actual instructions consume the contiguous executable
   allocation. */
PHDRS
{
  metadata PT_LOAD FLAGS(4);
  text     PT_LOAD FLAGS(5);
  rodata   PT_LOAD FLAGS(4);
  data     PT_LOAD FLAGS(6);
  dynamic  PT_DYNAMIC FLAGS(6);
}

SECTIONS
{
  . = SIZEOF_HEADERS;
  .hash             : { *(.hash) } :metadata
  .gnu.hash         : { *(.gnu.hash) } :metadata
  .dynsym           : { *(.dynsym) } :metadata
  .dynstr           : { *(.dynstr) } :metadata
  .gnu.version      : { *(.gnu.version) } :metadata
  .gnu.version_d    : { *(.gnu.version_d) } :metadata
  .gnu.version_r    : { *(.gnu.version_r) } :metadata
  .rela.dyn         :
  {
    *(.rela.init)
    *(.rela.text .rela.text.* .rela.gnu.linkonce.t.*)
    *(.rela.fini)
    *(.rela.rodata .rela.rodata.* .rela.gnu.linkonce.r.*)
    *(.rela.data .rela.data.* .rela.gnu.linkonce.d.*)
    *(.rela.tdata .rela.tdata.* .rela.gnu.linkonce.td.*)
    *(.rela.tbss .rela.tbss.* .rela.gnu.linkonce.tb.*)
    *(.rela.ctors .rela.dtors .rela.got)
    *(.rela.bss .rela.bss.* .rela.gnu.linkonce.b.*)
  } :metadata
  .rela.plt         : { *(.rela.plt) } :metadata

  . = ALIGN(0x1000);
  .text             :
  {
    *(.got.plt* .plt*)
    KEEP(*(.init.literal))
    KEEP(*(SORT_NONE(.init)))
    *(.literal .text .stub .literal.* .text.*)
    *(.gnu.linkonce.literal.* .gnu.linkonce.t.*.literal .gnu.linkonce.t.*)
    KEEP(*(.fini.literal))
    KEEP(*(SORT_NONE(.fini)))
  } :text =0

  . = ALIGN(0x1000);
  .rodata           : { *(.rodata .rodata.* .gnu.linkonce.r.*) } :rodata
  .rodata1          : { *(.rodata1) } :rodata
  .ctilde.manifest  : { KEEP(*(.ctilde.manifest)) } :rodata
  .eh_frame_hdr     : { *(.eh_frame_hdr) } :rodata
  .eh_frame         : { KEEP(*(.eh_frame)) } :rodata

  . = ALIGN(0x1000);
  .tdata            : { *(.tdata .tdata.* .gnu.linkonce.td.*) } :data
  .tbss             : { *(.tbss .tbss.* .gnu.linkonce.tb.*) *(.tcommon) } :data
  .preinit_array    :
  {
    PROVIDE_HIDDEN(__preinit_array_start = .);
    KEEP(*(.preinit_array))
    PROVIDE_HIDDEN(__preinit_array_end = .);
  } :data
  .init_array       :
  {
    PROVIDE_HIDDEN(__init_array_start = .);
    KEEP(*(SORT(.init_array.*)))
    KEEP(*(.init_array))
    PROVIDE_HIDDEN(__init_array_end = .);
  } :data
  .fini_array       :
  {
    PROVIDE_HIDDEN(__fini_array_start = .);
    KEEP(*(SORT(.fini_array.*)))
    KEEP(*(.fini_array))
    PROVIDE_HIDDEN(__fini_array_end = .);
  } :data
  .data.rel.ro      : { *(.data.rel.ro.local* .data.rel.ro .data.rel.ro.*) } :data
  .dynamic          : { *(.dynamic) } :data :dynamic
  .got              : { *(.got) } :data
  .data             : { *(.data .data.* .gnu.linkonce.d.*) } :data
  .lit4             : { *(.lit4 .lit4.* .gnu.linkonce.lit4.*) } :data
  .bss (NOLOAD)     :
  {
    *(.dynbss)
    *(.bss .bss.* .gnu.linkonce.b.*)
    *(COMMON)
  } :data

  /DISCARD/         : { *(.note.GNU-stack) *(.comment) }
}
