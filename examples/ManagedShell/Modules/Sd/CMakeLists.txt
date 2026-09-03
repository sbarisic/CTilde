cmake_minimum_required(VERSION 3.22)

include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(ctilde_managed_sd)
include(elf_loader)
project_so(ctilde_managed_sd)
