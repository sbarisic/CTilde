#ifndef CTILDE_ESP_SHIM_H
#define CTILDE_ESP_SHIM_H

#include <stdbool.h>
#include <stdint.h>

void ct_esp_delay_ms(uint32_t milliseconds);
uint32_t ct_esp_tick_count(void);
uint32_t ct_esp_stack_high_water_mark(void);

void ct_esp_restart(void);
uint32_t ct_esp_free_heap_size(void);
uint32_t ct_esp_minimum_free_heap_size(void);

bool ct_esp_gpio_configure_input(int32_t pin);
bool ct_esp_gpio_configure_output(int32_t pin);
bool ct_esp_gpio_write(int32_t pin, bool high);
bool ct_esp_gpio_read(int32_t pin);

#endif
