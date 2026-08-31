#include <stdint.h>

#if defined(_WIN32)
#define CT_FIXTURE_EXPORT __declspec(dllexport)
#else
#define CT_FIXTURE_EXPORT __attribute__((visibility("default")))
#endif

CT_FIXTURE_EXPORT int32_t ctilde_add(int32_t left, int32_t right)
{
    return left + right;
}
