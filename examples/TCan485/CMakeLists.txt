cmake_minimum_required(VERSION 3.22)

list(APPEND EXTRA_COMPONENT_DIRS "${CMAKE_CURRENT_LIST_DIR}/../../runtime/esp-idf")
include($ENV{IDF_PATH}/tools/cmake/project.cmake)
idf_build_set_property(MINIMAL_BUILD ON)
idf_build_set_property(DEPENDENCIES_LOCK "dependencies.lock.${IDF_TARGET}")
set(PROJECT_VER "0.1")
project(ctilde_tcan485)
