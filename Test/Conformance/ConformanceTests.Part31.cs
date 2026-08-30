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

        suite.Run("draft 0.38 HostedIo four-ray packets match scalar odd-width oracle", () =>
        {
            static string Harness(bool packet) => $$"""
                using System;
                using System.IO;
                namespace HostedIoExample;

                public static class PacketTestProgram
                {
                    [EntryPoint] public static void Main()
                    {
                        FileHandle image = File.Open("image.ppm", FileMode.Create, FileAccess.Write);
                        defer File.Close(image);
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
                        camera.{{(packet ? "Render" : "RenderScalar")}}(image, world,
                            RandomGenerator.DefaultRenderSeed);
                    }
                }
                """;

            var packet = CompileAndRun(HostedIoSources(Harness(true)), captureFile: "image.ppm");
            var scalar = CompileAndRun(HostedIoSources(Harness(false)), captureFile: "image.ppm");
            Assert(packet.ExitCode == 0, packet.StandardError);
            Assert(scalar.ExitCode == 0, scalar.StandardError);
            Assert((packet.CapturedFile ?? []).SequenceEqual(scalar.CapturedFile ?? []),
                "Four-ray production batching changed the odd-width seeded PPM.");

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
                        bool any = world.Hit4(rays, new Interval(0.001f, 1000.0f), PacketMasks.First(3), out hits);
                        Console.WriteLine(any && hits.HitMask.MoveMask() == 5u
                            && hits.Lane0.Distance == hits.Lane2.Distance);
                    }
                }
                """;
            var traversal = CompileAndRun(HostedIoSources(traversalHarness));
            Assert(traversal.ExitCode == 0 && traversal.StandardOutput.Trim() == "True",
                $"Packet traversal or inactive-lane handling failed: {traversal.StandardOutput}{traversal.StandardError}");
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
