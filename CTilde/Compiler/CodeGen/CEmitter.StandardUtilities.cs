namespace CTilde;

internal sealed partial class CEmitter
{
    private void EmitStandardUtilitySupport(CWriter writer)
    {
        var externNames = _externUses.Select(use => use.Method.ExternName).Where(name => name is not null).ToHashSet(StringComparer.Ordinal);
        var usesStringUtilities = externNames.Overlaps([
            "ct_string_region_equal", "ct_string_index_char", "ct_string_last_index_char",
            "ct_string_index_string", "ct_string_last_index_string", "ct_string_substring",
            "ct_string_compare_ordinal", "ct_string_from_array", "ct_string_format_builtin",
            "ct_string_argument_null", "ct_string_argument", "ct_string_argument_out_of_range",
            "ct_string_overflow", "ct_string_out_of_memory", "ct_string_bounds", "ct_string_format_invalid"]);
        if (usesStringUtilities)
            EmitStringUtilitySupport(writer, externNames);
        var usesUtf8Conversion = externNames.Overlaps([
            "ct_utf8_get_string_pointer", "ct_utf8_try_get_string_pointer", "ct_utf8_get_string_buffer",
            "ct_utf8_try_get_string_buffer", "ct_utf8_try_copy_to", "ct_encoding_get_bytes", "ct_encoding_get_string"]);
        if (usesUtf8Conversion)
            EmitUtf8ConversionSupport(writer, externNames);
        var usesParsing = _enumParseTypes.Count != 0 || externNames.Any(name => name is not null &&
            (name.StartsWith("ct_parse_", StringComparison.Ordinal) || name.StartsWith("ct_try_parse_", StringComparison.Ordinal)));
        if (usesParsing)
        {
            EmitParsingSupport(writer, externNames);
            EmitEnumParsingSupport(writer);
        }
        if (_usesMonotonicClock)
        {
            if (IsFreestanding || IsEspIdf && HasRuntimeImplementation(RuntimeImplementationRole.MonotonicNanoseconds))
            {
                writer.WriteLine("int64_t ct_monotonic_nanoseconds(void) { return ct_runtime_monotonic_nanoseconds(); }");
            }
            else
            {
                writer.WriteLine("int64_t ct_monotonic_nanoseconds(void)");
                writer.WriteLine("{");
                writer.WriteLine("#if defined(_WIN32)");
                writer.WriteLine("    LARGE_INTEGER frequency; LARGE_INTEGER counter;");
                writer.WriteLine("    if (!QueryPerformanceFrequency(&frequency) || frequency.QuadPart <= 0 || !QueryPerformanceCounter(&counter) || counter.QuadPart < 0) ct_fail(\"CTK0001\", \"<monotonic-clock>\", 0);");
                writer.WriteLine("    uint64_t divisor = (uint64_t)frequency.QuadPart; uint64_t value = (uint64_t)counter.QuadPart; uint64_t seconds = value / divisor; uint64_t remainder = value % divisor;");
                writer.WriteLine("    if (seconds > (uint64_t)INT64_MAX / UINT64_C(1000000000) || remainder > UINT64_MAX / UINT64_C(1000000000)) ct_fail(\"CTK0001\", \"<monotonic-clock>\", 0);");
                writer.WriteLine("    return (int64_t)(seconds * UINT64_C(1000000000) + remainder * UINT64_C(1000000000) / divisor);");
                writer.WriteLine("#elif defined(ESP_PLATFORM)");
                writer.WriteLine("    int64_t microseconds = esp_timer_get_time(); if (microseconds < 0 || microseconds > INT64_MAX / INT64_C(1000)) ct_fail(\"CTK0001\", \"<monotonic-clock>\", 0); return microseconds * INT64_C(1000);");
                writer.WriteLine("#else");
                writer.WriteLine("    struct timespec value; if (clock_gettime(CLOCK_MONOTONIC, &value) != 0 || value.tv_sec < 0 || value.tv_nsec < 0 || value.tv_nsec >= 1000000000L || (uint64_t)value.tv_sec > (uint64_t)INT64_MAX / UINT64_C(1000000000)) ct_fail(\"CTK0001\", \"<monotonic-clock>\", 0);");
                writer.WriteLine("    return (int64_t)((uint64_t)value.tv_sec * UINT64_C(1000000000) + (uint64_t)value.tv_nsec);");
                writer.WriteLine("#endif");
                writer.WriteLine("}");
            }
        }
        if (_usesRandomRangeFailure)
            writer.WriteLine("void ct_random_argument_out_of_range(void) { ct_raise_runtime_fault(CT_FAULT_ARGUMENT_OUT_OF_RANGE, \"CTR0001\", \"<random>\", 0); }");
        var freestandingFaults = new (string Symbol, string Kind, string Code)[]
        {
            ("ct_fs_fault_argument", "CT_FAULT_ARGUMENT", "CTA0001"),
            ("ct_fs_fault_argument_null", "CT_FAULT_ARGUMENT_NULL", "CTN0001"),
            ("ct_fs_fault_argument_out_of_range", "CT_FAULT_ARGUMENT_OUT_OF_RANGE", "CTR0001"),
            ("ct_fs_fault_end_of_stream", "CT_FAULT_ARGUMENT", "CTIO0002"),
            ("ct_fs_fault_index_out_of_range", "CT_FAULT_BOUNDS", "CTA0003"),
            ("ct_fs_fault_invalid_operation", "CT_FAULT_ARGUMENT", "CTO0004"),
            ("ct_fs_fault_key_not_found", "CT_FAULT_ARGUMENT", "CTK0002"),
            ("ct_fs_fault_object_disposed", "CT_FAULT_ARGUMENT", "CTO0005"),
            ("ct_fs_fault_out_of_memory", "CT_FAULT_OUT_OF_MEMORY", "CTM0001"),
            ("ct_fs_fault_overflow", "CT_FAULT_OVERFLOW", "CTS0002"),
        };
        foreach (var fault in freestandingFaults.Where(fault => externNames.Contains(fault.Symbol)))
            writer.WriteLine($"void {fault.Symbol}(void) {{ ct_raise_runtime_fault({fault.Kind}, \"{fault.Code}\", \"<standard-library>\", 0); }}");
        if (_usesSpinPause)
            writer.WriteLine("void ct_spin_pause(void) { ct_cpu_pause(); }");
        if (usesStringUtilities || usesUtf8Conversion || usesParsing || _usesMonotonicClock || _usesRandomRangeFailure || _usesSpinPause ||
            freestandingFaults.Any(fault => externNames.Contains(fault.Symbol)))
            writer.WriteLine();
    }

