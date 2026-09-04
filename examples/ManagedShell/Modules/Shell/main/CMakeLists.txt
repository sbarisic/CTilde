set(CTILDE_GENERATED_SOURCES)
include("${CMAKE_CURRENT_LIST_DIR}/generated/ctilde_sources.cmake" OPTIONAL)

idf_component_register(
    SRCS ${CTILDE_GENERATED_SOURCES} "shell_native.c"
    INCLUDE_DIRS "."
    REQUIRES esp_common)

target_compile_options(${COMPONENT_LIB} PRIVATE -Wall -Wextra -Werror)
