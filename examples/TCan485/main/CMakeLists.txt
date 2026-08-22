include("${CMAKE_CURRENT_LIST_DIR}/generated/ctilde_sources.cmake")

set(CTILDE_PRIVATE_REQUIRES
    esp_driver_gpio
    esp_system
    esp_timer
    freertos
    led_strip)
if(NOT DEFINED ENV{CTILDE_FATAL_RUNTIME_BUILD})
    list(APPEND CTILDE_PRIVATE_REQUIRES esp_gdbstub)
endif()

idf_component_register(
    SRCS ${CTILDE_GENERATED_SOURCES} "ctilde_esp_shim.c"
    INCLUDE_DIRS "."
    PRIV_REQUIRES ${CTILDE_PRIVATE_REQUIRES})

target_compile_options(${COMPONENT_LIB} PRIVATE -Wall -Wextra -Werror)
