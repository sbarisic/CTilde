namespace CTilde;

internal sealed partial class CEmitter
{
    private void EmitParsingSupport(CWriter writer, HashSet<string?> used)
    {
        writer.WriteLine("typedef enum ct_parse_status { CT_PARSE_OK = 0, CT_PARSE_FORMAT = 1, CT_PARSE_OVERFLOW = 2, CT_PARSE_NULL = 3 } ct_parse_status;");
        writer.WriteLine("static bool ct_parse_ascii_space(uint8_t value) { return value == 9u || value == 10u || value == 11u || value == 12u || value == 13u || value == 32u; }");
        writer.WriteLine("static bool ct_parse_style_valid(int32_t style, bool floating)");
        writer.WriteLine("{");
        writer.WriteLine("    const int32_t leading_white = 1, trailing_white = 2, leading_sign = 4, decimal_point = 8, exponent = 16, hex = 32;");
        writer.WriteLine("    int32_t allowed = leading_white | trailing_white | leading_sign | (floating ? decimal_point | exponent : hex);");
        writer.WriteLine("    if ((style & ~allowed) != 0) return false;");
        writer.WriteLine("    if ((style & hex) != 0 && (floating || (style & leading_sign) != 0)) return false;");
        writer.WriteLine("    return true;");
        writer.WriteLine("}");
        writer.WriteLine("static ct_parse_status ct_parse_trim(ct_string* text, int32_t style, const uint8_t** begin, const uint8_t** end)");
        writer.WriteLine("{");
        writer.WriteLine("    if (text == NULL) return CT_PARSE_NULL;");
        writer.WriteLine("    const uint8_t* first = text->Data;");
        writer.WriteLine("    const uint8_t* last = first + text->Length;");
        writer.WriteLine("    if ((style & 1) != 0) while (first < last && ct_parse_ascii_space(*first)) ++first;");
        writer.WriteLine("    if ((style & 2) != 0) while (last > first && ct_parse_ascii_space(last[-1])) --last;");
        writer.WriteLine("    *begin = first; *end = last; return first == last ? CT_PARSE_FORMAT : CT_PARSE_OK;");
        writer.WriteLine("}");
        writer.WriteLine("static ct_parse_status ct_parse_unsigned_core(ct_string* text, int32_t style, uint64_t maximum, uint64_t* result)");
        writer.WriteLine("{");
        writer.WriteLine("    *result = 0; if (!ct_parse_style_valid(style, false)) return CT_PARSE_FORMAT; const uint8_t *cursor, *end; ct_parse_status status = ct_parse_trim(text, style, &cursor, &end); if (status != CT_PARSE_OK) return status;");
        writer.WriteLine("    if ((style & 4) != 0 && cursor < end && (*cursor == (uint8_t)'+' || *cursor == (uint8_t)'-')) { if (*cursor++ == (uint8_t)'-') return CT_PARSE_OVERFLOW; }");
        writer.WriteLine("    uint32_t radix = (style & 32) != 0 ? 16u : 10u; if (cursor == end) return CT_PARSE_FORMAT; uint64_t value = 0;");
        writer.WriteLine("    while (cursor < end) { uint8_t c = *cursor++; uint32_t digit = c >= (uint8_t)'0' && c <= (uint8_t)'9' ? (uint32_t)(c - (uint8_t)'0') : c >= (uint8_t)'a' && c <= (uint8_t)'f' ? (uint32_t)(c - (uint8_t)'a' + 10u) : c >= (uint8_t)'A' && c <= (uint8_t)'F' ? (uint32_t)(c - (uint8_t)'A' + 10u) : UINT32_MAX; if (digit >= radix) return CT_PARSE_FORMAT; if (value > (maximum - digit) / radix) return CT_PARSE_OVERFLOW; value = value * radix + digit; }");
        writer.WriteLine("    *result = value; return CT_PARSE_OK;");
        writer.WriteLine("}");
        writer.WriteLine("static ct_parse_status ct_parse_signed_core(ct_string* text, int32_t style, uint8_t bits, int64_t minimum, int64_t maximum, int64_t* result)");
        writer.WriteLine("{");
        writer.WriteLine("    *result = 0; if (!ct_parse_style_valid(style, false)) return CT_PARSE_FORMAT; if ((style & 32) != 0) { uint64_t raw; uint64_t limit = bits == 64u ? UINT64_MAX : (UINT64_C(1) << bits) - UINT64_C(1); ct_parse_status status = ct_parse_unsigned_core(text, style, limit, &raw); if (status != CT_PARSE_OK) return status; if (bits == 64u) memcpy(result, &raw, sizeof(raw)); else { uint64_t sign = UINT64_C(1) << (bits - 1u); *result = (raw & sign) == 0u ? (int64_t)raw : (int64_t)(raw - (UINT64_C(1) << bits)); } return CT_PARSE_OK; }");
        writer.WriteLine("    const uint8_t *cursor, *end; ct_parse_status status = ct_parse_trim(text, style, &cursor, &end); if (status != CT_PARSE_OK) return status; bool negative = false; if ((style & 4) != 0 && cursor < end && (*cursor == (uint8_t)'+' || *cursor == (uint8_t)'-')) negative = *cursor++ == (uint8_t)'-'; if (cursor == end) return CT_PARSE_FORMAT;");
        writer.WriteLine("    uint64_t limit = negative ? (uint64_t)(-(minimum + 1)) + UINT64_C(1) : (uint64_t)maximum; uint64_t value = 0; while (cursor < end) { uint8_t c = *cursor++; if (c < (uint8_t)'0' || c > (uint8_t)'9') return CT_PARSE_FORMAT; uint32_t digit = (uint32_t)(c - (uint8_t)'0'); if (value > (limit - digit) / UINT64_C(10)) return CT_PARSE_OVERFLOW; value = value * UINT64_C(10) + digit; } *result = negative ? (value == limit ? minimum : -(int64_t)value) : (int64_t)value; return CT_PARSE_OK;");
        writer.WriteLine("}");
        writer.WriteLine("static CT_NORETURN void ct_parse_throw(ct_parse_status status)");
        writer.WriteLine("{ if (status == CT_PARSE_NULL) ct_raise_runtime_fault(CT_FAULT_ARGUMENT_NULL, \"CTN0001\", \"<parse>\", 0); if (status == CT_PARSE_OVERFLOW) ct_raise_runtime_fault(CT_FAULT_OVERFLOW, \"CTP0002\", \"<parse>\", 0); ct_raise_runtime_fault(CT_FAULT_FORMAT, \"CTP0001\", \"<parse>\", 0); }");

        EmitBooleanParsing(writer, used);
        EmitIntegerParsing(writer, used);
        EmitFloatingParsing(writer, used);
    }

