# CTilde changes to Espressif elf_loader

`components/elf_loader` is a local copy of Espressif elf_loader 1.3.3. Its upstream README, changelog, and license remain upstream documents. They describe the general component, not CTilde's complete managed-module contract.

Compared with the component-manager copy used by the module build projects, CTilde changes these files:

- `elf_loader.cmake`: adapts shared-object construction for the managed packaging path.
- `include/esp_dlfcn.h` and `src/dlso/dlfcn.c`: add `esp_dlmap` to map an ELF virtual address through a loaded module handle.
- `include/esp_elf.h` and `src/esp_elf.c`: add file-based relocation, retain separate executable and data allocations, read bounded ELF tables and relocations, and recognize the resident prefix of a CTilde overlay package.
- `src/esp_elf.c`: resolve defined GLOB_DAT symbols through their loaded section. Such relocations can hold function addresses as well as data addresses.
- `src/dlso/dlmod.c`: routes loading through the file-based relocation entry point.

The surrounding CTilde runtime performs managed ABI, package, dependency, and overlay validation. This remains trusted native-code loading. Upstream SoC support does not establish CTilde overlay support: overlays currently require ESP32/Xtensa managed applications or libraries.

`cmake/ctilde_project_so.cmake` supplies CTilde compile and link flags without editing component-manager directories. The allocator acceptance project also forwards component include directories and ESP-IDF's toolchain response file because its threaded generated code needs the selected FreeRTOS and C-library headers.
