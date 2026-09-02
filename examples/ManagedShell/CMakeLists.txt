cmake_minimum_required(VERSION 3.22)

set(EXTRA_COMPONENT_DIRS "${CMAKE_CURRENT_LIST_DIR}/../../runtime/esp-idf")
include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(ctilde_managed_shell)

littlefs_create_partition_image(storage storage FLASH_IN_PROJECT)