    private static void EmitBooleanParsing(CWriter writer, HashSet<string?> used)
    {
        if (!used.Contains("ct_parse_bool") && !used.Contains("ct_try_parse_bool")) return;
        writer.WriteLine("static ct_parse_status ct_parse_bool_core(ct_string* text, bool* result) { *result = false; const uint8_t *first, *last; ct_parse_status status = ct_parse_trim(text, 3, &first, &last); if (status != CT_PARSE_OK) return status; size_t length = (size_t)(last - first); if (length == 4u && (first[0] | 32u) == 't' && (first[1] | 32u) == 'r' && (first[2] | 32u) == 'u' && (first[3] | 32u) == 'e') { *result = true; return CT_PARSE_OK; } if (length == 5u && (first[0] | 32u) == 'f' && (first[1] | 32u) == 'a' && (first[2] | 32u) == 'l' && (first[3] | 32u) == 's' && (first[4] | 32u) == 'e') return CT_PARSE_OK; return CT_PARSE_FORMAT; }");
        if (used.Contains("ct_parse_bool")) writer.WriteLine("bool ct_parse_bool(ct_string* text) { bool result; ct_parse_status status = ct_parse_bool_core(text, &result); if (status != CT_PARSE_OK) ct_parse_throw(status); return result; }");
        if (used.Contains("ct_try_parse_bool")) writer.WriteLine("bool ct_try_parse_bool(ct_string* text, bool* result) { return ct_parse_bool_core(text, result) == CT_PARSE_OK; }");
    }

