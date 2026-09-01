using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart32(ConformanceSuite suite)
    {
        suite.Run("draft 0.39 native-import declaration semantics", () =>
        {
            const string valid = """
                public static class Imports
                {
                    [NativeImport("sqlite3")]
                    [NoAlloc]
                    [NoThrow]
                    [NoBlock]
                    [NoRuntime]
                    public static int Initialize();

                    [NativeImport("sqlite3", "sqlite3_open")]
                    public static unsafe int Open(NativeUtf8String filename, out void* database);
                }
                public static class Program { [EntryPoint] public static void Main() { } }
                """;
            Assert(!Compile(valid).GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                string.Join(Environment.NewLine, Compile(valid).GetDiagnostics()));

            foreach (var library in new[] { "", "libsqlite3", "sqlite3.dll", "sqlite3.so", "sqlite3.dylib", "../sqlite3", "C:sqlite3", "sqlite.3", "sqlite 3" })
            {
                var source = $$"""
                    public static class Imports { [NativeImport("{{library}}", "open")] public static int Open(); }
                    public static class Program { [EntryPoint] public static void Main() { } }
                    """;
                Assert(Compile(source).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1312"),
                    $"Invalid native library name '{library}' was accepted.");
            }

            const string invalidSymbol = "public static class I { [NativeImport(\"sqlite3\", \"bad-symbol\")] public static int F(); } public static class Program { [EntryPoint] public static void Main() { } }";
            Assert(Compile(invalidSymbol).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1312"), "An invalid native symbol was accepted.");

            const string invalidShape = "public class I<T> { [NativeImport(\"sqlite3\")] [Extern(\"x\")] public int F() { return 0; } } public static class Program { [EntryPoint] public static void Main() { } }";
            var shapeDiagnostics = Compile(invalidShape).GetDiagnostics();
            Assert(shapeDiagnostics.Any(diagnostic => diagnostic.Code == "CT1313"), "Conflicting, body-bearing, instance, generic NativeImport was accepted.");

            const string forbiddenAbi = "using System.Simd; public static class I { [NativeImport(\"fixture\")] public static F32x4 F(F32x4 value); } public static class Program { [EntryPoint] public static void Main() { } }";
            Assert(Compile(forbiddenAbi).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1279"),
                "A SIMD value was accepted across a native-import boundary.");

            var nonHosted = Compile(valid, new CompilationOptions(Target: CompilationTarget.Cosmopolitan));
            Assert(nonHosted.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1314"), "Cosmopolitan accepted NativeImport.");
        });

        suite.Run("draft 0.39 native-import ABI identity and reachability", () =>
        {
            const string compatible = """
                public static class A { [NativeImport("fixture", "sum")] public static int Sum(int a, int b); }
                public static class B { [NativeImport("fixture", "sum")] public static int SumAgain(int a, int b); }
                public static class C { [NativeImport("other", "sum")] public static long Sum(long a, long b); }
                public static class D { [NativeImport("unused", "sum")] public static int Sum(int a, int b); }
                public static class Program { [EntryPoint] public static void Main() { System.Console.WriteLine(A.Sum(1, 2)); System.Console.WriteLine(B.SumAgain(3, 4)); System.Console.WriteLine(C.Sum(5, 6)); } }
                """;
            var compatibleCompilation = Compile(compatible);
            Assert(!compatibleCompilation.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4102"),
                "Compatible native-import declarations, or the same symbol from another library, conflicted.");
            var generated = Emit(compatible);
            Assert(Count(generated, "dlsym(ct_native_import_libraries[0], \"sum\")") == 1,
                "Compatible native imports were not coalesced into one resolved slot.");
            Assert(!generated.Contains("libunused.so", StringComparison.Ordinal), "An unreachable native import emitted loader state.");
            Assert(generated.Contains("LoadLibraryExW(L\"fixture.dll\"", StringComparison.Ordinal) &&
                generated.Contains("dlopen(\"libfixture.so\", RTLD_NOW | RTLD_LOCAL)", StringComparison.Ordinal),
                "Platform filename mapping was not emitted.");
            Assert(generated.IndexOf("libfixture.so", StringComparison.Ordinal) < generated.IndexOf("libother.so", StringComparison.Ordinal),
                "Native-import libraries were not emitted in deterministic ordinal order.");
            var bundle = compatibleCompilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var header = string.Join('\n', bundle.Artifacts.Where(artifact => artifact.Kind is GeneratedCArtifactKind.InternalHeader or GeneratedCArtifactKind.DependencyHeader).Select(artifact => artifact.Content));
            var runtime = bundle.Artifacts.Single(artifact => artifact.Kind == GeneratedCArtifactKind.RuntimeSource).Content;
            var modules = string.Join('\n', bundle.Artifacts.Where(artifact => artifact.Kind == GeneratedCArtifactKind.NamespaceSource).Select(artifact => artifact.Content));
            Assert(header.Contains("extern int32_t (*ct_ni_", StringComparison.Ordinal) && runtime.Contains("int32_t (*ct_ni_", StringComparison.Ordinal) &&
                modules.Contains("ct_ni_", StringComparison.Ordinal), "Modular output did not share the resolved import slot.");
            using var mapWriter = new StringWriter();
            Assert(compatibleCompilation.EmitSymbolMap(mapWriter).Success && mapWriter.ToString().Contains("\"kind\": \"nativeImport\"", StringComparison.Ordinal) &&
                mapWriter.ToString().Contains("\"library\": \"fixture\"", StringComparison.Ordinal), "The symbol map omitted reachable native-import metadata.");

            const string incompatible = """
                public static class A { [NativeImport("fixture", "sum")] public static int Sum(int value); }
                public static class B { [NativeImport("fixture", "sum")] public static long Sum(long value); }
                public static class Program { [EntryPoint] public static void Main() { } }
                """;
            Assert(Compile(incompatible).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4102"),
                "Incompatible declarations for one native import were accepted.");

            const string unused = "public static class I { [NativeImport(\"unused\")] public static int F(); } public static class Program { [EntryPoint] public static void Main() { } }";
            var unusedGenerated = Emit(unused);
            Assert(!unusedGenerated.Contains("dlopen(", StringComparison.Ordinal) && !unusedGenerated.Contains("LoadLibraryExW(", StringComparison.Ordinal),
                "An unused native import emitted loader support.");

            static int Count(string text, string value)
            {
                var count = 0;
                for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
                    count++;
                return count;
            }
        });

        suite.Run("draft 0.39 native-import startup failure", () =>
        {
            const string source = """
                using System;
                public static class Imports { [NativeImport("ctilde_definitely_missing_native_import_fixture", "value")] public static int Value(); }
                public static class Program { [EntryPoint] public static void Main() { Console.WriteLine("ENTRYPOINT-RAN"); Console.WriteLine(Imports.Value()); } }
                """;
            var run = CompileAndRun(source);
            Assert(run.ExitCode != 0, "A missing native library did not terminate startup.");
            Assert(run.StandardError.Contains("CTI0001", StringComparison.Ordinal) &&
                run.StandardError.Contains("ctilde_definitely_missing_native_import_fixture", StringComparison.Ordinal),
                $"The missing-library diagnostic was incomplete: {run.StandardError}");
            Assert(!run.StandardOutput.Contains("ENTRYPOINT-RAN", StringComparison.Ordinal), "EntryPoint ran after native-import resolution failed.");

            var generated = Emit(source)
                .Replace("int main(void)", "int ct_generated_main(void)", StringComparison.Ordinal);
            const string host = """

                static void ct_test_native_import_panic(const ct_panic_info* info, void* context)
                {
                    (void)context;
                    (void)fprintf(stderr, "native-import-panic:%s\n", info->Code);
                }

                int main(void)
                {
                    ct_runtime_config config = { sizeof(ct_runtime_config), ct_test_native_import_panic, NULL };
                    ct_runtime_initialize(&config);
                    return 99;
                }
                """;
            var directory = Path.Combine(Path.GetTempPath(), "ctilde-native-import-panic-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var cPath = Path.Combine(directory, "panic.c");
                var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "panic.exe" : "panic");
                File.WriteAllText(cPath, generated + host, new System.Text.UTF8Encoding(false));
                var native = RunCompiler(cPath, executable);
                Assert(native.ExitCode == 0, native.StandardOutput + native.StandardError);
                var panic = RunCompiledProgram(executable);
                Assert(panic.ExitCode != 0 && panic.StandardError.Contains("native-import-panic:CTI0001", StringComparison.Ordinal),
                    "The configured panic callback did not receive CTI0001 for a missing native library.");
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        });

        suite.Run("draft 0.39 native-import fixture ABI and startup", () =>
        {
            const string source = """
                using System;
                using System.Runtime;
                using System.Threading;

                [NativeType("uintptr_t", "stdint.h")]
                public opaque Handle;

                public struct Pair
                {
                    public int Left;
                    public int Right;
                    public Pair(int left, int right) { Left = left; Right = right; }
                }

                public delegate int Transformer(int value);

                public static class Native
                {
                    [NativeImport("ctilde_native_import_fixture", "ctilde_add")] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
                    public static int Add(int left, int right);
                    [NativeImport("ctilde_native_import_fixture", "ctilde_pair_add")] public static Pair Add(Pair left, Pair right);
                    [NativeImport("ctilde_native_import_fixture", "ctilde_adjust")] public static void Adjust(ref int value, in int add, out uint result);
                    [NativeImport("ctilde_native_import_fixture", "ctilde_text_length")] public static uint TextLength(NativeUtf8String text);
                    [NativeImport("ctilde_native_import_fixture", "ctilde_sum")] public static unsafe uint Sum(ReadOnlyNativeBuffer<byte> bytes);
                    [NativeImport("ctilde_native_import_fixture", "ctilde_create")] [ReturnsOwned] public static Handle Create();
                    [NativeImport("ctilde_native_import_fixture", "ctilde_read")] public static int Read([Borrowed] Handle handle);
                    [NativeImport("ctilde_native_import_fixture", "ctilde_release")] public static void Release([Consumes] Handle handle);
                    [NativeImport("ctilde_native_import_fixture", "ctilde_invoke")] public static int Invoke([SynchronousCallback] Transformer callback, int value);
                    [NativeImport("ctilde_native_import_fixture", "ctilde_increment")] public static int Increment(int value);
                }

                public static class Program
                {
                    private static int initialized = Native.Add(20, 22);
                    private static int workerResult;
                    private static int Double(int value) { return value * 2; }
                    private static void Worker() { workerResult = Native.Add(20, 22); }

                    [EntryPoint] public static unsafe void Main()
                    {
                        Console.WriteLine(initialized);
                        Pair pair = Native.Add(new Pair(1, 2), new Pair(3, 4));
                        Console.WriteLine(pair.Left + pair.Right);
                        int value = 39;
                        readonly int add = 3;
                        uint adjusted;
                        Native.Adjust(ref value, in add, out adjusted);
                        Console.WriteLine(adjusted);
                        NativeUtf8String text = NativeUtf8String.Borrow("native-import");
                        Console.WriteLine(Native.TextLength(text));
                        NativeBuffer<byte> bytes = stackalloc byte[3];
                        bytes[0] = (byte)10; bytes[1] = (byte)20; bytes[2] = (byte)12;
                        ReadOnlyNativeBuffer<byte> readable = bytes;
                        Console.WriteLine(Native.Sum(readable));
                        Handle handle = Native.Create();
                        defer Native.Release(handle);
                        Console.WriteLine(Native.Read(handle));
                        Transformer callback = Double;
                        Console.WriteLine(Native.Invoke(callback, 21));
                        delegate* unmanaged<int, int, int> addPointer = &Native.Add;
                        Console.WriteLine(addPointer(19, 23));
                        delegate* unmanaged<int, int> incrementPointer = &Native.Increment;
                        Console.WriteLine(incrementPointer(41));
                        Thread worker = new Thread(Worker);
                        worker.Start();
                        worker.Join();
                        Console.WriteLine(workerResult);
                    }
                }
                """;
            const string fixture = """
                #include <stddef.h>
                #include <stdint.h>
                #include <string.h>
                #if defined(_WIN32)
                #include <windows.h>
                #define CT_FIXTURE_EXPORT __declspec(dllexport)
                BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
                {
                    (void)instance; (void)reserved;
                    if (reason == DLL_PROCESS_DETACH) { static const char message[] = "CTILDE_NATIVE_IMPORT_UNLOADED\n"; DWORD written = 0u; (void)WriteFile(GetStdHandle(STD_ERROR_HANDLE), message, (DWORD)(sizeof(message) - 1u), &written, NULL); }
                    return TRUE;
                }
                #else
                #include <unistd.h>
                #define CT_FIXTURE_EXPORT __attribute__((visibility("default")))
                __attribute__((destructor)) static void ctilde_fixture_unload(void) { static const char message[] = "CTILDE_NATIVE_IMPORT_UNLOADED\n"; (void)write(2, message, sizeof(message) - 1u); }
                #endif
                typedef struct ct_fixture_pair { int32_t Left; int32_t Right; } ct_fixture_pair;
                typedef int32_t (*ct_fixture_callback)(int32_t, void*);
                CT_FIXTURE_EXPORT int32_t ctilde_add(int32_t left, int32_t right) { return left + right; }
                CT_FIXTURE_EXPORT ct_fixture_pair ctilde_pair_add(ct_fixture_pair left, ct_fixture_pair right) { return (ct_fixture_pair){ left.Left + right.Left, left.Right + right.Right }; }
                CT_FIXTURE_EXPORT void ctilde_adjust(int32_t* value, const int32_t* add, uint32_t* result) { *value += *add; *result = (uint32_t)*value; }
                CT_FIXTURE_EXPORT uint32_t ctilde_text_length(const char* text) { return (uint32_t)strlen(text); }
                CT_FIXTURE_EXPORT uint32_t ctilde_sum(const uint8_t* bytes, size_t length) { uint32_t result = 0u; for (size_t index = 0u; index < length; ++index) result += bytes[index]; return result; }
                CT_FIXTURE_EXPORT uintptr_t ctilde_create(void) { return (uintptr_t)42u; }
                CT_FIXTURE_EXPORT int32_t ctilde_read(uintptr_t handle) { return (int32_t)handle; }
                CT_FIXTURE_EXPORT void ctilde_release(uintptr_t handle) { (void)handle; }
                CT_FIXTURE_EXPORT int32_t ctilde_invoke(ct_fixture_callback callback, void* context, int32_t value) { return callback(value, context); }
                CT_FIXTURE_EXPORT int32_t ctilde_increment(int32_t value) { return value + 1; }
                """;
            var run = CompileAndRunNativeImportFixture(source, fixture);
            Assert(run.ExitCode == 0, $"Native-import fixture exited {run.ExitCode}: {run.StandardOutput}{run.StandardError}");
            Assert(Normalize(run.StandardOutput) == "42\n10\n42\n13\n42\n42\n42\n42\n42\n42\n", $"Unexpected native-import fixture output: {run.StandardOutput}{run.StandardError}");
            Assert(run.StandardError.Contains("CTILDE_NATIVE_IMPORT_UNLOADED", StringComparison.Ordinal), "The native library was not unloaded during normal runtime shutdown.");

            const string failedInitializer = """
                using System;
                public static class Native { [NativeImport("ctilde_native_import_fixture", "ctilde_add")] public static int Add(int left, int right); }
                public static class Program
                {
                    private static int loaded = Native.Add(20, 22);
                    private static int failed = 1 / Zero();
                    private static int Zero() { return 0; }
                    [EntryPoint] public static void Main() { Console.WriteLine("ENTRYPOINT-RAN"); Console.WriteLine(loaded + failed); }
                }
                """;
            var initializerFailure = CompileAndRunNativeImportFixture(failedInitializer, fixture);
            Assert(initializerFailure.ExitCode != 0 && initializerFailure.StandardError.Contains("CTD0001", StringComparison.Ordinal),
                $"The failing static initializer did not report its fault: {initializerFailure.StandardOutput}{initializerFailure.StandardError}");
            Assert(initializerFailure.StandardError.Contains("CTILDE_NATIVE_IMPORT_UNLOADED", StringComparison.Ordinal),
                "The native library was not unloaded after exceptional static finalization.");
            Assert(!initializerFailure.StandardOutput.Contains("ENTRYPOINT-RAN", StringComparison.Ordinal),
                "EntryPoint ran after a static initializer failed.");

            const string missingSymbol = """
                using System;
                public static class Native { [NativeImport("ctilde_native_import_fixture", "ctilde_symbol_that_does_not_exist")] public static int Missing(); }
                public static class Program { [EntryPoint] public static void Main() { Console.WriteLine("ENTRYPOINT-RAN"); Console.WriteLine(Native.Missing()); } }
                """;
            var missing = CompileAndRunNativeImportFixture(missingSymbol, fixture);
            Assert(missing.ExitCode != 0 && missing.StandardError.Contains("CTI0002", StringComparison.Ordinal) &&
                missing.StandardError.Contains("ctilde_symbol_that_does_not_exist", StringComparison.Ordinal),
                $"The missing-symbol diagnostic was incomplete: {missing.StandardOutput}{missing.StandardError}");
            Assert(!missing.StandardOutput.Contains("ENTRYPOINT-RAN", StringComparison.Ordinal), "EntryPoint ran after native-symbol resolution failed.");
        });

        suite.Run("HostedIo Raylib ABI, dirty uploads, cadence, and lifecycle", () =>
        {
            const string harness = """
                using System;
                namespace HostedIoExample;

                public static class FakeRaylib
                {
                    [NativeImport("raylib", "ctilde_raylib_color_size")] public static int ColorSize();
                    [NativeImport("raylib", "ctilde_raylib_image_size")] public static int ImageSize();
                    [NativeImport("raylib", "ctilde_raylib_texture_size")] public static int TextureSize();
                    [NativeImport("raylib", "ctilde_raylib_rectangle_size")] public static int RectangleSize();
                    [NativeImport("raylib", "ctilde_raylib_pixel_red")] public static int PixelRed(int index);
                    [NativeImport("raylib", "ctilde_raylib_draw_count")] public static int DrawCount();
                    [NativeImport("raylib", "ctilde_raylib_update_count")] public static int UpdateCount();
                    [NativeImport("raylib", "ctilde_raylib_force_close")] public static void ForceClose();
                    [NativeImport("raylib", "ctilde_raylib_unload_texture_order")] public static int UnloadTextureOrder();
                    [NativeImport("raylib", "ctilde_raylib_unload_image_order")] public static int UnloadImageOrder();
                    [NativeImport("raylib", "ctilde_raylib_close_window_order")] public static int CloseWindowOrder();
                }

                public static class Program
                {
                    [EntryPoint]
                    public static unsafe void Main()
                    {
                        Console.WriteLine((int)sizeof(Color) == FakeRaylib.ColorSize());
                        Console.WriteLine((int)sizeof(Image) == FakeRaylib.ImageSize());
                        Console.WriteLine((int)sizeof(Texture2D) == FakeRaylib.TextureSize());
                        Console.WriteLine((int)sizeof(Rectangle) == FakeRaylib.RectangleSize());
                        RaylibWindow window = new RaylibWindow(5, 4, "fake");
                        bool running = true;
                        int index = 0;
                        while (index < 20)
                        {
                            running = running && window.SetPixel(index % 5, index / 5,
                                new Rgba32((byte)index, (byte)(index + 1), (byte)(index + 2), (byte)255));
                            index++;
                        }
                        window.Present();
                        Console.WriteLine(running);
                        Console.WriteLine(FakeRaylib.PixelRed(19) == 19);
                        Console.WriteLine(FakeRaylib.DrawCount() == 4);
                        Console.WriteLine(FakeRaylib.UpdateCount() == 5);
                        FakeRaylib.ForceClose();
                        Console.WriteLine(!window.Present());
                        window.Close();
                        Console.WriteLine(FakeRaylib.UnloadTextureOrder() == 1);
                        Console.WriteLine(FakeRaylib.UnloadImageOrder() == 2);
                        Console.WriteLine(FakeRaylib.CloseWindowOrder() == 3);
                    }
                }
                """;
            const string fixture = """
                #include <stdbool.h>
                #include <stdint.h>
                #include <stdlib.h>
                #if defined(_WIN32)
                #define CT_EXPORT __declspec(dllexport)
                #else
                #define CT_EXPORT __attribute__((visibility("default")))
                #endif
                typedef struct Color { uint8_t Red, Green, Blue, Alpha; } Color;
                typedef struct Rectangle { float X, Y, Width, Height; } Rectangle;
                typedef struct Image { void* Data; int32_t Width, Height, Mipmaps, Format; } Image;
                typedef struct Texture2D { uint32_t Id; int32_t Width, Height, Mipmaps, Format; } Texture2D;
                static Color* image_data;
                static int draw_count;
                static int update_count;
                static int close_requested;
                static int close_sequence;
                static int unload_texture_order;
                static int unload_image_order;
                static int close_window_order;
                static double fake_time;
                CT_EXPORT void InitWindow(int32_t width, int32_t height, const char* title) { (void)width; (void)height; (void)title; }
                CT_EXPORT bool WindowShouldClose(void) { return close_requested != 0; }
                CT_EXPORT void CloseWindow(void) { close_window_order = ++close_sequence; }
                CT_EXPORT void SetTargetFPS(int32_t fps) { (void)fps; }
                CT_EXPORT double GetTime(void) { fake_time += 0.001; return fake_time; }
                CT_EXPORT Image GenImageColor(int32_t width, int32_t height, Color color)
                {
                    Image image = { 0 };
                    image_data = (Color*)malloc((size_t)width * (size_t)height * sizeof(Color));
                    for (int32_t index = 0; index < width * height; ++index) image_data[index] = color;
                    image.Data = image_data; image.Width = width; image.Height = height; image.Mipmaps = 1; image.Format = 7;
                    return image;
                }
                CT_EXPORT void UnloadImage(Image image) { (void)image; unload_image_order = ++close_sequence; free(image_data); image_data = NULL; }
                CT_EXPORT Texture2D LoadTextureFromImage(Image image)
                {
                    Texture2D texture = { 1u, image.Width, image.Height, 1, image.Format };
                    return texture;
                }
                CT_EXPORT void UnloadTexture(Texture2D texture) { (void)texture; unload_texture_order = ++close_sequence; }
                CT_EXPORT void UpdateTextureRec(Texture2D texture, Rectangle rectangle, const void* pixels)
                { (void)texture; (void)rectangle; (void)pixels; ++update_count; }
                CT_EXPORT void BeginDrawing(void) { ++draw_count; }
                CT_EXPORT void ClearBackground(Color color) { (void)color; }
                CT_EXPORT void DrawTexture(Texture2D texture, int32_t x, int32_t y, Color tint)
                { (void)texture; (void)x; (void)y; (void)tint; }
                CT_EXPORT void EndDrawing(void) { }
                CT_EXPORT int32_t ctilde_raylib_color_size(void) { return (int32_t)sizeof(Color); }
                CT_EXPORT int32_t ctilde_raylib_image_size(void) { return (int32_t)sizeof(Image); }
                CT_EXPORT int32_t ctilde_raylib_texture_size(void) { return (int32_t)sizeof(Texture2D); }
                CT_EXPORT int32_t ctilde_raylib_rectangle_size(void) { return (int32_t)sizeof(Rectangle); }
                CT_EXPORT int32_t ctilde_raylib_pixel_red(int32_t index) { return image_data[index].Red; }
                CT_EXPORT int32_t ctilde_raylib_draw_count(void) { return draw_count; }
                CT_EXPORT int32_t ctilde_raylib_update_count(void) { return update_count; }
                CT_EXPORT void ctilde_raylib_force_close(void) { close_requested = 1; }
                CT_EXPORT int32_t ctilde_raylib_unload_texture_order(void) { return unload_texture_order; }
                CT_EXPORT int32_t ctilde_raylib_unload_image_order(void) { return unload_image_order; }
                CT_EXPORT int32_t ctilde_raylib_close_window_order(void) { return close_window_order; }
                """;
            var run = CompileAndRunNativeImportFixture(HostedIoSources(harness), fixture, "raylib");
            Assert(run.ExitCode == 0, $"Fake Raylib fixture exited {run.ExitCode}: {run.StandardOutput}{run.StandardError}");
            Assert(Normalize(run.StandardOutput) == "True\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\n",
                $"Unexpected fake Raylib output: {run.StandardOutput}{run.StandardError}");
        });
    }
}
