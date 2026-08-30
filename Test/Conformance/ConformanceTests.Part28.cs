using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart28(ConformanceSuite suite)
    {
        suite.Run("draft 0.37 scalar SIMD matrices and quaternions", () =>
        {
            const string source = """
                using System;
                using System.Runtime;
                using System.Simd;
                public static class Program
                {
                    private static bool Near(float left, float right) { return Math.Abs(left - right) < 0.0001f; }
                    private static F32x4 FourLaneWorkload(F32x4 value) { return F32x4.MultiplyAdd(value, F32x4.Splat(2.0f), F32x4.Splat(1.0f)); }
                    private static Matrix4x4 BuildWorld() { return Matrix4x4.CreateScale(2.0f) * Matrix4x4.CreateTranslation(5.0f, 6.0f, 7.0f); }
                    private static Vec3 RotatePoint(Quaternion rotation, Vec3 value) { return rotation.Transform(value); }
                    [EntryPoint] public static unsafe void Main()
                    {
                        Console.WriteLine(sizeof(F32x4));
                        Console.WriteLine(sizeof(Quaternion));
                        Console.WriteLine(sizeof(Matrix3x2));
                        Console.WriteLine(sizeof(Matrix4x4));

                        F32x4 lanes = FourLaneWorkload(F32x4.Create(1.0f, -2.0f, 3.0f, -4.0f));
                        Console.WriteLine(Near(lanes.Sum(), 0.0f));
                        Console.WriteLine(Near(F32x4.Dot(lanes, F32x4.Splat(1.0f)), 0.0f));

                        float[] managed = new float[5];
                        F32x4.Store(lanes, managed, 1);
                        F32x4 managedRoundTrip = F32x4.Load(managed, 1);
                        Console.WriteLine(Near(managedRoundTrip.GetLane<0>(), 3.0f) && Near(managedRoundTrip.GetLane<3>(), -7.0f));

                        NativeBuffer<float> native = stackalloc float[4];
                        F32x4.Store(lanes, native, (nuint)0);
                        ReadOnlyNativeBuffer<float> readableNative = native;
                        F32x4 nativeRoundTrip = F32x4.Load(readableNative, (nuint)0);
                        Console.WriteLine(Near(nativeRoundTrip.GetLane<1>(), -3.0f) && Near(nativeRoundTrip.GetLane<2>(), 7.0f));

                        NativeBuffer<byte> unalignedStorage = stackalloc byte[17];
                        float* unaligned = (float*)(unalignedStorage.Pointer + 1);
                        F32x4.StoreUnsafe(lanes, unaligned);
                        F32x4 unalignedRoundTrip = F32x4.LoadUnsafe(unaligned);
                        Console.WriteLine(Near(unalignedRoundTrip.GetLane<0>(), 3.0f) && Near(unalignedRoundTrip.GetLane<3>(), -7.0f));

                        Matrix3x2 affine = Matrix3x2.CreateScale(2.0f, 3.0f) * Matrix3x2.CreateTranslation(5.0f, 7.0f);
                        Vec2 point2 = affine.TransformPoint(new Vec2(1.0f, 2.0f));
                        Console.WriteLine(Near(point2.X, 7.0f) && Near(point2.Y, 13.0f));
                        Matrix3x2 inverse2;
                        Console.WriteLine(Matrix3x2.TryInvert(affine, out inverse2));
                        Vec2 restored2 = inverse2.TransformPoint(point2);
                        Console.WriteLine(Near(restored2.X, 1.0f) && Near(restored2.Y, 2.0f));

                        Matrix4x4 world = BuildWorld();
                        Vec3 point3 = world.TransformPoint(new Vec3(1.0f, 2.0f, 3.0f));
                        Console.WriteLine(Near(point3.X, 7.0f) && Near(point3.Y, 10.0f) && Near(point3.Z, 13.0f));
                        Matrix4x4 inverse4;
                        Console.WriteLine(Matrix4x4.TryInvert(world, out inverse4));
                        Vec3 restored3 = inverse4.TransformPoint(point3);
                        Console.WriteLine(Near(restored3.X, 1.0f) && Near(restored3.Y, 2.0f) && Near(restored3.Z, 3.0f));

                        Quaternion rotation = Quaternion.CreateFromAxisAngle(new Vec3(0.0f, 0.0f, 1.0f), Math.Pi * 0.5f);
                        Vec3 rotated = RotatePoint(rotation, new Vec3(1.0f, 0.0f, 0.0f));
                        Console.WriteLine(Near(rotated.X, 0.0f) && Near(rotated.Y, 1.0f));
                        Quaternion normalized;
                        Console.WriteLine(!Quaternion.TryNormalize(default(Quaternion), out normalized) && Near(normalized.W, 1.0f));
                    }
                }
                """;
            var compilation = Compile(source);
            var diagnostics = compilation.GetDiagnostics();
            Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, diagnostics));
            var run = CompileAndRun(source);
            Assert(run.ExitCode == 0, run.StandardError);
            Assert(run.StandardOutput.Replace("\r", string.Empty, StringComparison.Ordinal).Trim() == "16\n16\n24\n64\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue", $"Draft 0.37 scalar behavior changed: {run.StandardOutput}{run.StandardError}");

            var simdOptions = new CompilationOptions(Architecture: CompilationArchitecture.X64, CpuFeatures: [CpuFeature.Simd128]);
            var generated = Emit(source, simdOptions);
            Assert(generated.Contains("_mm_fmadd_ps", StringComparison.Ordinal) && generated.Contains("#if defined(__FMA__)", StringComparison.Ordinal), "Explicit multiply-add did not emit conditional FMA lowering.");
            Assert(generated.Contains("#define CT_MAT4_ROW(ROW) _mm_fmadd_ps", StringComparison.Ordinal), "Matrix multiplication did not emit a conditional FMA kernel.");
            Assert(generated.Contains("float ct_x = fmaf", StringComparison.Ordinal), "Quaternion multiplication did not emit a conditional FMA kernel.");
            var simdRun = CompileAndRun(source, simdOptions);
            Assert(simdRun.ExitCode == 0 && simdRun.StandardOutput == run.StandardOutput, $"SIMD and scalar Draft 0.37 behavior differed: {simdRun.StandardOutput}{simdRun.StandardError}");
            var armGenerated = Emit(source, new CompilationOptions(Architecture: CompilationArchitecture.Arm64, CpuFeatures: [CpuFeature.Simd128]));
            Assert(armGenerated.Contains("vld1q_f32", StringComparison.Ordinal) && armGenerated.Contains("vst1q_f32", StringComparison.Ordinal) && armGenerated.Contains("vfmaq_f32", StringComparison.Ordinal), "Arm64 SIMD, geometry, or conditional FMA lowering was not emitted.");
            Assert(!Emit(source).Contains("_mm_loadu_ps", StringComparison.Ordinal), "Scalar-default geometry unexpectedly selected hardware SIMD.");

            const string invalidShift = "using System.Simd; public static class P { [EntryPoint] public static void Main() { I32x4 value = I32x4.Zero.ShiftLeft<32>(); } }";
            Assert(Compile(invalidShift).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2220"), "An out-of-range SIMD shift count was accepted.");
            const string nativeSimd = "using System.Simd; public static class P { [Extern(\"native_simd\")] public static F32x4 Native(F32x4 value); [EntryPoint] public static void Main() { } }";
            Assert(Compile(nativeSimd).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1279"), "A SIMD value crossed an extern boundary.");
            const string callbackSimd = "using System.Simd; public delegate F32x4 Transform(F32x4 value); public static class P { [EntryPoint] public static void Main() { } }";
            Assert(Compile(callbackSimd).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1279"), "A SIMD value entered a callback signature.");
        });

        suite.Run("draft 0.37 geometry symbols are available to editor services", () =>
        {
            const string path = "draft037-editor.ct";
            const string source = "using System.Simd; public static class P { public static void M() { Matrix4x4 matrix = Matrix4x4.Identity; Quaternion rotation = Quaternion.Identity; F32x4 lanes = F32x4.Zero; matrix. } }";
            var service = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(source, path)]);
            var position = source.LastIndexOf("matrix.", StringComparison.Ordinal) + "matrix.".Length;
            var labels = service.GetCompletions(path, position).Select(item => item.Label).ToHashSet(StringComparer.Ordinal);
            Assert(labels.Contains("TransformPoint") && labels.Contains("TryTransformNormal") && labels.Contains("Determinant"), "Matrix4x4 completion is incomplete.");
            var matrixPosition = source.IndexOf("Matrix4x4 matrix", StringComparison.Ordinal) + 1;
            Assert(service.GetDefinition(path, matrixPosition)?.FilePath == "stdlib/System/Matrix4x4.ct", "Matrix4x4 definition did not navigate to its embedded source.");
            var quaternionPosition = source.IndexOf("Quaternion rotation", StringComparison.Ordinal) + 1;
            Assert(service.GetDefinition(path, quaternionPosition)?.FilePath == "stdlib/System/Quaternion.ct", "Quaternion definition did not navigate to its embedded source.");
            var simdPosition = source.IndexOf("F32x4 lanes", StringComparison.Ordinal) + 1;
            Assert(service.GetDefinition(path, simdPosition)?.FilePath == "stdlib/System/Simd.ct", "F32x4 definition did not navigate to its embedded source.");
        });
    }
}