    private void EmitIntegerParsing(CWriter writer, HashSet<string?> used)
    {
        var styleType = NameMangler.Type(Model.Types["System.Globalization.NumberStyles"]);
        var entries = new (string Suffix, string CType, bool Signed, string Min, string Max, int Bits)[]
        {
            ("u8", "uint8_t", false, "0", "UINT8_MAX", 8), ("i8", "int8_t", true, "INT8_MIN", "INT8_MAX", 8),
            ("u16", "uint16_t", false, "0", "UINT16_MAX", 16), ("i16", "int16_t", true, "INT16_MIN", "INT16_MAX", 16),
            ("u32", "uint32_t", false, "0", "UINT32_MAX", 32), ("i32", "int32_t", true, "INT32_MIN", "INT32_MAX", 32),
            ("u64", "uint64_t", false, "0", "UINT64_MAX", 64), ("i64", "int64_t", true, "INT64_MIN", "INT64_MAX", 64),
            ("nuint", "uintptr_t", false, "0", "UINTPTR_MAX", 0), ("nint", "intptr_t", true, "INTPTR_MIN", "INTPTR_MAX", 0),
        };
        foreach (var entry in entries)
        {
            var names = new[] { $"ct_parse_{entry.Suffix}", $"ct_parse_{entry.Suffix}_style", $"ct_try_parse_{entry.Suffix}", $"ct_try_parse_{entry.Suffix}_style" };
            if (!names.Any(used.Contains)) continue;
            var bits = entry.Bits == 0 ? $"(uint8_t)(sizeof({entry.CType}) * CHAR_BIT)" : $"{entry.Bits}u";
            var core = $"ct_parse_{entry.Suffix}_core";
            if (entry.Signed)
                writer.WriteLine($"static ct_parse_status {core}(ct_string* text, int32_t style, {entry.CType}* result) {{ int64_t value; ct_parse_status status = ct_parse_signed_core(text, style, {bits}, (int64_t){entry.Min}, (int64_t){entry.Max}, &value); *result = status == CT_PARSE_OK ? ({entry.CType})value : ({entry.CType})0; return status; }}");
            else
                writer.WriteLine($"static ct_parse_status {core}(ct_string* text, int32_t style, {entry.CType}* result) {{ uint64_t value; ct_parse_status status = ct_parse_unsigned_core(text, style, (uint64_t){entry.Max}, &value); *result = status == CT_PARSE_OK ? ({entry.CType})value : ({entry.CType})0; return status; }}");
            if (used.Contains(names[0])) writer.WriteLine($"{entry.CType} {names[0]}(ct_string* text) {{ {entry.CType} result; ct_parse_status status = {core}(text, 7, &result); if (status != CT_PARSE_OK) ct_parse_throw(status); return result; }}");
            if (used.Contains(names[1])) writer.WriteLine($"{entry.CType} {names[1]}(ct_string* text, {styleType} style) {{ if (!ct_parse_style_valid((int32_t)style, false)) ct_raise_runtime_fault(CT_FAULT_ARGUMENT, \"CTP0003\", \"<parse-style>\", 0); {entry.CType} result; ct_parse_status status = {core}(text, (int32_t)style, &result); if (status != CT_PARSE_OK) ct_parse_throw(status); return result; }}");
            if (used.Contains(names[2])) writer.WriteLine($"bool {names[2]}(ct_string* text, {entry.CType}* result) {{ return {core}(text, 7, result) == CT_PARSE_OK; }}");
            if (used.Contains(names[3])) writer.WriteLine($"bool {names[3]}(ct_string* text, {styleType} style, {entry.CType}* result) {{ if (!ct_parse_style_valid((int32_t)style, false)) ct_raise_runtime_fault(CT_FAULT_ARGUMENT, \"CTP0003\", \"<parse-style>\", 0); return {core}(text, (int32_t)style, result) == CT_PARSE_OK; }}");
        }
    }

