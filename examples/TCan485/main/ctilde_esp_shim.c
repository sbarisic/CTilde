#include "ctilde_esp_shim.h"

#include "driver/gpio.h"
#include "esp_system.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "led_strip.h"
#include "led_strip_rmt.h"

static uint64_t ct_esp_configured_pins;
static uint64_t ct_esp_output_pins;
static led_strip_handle_t ct_esp_ws2812_strip;
static int32_t ct_esp_ws2812_pin = -1;
static uint32_t ct_esp_ws2812_led_count;

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

bool ct_esp_gpio_configure_input(int32_t pin)
{
    if (!GPIO_IS_VALID_GPIO(pin))
        return false;
    if (gpio_set_direction((gpio_num_t)pin, GPIO_MODE_INPUT) != ESP_OK)
        return false;
    uint64_t mask = ct_esp_gpio_mask(pin);
    ct_esp_configured_pins |= mask;
    ct_esp_output_pins &= ~mask;
    return true;
}

bool ct_esp_gpio_configure_output(int32_t pin)
{
    if (!GPIO_IS_VALID_OUTPUT_GPIO(pin))
        return false;
    if (gpio_set_direction((gpio_num_t)pin, GPIO_MODE_OUTPUT) != ESP_OK)
        return false;
    uint64_t mask = ct_esp_gpio_mask(pin);
    ct_esp_configured_pins |= mask;
    ct_esp_output_pins |= mask;
    return true;
}

bool ct_esp_gpio_write(int32_t pin, bool high)
{
    if (!GPIO_IS_VALID_OUTPUT_GPIO(pin) ||
        (ct_esp_output_pins & ct_esp_gpio_mask(pin)) == 0u)
        return false;
    return gpio_set_level((gpio_num_t)pin, high ? 1u : 0u) == ESP_OK;
}

bool ct_esp_gpio_read(int32_t pin)
{
    return GPIO_IS_VALID_GPIO(pin) &&
           (ct_esp_configured_pins & ct_esp_gpio_mask(pin)) != 0u &&
           gpio_get_level((gpio_num_t)pin) != 0;
}

bool ct_esp_ws2812_configure(int32_t pin, uint32_t led_count)
{
    if (!GPIO_IS_VALID_OUTPUT_GPIO(pin) || led_count == 0u)
        return false;
    if (ct_esp_ws2812_strip != NULL)
        return pin == ct_esp_ws2812_pin && led_count == ct_esp_ws2812_led_count;

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
    if (led_strip_new_rmt_device(&strip_config, &rmt_config, &strip) != ESP_OK)
        return false;

    ct_esp_ws2812_strip = strip;
    ct_esp_ws2812_pin = pin;
    ct_esp_ws2812_led_count = led_count;
    return true;
}

bool ct_esp_ws2812_set_pixel(uint32_t index, uint32_t red, uint32_t green, uint32_t blue)
{
    if (ct_esp_ws2812_strip == NULL || index >= ct_esp_ws2812_led_count ||
        red > UINT8_MAX || green > UINT8_MAX || blue > UINT8_MAX)
        return false;
    return led_strip_set_pixel(ct_esp_ws2812_strip, index, red, green, blue) == ESP_OK;
}

bool ct_esp_ws2812_refresh(void)
{
    return ct_esp_ws2812_strip != NULL && led_strip_refresh(ct_esp_ws2812_strip) == ESP_OK;
}

bool ct_esp_ws2812_clear(void)
{
    return ct_esp_ws2812_strip != NULL && led_strip_clear(ct_esp_ws2812_strip) == ESP_OK;
}
