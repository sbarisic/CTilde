idf_component_register(
    SRCS "ctilde_managed_runtime.c" "ctilde_runtime_capabilities.c" "ctilde_runtime_buffer.c"
    INCLUDE_DIRS "include"
    REQUIRES elf_loader freertos esp_system esp_timer heap mbedtls
)

target_compile_options(${COMPONENT_LIB} PRIVATE -std=gnu23 -fno-strict-aliasing)