    private void EmitStringUtilitySupport(CWriter writer, HashSet<string?> used)
    {
        if (used.Contains("ct_string_argument_null"))
            writer.WriteLine("void ct_string_argument_null(void) { ct_raise_runtime_fault(CT_FAULT_ARGUMENT_NULL, \"CTN0001\", \"<string>\", 0); }");
        if (used.Contains("ct_string_argument"))
            writer.WriteLine("void ct_string_argument(void) { ct_raise_runtime_fault(CT_FAULT_ARGUMENT, \"CTS0003\", \"<string>\", 0); }");
        if (used.Contains("ct_string_argument_out_of_range"))
            writer.WriteLine("void ct_string_argument_out_of_range(void) { ct_raise_runtime_fault(CT_FAULT_ARGUMENT_OUT_OF_RANGE, \"CTR0001\", \"<string>\", 0); }");
        if (used.Contains("ct_string_overflow"))
            writer.WriteLine("void ct_string_overflow(void) { ct_raise_runtime_fault(CT_FAULT_OVERFLOW, \"CTS0001\", \"<string>\", 0); }");
        if (used.Contains("ct_string_out_of_memory"))
            writer.WriteLine("void ct_string_out_of_memory(void) { ct_raise_runtime_fault(CT_FAULT_OUT_OF_MEMORY, \"CTM0001\", \"<string-builder>\", 0); }");
        if (used.Contains("ct_string_bounds"))
            writer.WriteLine("void ct_string_bounds(void) { ct_raise_runtime_fault(CT_FAULT_BOUNDS, \"CTA0003\", \"<string-segment>\", 0); }");
        if (used.Contains("ct_string_format_invalid"))
            writer.WriteLine("void ct_string_format_invalid(void) { ct_raise_runtime_fault(CT_FAULT_FORMAT, \"CTS0006\", \"<format>\", 0); }");
        if (used.Contains("ct_string_region_equal"))
            writer.WriteLine("bool ct_string_region_equal(ct_string* left, int32_t left_start, ct_string* right, int32_t right_start, int32_t count) { return count == 0 || memcmp(left->Data + left_start, right->Data + right_start, (size_t)count) == 0; }");
        if (used.Contains("ct_string_index_char"))
        {
            writer.WriteLine("int32_t ct_string_index_char(ct_string* value, uint8_t search, int32_t start, int32_t count)");
            writer.WriteLine("{ const uint8_t* found = count == 0 ? NULL : (const uint8_t*)memchr(value->Data + start, search, (size_t)count); return found == NULL ? -1 : (int32_t)(found - value->Data); }");
        }
        if (used.Contains("ct_string_last_index_char"))
        {
            writer.WriteLine("int32_t ct_string_last_index_char(ct_string* value, uint8_t search, int32_t start, int32_t count)");
            writer.WriteLine("{ for (int32_t index = start + count; index > start; --index) if (value->Data[index - 1] == search) return index - 1; return -1; }");
        }
        if (used.Contains("ct_string_index_string"))
        {
            writer.WriteLine("int32_t ct_string_index_string(ct_string* value, ct_string* search, int32_t start, int32_t count)");
            writer.WriteLine("{");
            writer.WriteLine("    if (search->Length == 0) return start;");
            writer.WriteLine("    if (search->Length > count) return -1;");
            writer.WriteLine("    int32_t last = start + count - search->Length; for (int32_t index = start; index <= last; ++index) if (value->Data[index] == search->Data[0] && memcmp(value->Data + index, search->Data, (size_t)search->Length) == 0) return index; return -1;");
            writer.WriteLine("}");
        }
        if (used.Contains("ct_string_last_index_string"))
        {
            writer.WriteLine("int32_t ct_string_last_index_string(ct_string* value, ct_string* search, int32_t start, int32_t count)");
            writer.WriteLine("{");
            writer.WriteLine("    if (search->Length == 0) return start + count;");
            writer.WriteLine("    if (search->Length > count) return -1;");
            writer.WriteLine("    for (int32_t index = start + count - search->Length; index >= start; --index) if (value->Data[index] == search->Data[0] && memcmp(value->Data + index, search->Data, (size_t)search->Length) == 0) return index; return -1;");
            writer.WriteLine("}");
        }
        if (used.Contains("ct_string_substring"))
            writer.WriteLine("ct_string* ct_string_substring(ct_string* value, int32_t start, int32_t count) { return ct_string_from_bytes(value->Data + start, count, \"<string>\", 0); }");
        if (used.Contains("ct_string_compare_ordinal"))
        {
            writer.WriteLine("int32_t ct_string_compare_ordinal(ct_string* left, ct_string* right)");
            writer.WriteLine("{ if (left == right) return 0; if (left == NULL) return -1; if (right == NULL) return 1; int32_t count = left->Length < right->Length ? left->Length : right->Length; int result = count == 0 ? 0 : memcmp(left->Data, right->Data, (size_t)count); if (result != 0) return result < 0 ? -1 : 1; return left->Length < right->Length ? -1 : left->Length > right->Length ? 1 : 0; }");
        }
        if (used.Contains("ct_string_from_array"))
        {
            var bytes = NameMangler.Array(CType.Byte);
            writer.WriteLine($"ct_string* ct_string_from_array({bytes}* value, int32_t start, int32_t count) {{ return ct_string_from_bytes(value->Data + start, count, \"<string-builder>\", 0); }}");
        }
        if (used.Contains("ct_string_format_builtin"))
        {
            EmitRyuFormattingSupport(writer);
            writer.WriteLine("static void ct_parse_standard_format(ct_string* format, uint8_t* code, int32_t* precision)");
            writer.WriteLine("{");
            writer.WriteLine("    *code = (uint8_t)'G'; *precision = -1; if (format == NULL || format->Length == 0) return;");
            writer.WriteLine("    uint8_t value = format->Data[0]; if (!((value >= (uint8_t)'A' && value <= (uint8_t)'Z') || (value >= (uint8_t)'a' && value <= (uint8_t)'z'))) ct_string_format_invalid(); *code = value;");
            writer.WriteLine("    if (format->Length == 1) { return; }");
            writer.WriteLine("    int32_t parsed = 0; for (int32_t index = 1; index < format->Length; ++index) { uint8_t digit = format->Data[index]; if (digit < (uint8_t)'0' || digit > (uint8_t)'9') ct_string_format_invalid(); parsed = parsed * 10 + (int32_t)(digit - (uint8_t)'0'); if (parsed > 99) ct_string_format_invalid(); } *precision = parsed;");
            writer.WriteLine("}");
            writer.WriteLine("static ct_string* ct_format_u64_core(uint64_t value, bool negative, uint8_t bits, ct_string* format)");
            writer.WriteLine("{");
            writer.WriteLine("    uint8_t code; int32_t precision; ct_parse_standard_format(format, &code, &precision); bool hex = code == (uint8_t)'X' || code == (uint8_t)'x'; if (!(hex || code == (uint8_t)'D' || code == (uint8_t)'d' || code == (uint8_t)'G' || code == (uint8_t)'g')) ct_string_format_invalid();");
            writer.WriteLine("    if ((code == (uint8_t)'G' || code == (uint8_t)'g') && precision >= 0) { ct_string_format_invalid(); }");
            writer.WriteLine("    uint32_t radix = hex ? 16u : 10u; if (precision < 0) precision = 1;");
            writer.WriteLine("    if (hex && bits < 64u) { value &= (UINT64_C(1) << bits) - UINT64_C(1); }");
            writer.WriteLine("    uint8_t reversed[128]; int32_t length = 0; do { uint32_t digit = (uint32_t)(value % radix); value /= radix; reversed[length++] = (uint8_t)(digit < 10u ? '0' + digit : (code == (uint8_t)'x' ? 'a' : 'A') + digit - 10u); } while (value != 0u);");
            writer.WriteLine("    while (length < precision) { reversed[length++] = (uint8_t)'0'; }");
            writer.WriteLine("    if (negative && !hex) { reversed[length++] = (uint8_t)'-'; }");
            writer.WriteLine("    uint8_t output[128]; for (int32_t index = 0; index < length; ++index) output[index] = reversed[length - index - 1]; return ct_string_from_bytes(output, length, \"<format>\", 0);");
            writer.WriteLine("}");
            writer.WriteLine("static ct_string* ct_format_i64_core(int64_t value, uint8_t bits, ct_string* format) { uint64_t magnitude = value < 0 ? (uint64_t)(-(value + 1)) + UINT64_C(1) : (uint64_t)value; uint8_t code = format == NULL || format->Length == 0 ? (uint8_t)'G' : format->Data[0]; bool hex = code == (uint8_t)'X' || code == (uint8_t)'x'; return ct_format_u64_core(hex ? (uint64_t)value : magnitude, value < 0 && !hex, bits, format); }");
            writer.WriteLine("static int32_t ct_ryu_parse_exponent(const char* text, int32_t start, int32_t length)");
            writer.WriteLine("{ bool negative = start < length && text[start] == '-'; if (negative || (start < length && text[start] == '+')) ++start; int32_t value = 0; while (start < length) value = value * 10 + (int32_t)(text[start++] - '0'); return negative ? -value : value; }");
            writer.WriteLine("static ct_string* ct_format_general_ryu(const char* scientific, int32_t length, bool lowercase, int32_t significant_digits)");
            writer.WriteLine("{");
            writer.WriteLine("    if (length >= 3 && scientific[0] == 'N') return ct_string_from_bytes((const uint8_t*)\"NaN\", 3, \"<format>\", 0);");
            writer.WriteLine("    int32_t sign = scientific[0] == '-' ? 1 : 0; if (length - sign >= 8 && scientific[sign] == 'I') return ct_string_from_bytes((const uint8_t*)(sign ? \"-Infinity\" : \"Infinity\"), sign ? 9 : 8, \"<format>\", 0);");
            writer.WriteLine("    int32_t marker = sign; while (marker < length && scientific[marker] != 'E' && scientific[marker] != 'e') ++marker; if (marker == length) ct_string_format_invalid();");
            writer.WriteLine("    int32_t exponent = ct_ryu_parse_exponent(scientific, marker + 1, length); char digits[128]; int32_t digit_count = 0; for (int32_t index = sign; index < marker; ++index) if (scientific[index] != '.') digits[digit_count++] = scientific[index]; while (digit_count > 1 && digits[digit_count - 1] == '0') --digit_count;");
            writer.WriteLine("    char output[256]; int32_t out = 0; if (sign != 0) output[out++] = '-'; bool fixed = exponent >= -4 && exponent < significant_digits;");
            writer.WriteLine("    if (fixed) { int32_t point = exponent + 1; if (point <= 0) { output[out++] = '0'; output[out++] = '.'; while (point++ < 0) output[out++] = '0'; for (int32_t index = 0; index < digit_count; ++index) output[out++] = digits[index]; } else { int32_t index = 0; while (index < digit_count && index < point) output[out++] = digits[index++]; while (index < point) { output[out++] = '0'; ++index; } if (index < digit_count) { output[out++] = '.'; while (index < digit_count) output[out++] = digits[index++]; } } }");
            writer.WriteLine("    else { output[out++] = digits[0]; if (digit_count > 1) { output[out++] = '.'; for (int32_t index = 1; index < digit_count; ++index) output[out++] = digits[index]; } output[out++] = lowercase ? 'e' : 'E'; output[out++] = exponent < 0 ? '-' : '+'; uint32_t magnitude = (uint32_t)(exponent < 0 ? -exponent : exponent); if (magnitude >= 100u) output[out++] = (char)('0' + magnitude / 100u); output[out++] = (char)('0' + (magnitude / 10u) % 10u); output[out++] = (char)('0' + magnitude % 10u); }");
            writer.WriteLine("    return ct_string_from_bytes((const uint8_t*)output, out, \"<format>\", 0);");
            writer.WriteLine("}");
            writer.WriteLine("static ct_string* ct_format_f64_core(double value, bool single, ct_string* format)");
            writer.WriteLine("{");
            writer.WriteLine("    uint8_t code; int32_t precision; ct_parse_standard_format(format, &code, &precision); if (!(code == (uint8_t)'F' || code == (uint8_t)'f' || code == (uint8_t)'G' || code == (uint8_t)'g')) ct_string_format_invalid();");
            writer.WriteLine("    char buffer[768]; int32_t length; if (code == (uint8_t)'F' || code == (uint8_t)'f') { if (precision < 0) precision = 2; length = (int32_t)d2fixed_buffered_n(value, (uint32_t)precision, buffer); return ct_string_from_bytes((const uint8_t*)buffer, length, \"<format>\", 0); }");
            writer.WriteLine("    bool default_precision = precision < 0; if (default_precision) precision = single ? 9 : 17; else if (precision == 0) precision = 1; if (default_precision) length = single ? (int32_t)f2s_buffered_n((float)value, buffer) : (int32_t)d2s_buffered_n(value, buffer); else length = (int32_t)d2exp_buffered_n(value, (uint32_t)(precision - 1), buffer); return ct_format_general_ryu(buffer, length, code == (uint8_t)'g', precision);");
            writer.WriteLine("}");
            var objectType = NameMangler.Type(Model.Types["System.Object"]);
            writer.WriteLine($"ct_string* ct_string_format_builtin({objectType}* value, ct_string* format)");
            writer.WriteLine("{");
            writer.WriteLine("    if (value == NULL) return ct_empty_string;");
            writer.WriteLine("    ct_object* object = (ct_object*)(void*)value;");
            writer.WriteLine("    if (object->Type == &ct_desc_string) { if (format != NULL && format->Length != 0) ct_string_format_invalid(); ct_retain_fast(object); return (ct_string*)(void*)object; }");
            foreach (var type in BoxedTypes)
            {
                var descriptor = BoxDescriptorName(type);
                var valueExpression = $"(({BoxName(type)}*)(void*)object)->Value";
                var branch = type.Kind switch
                {
                    CTypeKind.Bool or CTypeKind.Char or CTypeKind.Rune or CTypeKind.Enum =>
                        $"if (object->Type == &{descriptor}) {{ if (format != NULL && format->Length != 0) ct_string_format_invalid(); return object->Type->VTable->ToString(object); }}",
                    CTypeKind.Byte => $"if (object->Type == &{descriptor}) return ct_format_u64_core((uint64_t){valueExpression}, false, 8u, format);",
                    CTypeKind.Ushort => $"if (object->Type == &{descriptor}) return ct_format_u64_core((uint64_t){valueExpression}, false, 16u, format);",
                    CTypeKind.Uint => $"if (object->Type == &{descriptor}) return ct_format_u64_core((uint64_t){valueExpression}, false, 32u, format);",
                    CTypeKind.Ulong => $"if (object->Type == &{descriptor}) return ct_format_u64_core((uint64_t){valueExpression}, false, 64u, format);",
                    CTypeKind.Nuint => $"if (object->Type == &{descriptor}) return ct_format_u64_core((uint64_t){valueExpression}, false, (uint8_t)(sizeof(uintptr_t) * CHAR_BIT), format);",
                    CTypeKind.Sbyte => $"if (object->Type == &{descriptor}) return ct_format_i64_core((int64_t){valueExpression}, 8u, format);",
                    CTypeKind.Short => $"if (object->Type == &{descriptor}) return ct_format_i64_core((int64_t){valueExpression}, 16u, format);",
                    CTypeKind.Int => $"if (object->Type == &{descriptor}) return ct_format_i64_core((int64_t){valueExpression}, 32u, format);",
                    CTypeKind.Long => $"if (object->Type == &{descriptor}) return ct_format_i64_core((int64_t){valueExpression}, 64u, format);",
                    CTypeKind.Nint => $"if (object->Type == &{descriptor}) return ct_format_i64_core((int64_t){valueExpression}, (uint8_t)(sizeof(intptr_t) * CHAR_BIT), format);",
                    CTypeKind.Float => $"if (object->Type == &{descriptor}) return ct_format_f64_core((double){valueExpression}, true, format);",
                    CTypeKind.Double => $"if (object->Type == &{descriptor}) return ct_format_f64_core((double){valueExpression}, false, format);",
                    _ => string.Empty,
                };
                if (branch.Length != 0)
                    writer.WriteLine("    " + branch);
            }
            writer.WriteLine("    return NULL;");
            writer.WriteLine("}");
        }
    }

