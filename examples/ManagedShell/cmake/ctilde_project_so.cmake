# Extends Espressif's project_so helper without modifying the component-manager
# checkout. The upstream helper does not consume target/source compile options,
# so managed-module profile flags must be injected into its custom commands.
function(ctilde_project_so project_name)
    if(NOT DEFINED ELF_LOADER_CMAKE_DIR)
        message(FATAL_ERROR "ctilde_project_so requires include(elf_loader) first")
    endif()

    file(READ "${ELF_LOADER_CMAKE_DIR}/elf_loader.cmake" _ctilde_elf_loader_source)
    string(FIND "${_ctilde_elf_loader_source}" "macro(project_so project_name)" _ctilde_macro_start)
    if(_ctilde_macro_start LESS 0)
        message(FATAL_ERROR "The installed elf_loader project_so implementation is unsupported")
    endif()
    string(SUBSTRING "${_ctilde_elf_loader_source}" ${_ctilde_macro_start} -1 _ctilde_project_so_source)
    string(REPLACE "macro(project_so project_name)" "macro(ctilde_project_so_impl project_name)"
        _ctilde_project_so_source "${_ctilde_project_so_source}")
    string(REPLACE
        "set(so_compile_flags -c"
        "set(so_compile_flags -c ${CTILDE_MANAGED_SO_COMPILE_FLAGS}"
        _ctilde_project_so_source "${_ctilde_project_so_source}")
    string(REPLACE
        "set(so_link_flags -shared"
        "set(so_link_flags -shared ${CTILDE_MANAGED_SO_LINK_FLAGS}"
        _ctilde_project_so_source "${_ctilde_project_so_source}")

    set(_ctilde_generated_helper "${CMAKE_BINARY_DIR}/ctilde_project_so.generated.cmake")
    file(WRITE "${_ctilde_generated_helper}" "${_ctilde_project_so_source}")
    include("${_ctilde_generated_helper}")
    ctilde_project_so_impl(${project_name})
endfunction()
