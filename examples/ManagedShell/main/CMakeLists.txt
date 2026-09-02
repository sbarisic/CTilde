set(CTILDE_GENERATED_SOURCES)
include("${CMAKE_CURRENT_LIST_DIR}/generated/ctilde_sources.cmake" OPTIONAL)

idf_component_register(
    SRCS ${CTILDE_GENERATED_SOURCES} "managed_shell_platform.c"
    INCLUDE_DIRS "."
    REQUIRES ctilde_managed_runtime esp_system littlefs)

target_compile_options(${COMPONENT_LIB} PRIVATE -Wall -Wextra -Werror)
