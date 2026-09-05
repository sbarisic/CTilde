set(CTILDE_GENERATED_SOURCES)
include("${CMAKE_CURRENT_LIST_DIR}/generated/ctilde_sources.cmake" OPTIONAL)
idf_component_register(SRCS ${CTILDE_GENERATED_SOURCES} INCLUDE_DIRS "." REQUIRES esp_common freertos)
