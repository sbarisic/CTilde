idf_component_register(
    SRCS "generated/ctilde_program.c" "ctilde_esp_shim.c"
    INCLUDE_DIRS "."
    PRIV_REQUIRES esp_driver_gpio esp_system freertos)

target_compile_options(${COMPONENT_LIB} PRIVATE -Wall -Wextra -Werror)
