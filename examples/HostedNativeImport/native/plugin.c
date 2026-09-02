#include <stdint.h>

#if defined(_WIN32)
#define CT_PLUGIN_EXPORT __declspec(dllexport)
#else
#define CT_PLUGIN_EXPORT __attribute__((visibility("default")))
#endif

static int32_t ct_plugin_counter;

CT_PLUGIN_EXPORT int32_t ctilde_plugin_api_version(void)
{
    return INT32_C(1);
}

CT_PLUGIN_EXPORT int32_t ctilde_plugin_add(int32_t left, int32_t right)
{
    return left + right;
}

CT_PLUGIN_EXPORT int32_t ctilde_plugin_next(void)
{
    ct_plugin_counter++;
    return ct_plugin_counter;
}
