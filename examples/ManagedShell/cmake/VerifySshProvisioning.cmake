if(NOT DEFINED PROVISIONING_ROOT)
    message(FATAL_ERROR "PROVISIONING_ROOT is required.")
endif()

set(required_files
    "${PROVISIONING_ROOT}/ssh/sshd.conf"
    "${PROVISIONING_ROOT}/ssh/ssh_host_ecdsa_key.pem"
    "${PROVISIONING_ROOT}/ssh/authorized_keys")
foreach(required_file IN LISTS required_files)
    if(NOT EXISTS "${required_file}")
        message(FATAL_ERROR
            "SSH packaging requires ${required_file}. Keep credentials in the gitignored provisioning.local tree.")
    endif()
endforeach()

file(GLOB wifi_profiles
    "${PROVISIONING_ROOT}/net/profiles/*.conf"
    "${PROVISIONING_ROOT}/sd/wifi_profile/*.conf")
if(NOT wifi_profiles)
    message(FATAL_ERROR
        "SSH packaging requires at least one Wi-Fi profile beneath provisioning.local/net/profiles or provisioning.local/sd/wifi_profile.")
endif()

message(STATUS "SSH provisioning is complete (${PROVISIONING_ROOT}).")
