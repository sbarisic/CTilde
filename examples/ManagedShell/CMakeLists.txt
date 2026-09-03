cmake_minimum_required(VERSION 3.22)

set(EXTRA_COMPONENT_DIRS "${CMAKE_CURRENT_LIST_DIR}/../../runtime/esp-idf")
include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(ctilde_managed_shell)

set(CTILDE_STORAGE_IMAGE_ROOT "${CMAKE_BINARY_DIR}/ctilde-storage-image")
add_custom_target(ctilde_storage_stage
    COMMAND "${CMAKE_COMMAND}" -E rm -rf "${CTILDE_STORAGE_IMAGE_ROOT}"
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${CTILDE_STORAGE_IMAGE_ROOT}"
    COMMAND "${CMAKE_COMMAND}" -E copy_directory
        "${CMAKE_CURRENT_LIST_DIR}/storage" "${CTILDE_STORAGE_IMAGE_ROOT}"
    VERBATIM)
if(EXISTS "${CMAKE_CURRENT_LIST_DIR}/provisioning.local")
    add_custom_command(TARGET ctilde_storage_stage POST_BUILD
        COMMAND "${CMAKE_COMMAND}" -E copy_directory
            "${CMAKE_CURRENT_LIST_DIR}/provisioning.local" "${CTILDE_STORAGE_IMAGE_ROOT}"
        VERBATIM)
endif()
littlefs_create_partition_image(storage "${CTILDE_STORAGE_IMAGE_ROOT}" FLASH_IN_PROJECT)
add_dependencies(littlefs_storage_bin ctilde_storage_stage)
littlefs_create_partition_image(sftp sftp FLASH_IN_PROJECT)
