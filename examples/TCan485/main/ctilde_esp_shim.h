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
int64_t ct_esp_timer_get_time_us(void);
int64_t ct_esp_invoke_i64(int64_t (*callback)(int64_t), int64_t value);

bool ct_esp_gpio_configure_input(int32_t pin);
bool ct_esp_gpio_configure_output(int32_t pin);
bool ct_esp_gpio_write(int32_t pin, bool high);
bool ct_esp_gpio_read(int32_t pin);

bool ct_esp_ws2812_configure(int32_t pin, uint32_t led_count);
bool ct_esp_ws2812_set_pixel(uint32_t index, uint32_t red, uint32_t green, uint32_t blue);
bool ct_esp_ws2812_refresh(void);
bool ct_esp_ws2812_clear(void);

#endif
