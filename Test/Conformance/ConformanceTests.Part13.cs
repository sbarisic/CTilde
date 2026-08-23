using System.Text.Json;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart13(ConformanceSuite suite)
    {
        suite.Run("draft 0.15 interface generic and lock syntax", () =>
        {
            const string source = "public interface I<T> { T Read(); } public abstract class Base<T> : object, I<T> { public abstract T Read(); } public sealed class Value : Base<int> { public override int Read() { return 1; } } public static class Program { private static volatile int state; [EntryPoint] public static void Main() { System.Threading.Mutex mutex = new System.Threading.Mutex(); lock (mutex) { state = 1; } } }";
            var tree = SyntaxTree.ParseText(source, "draft15-syntax.ct");
            Assert(tree.Diagnostics.IsEmpty, string.Join(Environment.NewLine, tree.Diagnostics));
            Assert(tree.ToFullString() == source, "Draft 0.15 syntax did not round-trip exactly.");
            Assert(tree.Tokens.Any(token => token.Kind == SyntaxKind.InterfaceKeyword) &&
                tree.Tokens.Any(token => token.Kind == SyntaxKind.AbstractKeyword) &&
                tree.Tokens.Any(token => token.Kind == SyntaxKind.VolatileKeyword) &&
                tree.Tokens.Any(token => token.Kind == SyntaxKind.LockKeyword), "Draft 0.15 keywords were not classified.");
            Assert(Descendants(tree.Root).OfType<LockStatementSyntax>().Count() == 1, "lock did not produce a syntax node.");
            Assert(Descendants(tree.Root).OfType<TypeParameterSyntax>().Count() >= 2, "Generic type parameters were not preserved.");
        });

        suite.Run("draft 0.15 interface generic atomic and volatile diagnostics", () =>
        {
            const string source = "public interface I { int Read(); } public abstract class Abstract { public abstract int Read(); } public sealed class Missing : object, I { } public struct Holder { public System.Threading.Atomic<int> Value; } public static class Program { private static volatile int state; [EntryPoint] public static void Main() { Abstract value = new Abstract(); state += 1; System.Threading.Atomic<int> first = new System.Threading.Atomic<int>(0); System.Threading.Atomic<int> second = first; lock (state) { } } }";
            var diagnostics = Compile(source).GetDiagnostics();
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1275"), "A missing interface implementation was accepted.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1276"), "Construction of an abstract class was accepted.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1274"), "A volatile compound assignment was accepted.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1278"), "Copyable Atomic<T> storage was accepted.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2137"), "lock accepted a non-Mutex expression.");

            const string boundary = "public interface I { int Read(); } public static class Program { [Extern(\"bad\")] public static I Bad(I value); [EntryPoint] public static void Main() { } }";
            Assert(Compile(boundary).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1279"), "An interface value crossed an extern boundary.");
        });

        suite.Run("draft 0.15 interfaces generics atomics threads and locks runtime", () =>
        {
            const string source = """
                using System;
                using System.Threading;
                public interface IValue<T> { T Read(); }
                public struct Value<T> : IValue<T>
                {
                    private T value;
                    public Value(T value) { this.value = value; }
                    public T Read() { return value; }
                }
                public sealed class Box<T>
                {
                    public static int Count;
                    public T Value { get; private set; }
                    public Box(T value) { Value = value; Count = Count + 1; }
                }
                public delegate T Transform<T>(T value);
                public static class Program
                {
                    private static Mutex mutex = new Mutex();
                    private static Atomic<int> counter = new Atomic<int>(0);
                    private static volatile int published;
                    private static int protectedValue;
                    public static T Identity<T>(T value) { return value; }
                    public static int Increment(int value) { return value + 1; }
                    public static void Worker()
                    {
                        for (int index = 0; index < 1000; index = index + 1)
                            counter.FetchAdd(1, MemoryOrder.Relaxed);
                        lock (mutex) { protectedValue = protectedValue + 1; }
                        published = 1;
                    }
                    public static int ReturnUnderLock() { lock (mutex) { return 7; } }
                    [EntryPoint]
                    public static void Main()
                    {
                        IValue<int> boxed = new Value<int>(Identity(42));
                        Console.WriteLine(boxed.Read());
                        Box<int> box = new Box<int>(3);
                        Box<string> text = new Box<string>("x");
                        Console.WriteLine(Box<int>.Count);
                        Console.WriteLine(Box<string>.Count);
                        Transform<int> transform = Increment;
                        Console.WriteLine(transform(box.Value));
                        Thread first = new Thread(Worker);
                        Thread second = new Thread(Worker);
                        first.Start(); second.Start(); first.Join(); second.Join();
                        Console.WriteLine(counter.Load(MemoryOrder.Acquire));
                        Console.WriteLine(protectedValue);
                        Console.WriteLine(published);
                        Console.WriteLine(ReturnUnderLock());
                        try { lock (mutex) { throw new Exception("test"); } } catch (Exception error) { Console.WriteLine(error.Message); }
                        bool entered = mutex.TryEnter(); Console.WriteLine(entered); if (entered) mutex.Exit();
                        Atomic.Fence(MemoryOrder.SequentiallyConsistent);
                    }
                }
                """;
            var result = CompileAndRun(source, threads: true);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "42\n1\n1\n4\n2000\n2\n1\n7\ntest\nTrue\n", result.StandardOutput);
            var generated = Emit(source);
            Assert(generated.StartsWith("/* Generated by C~ draft 0.15", StringComparison.Ordinal), "Generated C did not identify draft 0.15.");
            Assert(generated.Contains("ct_managed_thread_start", StringComparison.Ordinal) && generated.Contains("ct_managed_mutex_enter", StringComparison.Ordinal), "Managed concurrency helpers were not emitted.");
            var espReady = generated.IndexOf("ct_atomic_store_release(&payload->Ready, 1u); created = xTaskCreate", StringComparison.Ordinal);
            Assert(espReady >= 0, "The ESP worker readiness gate must be published before xTaskCreate can preempt its creator.");
        });

        suite.Run("draft 0.15 abstract interface dispatch and generic constraints", () =>
        {
            const string source = """
                using System;
                public interface INamed { string Name { get; } }
                public interface IValue { int Read(); }
                public abstract class Base : object, INamed
                {
                    public virtual string Name { get { return "base"; } }
                    public abstract int ReadCore();
                }
                public sealed class Item : Base, IValue
                {
                    private int value;
                    public Item() { value = 17; }
                    public override string Name { get { return "item"; } }
                    public override int ReadCore() { return value; }
                    public int Read() { return ReadCore(); }
                }
                public static class Factory
                {
                    public static T Create<T>() where T : class, new() { return new T(); }
                    public static int Read<T>(T value) where T : IValue { return value.Read(); }
                }
                public static class Program
                {
                    [EntryPoint] public static void Main()
                    {
                        Item item = Factory.Create<Item>();
                        INamed named = item;
                        IValue value = item;
                        Console.WriteLine(named.Name);
                        Console.WriteLine(Factory.Read<IValue>(value));
                        Console.WriteLine(value is Item);
                        Console.WriteLine((value as INamed).Name);
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "item\n17\nTrue\nitem\n", result.StandardOutput);
            var generated = Emit(source);
            Assert(generated.Contains("struct ct_interface_entry", StringComparison.Ordinal) &&
                generated.Contains("static const ct_interface_entry ct_i_", StringComparison.Ordinal) &&
                generated.Contains("current->Interfaces[index].Type == target", StringComparison.Ordinal),
                "Concrete interface dispatch metadata was not emitted.");

            const string invalid = "public sealed class Item { public Item(int value) { } } public static class Program { private static T Create<T>() where T : class, new() { return new T(); } [EntryPoint] public static void Main() { Item item = Create<Item>(); } }";
            Assert(Compile(invalid).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1271"), "A type without a public parameterless constructor satisfied new().");
        });

        suite.Run("draft 0.15 atomic ordering and thread lifecycle failures", () =>
        {
            const string source = """
                using System;
                using System.Threading;
                public static class Program
                {
                    private static void Empty() { }
                    [EntryPoint] public static void Main()
                    {
                        Atomic<int> value = new Atomic<int>(1);
                        try { value.Load(MemoryOrder.Release); } catch (ArgumentException) { Console.WriteLine("load-order"); }
                        try { value.CompareExchange(2, 1, MemoryOrder.Relaxed, MemoryOrder.Acquire); } catch (ArgumentException) { Console.WriteLine("failure-order"); }
                        Thread beforeStart = new Thread(Empty);
                        try { beforeStart.Join(); } catch (ThreadStateException) { Console.WriteLine("join-before-start"); }
                        Thread once = new Thread(Empty);
                        once.Start(); once.Join();
                        try { once.Start(); } catch (ThreadStateException) { Console.WriteLine("start-twice"); }
                        Mutex missing = null;
                        try { lock (missing) { } } catch (NullReferenceException) { Console.WriteLine("null-lock"); }
                    }
                }
                """;
            var result = CompileAndRun(source, threads: true);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "load-order\nfailure-order\njoin-before-start\nstart-twice\nnull-lock\n", result.StandardOutput);
        });

        suite.Run("draft 0.15 ABI and debug metadata", () =>
        {
            const string source = "public static class Program { [EntryPoint] public static void Main() { System.Threading.Atomic<int> value = new System.Threading.Atomic<int>(0); value.Store(1, System.Threading.MemoryOrder.Release); } }";
            var compilation = Compile(source, new CompilationOptions(DebugInformation: DebugInformationMode.Instrumented, DebugMemory: DebugMemoryMode.Objects));
            using var c = new StringWriter();
            using var map = new StringWriter();
            using var header = new StringWriter();
            Assert(compilation.EmitC(c).Success && compilation.EmitDebugMap(map).Success && compilation.EmitCHeader(header).Success, "Draft 0.15 artifact emission failed.");
            using var document = JsonDocument.Parse(map.ToString());
            Assert(document.RootElement.GetProperty("version").GetInt32() == 3 && document.RootElement.GetProperty("runtimeAbi").GetInt32() == 15, "Debug metadata did not use v3/ABI 15.");
            Assert(header.ToString().Contains("CTILDE_RUNTIME_ABI_VERSION UINT32_C(15)", StringComparison.Ordinal), "The native header did not declare ABI 15.");
        });
    }
}
