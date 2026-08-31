namespace CTilde;

internal sealed partial class CEmitter
{
    private void EmitStandardUtilitySupport(CWriter writer)
    {
        if (_usesMonotonicClock)
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
        if (_usesRandomRangeFailure)
            writer.WriteLine("void ct_random_argument_out_of_range(void) { ct_raise_runtime_fault(CT_FAULT_ARGUMENT_OUT_OF_RANGE, \"CTR0001\", \"<random>\", 0); }");
        if (_usesSpinPause)
            writer.WriteLine("void ct_spin_pause(void) { ct_cpu_pause(); }");
        if (_usesMonotonicClock || _usesRandomRangeFailure || _usesSpinPause)
            writer.WriteLine();
    }
}
