#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define CT_OVERLAY_PROBE_MODULE_NAME_CAPACITY 64u

typedef struct ct_overlay_probe_debug_state {
    uint32_t Size;
    uint32_t ProcessId;
    uintptr_t WindowAddress;
    size_t WindowSize;
    uint32_t OverlayId;
    uint32_t Generation;
    char ModuleName[CT_OVERLAY_PROBE_MODULE_NAME_CAPACITY];
} ct_overlay_probe_debug_state;

extern uintptr_t ct_managed_process_current(void);
extern uint32_t ct_managed_process_id(uintptr_t handle);
extern bool ctilde_managed_overlay_debug_state(
    uint32_t process_id,
    ct_overlay_probe_debug_state *state);

uint32_t ct_overlay_fixture_generation(void)
{
    uintptr_t process = ct_managed_process_current();
    ct_overlay_probe_debug_state state = {
        .Size = sizeof(state),
    };
    if (process == 0u ||
        !ctilde_managed_overlay_debug_state(ct_managed_process_id(process), &state)) {
        return 0u;
    }
    return state.Generation;
}
