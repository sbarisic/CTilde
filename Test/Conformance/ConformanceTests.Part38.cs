using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart38(ConformanceSuite suite)
    {
        suite.Run("draft 0.43 freestanding standard-library runtime services", () =>
        {
            var compilation = Compile(RuntimeBackendSource + """

                public static class Kernel
                {
                    [Export("kernel_main")]
                    public static int Main()
                    {
                        System.Collections.List<int> values = new System.Collections.List<int>();
                        values.Add(3);
                        System.Text.StringBuilder text = new System.Text.StringBuilder();
                        text.Append(values[0]);
                        System.Console.WriteLine(text.ToString());
                        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                        float root = System.Math.Sqrt(9.0f);
                        bool file = System.IO.File.Exists("settings.bin");
                        bool directory = System.IO.Directory.Exists("data");
                        System.IO.FileStream stream = new System.IO.FileStream("output.bin", System.IO.FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite);
                        stream.WriteByte((byte)values[0]);
                        stream.Dispose();
                        System.Threading.Thread.Yield();
                        System.Threading.Mutex mutex = new System.Threading.Mutex();
                        if (mutex.TryEnter()) mutex.Exit();
                        if (file || directory || root != 3.0f || watch.ElapsedNanoseconds < 0L) return 1;
                        return 0;
                    }
                }
                """, new CompilationOptions(CompilationTarget.Freestanding, Architecture: CompilationArchitecture.X64));
            var diagnostics = compilation.GetDiagnostics();
            Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, diagnostics));
            using var generated = new StringWriter();
            var emitted = compilation.EmitC(generated);
            var cSource = generated.ToString();
            Assert(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            foreach (var marker in new[] { "ct_runtime_console_write", "ct_runtime_path_metadata", "ct_managed_thread_yield", "ct_managed_mutex_try_enter", "ct_math_sqrt" })
                Assert(cSource.Contains(marker, StringComparison.Ordinal), $"Freestanding service provider marker '{marker}' was not emitted.");
            Assert(!cSource.Contains("#include <stdio.h>", StringComparison.Ordinal) &&
                !cSource.Contains("#include <stdlib.h>", StringComparison.Ordinal) &&
                !cSource.Contains("#include <math.h>", StringComparison.Ordinal) &&
                !cSource.Contains("_Thread_local", StringComparison.Ordinal),
                "Freestanding standard-library services introduced a hosted native dependency.");
            if (CompileFreestandingObject(cSource) is { } nativeCompile)
                Assert(nativeCompile.ExitCode == 0, $"Freestanding runtime-service C compilation failed:{Environment.NewLine}{nativeCompile.StandardOutput}{nativeCompile.StandardError}");
            var missing = Compile("""
                using System;
                using System.Runtime;
                public static class Backend
                {
                    [RuntimeImpl(Runtime.Panic)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
                    public static unsafe void Panic(RuntimePanicInfo info) { while (true) { Cpu.Pause(); } }
                }
                public static class Kernel { [Export("kernel_main")] public static float Main() { return Math.Sqrt(4.0f); } }
                """, new CompilationOptions(CompilationTarget.Freestanding, Architecture: CompilationArchitecture.X64));
            Assert(missing.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4114" && diagnostic.Message.Contains("MathFloatUnary", StringComparison.Ordinal)),
                "A reachable freestanding math call did not require its runtime role.");
        });

        suite.Run("draft 0.43 runtime service signatures and ESP safety", () =>
        {
            var invalid = Compile("""
                using System.Runtime;
                public static class Backend
                {
                    [RuntimeImpl(Runtime.ConsoleWrite)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
                    public static RuntimeResult Write(ReadOnlyNativeBuffer<byte> value) { return RuntimeResult.Ok; }
                }
                public static class Kernel { [Export("kernel_main")] public static int Main() { return 0; } }
                """, new CompilationOptions(CompilationTarget.Freestanding, Architecture: CompilationArchitecture.X64));
            Assert(invalid.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1299"),
                "An invalid runtime-service signature was accepted.");

            var incompleteEsp = Compile("""
                using System;
                using System.Runtime;
                public static class Backend
                {
                    [RuntimeImpl(Runtime.ConsoleWrite)] [NoAlloc] [NoThrow] [NoRuntime]
                    public static unsafe RuntimeTransferResult Write(ReadOnlyNativeBuffer<byte> value)
                    {
                        return new RuntimeTransferResult(RuntimeStatus.Success, 0, value.Length);
                    }
                }
                public static class Program { [EntryPoint] public static void Main() { Console.WriteLine("partial"); } }
                """, new CompilationOptions(CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa));
            Assert(incompleteEsp.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4114" &&
                    diagnostic.Message.Contains("ConsoleRead", StringComparison.Ordinal)),
                "An incomplete ESP-IDF console override group was accepted.");

            var espUnsafe = Compile("""
                using System;
                using System.Runtime;
                public static class Backend
                {
                    [RuntimeImpl(Runtime.MathFloatUnary)] [NoAlloc]
                    public static float Math(RuntimeUnaryMathOperation operation, float value)
                    {
                        object forbidden = new object();
                        return value;
                    }
                }
                public static class Program { [EntryPoint] public static void Main() { float value = MathF(); } private static float MathF() { return System.Math.Sqrt(4.0f); } }
                """, new CompilationOptions(CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa));
            Assert(espUnsafe.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2211"),
                "An ESP-IDF runtime provider with managed bootstrap effects was accepted.");

            var esp = Compile(RuntimeBackendSource + """

                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        System.Console.WriteLine(System.Math.Sqrt(4.0f));
                        bool exists = System.IO.File.Exists("settings.bin");
                        System.Threading.Thread.Yield();
                        if (exists) System.Environment.Exit(1);
                        System.Environment.Exit(0);
                    }
                }
                """, new CompilationOptions(CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa));
            var espDiagnostics = esp.GetDiagnostics();
            Assert(!espDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, espDiagnostics));
            using var espGenerated = new StringWriter();
            Assert(esp.EmitC(espGenerated).Success && espGenerated.ToString().Contains("ct_runtime_path_metadata", StringComparison.Ordinal) &&
                espGenerated.ToString().Contains("ct_runtime_console_write", StringComparison.Ordinal) &&
                espGenerated.ToString().Contains("ct_environment_exit", StringComparison.Ordinal),
                "ESP-IDF runtime providers did not override the default adapters.");
        });
    }

    private const string RuntimeBackendSource = """
        using System;
        using System.Runtime;

        public static class Backend
        {
            [RuntimeImpl(Runtime.Allocate)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static unsafe void* Allocate(nuint size) { return (void*)null; }
            [RuntimeImpl(Runtime.Free)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static unsafe void Free(void* value) { }
            [RuntimeImpl(Runtime.Panic)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static unsafe void Panic(RuntimePanicInfo info) { while (true) { Cpu.Pause(); } }
            [RuntimeImpl(Runtime.Exit)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static void Exit(int code) { while (true) { Cpu.Pause(); } }
            [RuntimeImpl(Runtime.ConsoleWrite)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static unsafe RuntimeTransferResult ConsoleWrite(ReadOnlyNativeBuffer<byte> value) { return new RuntimeTransferResult(RuntimeStatus.Success, 0, value.Length); }
            [RuntimeImpl(Runtime.ConsoleRead)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static unsafe RuntimeTransferResult ConsoleRead(NativeBuffer<byte> value) { return new RuntimeTransferResult(RuntimeStatus.EndOfStream, 0, 0U); }
            [RuntimeImpl(Runtime.ConsoleFlush)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static RuntimeResult ConsoleFlush() { return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.MonotonicNanoseconds)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static long MonotonicNanoseconds() { return 0L; }
            [RuntimeImpl(Runtime.PathSeparator)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static char PathSeparator() { return '/'; }
            [RuntimeImpl(Runtime.MathFloatUnary)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static float MathFloatUnary(RuntimeUnaryMathOperation operation, float value) { return value; }
            [RuntimeImpl(Runtime.MathFloatBinary)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static float MathFloatBinary(RuntimeBinaryMathOperation operation, float left, float right) { return left; }
            [RuntimeImpl(Runtime.MathDoubleUnary)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static double MathDoubleUnary(RuntimeUnaryMathOperation operation, double value) { return value; }
            [RuntimeImpl(Runtime.MathDoubleBinary)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static double MathDoubleBinary(RuntimeBinaryMathOperation operation, double left, double right) { return left; }

            [RuntimeImpl(Runtime.FileOpen)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static unsafe RuntimeResult FileOpen(NativeUtf8String path, RuntimeFileMode mode, RuntimeFileAccess access, out nuint handle) { handle = 1U; return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.FileRead)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static unsafe RuntimeTransferResult FileRead(nuint handle, NativeBuffer<byte> value) { return new RuntimeTransferResult(RuntimeStatus.EndOfStream, 0, 0U); }
            [RuntimeImpl(Runtime.FileWrite)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static unsafe RuntimeTransferResult FileWrite(nuint handle, ReadOnlyNativeBuffer<byte> value) { return new RuntimeTransferResult(RuntimeStatus.Success, 0, value.Length); }
            [RuntimeImpl(Runtime.FileSeek)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static RuntimeResult FileSeek(nuint handle, long offset, RuntimeSeekOrigin origin, out long position) { position = 0L; return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.FileLength)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static RuntimeResult FileLength(nuint handle, out long length) { length = 0L; return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.FileSetLength)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static RuntimeResult FileSetLength(nuint handle, long length) { return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.FileFlush)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static RuntimeResult FileFlush(nuint handle) { return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.FileClose)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static RuntimeResult FileClose(nuint handle) { return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.PathMetadata)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static unsafe RuntimeResult PathMetadata(NativeUtf8String path, out RuntimeFileMetadata metadata) { metadata = new RuntimeFileMetadata(); return new RuntimeResult(RuntimeStatus.NotFound, 0); }
            [RuntimeImpl(Runtime.FileDelete)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static unsafe RuntimeResult FileDelete(NativeUtf8String path) { return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.PathMove)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static unsafe RuntimeResult PathMove(NativeUtf8String source, NativeUtf8String destination, bool overwrite) { return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.DirectoryCreate)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static unsafe RuntimeResult DirectoryCreate(NativeUtf8String path) { return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.DirectoryDelete)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static unsafe RuntimeResult DirectoryDelete(NativeUtf8String path, bool recursive) { return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.DirectoryOpen)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static unsafe RuntimeResult DirectoryOpen(NativeUtf8String path, out nuint handle) { handle = 1U; return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.DirectoryRead)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static unsafe RuntimeTransferResult DirectoryRead(nuint handle, NativeBuffer<byte> name, out RuntimeFileMetadata metadata) { metadata = new RuntimeFileMetadata(); return new RuntimeTransferResult(RuntimeStatus.EndOfStream, 0, 0U); }
            [RuntimeImpl(Runtime.DirectoryClose)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static RuntimeResult DirectoryClose(nuint handle) { return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.CurrentDirectoryGet)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static unsafe RuntimeTransferResult CurrentDirectoryGet(NativeBuffer<byte> value) { return new RuntimeTransferResult(RuntimeStatus.Success, 0, 0U); }
            [RuntimeImpl(Runtime.CurrentDirectorySet)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static unsafe RuntimeResult CurrentDirectorySet(NativeUtf8String path) { return RuntimeResult.Ok; }

            [RuntimeImpl(Runtime.ThreadCreate)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static unsafe RuntimeResult ThreadCreate(delegate* unmanaged<void*, void> entry, void* context, uint stackSize, RuntimeThreadPriority priority, out nuint handle, out uint id) { handle = 1U; id = 1U; return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.ThreadJoin)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static RuntimeResult ThreadJoin(nuint handle) { return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.ThreadClose)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static RuntimeResult ThreadClose(nuint handle) { return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.ThreadSleep)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static RuntimeResult ThreadSleep(uint milliseconds) { return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.ThreadYield)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static RuntimeResult ThreadYield() { return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.ThreadStateGet)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static unsafe void* ThreadStateGet() { return (void*)null; }
            [RuntimeImpl(Runtime.ThreadStateSet)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static unsafe void ThreadStateSet(void* value) { }
            [RuntimeImpl(Runtime.MutexCreate)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static RuntimeResult MutexCreate(out nuint handle) { handle = 1U; return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.MutexEnter)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static RuntimeResult MutexEnter(nuint handle) { return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.MutexTryEnter)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static RuntimeResult MutexTryEnter(nuint handle, out bool entered) { entered = true; return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.MutexExit)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static RuntimeResult MutexExit(nuint handle) { return RuntimeResult.Ok; }
            [RuntimeImpl(Runtime.MutexClose)] [NoAlloc] [NoThrow] [NoBlock] [NoRuntime]
            public static RuntimeResult MutexClose(nuint handle) { return RuntimeResult.Ok; }
        }
        """;
}
