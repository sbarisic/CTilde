cmake_minimum_required(VERSION 3.22)

include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(ctilde_managed_hello)
include(elf_loader)
include("${CMAKE_CURRENT_LIST_DIR}/../../cmake/ctilde_project_so.cmake")
ctilde_project_so(ctilde_managed_hello)
