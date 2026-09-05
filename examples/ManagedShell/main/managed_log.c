#include "managed_log.h"
#include <stdarg.h>
#include <stdbool.h>
#include <stdio.h>
#include <string.h>
#include "ctilde_storage.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

/* Producers never perform filesystem I/O while ESP-IDF holds its log lock.
   The existing SD control task drains this bounded queue. */
static char s_pending[2048];
static size_t s_read, s_count;
static portMUX_TYPE s_lock = portMUX_INITIALIZER_UNLOCKED;
static vprintf_like_t s_console = vprintf;
static TaskHandle_t s_writer;

static int log_output(const char *format, va_list arguments)
{
    if (xTaskGetCurrentTaskHandle() == __atomic_load_n(&s_writer, __ATOMIC_ACQUIRE))
        return s_console(format, arguments);
    char text[512];
    va_list copy;
    va_copy(copy, arguments);
    const int length = vsnprintf(text, sizeof(text), format, copy);
    va_end(copy);
    if (length < 0 || (size_t)length >= sizeof(text)) return s_console(format, arguments);
    portENTER_CRITICAL(&s_lock);
    const bool fits = (size_t)length <= sizeof(s_pending) - s_count;
    if (fits) {
        for (int index = 0; index < length; ++index)
            s_pending[(s_read + s_count + (size_t)index) % sizeof(s_pending)] = text[index];
        s_count += (size_t)length;
    }
    portEXIT_CRITICAL(&s_lock);
    /* Preserve messages if logging storage is unavailable or saturated. */
    return fits ? length : s_console(format, arguments);
}

void ct_managed_log_initialize(void)
{
    s_console = esp_log_set_vprintf(log_output);
}

void ct_managed_log_drain(uintptr_t storage_monitor)
{
    char bytes[256];
    __atomic_store_n(&s_writer, xTaskGetCurrentTaskHandle(), __ATOMIC_RELEASE);
    /* Bound each drain even if filesystem errors produce additional logs. */
    for (size_t batch = 0; batch < sizeof(s_pending) / sizeof(bytes); ++batch) {
        portENTER_CRITICAL(&s_lock);
        const size_t count = s_count < sizeof(bytes) ? s_count : sizeof(bytes);
        for (size_t index = 0; index < count; ++index)
            bytes[index] = s_pending[(s_read + index) % sizeof(s_pending)];
        s_read = (s_read + count) % sizeof(s_pending);
        s_count -= count;
        portEXIT_CRITICAL(&s_lock);
        if (count == 0) break;
        if (ct_storage_monitor_append_run_log(storage_monitor, bytes, count) != 0)
            (void)fwrite(bytes, 1, count, stdout);
    }
    __atomic_store_n(&s_writer, NULL, __ATOMIC_RELEASE);
}
