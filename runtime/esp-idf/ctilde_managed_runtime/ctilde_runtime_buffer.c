/* Pure shared helpers. Ownership and exception handling remain with callers. */
#include "ctilde_managed_runtime.h"

int32_t ctilde_buffer_format_unsigned(uint64_t value, bool negative, char *output)
{
    char reversed[20];
    int32_t count = 0;
    do {
        reversed[count++] = (char)('0' + value % UINT64_C(10));
        value /= UINT64_C(10);
    } while (value != 0u);
    int32_t length = 0;
    if (negative) output[length++] = '-';
    while (count > 0) output[length++] = reversed[--count];
    return length;
}

int32_t ctilde_buffer_format_signed(int64_t value, char *output)
{
    const bool negative = value < 0;
    const uint64_t magnitude = negative ? (uint64_t)(-(value + 1)) + UINT64_C(1) : (uint64_t)value;
    return ctilde_buffer_format_unsigned(magnitude, negative, output);
}

uint32_t ctilde_buffer_hash_bytes(const void* value, size_t size) { const uint8_t* bytes = (const uint8_t*)value; uint32_t hash = UINT32_C(2166136261); for (size_t i = 0; i < size; ++i) { hash ^= bytes[i]; hash *= UINT32_C(16777619); } return hash; }

int32_t ctilde_buffer_encode_rune(uint32_t value, uint8_t buffer[4]) { if (value <= UINT32_C(0x7f)) { buffer[0] = (uint8_t)value; return 1; } if (value <= UINT32_C(0x7ff)) { buffer[0] = (uint8_t)(UINT32_C(0xc0) | (value >> 6)); buffer[1] = (uint8_t)(UINT32_C(0x80) | (value & UINT32_C(0x3f))); return 2; } if (value <= UINT32_C(0xffff)) { buffer[0] = (uint8_t)(UINT32_C(0xe0) | (value >> 12)); buffer[1] = (uint8_t)(UINT32_C(0x80) | ((value >> 6) & UINT32_C(0x3f))); buffer[2] = (uint8_t)(UINT32_C(0x80) | (value & UINT32_C(0x3f))); return 3; } buffer[0] = (uint8_t)(UINT32_C(0xf0) | (value >> 18)); buffer[1] = (uint8_t)(UINT32_C(0x80) | ((value >> 12) & UINT32_C(0x3f))); buffer[2] = (uint8_t)(UINT32_C(0x80) | ((value >> 6) & UINT32_C(0x3f))); buffer[3] = (uint8_t)(UINT32_C(0x80) | (value & UINT32_C(0x3f))); return 4; }

bool ctilde_buffer_validate_utf8(const uint8_t* data, size_t length)
{
    size_t index = 0u; while (index < length) {
        uint8_t first = data[index++]; if (first <= UINT8_C(0x7F)) continue;
        uint32_t scalar; size_t continuation;
        if (first >= UINT8_C(0xC2) && first <= UINT8_C(0xDF)) { scalar = (uint32_t)(first & UINT8_C(0x1F)); continuation = 1u; }
        else if (first >= UINT8_C(0xE0) && first <= UINT8_C(0xEF)) { scalar = (uint32_t)(first & UINT8_C(0x0F)); continuation = 2u; }
        else if (first >= UINT8_C(0xF0) && first <= UINT8_C(0xF4)) { scalar = (uint32_t)(first & UINT8_C(0x07)); continuation = 3u; }
        else return false;
        if (continuation > length - index) return false;
        for (size_t lane = 0u; lane < continuation; ++lane) { uint8_t next = data[index++]; if ((next & UINT8_C(0xC0)) != UINT8_C(0x80)) return false; scalar = (scalar << 6) | (uint32_t)(next & UINT8_C(0x3F)); }
        if ((continuation == 1u && scalar < UINT32_C(0x80)) || (continuation == 2u && scalar < UINT32_C(0x800)) || (continuation == 3u && scalar < UINT32_C(0x10000)) || (scalar >= UINT32_C(0xD800) && scalar <= UINT32_C(0xDFFF)) || scalar > UINT32_C(0x10FFFF)) return false;
    } return true;
}