    private void EmitFloatingParsing(CWriter writer, HashSet<string?> used)
    {
        var any = used.Any(name => name is "ct_parse_f32" or "ct_parse_f32_style" or "ct_try_parse_f32" or "ct_try_parse_f32_style" or "ct_parse_f64" or "ct_parse_f64_style" or "ct_try_parse_f64" or "ct_try_parse_f64_style");
        if (!any) return;
        EmitRyuParserSources(writer);
        var styleType = NameMangler.Type(Model.Types["System.Globalization.NumberStyles"]);
        writer.WriteLine("static bool ct_parse_literal(const uint8_t* first, const uint8_t* last, const char* literal) { size_t length = (size_t)(last - first); size_t expected = strlen(literal); if (length != expected) return false; for (size_t index = 0; index < length; ++index) { uint8_t left = first[index], right = (uint8_t)literal[index]; if (left >= 'A' && left <= 'Z') left |= 32u; if (right >= 'A' && right <= 'Z') right |= 32u; if (left != right) return false; } return true; }");
        writer.WriteLine("static ct_parse_status ct_parse_f64_core(ct_string* text, int32_t style, double* result) { *result = 0.0; if (!ct_parse_style_valid(style, true)) return CT_PARSE_FORMAT; const uint8_t *first, *last; ct_parse_status status = ct_parse_trim(text, style, &first, &last); if (status != CT_PARSE_OK) return status; if (ct_parse_literal(first, last, \"NaN\")) { *result = NAN; return CT_PARSE_OK; } if (ct_parse_literal(first, last, \"Infinity\") || ct_parse_literal(first, last, \"+Infinity\")) { *result = INFINITY; return CT_PARSE_OK; } if (ct_parse_literal(first, last, \"-Infinity\")) { *result = -INFINITY; return CT_PARSE_OK; } if ((style & 4) == 0 && (*first == '+' || *first == '-')) return CT_PARSE_FORMAT; if (*first == '+') ++first; for (const uint8_t* p = first; p < last; ++p) { if (*p == '.' && (style & 8) == 0) return CT_PARSE_FORMAT; if ((*p == 'e' || *p == 'E') && (style & 16) == 0) return CT_PARSE_FORMAT; } enum Status parsed = s2d_n((const char*)first, (int)(last - first), result); if (parsed != SUCCESS) { *result = 0.0; return parsed == INPUT_TOO_LONG ? CT_PARSE_OVERFLOW : CT_PARSE_FORMAT; } if (isinf(*result)) { *result = 0.0; return CT_PARSE_OVERFLOW; } return CT_PARSE_OK; }");
        writer.WriteLine("static ct_parse_status ct_parse_f32_core(ct_string* text, int32_t style, float* result) { *result = 0.0f; if (!ct_parse_style_valid(style, true)) return CT_PARSE_FORMAT; const uint8_t *first, *last; ct_parse_status status = ct_parse_trim(text, style, &first, &last); if (status != CT_PARSE_OK) return status; if (ct_parse_literal(first, last, \"NaN\")) { *result = NAN; return CT_PARSE_OK; } if (ct_parse_literal(first, last, \"Infinity\") || ct_parse_literal(first, last, \"+Infinity\")) { *result = INFINITY; return CT_PARSE_OK; } if (ct_parse_literal(first, last, \"-Infinity\")) { *result = -INFINITY; return CT_PARSE_OK; } if ((style & 4) == 0 && (*first == '+' || *first == '-')) return CT_PARSE_FORMAT; if (*first == '+') ++first; for (const uint8_t* p = first; p < last; ++p) { if (*p == '.' && (style & 8) == 0) return CT_PARSE_FORMAT; if ((*p == 'e' || *p == 'E') && (style & 16) == 0) return CT_PARSE_FORMAT; } enum Status parsed = s2f_n((const char*)first, (int)(last - first), result); if (parsed != SUCCESS) { *result = 0.0f; return parsed == INPUT_TOO_LONG ? CT_PARSE_OVERFLOW : CT_PARSE_FORMAT; } if (isinf(*result)) { *result = 0.0f; return CT_PARSE_OVERFLOW; } return CT_PARSE_OK; }");
        EmitFloatWrappers(writer, used, styleType, "f32", "float", "0.0f");
        EmitFloatWrappers(writer, used, styleType, "f64", "double", "0.0");
    }

    private static void EmitFloatWrappers(CWriter writer, HashSet<string?> used, string styleType, string suffix, string cType, string zero)
    {
        if (used.Contains($"ct_parse_{suffix}")) writer.WriteLine($"{cType} ct_parse_{suffix}(ct_string* text) {{ {cType} result; ct_parse_status status = ct_parse_{suffix}_core(text, 31, &result); if (status != CT_PARSE_OK) ct_parse_throw(status); return result; }}");
        if (used.Contains($"ct_parse_{suffix}_style")) writer.WriteLine($"{cType} ct_parse_{suffix}_style(ct_string* text, {styleType} style) {{ if (!ct_parse_style_valid((int32_t)style, true)) ct_raise_runtime_fault(CT_FAULT_ARGUMENT, \"CTP0003\", \"<parse-style>\", 0); {cType} result = {zero}; ct_parse_status status = ct_parse_{suffix}_core(text, (int32_t)style, &result); if (status != CT_PARSE_OK) ct_parse_throw(status); return result; }}");
        if (used.Contains($"ct_try_parse_{suffix}")) writer.WriteLine($"bool ct_try_parse_{suffix}(ct_string* text, {cType}* result) {{ return ct_parse_{suffix}_core(text, 31, result) == CT_PARSE_OK; }}");
        if (used.Contains($"ct_try_parse_{suffix}_style")) writer.WriteLine($"bool ct_try_parse_{suffix}_style(ct_string* text, {styleType} style, {cType}* result) {{ if (!ct_parse_style_valid((int32_t)style, true)) ct_raise_runtime_fault(CT_FAULT_ARGUMENT, \"CTP0003\", \"<parse-style>\", 0); return ct_parse_{suffix}_core(text, (int32_t)style, result) == CT_PARSE_OK; }}");
    }

