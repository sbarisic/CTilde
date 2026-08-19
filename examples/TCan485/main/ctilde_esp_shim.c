#include "ctilde_esp_shim.h"
#include "generated/ctilde_exports.h"

#include <stdlib.h>
#include <string.h>
#include "driver/gpio.h"
#include "esp_system.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "led_strip.h"
#include "led_strip_rmt.h"

static uint64_t ct_esp_configured_pins;
static uint64_t ct_esp_output_pins;
static led_strip_handle_t ct_esp_ws2812_strip;
static int32_t ct_esp_ws2812_pin = -1;
static uint32_t ct_esp_ws2812_led_count;

struct ct_esp_test_resource
{
    int32_t value;
};

static uint64_t ct_esp_gpio_mask(int32_t pin)
{
    return UINT64_C(1) << (uint32_t)pin;
}

void ct_esp_delay_ms(uint32_t milliseconds)
{
    TickType_t ticks = pdMS_TO_TICKS(milliseconds);
    if (milliseconds != 0u && ticks == 0u)
        ticks = 1u;
    vTaskDelay(ticks);
}

uint32_t ct_esp_tick_count(void)
{
    return (uint32_t)xTaskGetTickCount();
}

uint32_t ct_esp_stack_high_water_mark(void)
{
    return (uint32_t)uxTaskGetStackHighWaterMark(NULL);
}

void ct_esp_restart(void)
{
    esp_restart();
}

uint32_t ct_esp_free_heap_size(void)
{
    return esp_get_free_heap_size();
}

uint32_t ct_esp_minimum_free_heap_size(void)
{
    return esp_get_minimum_free_heap_size();
}

int64_t ct_esp_timer_get_time_us(void)
{
    return esp_timer_get_time();
}

int64_t ct_esp_invoke_i64(int64_t (*callback)(int64_t), int64_t value)
{
    return callback(value);
}

uint32_t ct_esp_fill_buffer(uint8_t* data, size_t length)
{
    if (data == NULL || length == 0u)
        return 0u;
    data[0] = 42u;
    return 42u;
}

void* ct_esp_current_task(void)
{
    return (void*)xTaskGetCurrentTaskHandle();
}

uint32_t ct_esp_utf8_length(const char* value)
{
    return value == NULL ? 0u : (uint32_t)strlen(value);
}

esp_err_t ct_esp_test_resource_create(ct_esp_test_handle_t* handle)
{
    if (handle == NULL)
        return ESP_ERR_INVALID_ARG;
    *handle = malloc(sizeof(**handle));
    if (*handle == NULL)
        return ESP_ERR_NO_MEM;
    (*handle)->value = 42;
    return ESP_OK;
}

int32_t ct_esp_test_resource_value(ct_esp_test_handle_t handle)
{
    return handle == NULL ? 0 : handle->value;
}

void ct_esp_test_resource_release(ct_esp_test_handle_t handle)
{
    free(handle);
}

int32_t ct_esp_invoke_delegate(int32_t (*callback)(int32_t value, void* context), void* callback_context, int32_t value)
{
    return callback(value, callback_context);
}

int32_t ct_esp_call_export(int32_t left, int32_t right)
{
    return ctilde_add(left, right);
}

esp_err_t ct_esp_gpio_configure_input(int32_t pin)
{
    if (!GPIO_IS_VALID_GPIO(pin))
        return ESP_ERR_INVALID_ARG;
    esp_err_t result = gpio_set_direction((gpio_num_t)pin, GPIO_MODE_INPUT);
    if (result != ESP_OK)
        return result;
    uint64_t mask = ct_esp_gpio_mask(pin);
    ct_esp_configured_pins |= mask;
    ct_esp_output_pins &= ~mask;
    return ESP_OK;
}

esp_err_t ct_esp_gpio_configure_output(int32_t pin)
{
    if (!GPIO_IS_VALID_OUTPUT_GPIO(pin))
        return ESP_ERR_INVALID_ARG;
    esp_err_t result = gpio_set_direction((gpio_num_t)pin, GPIO_MODE_OUTPUT);
    if (result != ESP_OK)
        return result;
    uint64_t mask = ct_esp_gpio_mask(pin);
    ct_esp_configured_pins |= mask;
    ct_esp_output_pins |= mask;
    return ESP_OK;
}

esp_err_t ct_esp_gpio_write(int32_t pin, bool high)
{
    if (!GPIO_IS_VALID_OUTPUT_GPIO(pin) ||
        (ct_esp_output_pins & ct_esp_gpio_mask(pin)) == 0u)
        return ESP_ERR_INVALID_STATE;
    return gpio_set_level((gpio_num_t)pin, high ? 1u : 0u);
}

bool ct_esp_gpio_read(int32_t pin)
{
    return GPIO_IS_VALID_GPIO(pin) &&
           (ct_esp_configured_pins & ct_esp_gpio_mask(pin)) != 0u &&
           gpio_get_level((gpio_num_t)pin) != 0;
}

esp_err_t ct_esp_ws2812_configure(int32_t pin, uint32_t led_count)
{
    if (!GPIO_IS_VALID_OUTPUT_GPIO(pin) || led_count == 0u)
        return ESP_ERR_INVALID_ARG;
    if (ct_esp_ws2812_strip != NULL)
        return pin == ct_esp_ws2812_pin && led_count == ct_esp_ws2812_led_count ? ESP_OK : ESP_ERR_INVALID_STATE;

    const led_strip_config_t strip_config = {
        .strip_gpio_num = pin,
        .max_leds = led_count,
        .led_model = LED_MODEL_WS2812,
        .color_component_format = LED_STRIP_COLOR_COMPONENT_FMT_GRB,
        .flags = {
            .invert_out = false,
        },
    };
    const led_strip_rmt_config_t rmt_config = {
        .clk_src = RMT_CLK_SRC_DEFAULT,
        .resolution_hz = 10u * 1000u * 1000u,
        .mem_block_symbols = 64u,
        .flags = {
            .with_dma = false,
        },
    };
    led_strip_handle_t strip = NULL;
    esp_err_t result = led_strip_new_rmt_device(&strip_config, &rmt_config, &strip);
    if (result != ESP_OK)
        return result;

    ct_esp_ws2812_strip = strip;
    ct_esp_ws2812_pin = pin;
    ct_esp_ws2812_led_count = led_count;
    return ESP_OK;
}

esp_err_t ct_esp_ws2812_set_pixel(uint32_t index, uint32_t red, uint32_t green, uint32_t blue)
{
    if (ct_esp_ws2812_strip == NULL || index >= ct_esp_ws2812_led_count ||
        red > UINT8_MAX || green > UINT8_MAX || blue > UINT8_MAX)
        return ESP_ERR_INVALID_ARG;
    return led_strip_set_pixel(ct_esp_ws2812_strip, index, red, green, blue);
}

esp_err_t ct_esp_ws2812_refresh(void)
{
    return ct_esp_ws2812_strip == NULL ? ESP_ERR_INVALID_STATE : led_strip_refresh(ct_esp_ws2812_strip);
}

esp_err_t ct_esp_ws2812_clear(void)
{
    return ct_esp_ws2812_strip == NULL ? ESP_ERR_INVALID_STATE : led_strip_clear(ct_esp_ws2812_strip);
}
