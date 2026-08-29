set(CTILDE_GENERATED_SOURCES)
set(CTILDE_BINDING_SOURCES)
set(CTILDE_BINDING_REQUIRES)

# A clean checkout does not contain generated artifacts.  Optional includes let
# ESP-IDF establish the build directory before the C~ compiler writes them.
include("${CMAKE_CURRENT_LIST_DIR}/generated/ctilde_sources.cmake" OPTIONAL)
include("${CMAKE_CURRENT_LIST_DIR}/generated/ctilde_bindings.cmake" OPTIONAL)

# The QEMU program statically eliminates every hardware-only C~ branch.  Do
# not retain the all-target binding translation unit here: its Wi-Fi symbols
# pull in ESP32-C3 ADC calibration, which QEMU does not emulate.
if("$ENV{CTILDE_TARGET_ENVIRONMENT}" STREQUAL "qemu")
    set(CTILDE_BINDING_SOURCES)
endif()

set(CTILDE_PRIVATE_REQUIRES
    esp_driver_gpio
    esp_driver_gptimer
    esp_system
    esp_timer
    freertos
    led_strip)
if(NOT DEFINED ENV{CTILDE_FATAL_RUNTIME_BUILD} AND NOT "$ENV{CTILDE_TARGET_ENVIRONMENT}" STREQUAL "qemu")
    list(APPEND CTILDE_PRIVATE_REQUIRES esp_gdbstub)
endif()

idf_component_register(
    SRCS ${CTILDE_GENERATED_SOURCES} ${CTILDE_BINDING_SOURCES} "ctilde_esp_shim.c"
    INCLUDE_DIRS "."
    PRIV_REQUIRES ${CTILDE_PRIVATE_REQUIRES} ${CTILDE_BINDING_REQUIRES})

target_compile_options(${COMPONENT_LIB} PRIVATE -Wall -Wextra -Werror)

if(DEFINED ENV{CTILDE_MEMORY_VALIDATION_BUILD})
    target_compile_definitions(${COMPONENT_LIB} PRIVATE
        CTILDE_CONFORMANCE=1
        CT_MEMORY_DIAGNOSTICS=1
        CT_MEMORY_LAYOUT_DIAGNOSTICS=1)
endif()

if(DEFINED ENV{CTILDE_DRAFT023_VALIDATION_BUILD})
    target_compile_definitions(${COMPONENT_LIB} PRIVATE CTILDE_DRAFT023_VALIDATION=1)
endif()