    private void EmitRyuFormattingSupport(CWriter writer)
    {
        if (_ryuCoreEmitted)
            return;
        _ryuCoreEmitted = true;
        writer.WriteLine("/* CTILDE_INTERNAL_HEADER_SKIP_BEGIN */");
        writer.WriteLine("/* Ryu 4c0618b0, Boost Software License 1.0; see third_party/ryu/4c0618b0. */");
        writer.WriteLine("#if defined(__GNUC__) || defined(__clang__)");
        writer.WriteLine("#pragma GCC diagnostic push");
        writer.WriteLine("#pragma GCC diagnostic ignored \"-Wunused-function\"");
        writer.WriteLine("#endif");
        writer.WriteLine("#ifndef NDEBUG");
        writer.WriteLine("#define CT_RYU_RESTORE_NDEBUG 1");
        writer.WriteLine("#define NDEBUG 1");
        writer.WriteLine("#endif");
        if (IsFreestanding)
            writer.WriteLine("#define assert(condition) ((void)0)");
        foreach (var file in new[] { "ryu.h", "common.h", "digit_table.h", "d2s_full_table.h", "d2s_intrinsics.h", "f2s_intrinsics.h", "d2fixed_full_table.h", "d2s.c" })
            WriteRyuResource(writer, file);
        writer.WriteLine("#define to_chars ct_ryu_f2s_to_chars");
        WriteRyuResource(writer, "f2s.c");
        writer.WriteLine("#undef to_chars");
        writer.WriteLine("#undef DOUBLE_MANTISSA_BITS");
        writer.WriteLine("#undef DOUBLE_EXPONENT_BITS");
        writer.WriteLine("#undef DOUBLE_BIAS");
        WriteRyuResource(writer, "d2fixed.c");
        writer.WriteLine("#ifdef CT_RYU_RESTORE_NDEBUG");
        writer.WriteLine("#undef CT_RYU_RESTORE_NDEBUG");
        writer.WriteLine("#undef NDEBUG");
        writer.WriteLine("#endif");
        if (IsFreestanding)
            writer.WriteLine("#undef assert");
        writer.WriteLine("#if defined(__GNUC__) || defined(__clang__)");
        writer.WriteLine("#pragma GCC diagnostic pop");
        writer.WriteLine("#endif");
        writer.WriteLine("/* CTILDE_INTERNAL_HEADER_SKIP_END */");
    }

