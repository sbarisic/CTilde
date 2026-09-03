idf_component_register(
    SRCS "ctilde_storage.c"
    INCLUDE_DIRS "include"
    REQUIRES esp_blockdev esp_blockdev_util esp_driver_sdspi esp_driver_spi fatfs freertos sdmmc vfs
)

target_compile_options(${COMPONENT_LIB} PRIVATE -std=gnu23 -Wall -Wextra -Werror)
