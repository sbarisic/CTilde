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

    # The upstream custom commands track only the C source. Generated type
    # layouts also affect unchanged constructor sources, so stale objects can
    # allocate too few bytes. Track the compiler's complete include closure.
    set(_ctilde_compile_command [=[COMMAND ${CMAKE_C_COMPILER} ${so_compile_flags} ${def_flags} ${include_flags} ${c_file} -o ${obj_file}]=])
    set(_ctilde_dependency_command [=[COMMAND ${CMAKE_C_COMPILER} ${so_compile_flags} ${def_flags} ${include_flags} -MD -MF "${obj_file}.d" -MQ "${obj_file}" ${c_file} -o ${obj_file}
                    DEPFILE "${obj_file}.d"
                    VERBATIM]=])
    string(FIND "${_ctilde_project_so_source}" "${_ctilde_compile_command}" _ctilde_compile_start)
    if(_ctilde_compile_start LESS 0)
        message(FATAL_ERROR "The installed elf_loader compile command is unsupported; header dependency tracking cannot be installed")
    endif()
    string(REPLACE "${_ctilde_compile_command}" "${_ctilde_dependency_command}"
        _ctilde_project_so_source "${_ctilde_project_so_source}")

    set(_ctilde_generated_helper "${CMAKE_BINARY_DIR}/ctilde_project_so.generated.cmake")
    file(WRITE "${_ctilde_generated_helper}" "${_ctilde_project_so_source}")
    include("${_ctilde_generated_helper}")
    ctilde_project_so_impl(${project_name})
    # Keep exact link inputs. Removed profile sources can leave stale objects.
    string(JOIN "\n" _ctilde_object_manifest ${so_obj_files})
    file(GENERATE OUTPUT "${CMAKE_BINARY_DIR}/ctilde-so-objects.txt"
        CONTENT "${_ctilde_object_manifest}\n")
endfunction()