    private void WriteRyuResource(CWriter writer, string file)
    {
        using var stream = typeof(CEmitter).Assembly.GetManifestResourceStream($"CTilde.Ryu.{file}") ??
            throw new InvalidOperationException($"Missing embedded Ryu resource '{file}'.");
        using var reader = new StreamReader(stream);
        var skippingFunction = false;
        var braceDepth = 0;
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("#include \"ryu/", StringComparison.Ordinal) ||
                (IsFreestanding && trimmed.StartsWith("#include <", StringComparison.Ordinal)))
                continue;
            if (!skippingFunction && IsUnusedRyuWrapper(trimmed))
            {
                skippingFunction = true;
                braceDepth = line.Count(character => character == '{') - line.Count(character => character == '}');
                continue;
            }
            if (skippingFunction)
            {
                braceDepth += line.Count(character => character == '{') - line.Count(character => character == '}');
                if (braceDepth == 0)
                    skippingFunction = false;
                continue;
            }
            writer.WriteLine(line);
        }
    }

    private static bool IsUnusedRyuWrapper(string line) =>
        line.StartsWith("void d2s_buffered(", StringComparison.Ordinal) || line.StartsWith("char* d2s(", StringComparison.Ordinal) ||
        line.StartsWith("void f2s_buffered(", StringComparison.Ordinal) || line.StartsWith("char* f2s(", StringComparison.Ordinal) ||
        line.StartsWith("void d2fixed_buffered(", StringComparison.Ordinal) || line.StartsWith("char* d2fixed(", StringComparison.Ordinal) ||
        line.StartsWith("void d2exp_buffered(", StringComparison.Ordinal) || line.StartsWith("char* d2exp(", StringComparison.Ordinal);

    private void EmitUtf8ConversionSupport(CWriter writer, HashSet<string?> used)
    {
        if (IsManagedModule)
            writer.WriteLine("static bool ct_utf8_validate_bytes(const uint8_t* data, size_t length) { return ct_buffer_api->ValidateUtf8(data, length); }");
        else
        {
            writer.WriteLine("static bool ct_utf8_validate_bytes(const uint8_t* data, size_t length)");
            writer.WriteLine("{");
            writer.WriteLine("    size_t index = 0u; while (index < length) {");
            writer.WriteLine("        uint8_t first = data[index++]; if (first <= UINT8_C(0x7F)) continue;");
            writer.WriteLine("        uint32_t scalar; size_t continuation;");
            writer.WriteLine("        if (first >= UINT8_C(0xC2) && first <= UINT8_C(0xDF)) { scalar = (uint32_t)(first & UINT8_C(0x1F)); continuation = 1u; }");
            writer.WriteLine("        else if (first >= UINT8_C(0xE0) && first <= UINT8_C(0xEF)) { scalar = (uint32_t)(first & UINT8_C(0x0F)); continuation = 2u; }");
            writer.WriteLine("        else if (first >= UINT8_C(0xF0) && first <= UINT8_C(0xF4)) { scalar = (uint32_t)(first & UINT8_C(0x07)); continuation = 3u; }");
            writer.WriteLine("        else return false;");
            writer.WriteLine("        if (continuation > length - index) return false;");
            writer.WriteLine("        for (size_t lane = 0u; lane < continuation; ++lane) { uint8_t next = data[index++]; if ((next & UINT8_C(0xC0)) != UINT8_C(0x80)) return false; scalar = (scalar << 6) | (uint32_t)(next & UINT8_C(0x3F)); }");
            writer.WriteLine("        if ((continuation == 1u && scalar < UINT32_C(0x80)) || (continuation == 2u && scalar < UINT32_C(0x800)) || (continuation == 3u && scalar < UINT32_C(0x10000)) || (scalar >= UINT32_C(0xD800) && scalar <= UINT32_C(0xDFFF)) || scalar > UINT32_C(0x10FFFF)) return false;");
            writer.WriteLine("    } return true;");
            writer.WriteLine("}");
        }
        writer.WriteLine("static ct_string* ct_utf8_copy_checked(const uint8_t* data, size_t length, bool throwing, bool missing_terminator, bool* success)");
        writer.WriteLine("{");
        writer.WriteLine("    if (success != NULL) *success = false;");
        writer.WriteLine("    if (missing_terminator) { if (throwing) ct_raise_runtime_fault(CT_FAULT_ARGUMENT, \"CTS0005\", \"<utf8>\", 0); return NULL; }");
        writer.WriteLine("    if (length > (size_t)INT32_MAX || !ct_utf8_validate_bytes(data, length)) { if (throwing) ct_raise_runtime_fault(CT_FAULT_ARGUMENT, \"CTS0004\", \"<utf8>\", 0); return NULL; }");
        writer.WriteLine("    ct_string* result = ct_string_from_bytes(data, (int32_t)length, \"<utf8>\", 0); if (success != NULL) *success = true; return result;");
        writer.WriteLine("}");
        if (used.Contains("ct_utf8_get_string_pointer"))
            writer.WriteLine("ct_string* ct_utf8_get_string_pointer(uint8_t* value, uintptr_t max_bytes) { if (value == NULL) return NULL; const uint8_t* end = (const uint8_t*)memchr(value, 0, (size_t)max_bytes); return ct_utf8_copy_checked(value, end == NULL ? 0u : (size_t)(end - value), true, end == NULL, NULL); }");
        if (used.Contains("ct_utf8_try_get_string_pointer"))
            writer.WriteLine("ct_string* ct_utf8_try_get_string_pointer(uint8_t* value, uintptr_t max_bytes, bool* success) { if (value == NULL) { *success = true; return NULL; } const uint8_t* end = (const uint8_t*)memchr(value, 0, (size_t)max_bytes); return ct_utf8_copy_checked(value, end == NULL ? 0u : (size_t)(end - value), false, end == NULL, success); }");
        if (used.Contains("ct_utf8_get_string_buffer"))
            writer.WriteLine("ct_string* ct_utf8_get_string_buffer(const uint8_t* value_data, size_t value_length) { return ct_utf8_copy_checked(value_data, value_length, true, false, NULL); }");
        if (used.Contains("ct_utf8_try_get_string_buffer"))
            writer.WriteLine("ct_string* ct_utf8_try_get_string_buffer(const uint8_t* value_data, size_t value_length, bool* success) { return ct_utf8_copy_checked(value_data, value_length, false, false, success); }");
        if (used.Contains("ct_utf8_try_copy_to"))
            writer.WriteLine("bool ct_utf8_try_copy_to(ct_string* value, uint8_t* destination_data, size_t destination_length, bool null_terminate, uintptr_t* bytes_written) { *bytes_written = 0u; size_t required = (size_t)value->Length + (null_terminate ? 1u : 0u); if (destination_length < required) return false; if (value->Length != 0) memcpy(destination_data, value->Data, (size_t)value->Length); if (null_terminate) destination_data[value->Length] = 0; *bytes_written = (uintptr_t)required; return true; }");
        if (used.Contains("ct_encoding_get_bytes"))
        {
            var bytes = NameMangler.Array(CType.Byte);
            writer.WriteLine($"{bytes}* ct_encoding_get_bytes(ct_string* value) {{ (void)ct_require_nonnull(value, \"<encoding>\", 0); {bytes}* result = ct_new_{bytes}(value->Length, \"<encoding>\", 0); if (value->Length != 0) memcpy(result->Data, value->Data, (size_t)value->Length); return result; }}");
        }
        if (used.Contains("ct_encoding_get_string"))
        {
            var bytes = NameMangler.Array(CType.Byte);
            writer.WriteLine($"ct_string* ct_encoding_get_string({bytes}* value, int32_t offset, int32_t count) {{ (void)ct_require_nonnull(value, \"<encoding>\", 0); if (!ct_utf8_validate_bytes(value->Data + offset, (size_t)count)) ct_raise_runtime_fault(CT_FAULT_DECODER, \"CTS0004\", \"<encoding>\", 0); return ct_string_from_bytes(value->Data + offset, count, \"<encoding>\", 0); }}");
        }
    }
}
