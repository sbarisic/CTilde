set(CTILDE_GENERATED_SOURCES)
include("${CMAKE_CURRENT_LIST_DIR}/generated/ctilde_sources.cmake" OPTIONAL)

idf_component_register(
    SRCS ${CTILDE_GENERATED_SOURCES} "managed_shell_platform.c" "managed_shell_led.c" "managed_shell_host.c" "managed_diagnostics_host.c" "managed_storage_host.c" "managed_network_host.c" "managed_ssh_host.c" "managed_log.c"
    INCLUDE_DIRS "."
    REQUIRES console ctilde_managed_runtime ctilde_storage esp_system littlefs
    PRIV_REQUIRES esp_driver_gpio esp_event esp_netif esp_wifi freertos heap led_strip lwip mbedtls nvs_flash)

target_compile_options(${COMPONENT_LIB} PRIVATE -Wall -Wextra -Werror)
