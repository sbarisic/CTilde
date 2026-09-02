set(CTILDE_GENERATED_SOURCES)
include("${CMAKE_CURRENT_LIST_DIR}/generated/ctilde_sources.cmake" OPTIONAL)

idf_component_register(
    SRCS ${CTILDE_GENERATED_SOURCES} "managed_shell_platform.c" "managed_shell_led.c"
    INCLUDE_DIRS "."
    REQUIRES console ctilde_managed_runtime esp_system littlefs
    PRIV_REQUIRES esp_driver_gpio led_strip)

target_compile_definitions(${COMPONENT_LIB} PRIVATE
    CTILDE_USE_LINENOISE=1
    CTILDE_CONSOLE_INPUT_ACTIVITY=ct_managed_shell_input_activity)
target_compile_options(${COMPONENT_LIB} PRIVATE -Wall -Wextra -Werror)
