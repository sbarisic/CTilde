/* The upstream project_so link is a validation build. Keep Xtensa literal
   pools ahead of their overlay bodies; the CTilde packager performs the final
   resident/overlay link from the retained relocatable objects. */
SECTIONS
{
  .ctilde.initial.overlays :
  {
    *(.ctilde.overlay.*.literal)
    *(.ctilde.overlay.*.text)
  }
}
INSERT AFTER .bss;
