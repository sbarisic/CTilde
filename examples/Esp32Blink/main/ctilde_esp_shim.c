#include "ctilde_esp_shim.h"

#include "driver/gpio.h"
#include "esp_system.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

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
    return gpio_set_direction((gpio_num_t)pin, GPIO_MODE_INPUT) == ESP_OK;
}

bool ct_esp_gpio_configure_output(int32_t pin)
{
    if (!GPIO_IS_VALID_OUTPUT_GPIO(pin))
        return false;
    return gpio_set_direction((gpio_num_t)pin, GPIO_MODE_OUTPUT) == ESP_OK;
}

bool ct_esp_gpio_write(int32_t pin, bool high)
{
    if (!GPIO_IS_VALID_OUTPUT_GPIO(pin))
        return false;
    return gpio_set_level((gpio_num_t)pin, high ? 1u : 0u) == ESP_OK;
}

bool ct_esp_gpio_read(int32_t pin)
{
    return GPIO_IS_VALID_GPIO(pin) && gpio_get_level((gpio_num_t)pin) != 0;
}
