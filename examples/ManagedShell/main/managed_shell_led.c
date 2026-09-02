#include <limits.h>
#include <stdint.h>

#include "driver/gpio.h"
#include "esp_err.h"
#include "led_strip.h"
#include "led_strip_rmt.h"

static led_strip_handle_t s_strip;
static int32_t s_pin = -1;
static uint32_t s_led_count;

esp_err_t ct_esp_ws2812_configure(int32_t pin, uint32_t led_count)
{
    if (!GPIO_IS_VALID_OUTPUT_GPIO(pin) || led_count == 0u)
        return ESP_ERR_INVALID_ARG;
    if (s_strip != NULL)
        return pin == s_pin && led_count == s_led_count ? ESP_OK : ESP_ERR_INVALID_STATE;

    const led_strip_config_t strip_configuration = {
        .strip_gpio_num = pin,
        .max_leds = led_count,
        .led_model = LED_MODEL_WS2812,
        .color_component_format = LED_STRIP_COLOR_COMPONENT_FMT_GRB,
        .flags = {
            .invert_out = false,
        },
    };
    const led_strip_rmt_config_t rmt_configuration = {
        .clk_src = RMT_CLK_SRC_DEFAULT,
        .resolution_hz = 10u * 1000u * 1000u,
        .mem_block_symbols = 64u,
        .flags = {
            .with_dma = false,
        },
    };
    led_strip_handle_t strip = NULL;
    const esp_err_t result = led_strip_new_rmt_device(
        &strip_configuration, &rmt_configuration, &strip);
    if (result != ESP_OK)
        return result;

    s_strip = strip;
    s_pin = pin;
    s_led_count = led_count;
    return ESP_OK;
}

esp_err_t ct_esp_ws2812_set_pixel(
    uint32_t index, uint32_t red, uint32_t green, uint32_t blue)
{
    if (s_strip == NULL || index >= s_led_count ||
        red > UINT8_MAX || green > UINT8_MAX || blue > UINT8_MAX)
        return ESP_ERR_INVALID_ARG;
    return led_strip_set_pixel(s_strip, index, red, green, blue);
}

esp_err_t ct_esp_ws2812_refresh(void)
{
    return s_strip == NULL ? ESP_ERR_INVALID_STATE : led_strip_refresh(s_strip);
}

esp_err_t ct_esp_ws2812_clear(void)
{
    return s_strip == NULL ? ESP_ERR_INVALID_STATE : led_strip_clear(s_strip);
}
