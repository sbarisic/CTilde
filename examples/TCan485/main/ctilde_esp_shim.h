#ifndef CTILDE_ESP_SHIM_H
#define CTILDE_ESP_SHIM_H

#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>
#if __has_include("esp_err.h")
#include "esp_err.h"
#else
typedef int esp_err_t;
#define ESP_OK 0
const char* esp_err_to_name(esp_err_t code);
#endif

typedef struct ct_esp_test_resource* ct_esp_test_handle_t;

void ct_esp_delay_ms(uint32_t milliseconds);
uint32_t ct_esp_tick_count(void);
uint32_t ct_esp_stack_high_water_mark(void);

void ct_esp_restart(void);
uint32_t ct_esp_free_heap_size(void);
uint32_t ct_esp_minimum_free_heap_size(void);
int64_t ct_esp_timer_get_time_us(void);
int64_t ct_esp_invoke_i64(int64_t (*callback)(int64_t), int64_t value);
uint32_t ct_esp_fill_buffer(uint8_t* data, size_t length);
void* ct_esp_current_task(void);
typedef void (*ct_esp_thread_state_delete_fn)(int index, void* value);
void* ct_esp_thread_state_get(void);
void ct_esp_thread_state_set(void* state, ct_esp_thread_state_delete_fn delete_callback);
uint32_t ct_esp_utf8_length(const char* value);
esp_err_t ct_esp_test_resource_create(ct_esp_test_handle_t* handle);
int32_t ct_esp_test_resource_value(ct_esp_test_handle_t handle);
void ct_esp_test_resource_release(ct_esp_test_handle_t handle);
int32_t ct_esp_invoke_delegate(int32_t (*callback)(int32_t value, void* context), void* callback_context, int32_t value);
int32_t ct_esp_call_export(int32_t left, int32_t right);
int32_t ct_esp_threading_self_test(int32_t (*callback)(int32_t value, void* context), void* callback_context);
void ct_esp_thread_cleanup(int32_t value);

extern volatile uint32_t ct_draft018_native_data;
uintptr_t ct_draft018_mmio_address(void);
int32_t ct_draft018_start_task(void);

#if defined(CTILDE_DRAFT023_VALIDATION)
void ct_draft023_ack(void* context);
int32_t ct_draft023_start_timer(void);
int32_t ct_draft023_stop_timer(void);
uint32_t ct_draft023_ack_count(void);
#endif

esp_err_t ct_esp_gpio_configure_input(int32_t pin);
esp_err_t ct_esp_gpio_configure_output(int32_t pin);
esp_err_t ct_esp_gpio_write(int32_t pin, bool high);
bool ct_esp_gpio_read(int32_t pin);

esp_err_t ct_esp_ws2812_configure(int32_t pin, uint32_t led_count);
esp_err_t ct_esp_ws2812_set_pixel(uint32_t index, uint32_t red, uint32_t green, uint32_t blue);
esp_err_t ct_esp_ws2812_refresh(void);
esp_err_t ct_esp_ws2812_clear(void);

#endif
