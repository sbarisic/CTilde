cmake_minimum_required(VERSION 3.22)

include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(ctilde_managed_shell_module)
include(elf_loader)
include("${CMAKE_CURRENT_LIST_DIR}/../../cmake/ctilde_project_so.cmake")
set(CTILDE_MANAGED_SO_COMPILE_FLAGS -Os -flto -ffat-lto-objects -mlongcalls -fno-fast-math -ffp-contract=off)
set(CTILDE_MANAGED_SO_LINK_FLAGS -Os -flto -Wl,--no-relax -Wl,-T,${CMAKE_CURRENT_LIST_DIR}/../../cmake/ctilde_project_so_initial.ld)
ctilde_project_so(ctilde_managed_shell_module)
