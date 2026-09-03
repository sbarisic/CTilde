cmake_minimum_required(VERSION 3.22)

include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(ctilde_managed_overlay_library)
include(elf_loader)
project_so(ctilde_managed_overlay_library)