    private void EmitRyuParserSources(CWriter writer)
    {
        writer.WriteLine("/* CTILDE_INTERNAL_HEADER_SKIP_BEGIN */");
        writer.WriteLine("/* Ryu 4c0618b0, Boost Software License 1.0; deterministic string-to-float conversion. */");
        writer.WriteLine("#if defined(_MSC_VER)\n#pragma warning(push)\n#pragma warning(disable:4057 4267)\n#endif");
        WriteRyuResource(writer, "ryu_parse.h");
        if (!_ryuCoreEmitted)
        {
            foreach (var file in new[] { "common.h", "d2s_full_table.h", "d2s_intrinsics.h", "f2s_intrinsics.h" }) WriteRyuResource(writer, file);
            _ryuCoreEmitted = true;
        }
        writer.WriteLine("#define floor_log2 ct_ryu_s2d_floor_log2");
        writer.WriteLine("#define max32 ct_ryu_s2d_max32");
        writer.WriteLine("#define int64Bits2Double ct_ryu_int64_bits_to_double");
        WriteRyuResource(writer, "s2d.c");
        writer.WriteLine("#undef floor_log2\n#undef max32\n#undef int64Bits2Double\n#undef DOUBLE_MANTISSA_BITS\n#undef DOUBLE_EXPONENT_BITS\n#undef DOUBLE_EXPONENT_BIAS");
        writer.WriteLine("#define floor_log2 ct_ryu_s2f_floor_log2");
        writer.WriteLine("#define max32 ct_ryu_s2f_max32");
        writer.WriteLine("#define int32Bits2Float ct_ryu_int32_bits_to_float");
        WriteRyuResource(writer, "s2f.c");
        writer.WriteLine("#undef floor_log2\n#undef max32\n#undef int32Bits2Float\n#undef FLOAT_MANTISSA_BITS\n#undef FLOAT_EXPONENT_BITS\n#undef FLOAT_EXPONENT_BIAS");
        writer.WriteLine("#if defined(_MSC_VER)\n#pragma warning(pop)\n#endif");
        writer.WriteLine("/* CTILDE_INTERNAL_HEADER_SKIP_END */");
    }

