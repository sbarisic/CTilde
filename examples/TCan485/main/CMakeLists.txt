include("${CMAKE_CURRENT_LIST_DIR}/generated/ctilde_sources.cmake")

idf_component_register(
    SRCS ${CTILDE_GENERATED_SOURCES} "ctilde_esp_shim.c"
    INCLUDE_DIRS "."
    PRIV_REQUIRES esp_driver_gpio esp_gdbstub esp_system esp_timer freertos led_strip)

target_compile_options(${COMPONENT_LIB} PRIVATE -Wall -Wextra -Werror)
