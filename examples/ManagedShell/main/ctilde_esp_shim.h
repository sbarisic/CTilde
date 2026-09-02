#pragma once

typedef void (*ct_esp_thread_state_delete_fn)(int index, void *value);

void *ct_esp_thread_state_get(void);
void ct_esp_thread_state_set(void *state, ct_esp_thread_state_delete_fn delete_callback);