    private void EmitEnumParsingSupport(CWriter writer)
    {
        if (_enumParseTypes.Count == 0) return;
        writer.WriteLine("static bool ct_enum_name_equal(const uint8_t* text, size_t length, const char* name, bool ignore_case) { size_t expected = strlen(name); if (length != expected) return false; for (size_t index = 0u; index < length; ++index) { uint8_t left = text[index], right = (uint8_t)name[index]; if (ignore_case) { if (left >= 'A' && left <= 'Z') left |= 32u; if (right >= 'A' && right <= 'Z') right |= 32u; } if (left != right) return false; } return true; }");
        foreach (var type in _enumParseTypes.OrderBy(value => value.FullName, StringComparer.Ordinal))
        {
            var underlying = type.UnderlyingType ?? CType.Int;
            var cType = CTypeName(type.Type);
            var helper = $"ct_enum_parse_{NameMangler.TypeCode(type.Type)}";
            var unsigned = underlying.Kind is CTypeKind.Byte or CTypeKind.Ushort or CTypeKind.Uint or CTypeKind.Ulong or CTypeKind.Nuint;
            var min = underlying.Kind switch
            {
                CTypeKind.Sbyte => "INT8_MIN",
                CTypeKind.Short => "INT16_MIN",
                CTypeKind.Int => "INT32_MIN",
                CTypeKind.Long => "INT64_MIN",
                CTypeKind.Nint => "INTPTR_MIN",
                _ => "0",
            };
            var max = underlying.Kind switch
            {
                CTypeKind.Byte => "UINT8_MAX",
                CTypeKind.Sbyte => "INT8_MAX",
                CTypeKind.Ushort => "UINT16_MAX",
                CTypeKind.Short => "INT16_MAX",
                CTypeKind.Uint => "UINT32_MAX",
                CTypeKind.Int => "INT32_MAX",
                CTypeKind.Ulong => "UINT64_MAX",
                CTypeKind.Long => "INT64_MAX",
                CTypeKind.Nuint => "UINTPTR_MAX",
                CTypeKind.Nint => "INTPTR_MAX",
                _ => "INT32_MAX",
            };
            var bits = underlying.Kind switch { CTypeKind.Byte or CTypeKind.Sbyte => 8, CTypeKind.Ushort or CTypeKind.Short => 16, CTypeKind.Uint or CTypeKind.Int => 32, _ => 64 };
            writer.WriteLine($"static bool {helper}(ct_string* text, bool ignore_case, bool throwing, {cType}* result)");
            writer.WriteLine("{");
            writer.WriteLine($"    *result = ({cType})0; if (text == NULL) {{ if (throwing) ct_parse_throw(CT_PARSE_NULL); return false; }} const uint8_t* first = text->Data; const uint8_t* last = first + text->Length; while (first < last && ct_parse_ascii_space(*first)) ++first; while (last > first && ct_parse_ascii_space(last[-1])) --last; if (first == last) {{ if (throwing) ct_parse_throw(CT_PARSE_FORMAT); return false; }}");
            writer.WriteLine("    bool has_comma = memchr(first, ',', (size_t)(last - first)) != NULL; bool numeric = !has_comma && ((*first >= '0' && *first <= '9') || *first == '+' || *first == '-');");
            writer.WriteLine("    if (numeric) {");
            writer.WriteLine("        bool negative = false; const uint8_t* cursor = first; if (*cursor == '+' || *cursor == '-') { negative = *cursor++ == '-'; if (cursor == last) { if (throwing) ct_parse_throw(CT_PARSE_FORMAT); return false; } }");
            if (unsigned)
                writer.WriteLine($"        if (negative) {{ if (throwing) ct_parse_throw(CT_PARSE_OVERFLOW); return false; }} uint64_t limit = (uint64_t){max}; uint64_t value = 0; while (cursor < last) {{ uint8_t c = *cursor++; if (c < '0' || c > '9') {{ if (throwing) ct_parse_throw(CT_PARSE_FORMAT); return false; }} uint32_t digit = (uint32_t)(c - '0'); if (value > (limit - digit) / 10u) {{ if (throwing) ct_parse_throw(CT_PARSE_OVERFLOW); return false; }} value = value * 10u + digit; }} *result = ({cType})value; return true;");
            else
                writer.WriteLine($"        uint64_t limit = negative ? (uint64_t)(-((int64_t){min} + 1)) + 1u : (uint64_t){max}; uint64_t magnitude = 0; while (cursor < last) {{ uint8_t c = *cursor++; if (c < '0' || c > '9') {{ if (throwing) ct_parse_throw(CT_PARSE_FORMAT); return false; }} uint32_t digit = (uint32_t)(c - '0'); if (magnitude > (limit - digit) / 10u) {{ if (throwing) ct_parse_throw(CT_PARSE_OVERFLOW); return false; }} magnitude = magnitude * 10u + digit; }} int64_t value = negative ? (magnitude == limit ? (int64_t){min} : -(int64_t)magnitude) : (int64_t)magnitude; *result = ({cType})value; return true;");
            writer.WriteLine("    }");
            writer.WriteLine("    uint64_t combined = 0u; const uint8_t* cursor = first; while (cursor < last) { const uint8_t* end = (const uint8_t*)memchr(cursor, ',', (size_t)(last - cursor)); if (end == NULL) end = last; while (cursor < end && ct_parse_ascii_space(*cursor)) ++cursor; while (end > cursor && ct_parse_ascii_space(end[-1])) --end; if (cursor == end) { if (throwing) ct_parse_throw(CT_PARSE_FORMAT); return false; } bool matched = false;");
            foreach (var value in type.EnumValues.OrderBy(value => value.Syntax.Span.Start))
            {
                var escaped = EscapeCString(value.Name);
                var constant = FormatIntegralConstant(value.Value, underlying);
                writer.WriteLine($"        if (!matched && ct_enum_name_equal(cursor, (size_t)(end - cursor), \"{escaped}\", ignore_case)) {{ combined |= (uint64_t)({CTypeName(underlying)})({constant}); matched = true; }}");
            }
            writer.WriteLine("        if (!matched) { if (throwing) ct_parse_throw(CT_PARSE_FORMAT); return false; } cursor = end; if (cursor < last) ++cursor; }");
            writer.WriteLine($"    *result = ({cType})combined; return true;");
            writer.WriteLine("}");
        }
    }
}
