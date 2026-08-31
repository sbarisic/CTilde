using CTilde;

namespace CTilde.Cli;

internal static class NativeOptimizationSettings
{
    public static IReadOnlyList<string> MsvcCompile(BuildRequest request)
    {
        var flags = new List<string>();
        if (request.Configuration == CTildeNativeBuildConfiguration.Debug)
            flags.AddRange(["/Od", "/Zi", "/Oy-"]);
        else
        {
            flags.Add("/O2");
            if (request.Optimization == NativeOptimization.Aggressive)
                flags.Add("/Ob3");
            flags.AddRange(["/Gy", "/Gw"]);
        }
        if (request.CpuTarget == NativeCpuTarget.Avx2)
            flags.Add("/arch:AVX2");
        if (request.FloatingPoint == NativeFloatingPointMode.Precise)
            flags.Add("/fp:precise");
        else if (request.FloatingPoint == NativeFloatingPointMode.Fast)
            flags.Add("/fp:fast");
        return flags;
    }

    public static IReadOnlyList<string> GnuCompile(BuildRequest request, bool includeSections)
    {
        var flags = new List<string>();
        if (request.Configuration == CTildeNativeBuildConfiguration.Debug)
            flags.AddRange(["-Og", "-g3", "-fno-omit-frame-pointer", "-fno-optimize-sibling-calls"]);
        else
        {
            flags.Add(request.Optimization == NativeOptimization.Aggressive ? "-O3" : "-O2");
            if (includeSections)
                flags.AddRange(["-ffunction-sections", "-fdata-sections"]);
        }
        flags.AddRange(GnuCpuAndFloatingPoint(request));
        return flags;
    }

    public static IReadOnlyList<string> GnuLink(BuildRequest request)
    {
        var flags = new List<string>();
        if (request.Configuration == CTildeNativeBuildConfiguration.Release)
            flags.Add(request.Optimization == NativeOptimization.Aggressive ? "-O3" : "-O2");
        flags.AddRange(GnuCpuAndFloatingPoint(request));
        if (request.Lto)
            flags.Add("-flto");
        return flags;
    }

    public static IReadOnlyList<string> CosmopolitanCompile(BuildRequest request)
    {
        if (request.Configuration == CTildeNativeBuildConfiguration.Debug)
            return ["-Og", "-g3", "-fno-omit-frame-pointer", "-fno-optimize-sibling-calls", .. GnuCpuAndFloatingPoint(request)];
        if (request.CosmopolitanMode == CosmopolitanRuntimeMode.Tiny)
            return ["-Os", .. GnuCpuAndFloatingPoint(request)];
        return [request.Optimization == NativeOptimization.Aggressive ? "-O3" : "-O2", .. GnuCpuAndFloatingPoint(request)];
    }

    public static IReadOnlyList<string> CosmopolitanLink(BuildRequest request)
    {
        var flags = new List<string>();
        if (request.Configuration == CTildeNativeBuildConfiguration.Release)
            flags.Add(request.CosmopolitanMode == CosmopolitanRuntimeMode.Tiny
                ? "-Os"
                : request.Optimization == NativeOptimization.Aggressive ? "-O3" : "-O2");
        flags.AddRange(GnuCpuAndFloatingPoint(request));
        if (request.Lto)
            flags.Add("-flto");
        return flags;
    }

    public static IReadOnlyList<string> EspGeneratedCompile(BuildRequest request)
    {
        var flags = new List<string>();
        if (request.Optimization is not null)
            flags.Add(request.Optimization == NativeOptimization.Aggressive ? "-O3" : "-O2");
        flags.AddRange(GnuCpuAndFloatingPoint(request));
        return flags;
    }

    public static string AppendEspGeneratedSourceOptions(string contents, BuildRequest request)
    {
        var flags = EspGeneratedCompile(request);
        if (flags.Count == 0)
            return contents;
        var escaped = flags.Select(flag => flag.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal));
        return contents + "\n# Draft 0.41 controlled options apply only to C~-generated sources.\n" +
            "set_property(SOURCE ${CTILDE_GENERATED_SOURCES} APPEND PROPERTY COMPILE_OPTIONS " +
            string.Join(' ', escaped.Select(flag => $"\"{flag}\"")) + ")\n";
    }

    public static string Describe(BuildRequest request) =>
        $"optimization={Name(request.Optimization, "legacy")} cpu={Name(request.CpuTarget, "baseline")} " +
        $"floating-point={Name(request.FloatingPoint, "toolchain-default")} pgo={request.PgoMode.ToString().ToLowerInvariant()} lto={request.Lto}";

    private static IReadOnlyList<string> GnuCpuAndFloatingPoint(BuildRequest request)
    {
        var flags = new List<string>();
        if (request.CpuTarget == NativeCpuTarget.Avx2)
            flags.AddRange(["-march=x86-64-v3", "-mtune=generic"]);
        if (request.FloatingPoint == NativeFloatingPointMode.Precise)
            flags.AddRange(["-fno-fast-math", "-ffp-contract=off"]);
        else if (request.FloatingPoint == NativeFloatingPointMode.Fast)
            flags.Add("-ffast-math");
        return flags;
    }

    private static string Name<T>(T? value, string fallback) where T : struct =>
        value.HasValue ? $"{value.Value}".ToLowerInvariant() : fallback;
}
