using System.Collections.Immutable;
using System.Text.Json;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart31(ConformanceSuite suite)
    {
        suite.Run("draft 0.38 SIMD optimization manifest contract", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ctilde-simd-manifest", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "Program.ct"),
                    "public static class Program { [EntryPoint] public static void Main() { } }");

                string Manifest(string extra) => $$"""
                    {
                      "target": "hosted",
                      "sources": ["*.ct"]{{extra}}
                    }
                    """;

                var path = Path.Combine(root, "ctilde.json");
                File.WriteAllText(path, Manifest(string.Empty));
                var defaults = CTildeProjectFile.Load(path);
                Assert(!defaults.Configuration.SimdOptimizations, "SIMD optimizations did not default to false.");
                Assert(!defaults.Configuration.CpuFeatures.Contains(CpuFeature.Simd128), "The disabled manifest implicitly selected SIMD128.");

                File.WriteAllText(path, Manifest(",\n  \"simdOptimizations\": true"));
                var enabled = CTildeProjectFile.Load(path);
                Assert(enabled.Configuration.SimdOptimizations, "The manifest did not retain simdOptimizations=true.");
                var enabledOptions = new CompilationOptions(Architecture: CompilationArchitecture.X64,
                    CpuFeatures: enabled.Configuration.CpuFeatures, SimdOptimizations: enabled.Configuration.SimdOptimizations);
                Assert(enabledOptions.EffectiveCpuFeatures.Contains(CpuFeature.Simd128), "Automatic SIMD optimization did not imply SIMD128.");

                File.WriteAllText(path, Manifest(",\n  \"cpuFeatures\": [\"simd128\"],\n  \"simdOptimizations\": false"));
                var explicitFeature = CTildeProjectFile.Load(path);
                Assert(!explicitFeature.Configuration.SimdOptimizations && explicitFeature.Configuration.CpuFeatures.Contains(CpuFeature.Simd128),
                    "simdOptimizations=false overrode an explicit SIMD128 feature.");

                File.WriteAllText(path, Manifest(",\n  \"architecture\": \"x86\",\n  \"simdOptimizations\": true"));
                AssertThrowsProject(path, "x64");
                File.WriteAllText(path, "{ \"target\": \"freestanding\", \"architecture\": \"x64\", \"sources\": [\"*.ct\"], \"simdOptimizations\": true }");
                AssertThrowsProject(path, "hosted");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });

        suite.Run("draft 0.38 Vec3x4 layout semantics and ABI", () =>
        {
            const string source = """
                using System;
                using System.Simd;
                public static class Program
                {
                    private static bool Near(float left, float right) { return Math.Abs(left - right) < 0.0001f; }
                    [EntryPoint] public static unsafe void Main()
                    {
                        Vec3x4 values = new Vec3x4(F32x4.Create(1.0f, 2.0f, 3.0f, 4.0f),
                            F32x4.Create(5.0f, 6.0f, 7.0f, 8.0f), F32x4.Create(9.0f, 10.0f, 11.0f, 12.0f));
                        Vec3 lane = values.GetLane<2>();
                        Vec3x4 replaced = values.WithLane<1>(new Vec3(-2.0f, -3.0f, -4.0f));
                        Vec3x4 other = Vec3x4.Splat(new Vec3(2.0f, 3.0f, 4.0f));
                        F32x4 dots = values.Dot(other);
                        Vec3 cross = values.Cross(other).GetLane<0>();
                        Mask32x4 mask = F32x4.CompareLessThan(F32x4.Create(0.0f, 2.0f, 0.0f, 2.0f), F32x4.Splat(1.0f));
                        Vec3x4 selected = Vec3x4.Select(mask, values, other);
                        Console.WriteLine(sizeof(Vec3x4));
                        Console.WriteLine(Near(lane.X, 3.0f) && Near(lane.Y, 7.0f) && Near(lane.Z, 11.0f));
                        Console.WriteLine(Near(replaced.GetLane<1>().Z, -4.0f));
                        Console.WriteLine(Near(dots.GetLane<0>(), 53.0f));
                        Console.WriteLine(Near(cross.X, -7.0f) && Near(cross.Y, 14.0f) && Near(cross.Z, -7.0f));
                        Console.WriteLine(Near(selected.GetLane<0>().X, 1.0f) && Near(selected.GetLane<1>().X, 2.0f));
                    }
                }
                """;
            var run = CompileAndRun(source);
            Assert(run.ExitCode == 0, run.StandardError);
            Assert(run.StandardOutput.Replace("\r", string.Empty, StringComparison.Ordinal).Trim() == "48\nTrue\nTrue\nTrue\nTrue\nTrue",
                $"Vec3x4 behavior or storage changed: {run.StandardOutput}{run.StandardError}");

            Assert(Compile(source.Replace("GetLane<2>()", "GetLane<4>()", StringComparison.Ordinal)).GetDiagnostics()
                .Any(diagnostic => diagnostic.Code == "CT2220"), "An invalid Vec3x4 lane was accepted.");
            const string nativePacket = "using System.Simd; public static class P { [Extern(\"native_packet\")] public static Vec3x4 Native(Vec3x4 value); [EntryPoint] public static void Main() { } }";
            Assert(Compile(nativePacket).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1279"),
                "Vec3x4 crossed a native ABI boundary.");

            var debugCompilation = Compile(source, new CompilationOptions(DebugInformation: DebugInformationMode.Source));
            using var debugMap = new StringWriter();
            Assert(debugCompilation.EmitDebugMap(debugMap).Success, "Vec3x4 debug-map emission failed.");
            using var debugDocument = JsonDocument.Parse(debugMap.ToString());
            var vec3x4 = debugDocument.RootElement.GetProperty("types").EnumerateArray()
                .Single(type => type.GetProperty("name").GetString() == "System.Simd.Vec3x4");
            var shape = vec3x4.GetProperty("simd");
            Assert(shape.GetProperty("laneType").GetString() == "float32" && shape.GetProperty("laneCount").GetInt32() == 4
                && shape.GetProperty("componentCount").GetInt32() == 3,
                "Vec3x4 debug metadata lost its three-component four-lane shape.");
        });

        suite.Run("draft 0.38 hosted x64 scalar geometry lowering", () =>
        {
            const string source = """
                using System;
                using System.Runtime;
                using System.Simd;
                public static class Program
                {
                    public static volatile int RuntimeZero;
                    private static Vec2 Work2(Vec2 left, Vec2 right) { return Vec2.Abs(left + right * 2.0f); }
                    private static Vec3 Work3(Vec3 left, Vec3 right) { return left.Cross(right) + right; }
                    private static Vec4 Work4(Vec4 left, Vec4 right) { return(left - right) / 2.0f; }
                    private static Matrix3x2 Work32(Matrix3x2 left, Matrix3x2 right) { return left * right + left; }
                    private static Matrix4x4 Work44(Matrix4x4 left, Matrix4x4 right) { return left + right * 2.0f; }
                    private static Quaternion WorkQ(Quaternion left, Quaternion right) { return left + right * 2.0f; }
                    private static unsafe bool NegativeZero(float value) { return F32x4.AsU32(F32x4.Splat(value)).GetLane<0>() == 0x80000000u; }
                    [EntryPoint] public static unsafe void Main()
                    {
                        Console.WriteLine(Target.HasFeature(CpuFeature.Simd128));
                        Console.WriteLine(Work2(new Vec2(1.0f, -2.0f), new Vec2(2.0f, 3.0f)).X);
                        Console.WriteLine(Work3(new Vec3(1.0f, 0.0f, 0.0f), new Vec3(0.0f, 1.0f, 0.0f)).Z);
                        Console.WriteLine(Work4(new Vec4(4.0f), new Vec4(2.0f)).W);
                        Console.WriteLine(Work32(Matrix3x2.Identity, Matrix3x2.Identity).M11);
                        Console.WriteLine(Work44(Matrix4x4.Identity, Matrix4x4.Identity).M11);
                        Console.WriteLine(WorkQ(Quaternion.Identity, Quaternion.Identity).W);
                        RuntimeZero = 0;
                        float positiveZero = RuntimeZero;
                        float negativeZero = -positiveZero;
                        float nan = Math.Sqrt(-1.0f);
                        Vec4 minimum = Vec4.Min(new Vec4(positiveZero, negativeZero, nan, 4.0f),
                            new Vec4(negativeZero, positiveZero, 3.0f, nan));
                        Vec4 maximum = Vec4.Max(new Vec4(positiveZero, negativeZero, nan, 4.0f),
                            new Vec4(negativeZero, positiveZero, 3.0f, nan));
                        Console.WriteLine(NegativeZero(minimum.X) && NegativeZero(minimum.Y)
                            && minimum.Z == 3.0f && minimum.W == 4.0f);
                        Console.WriteLine(!NegativeZero(maximum.X) && !NegativeZero(maximum.Y)
                            && maximum.Z == 3.0f && maximum.W == 4.0f);
                    }
                }
                """;
            var disabled = Emit(source, new CompilationOptions(Architecture: CompilationArchitecture.X64));
            Assert(!disabled.Contains("_mm_setr_ps", StringComparison.Ordinal), "Transparent geometry intrinsics were emitted while disabled.");

            var options = new CompilationOptions(Architecture: CompilationArchitecture.X64, SimdOptimizations: true);
            var enabled = Emit(source, options);
            Assert(enabled.Contains("#define CTILDE_CPU_SIMD128 1", StringComparison.Ordinal), "The optimization flag did not expose SIMD128 to Target.HasFeature.");
            Assert(enabled.Contains("_mm_setr_ps", StringComparison.Ordinal) && enabled.Contains("_mm_add_ps", StringComparison.Ordinal)
                && enabled.Contains("_mm_storeu_ps", StringComparison.Ordinal), "Representative scalar geometry SSE2 kernels were not emitted.");
            var enabledRun = CompileAndRun(source, options);
            var explicitRun = CompileAndRun(source, new CompilationOptions(Architecture: CompilationArchitecture.X64,
                CpuFeatures: ImmutableArray.Create(CpuFeature.Simd128)));
            Assert(enabledRun.ExitCode == 0 && explicitRun.ExitCode == 0 && enabledRun.StandardOutput == explicitRun.StandardOutput,
                $"Automatic and explicit SIMD executions differed. Automatic:\n{enabledRun.StandardOutput}{enabledRun.StandardError}\nExplicit:\n{explicitRun.StandardOutput}{explicitRun.StandardError}");

            Assert(Compile(source, new CompilationOptions(CompilationTarget.Hosted, Architecture: CompilationArchitecture.X86,
                SimdOptimizations: true)).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4122"),
                "The API accepted automatic SIMD optimization on hosted x86.");
            Assert(Compile(source, new CompilationOptions(CompilationTarget.Freestanding, Architecture: CompilationArchitecture.X64,
                SimdOptimizations: true)).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4122"),
                "The API accepted automatic SIMD optimization on freestanding x64.");
        });

        suite.Run("draft 0.38 hosted x64 packet SIMD operations", () =>
        {
            const string source = """
                using System;
                using System.Simd;
                public static class Program
                {
                    [NoAlloc]
                    [NoThrow]
                    [NoBlock]
                    [NoRuntime]
                    private static U32x4 Fused(U32x4 left, U32x4 right, U32x4 addend)
                    {
                        return left * right + addend;
                    }

                    [EntryPoint] public static unsafe void Main()
                    {
                        U32x4 left = U32x4.Create(0xffffffffu, 0x80000000u, 3u, 0x40000001u);
                        U32x4 product = Fused(left, U32x4.Create(2u, 3u, 7u, 4u), U32x4.Zero);
                        Console.WriteLine(product.GetLane<0>() == 0xfffffffeu && product.GetLane<1>() == 0x80000000u
                            && product.GetLane<2>() == 21u && product.GetLane<3>() == 4u);
                        U32x4 shifted = left.ShiftLeft<0>().ShiftRight<31>();
                        Console.WriteLine(shifted.GetLane<0>() == 1u && shifted.GetLane<1>() == 1u
                            && shifted.GetLane<2>() == 0u && shifted.GetLane<3>() == 0u);
                        U32x4 shiftedBoundary = left.ShiftLeft<31>().ShiftRight<31>();
                        Console.WriteLine(shiftedBoundary.GetLane<0>() == 1u && shiftedBoundary.GetLane<1>() == 0u
                            && shiftedBoundary.GetLane<2>() == 1u && shiftedBoundary.GetLane<3>() == 1u);
                        Mask32x4 unsignedLess = U32x4.CompareLessThan(left, U32x4.Create(0u, 0xffffffffu, 4u, 0x40000000u));
                        U32x4 selected = U32x4.Select(unsignedLess, U32x4.Splat(11u), U32x4.Splat(22u));
                        Console.WriteLine(unsignedLess.MoveMask() == 6u && selected.GetLane<0>() == 22u
                            && selected.GetLane<1>() == 11u && selected.GetLane<2>() == 11u && selected.GetLane<3>() == 22u);
                        I32x4 signed = I32x4.Create(-2147483648, -1, 0, 2147483647);
                        Console.WriteLine(I32x4.CompareLessThan(signed, I32x4.Zero).MoveMask() == 3u
                            && signed.ShiftRight<31>().GetLane<0>() == -1 && signed.ShiftRight<31>().GetLane<2>() == 0);
                        float nan = System.Math.Sqrt(-1.0f);
                        F32x4 floats = F32x4.Create(-0.0f, 0.0f, nan, 2.0f);
                        Console.WriteLine(F32x4.CompareEqual(floats, F32x4.Zero).MoveMask() == 3u
                            && F32x4.CompareNotEqual(floats, F32x4.Zero).MoveMask() == 12u);
                        F32x4 converted = F32x4.FromU32(U32x4.Create(0u, 16777217u, 0x80000000u, 0xffffffffu));
                        Console.WriteLine(converted.GetLane<0>() == 0.0f && converted.GetLane<1>() == 16777216.0f
                            && converted.GetLane<2>() == 2147483648.0f && converted.GetLane<3>() == 4294967296.0f);
                        Mask32x4 all = Mask32x4.Not(Mask32x4.FromBools(false, false, false, false));
                        Console.WriteLine(Mask32x4.AndNot(unsignedLess, all).MoveMask() == 9u);
                        Console.WriteLine(unsignedLess.Any() && !unsignedLess.All() && !unsignedLess.None()
                            && all.All() && !all.None());
                        F32x4 minimum = F32x4.Min(F32x4.Create(0.0f, -0.0f, nan, 4.0f),
                            F32x4.Create(-0.0f, 0.0f, 3.0f, nan));
                        F32x4 maximum = F32x4.Max(F32x4.Create(0.0f, -0.0f, nan, 4.0f),
                            F32x4.Create(-0.0f, 0.0f, 3.0f, nan));
                        Console.WriteLine(F32x4.AsU32(minimum).GetLane<0>() == 0x80000000u
                            && F32x4.AsU32(minimum).GetLane<1>() == 0x80000000u
                            && minimum.GetLane<2>() == 3.0f && minimum.GetLane<3>() == 4.0f);
                        Console.WriteLine(F32x4.AsU32(maximum).GetLane<0>() == 0u
                            && F32x4.AsU32(maximum).GetLane<1>() == 0u
                            && maximum.GetLane<2>() == 3.0f && maximum.GetLane<3>() == 4.0f);
                    }
                }
                """;
            var scalar = CompileAndRun(source, new CompilationOptions(Architecture: CompilationArchitecture.X64));
            var options = new CompilationOptions(Architecture: CompilationArchitecture.X64,
                CpuFeatures: ImmutableArray.Create(CpuFeature.Simd128));
            var simd = CompileAndRun(source, options);
            Assert(scalar.ExitCode == 0 && simd.ExitCode == 0 && scalar.StandardOutput == simd.StandardOutput,
                $"Scalar and SSE2 packet operations differed. Scalar:\n{scalar.StandardOutput}{scalar.StandardError}\nSSE2:\n{simd.StandardOutput}{simd.StandardError}");
            Assert(simd.StandardOutput.Replace("\r", string.Empty, StringComparison.Ordinal).Trim() ==
                "True\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue", $"Unexpected SIMD operation result: {simd.StandardOutput}");
            var emitted = Emit(source, options);
            Assert(emitted.Contains("_mm_mul_epu32", StringComparison.Ordinal) && emitted.Contains("_mm_slli_epi32", StringComparison.Ordinal)
                && emitted.Contains("_mm_srli_epi32", StringComparison.Ordinal) && emitted.Contains("_mm_srai_epi32", StringComparison.Ordinal)
                && emitted.Contains("_mm_cmplt_epi32", StringComparison.Ordinal) && emitted.Contains("_mm_cmpneq_ps", StringComparison.Ordinal)
                && emitted.Contains("_mm_cvtepi32_ps", StringComparison.Ordinal) && emitted.Contains("_mm_andnot_si128", StringComparison.Ordinal)
                && emitted.Contains("_mm_movemask_ps", StringComparison.Ordinal) && emitted.Contains("_mm_min_ps", StringComparison.Ordinal)
                && emitted.Contains("_mm_max_ps", StringComparison.Ordinal) && emitted.Contains("_mm_set1_epi32", StringComparison.Ordinal),
                "The hosted x64 backend did not emit every packet SIMD operation family.");
            Assert(System.Text.RegularExpressions.Regex.IsMatch(emitted,
                    @"=\s+ct_o_[0-9a-f]+\(ct_o_[0-9a-f]+\("),
                "Single-use same-block SIMD operators were materialized instead of fused.");
        });

        suite.Run("draft 0.38 HostedIo packet golden and traversal fixtures", () =>
        {
            static string Harness(bool packet) => $$"""
                using System;
                namespace HostedIoExample;

                public static class PacketTestProgram
                {
                    [EntryPoint] public static void Main()
                    {
                        HittableList objects = new HittableList();
                        objects.Add(new Sphere(new Vec3(0.0f, 0.0f, -1.0f), 0.5f,
                            new Lambertian(new Vec3(0.7f, 0.3f, 0.2f))));
                        objects.Add(new Sphere(new Vec3(0.0f, -100.5f, -1.0f), 100.0f,
                            new Lambertian(new Vec3(0.8f, 0.8f, 0.0f))));
                        Hittable world = objects.BuildBvh();
                        Camera camera = new Camera();
                        camera.AspectRatio = 1.7f;
                        camera.ImageWidth = 17;
                        camera.SamplesPerPixel = 3;
                        camera.MaxDepth = 5;
                        camera.DefocusAngle = 0.25f;
                        camera.FocusDistance = 1.0f;
                        camera.ProgressRows = 0;
                        Rgba32[] pixels = new Rgba32[camera.ImageWidth * camera.ImageHeight];
                        camera.{{(packet ? "Render" : "RenderScalar")}}(pixels, world,
                            RandomGenerator.DefaultRenderSeed);
                        Console.WriteLine(PixelBuffer.Checksum(pixels));
                    }
                }
                """;

            var packet = CompileAndRun(HostedIoSources(Harness(true)));
            Assert(packet.ExitCode == 0, packet.StandardError);
            var packetHash = Normalize(packet.StandardOutput).Trim();
            Assert(packetHash == "1657345586", $"The optimized odd-width RGBA golden changed: {packetHash}.");
            var repeated = CompileAndRun(HostedIoSources(Harness(true)));
            Assert(repeated.ExitCode == 0 && Normalize(packet.StandardOutput) == Normalize(repeated.StandardOutput),
                "The optimized packet renderer was not deterministic across repeated seeded renders.");

            const string parallelHarness = """
                using System;
                namespace HostedIoExample;

                public static class ParallelPacketTestProgram
                {
                    [EntryPoint] public static void Main()
                    {
                        HittableList objects = new HittableList();
                        objects.Add(new Sphere(new Vec3(0.0f, 0.0f, -1.0f), 0.5f,
                            new Lambertian(new Vec3(0.7f, 0.3f, 0.2f))));
                        objects.Add(new Sphere(new Vec3(0.0f, -100.5f, -1.0f), 100.0f,
                            new Lambertian(new Vec3(0.8f, 0.8f, 0.0f))));
                        Hittable world = objects.BuildBvh();
                        Camera camera = new Camera();
                        camera.AspectRatio = 1.7f;
                        camera.ImageWidth = 17;
                        camera.SamplesPerPixel = 3;
                        camera.MaxDepth = 5;
                        camera.DefocusAngle = 0.25f;
                        camera.FocusDistance = 1.0f;
                        camera.ProgressRows = 0;
                        camera.InitializeForRender();
                        ParallelRenderSession render = new ParallelRenderSession(camera,
                            world, RandomGenerator.DefaultRenderSeed);
                        render.Start();
                        render.Join();
                        Console.WriteLine(render.IsComplete);
                        Console.WriteLine(render.PixelChecksum());

                        RenderWorkState workState = new RenderWorkState(camera.ImageWidth,
                            camera.ImageHeight);
                        Rgba32[] bandPixels = new Rgba32[camera.ImageWidth
                            * camera.ImageHeight];
                        RenderWorker band = new RenderWorker(workState, camera, world,
                            bandPixels, RandomGenerator.DefaultRenderSeed, 2, 4);
                        band.Run();
                        bool linear = band.PublishedCount == camera.ImageWidth * 2;
                        int completed = 0;
                        while (completed < band.PublishedCount)
                        {
                            if (band.CompletedPixelAt(completed)
                                != camera.ImageWidth * 2 + completed)
                                linear = false;
                            completed++;
                        }
                        Console.WriteLine(linear);
                    }
                }
                """;
            var parallel = CompileAndRun(HostedIoSources(parallelHarness), threads: true);
            Assert(parallel.ExitCode == 0, parallel.StandardError);
            Assert(Normalize(parallel.StandardOutput).Trim() == "True\n1657345586\nTrue",
                $"The twelve-worker linear-band renderer diverged: {parallel.StandardOutput}{parallel.StandardError}");

            const string traversalHarness = """
                using System;
                using System.Simd;
                namespace HostedIoExample;
                public static class PacketTraversalProgram
                {
                    [EntryPoint] public static void Main()
                    {
                        HittableList objects = new HittableList();
                        objects.Add(new Sphere(new Vec3(0.0f, 0.0f, -1.0f), 0.5f,
                            new Lambertian(Vec3.One)));
                        Hittable world = objects.BuildBvh();
                        RayPacket rays = RayPacket.Create(
                            new Ray(Vec3.Zero, new Vec3(0.0f, 0.0f, -1.0f)),
                            new Ray(Vec3.Zero, new Vec3(2.0f, 0.0f, -1.0f)),
                            new Ray(Vec3.Zero, new Vec3(0.0f, 0.0f, -1.0f)),
                            new Ray(Vec3.Zero, new Vec3(0.0f, 0.0f, -1.0f)));
                        HitPacket hits;
                        RayInterval4 interval = RayInterval4.Splat(0.001f, 1000.0f);
                        bool any = world.Hit4(ref rays, ref interval, PacketMasks.First(3), out hits);
                        Console.WriteLine(any && hits.HitMask.MoveMask() == 5u
                            && hits.Distances.GetLane<0>() == hits.Distances.GetLane<2>());

                        Aabb box = new Aabb(new Vec3(-1.0f), new Vec3(1.0f));
                        RayPacket boxRays = RayPacket.Create(
                            new Ray(new Vec3(0.0f, 0.0f, 2.0f), new Vec3(0.0f, 0.0f, -1.0f)),
                            new Ray(new Vec3(2.0f, 0.0f, 0.0f), new Vec3(0.0f, 1.0f, 0.0f)),
                            new Ray(new Vec3(0.0f, 0.0f, -2.0f), new Vec3(0.0f, 0.0f, 1.0f)),
                            new Ray(new Vec3(0.0f, 0.0f, 2.0f), new Vec3(0.0f, 0.0f, -1.0f)));
                        F32x4 entries;
                        Mask32x4 boxHits = box.Hit4(ref boxRays, ref interval, PacketMasks.First(3), out entries);
                        Console.WriteLine(boxHits.MoveMask() == 5u && entries.GetLane<0>() == 1.0f
                            && entries.GetLane<2>() == 1.0f);

                        HittableList divergent = new HittableList();
                        divergent.Add(new Sphere(new Vec3(-1.0f, 0.0f, -3.0f), 0.75f,
                            new Lambertian(new Vec3(0.25f, 0.5f, 0.75f))));
                        divergent.Add(new Sphere(new Vec3(1.0f, 0.0f, -3.0f), 0.75f,
                            new Metal(new Vec3(0.75f, 0.5f, 0.25f), 0.0f)));
                        Hittable divergentWorld = divergent.BuildBvh();
                        RayPacket divergentRays = RayPacket.Create(
                            new Ray(Vec3.Zero, new Vec3(-1.0f, 0.0f, -3.0f)),
                            new Ray(Vec3.Zero, new Vec3(1.0f, 0.0f, -3.0f)),
                            new Ray(Vec3.Zero, new Vec3(-1.0f, 0.0f, -3.0f)),
                            new Ray(Vec3.Zero, new Vec3(1.0f, 0.0f, -3.0f)));
                        HitPacket divergentHits;
                        bool divergentAny = divergentWorld.Hit4(ref divergentRays, ref interval,
                            PacketMasks.First(4), out divergentHits);
                        Console.WriteLine(divergentAny && divergentHits.HitMask.MoveMask() == 15u
                            && divergentHits.Materials.Kinds.GetLane<0>() == (uint)MaterialKind.Lambertian
                            && divergentHits.Materials.Kinds.GetLane<1>() == (uint)MaterialKind.Metal);

                        HitPacket materialHits = HitPacket.Empty(F32x4.Splat(100.0f));
                        materialHits.Points = Vec3x4.Zero;
                        materialHits.Normals = Vec3x4.Splat(Vec3.UnitY);
                        materialHits.HitMask = PacketMasks.First(3);
                        materialHits.FrontFaceMask = PacketMasks.First(3);
                        materialHits.Materials = new MaterialPacket(U32x4.Create(
                            (uint)MaterialKind.Lambertian, (uint)MaterialKind.Metal,
                            (uint)MaterialKind.Dielectric, (uint)MaterialKind.None),
                            new Vec3x4(F32x4.Create(0.2f, 0.4f, 1.0f, 0.0f),
                                F32x4.Create(0.3f, 0.5f, 1.0f, 0.0f),
                                F32x4.Create(0.4f, 0.6f, 1.0f, 0.0f)),
                            F32x4.Create(0.0f, 0.0f, 1.5f, 0.0f));
                        RayPacket incoming = new RayPacket(Vec3x4.Zero,
                            Vec3x4.Splat(new Vec3(0.0f, -1.0f, 0.0f)));
                        PacketRandomGenerator random = new PacketRandomGenerator(123u);
                        random.Reseed(PacketRandomGenerator.SampleSeeds(123u, 0, 0, 0,
                            PacketMasks.First(3)));
                        Vec3x4 attenuation;
                        RayPacket scattered;
                        Mask32x4 scatteredMask = PacketMaterials.Scatter(ref incoming,
                            ref materialHits, PacketMasks.First(3), random, out attenuation,
                            out scattered);
                        Console.WriteLine(scatteredMask.MoveMask() == 7u
                            && attenuation.X.GetLane<0>() == 0.2f
                            && attenuation.Y.GetLane<1>() == 0.5f
                            && attenuation.Z.GetLane<2>() == 1.0f);

                        PacketRandomGenerator firstRandom = new PacketRandomGenerator(321u);
                        PacketRandomGenerator secondRandom = new PacketRandomGenerator(321u);
                        Vec3x4 firstSample = firstRandom.InUnitSphere(PacketMasks.First(3));
                        Vec3x4 secondSample = secondRandom.InUnitSphere(PacketMasks.First(3));
                        Console.WriteLine(F32x4.CompareEqual(firstSample.X, secondSample.X).All()
                            && F32x4.CompareEqual(firstSample.Y, secondSample.Y).All()
                            && F32x4.CompareEqual(firstSample.Z, secondSample.Z).All());
                    }
                }
                """;
            var traversal = CompileAndRun(HostedIoSources(traversalHarness));
            Assert(traversal.ExitCode == 0 && traversal.StandardOutput.Replace("\r", string.Empty, StringComparison.Ordinal).Trim() ==
                "True\nTrue\nTrue\nTrue\nTrue",
                $"Packet traversal or inactive-lane handling failed: {traversal.StandardOutput}{traversal.StandardError}");
        });

        suite.Run("draft 0.44 flattened SAH BVH construction and equivalence", () =>
        {
            const string harness = """
                using System;
                namespace HostedIoExample;
                public static class SahBvhTestProgram
                {
                    private static bool Close(float left, float right)
                    {
                        return Math.Abs(left - right) < 0.0001f;
                    }

                    [EntryPoint] public static unsafe void Main()
                    {
                        Ray forward = new Ray(Vec3.Zero, new Vec3(0.0f, 0.0f, -1.0f));
                        Interval interval = new Interval(0.001f, 1000.0f);
                        HittableList empty = new HittableList();
                        HitRecord emptyHit;
                        Console.WriteLine(!empty.BuildSahBvh().Hit(forward, interval, out emptyHit));

                        Material material = new Lambertian(Vec3.One);
                        HittableList singletonList = new HittableList();
                        singletonList.Add(new Sphere(new Vec3(0.0f, 0.0f, -2.0f), 0.5f, material));
                        FlattenedSahBvh singleton = (FlattenedSahBvh)singletonList.BuildSahBvh();
                        Console.WriteLine(singleton.NodeCount == 1 && singleton.LeafCount == 1
                            && singleton.MaximumDepth == 0 && singleton.PrimitiveCount == 1);

                        HittableList equalCentroids = new HittableList();
                        int index = 0;
                        while (index < 17)
                        {
                            equalCentroids.Add(new Sphere(new Vec3(0.0f, 0.0f, -4.0f),
                                0.1f + (float)index * 0.01f, material));
                            index++;
                        }
                        FlattenedSahBvh equalSah = (FlattenedSahBvh)equalCentroids.BuildSahBvh();
                        Hittable equalMidpoint = equalCentroids.BuildMidpointBvh();
                        HitRecord listHit;
                        HitRecord sahHit;
                        HitRecord midpointHit;
                        bool listFound = equalCentroids.Hit(forward, interval, out listHit);
                        bool sahFound = equalSah.Hit(forward, interval, out sahHit);
                        bool midpointFound = equalMidpoint.Hit(forward, interval, out midpointHit);
                        Console.WriteLine(listFound && sahFound && midpointFound
                            && Close(listHit.Distance, sahHit.Distance)
                            && Close(listHit.Distance, midpointHit.Distance)
                            && equalSah.NodeCount == equalSah.LeafCount * 2 - 1
                            && equalSah.MaximumDepth <= 32);

                        HittableList degenerateAxis = new HittableList();
                        index = 0;
                        while (index < 64)
                        {
                            degenerateAxis.Add(new Sphere(new Vec3(0.0f, 0.0f,
                                -2.0f - (float)index), 0.2f, material));
                            index++;
                        }
                        FlattenedSahBvh first = (FlattenedSahBvh)degenerateAxis.BuildSahBvh();
                        FlattenedSahBvh second = (FlattenedSahBvh)degenerateAxis.BuildSahBvh();
                        HitRecord firstHit;
                        HitRecord secondHit;
                        bool firstFound = first.Hit(forward, interval, out firstHit);
                        bool secondFound = second.Hit(forward, interval, out secondHit);
                        Console.WriteLine(first.NodeCount == second.NodeCount
                            && first.LeafCount == second.LeafCount
                            && first.MaximumDepth == second.MaximumDepth
                            && first.PrimitiveCount == 64 && first.MaximumDepth <= 32
                            && firstFound == secondFound && Close(firstHit.Distance, secondHit.Distance));
                    }
                }
                """;
            var result = CompileAndRun(HostedIoSources(harness));
            Assert(result.ExitCode == 0 && Normalize(result.StandardOutput).Trim() == "True\nTrue\nTrue\nTrue",
                $"Flattened SAH construction or traversal diverged: {result.StandardOutput}{result.StandardError}");
        });
    }

    private static void AssertThrowsProject(string manifestPath, string fragment)
    {
        try
        {
            _ = CTildeProjectFile.Load(manifestPath);
            Assert(false, $"Manifest '{manifestPath}' was accepted.");
        }
        catch (CTildeProjectException exception)
        {
            Assert(exception.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase), exception.Message);
        }
    }
}
