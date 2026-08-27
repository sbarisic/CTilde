using System.Diagnostics;
using System.Globalization;
using System.Text;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart3(ConformanceSuite suite)
    {
        suite.Run("complete feature example", () =>
        {
            var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Examples", "Features.ct"));
            const string native = """

                uint32_t ct_native_buffer_sum(const uint8_t* data, size_t length)
                {
                    uint32_t result = 0;
                    for (size_t index = 0; index < length; index++) result += data[index];
                    return result;
                }

                uint32_t ct_native_utf8_length(const char* value)
                {
                    return value == NULL ? 0u : (uint32_t)strlen(value);
                }

                int32_t ct_native_resource_create(uintptr_t* resource)
                {
                    *resource = (uintptr_t)42;
                    return 0;
                }

                int32_t ct_native_resource_value(uintptr_t resource)
                {
                    return (int32_t)resource;
                }

                void ct_native_resource_release(uintptr_t resource)
                {
                    (void)resource;
                }

                int32_t ct_native_invoke_delegate(int32_t (*callback)(int32_t, void*), void* context, int32_t value)
                {
                    return callback(value, context);
                }

                int32_t ct_native_call_export(int32_t left, int32_t right)
                {
                    return ctilde_add(left, right);
                }
                """;
            var result = CompileAndRun(source, nativeSuffix: native);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "14\n4\n12\n6\neast\n2\nA\nText.Length < 10!\n10\n42\n-9223372036854775808\n18446744073709551615\n42\n42\n42\n42\n6\n42\n42\n42\nBefore deferred, i hope?\ndeferred\n", $"Unexpected output: {result.StandardOutput}");
        });

        suite.Run("object model example", () =>
        {
            var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Examples", "ObjectModel.ct"));
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "5\nExamples.Pair\n", result.StandardOutput);
        });

        suite.Run("exception syntax and diagnostics", () =>
        {
            const string syntaxSource = "public static class Program { [EntryPoint] public static void Main() { try { throw new System.Exception(\"x\"); } catch (System.Exception value) { throw; } finally { } } }";
            var tree = SyntaxTree.ParseText(syntaxSource, "exceptions.ct");
            Assert(tree.ToFullString() == syntaxSource, "Exception syntax did not round-trip exactly.");
            Assert(!tree.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, tree.Diagnostics));
            const string recoveredSource = "public static class Program { [EntryPoint] public static void Main() { try { throw; } catch (System.Exception value { } } }";
            var recovered = SyntaxTree.ParseText(recoveredSource, "recovered-exceptions.ct");
            Assert(recovered.ToFullString() == recoveredSource, "Recovered exception syntax did not round-trip exactly.");
            Assert(recovered.Tokens.Any(token => token.IsMissing), "Exception recovery did not retain a missing token.");

            const string invalid = "public static class Program { [EntryPoint] public static void Main() { try { } throw 1; throw; try { } catch (int) { } } }";
            var diagnostics = Compile(invalid).GetDiagnostics();
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT0108"), "A try statement without catch or finally was accepted.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2151"), "A non-Exception throw was accepted.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2152"), "A non-Exception catch type was accepted.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2154"), "A rethrow outside catch was accepted.");
            var invalidCompilation = Compile(invalid);
            using var invalidOutput = new StringWriter(CultureInfo.InvariantCulture);
            Assert(!invalidCompilation.EmitC(invalidOutput).Success && invalidOutput.GetStringBuilder().Length == 0, "Invalid exception syntax produced C output.");

            const string catchOrder = "using System; public class DerivedException : Exception { } public static class Program { [EntryPoint] public static void Main() { try { } catch (Exception) { } catch (DerivedException) { } catch { } catch (Exception) { } } }";
            Assert(Compile(catchOrder).GetDiagnostics().Count(diagnostic => diagnostic.Code == "CT2153") >= 2, "Unreachable or misplaced catches were accepted.");

            const string completeReturns = "using System; public static class Program { private static int Throwing() { throw new Exception(); } private static int Handled() { try { return 1; } catch { return 2; } } [EntryPoint] public static void Main() { } }";
            Assert(!Compile(completeReturns).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT3100"), "Throw and fully returning catches did not complete return flow.");
        });

        suite.Run("exception C lowering snapshot", () =>
        {
            var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Examples", "Exceptions.ct"));
            var first = Emit(source);
            var second = Emit(source);
            Assert(first == second, "Exception lowering was not byte-identical across repeated emission.");
            Assert(first.Contains("#include <setjmp.h>", StringComparison.Ordinal), "Exception lowering did not include setjmp support.");
            Assert(first.Contains("typedef struct ct_exception_frame", StringComparison.Ordinal), "The exception frame ABI was not emitted.");
            Assert(first.Contains("if (setjmp(*ct_eh_0_finally->Target) == 0)", StringComparison.Ordinal), "The lexical finally handler was not emitted in controlling-expression form.");
            Assert(first.Contains("ct_exception_top = ct_eh_0_finally->Previous;", StringComparison.Ordinal), "A handler-pop path was not emitted.");
            Assert(first.Contains("ct_state.ct_ep_0 = 1;", StringComparison.Ordinal), "The pending return cleanup action was not emitted.");
            var faultReady = Emit("public static class Program { [EntryPoint] public static void Main() { } }");
            Assert(faultReady.Contains("#include <setjmp.h>", StringComparison.Ordinal) && faultReady.Contains("ct_runtime_faults_init", StringComparison.Ordinal), "A program without explicit throw syntax omitted catchable runtime-fault support.");
            var durable = Emit("using System; public static class Program { private static void M(int value) { int local = value; try { local = 2; throw new Exception(); } catch { Console.WriteLine(local); } } [EntryPoint] public static void Main() { M(1); } }");
            Assert(!durable.Contains("int32_t ct_pp_0;", StringComparison.Ordinal) && durable.Contains("int32_t ct_lp_0;", StringComparison.Ordinal), "Exception liveness did not isolate the modified local from the unchanged parameter.");
            Assert(durable.Contains("volatile struct", StringComparison.Ordinal), "Exception methods did not place durable state in an automatic volatile aggregate.");
            Assert(!durable.Contains("ct_alloc(sizeof(int32_t)", StringComparison.Ordinal), "Exception lowering allocated a durable scalar on the heap.");
            Assert(!durable.Contains("int32_t ct_l_0", StringComparison.Ordinal), "An ordinary C automatic represented a C~ local across setjmp.");
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0 && Normalize(result.StandardOutput) == "handled\ncleanup\n5\n", result.StandardOutput + result.StandardError);
        });

        suite.Run("exception catches rethrow and finally", () =>
        {
            const string source = """
                using System;
                public class DerivedException : Exception
                {
                    public DerivedException(string message) : base(message) { }
                }
                public static class Program
                {
                    private static Exception saved;
                    private static void Fail(int value)
                    {
                        if (value == 1) throw new DerivedException("derived");
                        throw new Exception("base");
                    }
                    [EntryPoint]
                    public static void Main()
                    {
                        int value = 1;
                        try
                        {
                            try { Fail(value); }
                            catch (DerivedException caught)
                            {
                                Console.WriteLine(caught.Message);
                                saved = caught;
                                throw;
                            }
                        }
                        catch (Exception caught)
                        {
                            Console.WriteLine(caught.Message);
                            Console.WriteLine(Object.ReferenceEquals(saved, caught));
                        }
                        finally { Console.WriteLine("finally"); }
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "derived\nderived\nTrue\nfinally\n", result.StandardOutput);
        });

        suite.Run("defer capture order and transfers", () =>
        {
            const string source = """
                using System;
                public static class Program
                {
                    private static void Record(int value) { Console.Write(value); }
                    private static int ReturnValue()
                    {
                        int value = 1;
                        defer Record(value);
                        value = 9;
                        defer Record(2);
                        return 7;
                    }
                    private static int NestedFinally()
                    {
                        try
                        {
                            defer Record(3);
                            return 8;
                        }
                        finally { Record(4); }
                    }
                    [EntryPoint]
                    public static void Main()
                    {
                        Console.Write(ReturnValue());
                        Console.Write(NestedFinally());
                        if (true)
                        {
                            defer Record(5);
                            Record(6);
                        }
                        int index = 0;
                        while (index < 2)
                        {
                            defer Record(index);
                            index++;
                            if (index == 1) continue;
                            break;
                        }
                        Console.WriteLine();
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "2173486501\n", result.StandardOutput);
            var generated = Emit(source);
            Assert(!generated.Contains("ct_alloc(sizeof(ct_exception_frame)", StringComparison.Ordinal), "defer allocated an exception frame on the heap.");
        });

        suite.Run("defer syntax diagnostics and cleanup exceptions", () =>
        {
            const string syntaxSource = "public static class Program { private static void F() { } [EntryPoint] public static void Main() { defer F(); } }";
            var tree = SyntaxTree.ParseText(syntaxSource, "defer.ct");
            Assert(tree.ToFullString() == syntaxSource, "defer syntax did not round-trip exactly.");
            Assert(tree.Tokens.Any(token => token.Kind == SyntaxKind.DeferKeyword), "defer was not classified as a keyword.");
            const string recoveredSource = "public static class Program { private static void F() { } [EntryPoint] public static void Main() { defer F() } }";
            var recovered = SyntaxTree.ParseText(recoveredSource, "recovered-defer.ct");
            Assert(recovered.ToFullString() == recoveredSource, "Recovered defer syntax did not round-trip exactly.");
            Assert(recovered.Tokens.Any(token => token.Kind == SyntaxKind.SemicolonToken && token.IsMissing), "A missing defer semicolon was not retained.");

            const string invalid = "public static class Program { private static void F() { } [EntryPoint] public static void Main() { defer 1; if (true) defer F(); switch (1) { case 1: defer F(); break; } } }";
            var diagnostics = Compile(invalid).GetDiagnostics();
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2156"), "A non-invocation defer was accepted.");
            Assert(diagnostics.Count(diagnostic => diagnostic.Code == "CT3111") == 2, "An unbraced branch or switch-section defer was accepted.");

            const string source = """
                using System;
                public sealed class Recorder
                {
                    private int prefix;
                    public Recorder(int value) { prefix = value; }
                    public int Write(int value) { Console.Write(prefix); Console.Write(value); return value; }
                }
                public static class Program
                {
                    private static void Record(int value) { Console.Write(value); }
                    private static void CleanupFailure() { throw new Exception("cleanup"); }
                    private static void Run()
                    {
                        Recorder current = new Recorder(4);
                        defer current.Write(2);
                        current = new Recorder(8);
                        defer Record(9);
                        defer CleanupFailure();
                        defer Record(1);
                        throw new Exception("body");
                    }
                    [EntryPoint]
                    public static void Main()
                    {
                        try { Run(); }
                        catch (Exception value) { Console.WriteLine(value.Message); }
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "1942cleanup\n", result.StandardOutput);
        });

        suite.Run("NoAlloc effects and contracts", () =>
        {
            const string valid = """
                public struct Pair
                {
                    public int Value;
                    public Pair(int value) { Value = value; }
                }
                public static class Native
                {
                    [Extern("native_noop")]
                    [NoAlloc]
                    public static void Noop(int value);
                }
                public class Base
                {
                    [NoAlloc]
                    public virtual int Read() { return 1; }
                }
                public class Derived : Base
                {
                    public override int Read() { return 2; }
                }
                public class PropertyBase
                {
                    [NoAlloc]
                    public virtual int Number { get { return 4; } }
                }
                public class PropertyDerived : PropertyBase
                {
                    public override int Number { get { return 5; } }
                }
                public static class Program
                {
                    [NoAlloc]
                    public static int Number { get { return 3; } }
                    private static int Pure(int value) { return value + 1; }
                    [NoAlloc]
                    private static int CastAndUnbox(object value)
                    {
                        int number = (int)value;
                        Base typed = value as Base;
                        object same = typed;
                        return number;
                    }
                    [NoAlloc]
                    private static int Recursive(int value)
                    {
                        if (value == 0) return 0;
                        return Recursive(value - 1);
                    }
                    [NoAlloc]
                    private static int Handle(Exception error)
                    {
                        try { throw error; }
                        catch { return 1; }
                    }
                    [NoAlloc]
                    private static int Work(Base value, PropertyBase property)
                    {
                        string constant = "a" + "b";
                        string same = constant.ToString();
                        Pair pair = new Pair(Pure(Number));
                        defer Native.Noop(pair.Value);
                        try { return value.Read() + property.Number + Recursive(2); }
                        finally { Native.Noop(0); }
                    }
                    [EntryPoint]
                    public static void Main() { }
                }
                """;
            var validDiagnostics = Compile(valid).GetDiagnostics();
            Assert(!validDiagnostics.Any(diagnostic => diagnostic.Code is "CT1233" or "CT2155"), string.Join(Environment.NewLine, validDiagnostics));

            const string invalid = """
                public delegate int Reader();
                public class Box { }
                public class VirtualBase { public virtual int Read() { return 1; } public virtual int Number { get { return 2; } } }
                public class ContractBase { [NoAlloc] public virtual string Text() { return "ok"; } }
                public class BadOverride : ContractBase { public override string Text() { return 1.ToString(); } }
                public class ContractPropertyBase { [NoAlloc] public virtual string Text { get { return "ok"; } } }
                public class BadPropertyOverride : ContractPropertyBase { public override string Text { get { return 3.ToString(); } } }
                public sealed class AccessorBox { public int Number { get { string text = 4.ToString(); return 4; } set { } } }
                public static class Native { [Extern("native_unknown")] public static void Unknown(); }
                public static class Program
                {
                    private static int Read() { return 1; }
                    private static Box Allocate() { return new Box(); }
                    private static void AllocatingSink(string value) { string copy = value + "!"; }
                    [NoAlloc] private static Box ClassAllocation() { return new Box(); }
                    [NoAlloc] private static int[] ArrayAllocation() { return new int[1]; }
                    [NoAlloc] private static string Concatenate(string value) { return value + "!"; }
                    [NoAlloc] private static string Format() { return 1.ToString(); }
                    [NoAlloc] private static object BoxValue() { return 1; }
                    [NoAlloc] private static Box Transitive() { return Allocate(); }
                    [NoAlloc] private static void NativeBoundary() { Native.Unknown(); }
                    [NoAlloc] private static int VirtualBoundary(VirtualBase value) { return value.Read(); }
                    [NoAlloc] private static int VirtualPropertyBoundary(VirtualBase value) { return value.Number; }
                    [NoAlloc] private static void IncrementProperty(AccessorBox value) { value.Number++; }
                    [NoAlloc] private static void Deferred() { defer AllocatingSink(1.ToString()); }
                    [NoAlloc] private static Reader CreateDelegate() { return Read; }
                    [NoAlloc] private static int InvokeDelegate(Reader value) { return value(); }
                    [NoAlloc] private static unsafe int InvokePointer(delegate* unmanaged<int, int> value) { return value(1); }
                    [NoAlloc] public static string Property { get { return 2.ToString(); } }
                    [NoAlloc] public static int SetterProperty { set { string text = value.ToString(); } }
                    [NoAlloc(1)] public static int BadPropertyArguments { get { return 0; } }
                    [NoAlloc(1)] private static void BadArguments() { }
                    [EntryPoint] public static void Main() { }
                }
                """;
            var invalidDiagnostics = Compile(invalid).GetDiagnostics();
            var repeatedEffects = Compile(invalid).GetDiagnostics().Where(diagnostic => diagnostic.Code == "CT2155").Select(diagnostic => (diagnostic.Message, diagnostic.Location)).ToArray();
            Assert(invalidDiagnostics.Where(diagnostic => diagnostic.Code == "CT2155").Select(diagnostic => (diagnostic.Message, diagnostic.Location)).SequenceEqual(repeatedEffects), "NoAlloc witnesses were not deterministic.");
            Assert(invalidDiagnostics.Any(diagnostic => diagnostic.Code == "CT1233"), "NoAlloc arguments were accepted.");
            Assert(invalidDiagnostics.Count(diagnostic => diagnostic.Code == "CT2155") >= 15, string.Join(Environment.NewLine, invalidDiagnostics));
            Assert(invalidDiagnostics.Any(diagnostic => diagnostic.Message.Contains("Transitive", StringComparison.Ordinal) && diagnostic.Message.Contains("Allocate", StringComparison.Ordinal)), "The transitive allocation witness was not reported.");
            Assert(invalidDiagnostics.Any(diagnostic => diagnostic.Message.Contains("extern call", StringComparison.Ordinal)), "An uncontracted extern boundary was accepted.");
            Assert(invalidDiagnostics.Any(diagnostic => diagnostic.Message.Contains("virtual call", StringComparison.Ordinal)), "An uncontracted virtual boundary was accepted.");
            Assert(invalidDiagnostics.Any(diagnostic => diagnostic.Message.Contains("BadOverride", StringComparison.Ordinal)), "An allocating override did not inherit the NoAlloc contract.");
            Assert(invalidDiagnostics.Any(diagnostic => diagnostic.Message.Contains("BadPropertyOverride", StringComparison.Ordinal)), "An allocating property override did not inherit the NoAlloc contract.");
            Assert(invalidDiagnostics.Any(diagnostic => diagnostic.Message.Contains("Property", StringComparison.Ordinal)), "An allocating contracted property accessor was accepted.");
            Assert(invalidDiagnostics.Any(diagnostic => diagnostic.Message.Contains("IncrementProperty", StringComparison.Ordinal) && diagnostic.Message.Contains("get_Number", StringComparison.Ordinal)), "A transitive property getter allocation was not reported.");
            Assert(invalidDiagnostics.Count(diagnostic => diagnostic.Code == "CT2155" && diagnostic.Message.Contains("Deferred", StringComparison.Ordinal)) >= 2, "defer capture and deferred-call effects were not both analyzed.");
            Assert(invalidDiagnostics.Any(diagnostic => diagnostic.Code == "CT2155" && diagnostic.Message.Contains("delegate", StringComparison.OrdinalIgnoreCase)), "Delegate creation or invocation was accepted in NoAlloc code.");
            Assert(invalidDiagnostics.Any(diagnostic => diagnostic.Code == "CT2155" && diagnostic.Message.Contains("function-pointer", StringComparison.OrdinalIgnoreCase)), "Function-pointer invocation was accepted in NoAlloc code.");

            const string invalidTargets = "[NoAlloc] public class Value { [NoAlloc] public int Field; [NoAlloc] public Value() { } } public static class Program { [EntryPoint] public static void Main() { } }";
            Assert(Compile(invalidTargets).GetDiagnostics().Count(diagnostic => diagnostic.Code == "CT1213") == 2, "NoAlloc was accepted on a type or field, or rejected on a constructor.");
        });

        suite.Run("exception unhandled and null failures", () =>
        {
            const string formattingSource = "using System; public class NamedException : Exception { public NamedException(string message) : base(message) { } } public static class Program { [EntryPoint] public static void Main() { Exception empty = new Exception(); Console.WriteLine(empty.Message.Length); Console.WriteLine(new Exception(null)); Console.WriteLine(new NamedException(\"detail\")); } }";
            var formatting = CompileAndRun(formattingSource);
            Assert(formatting.ExitCode == 0 && Normalize(formatting.StandardOutput) == "0\nSystem.Exception\nNamedException: detail\n", formatting.StandardOutput + formatting.StandardError);

            var unhandled = CompileAndRun("using System; public static class Program { [EntryPoint] public static void Main() { try { throw new Exception(\"caught\"); } catch { } throw new Exception(\"boom\"); } }");
            Assert(unhandled.ExitCode != 0 && unhandled.StandardError.Contains("CTE0001", StringComparison.Ordinal) && unhandled.StandardError.Contains("System.Exception: boom", StringComparison.Ordinal), unhandled.StandardError);

            var nullThrow = CompileAndRun("using System; public static class Program { [EntryPoint] public static void Main() { Exception value = null; throw value; } }");
            Assert(nullThrow.ExitCode != 0 && nullThrow.StandardError.Contains("CTE0002", StringComparison.Ordinal), nullThrow.StandardError);
        });

        suite.Run("exception cleanup transfers and stack state", () =>
        {
            const string source = """
                using System;
                public struct Pair { public int Value; public Pair(int value) { Value = value; } }
                public static class Program
                {
                    private static int ReturnThroughFinally()
                    {
                        try { return 5; }
                        finally { Console.WriteLine("return cleanup"); }
                    }
                    private static void PreserveState(int value)
                    {
                        string text = "before";
                        Pair pair = new Pair(2);
                        try
                        {
                            value = 7;
                            text = "after";
                            pair.Value = 9;
                            throw new Exception("state");
                        }
                        catch
                        {
                            Console.WriteLine(value);
                            Console.WriteLine(text);
                            Console.WriteLine(pair.Value);
                        }
                    }
                    private static void PreserveForeachState()
                    {
                        int[] values = new int[1];
                        foreach (int value in values)
                        {
                            try
                            {
                                value = 11;
                                throw new Exception("foreach");
                            }
                            catch { Console.WriteLine(value); }
                        }
                    }
                    [EntryPoint]
                    public static void Main()
                    {
                        Console.WriteLine(ReturnThroughFinally());
                        int index = 0;
                        while (index < 2)
                        {
                            index++;
                            try { continue; }
                            finally { Console.WriteLine(index); }
                        }
                        while (true)
                        {
                            try { break; }
                            finally
                            {
                                while (true) { break; }
                                Console.WriteLine("break cleanup");
                            }
                        }
                        PreserveState(1);
                        PreserveForeachState();
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "return cleanup\n5\n1\n2\nbreak cleanup\n7\nafter\n9\n11\n", result.StandardOutput);
        });

        suite.Run("exceptions across constructors virtual calls and catches", () =>
        {
            const string source = """
                using System;
                public class DemoException : Exception { public DemoException(string message) : base(message) { } }
                public class Base
                {
                    public Base() { Run(); }
                    protected virtual void Run() { }
                }
                public class Derived : Base
                {
                    public Derived() : base() { }
                    protected override void Run() { throw new DemoException("virtual constructor"); }
                }
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        try { Derived value = new Derived(); }
                        catch (DemoException value) { Console.WriteLine(value.Message); }
        
                        try
                        {
                            try { throw new DemoException("first"); }
                            catch (DemoException) { throw new Exception("from catch"); }
                            catch (Exception) { Console.WriteLine("wrong sibling"); }
                        }
                        catch (Exception value) { Console.WriteLine(value.Message); }
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "virtual constructor\nfrom catch\n", result.StandardOutput);
        });

        suite.Run("exception finally diagnostics and replacement", () =>
        {
            const string invalid = "public static class Program { private static void M() { while (true) { try { } finally { break; continue; return; } } } [EntryPoint] public static void Main() { } }";
            Assert(Compile(invalid).GetDiagnostics().Count(diagnostic => diagnostic.Code == "CT3110") == 3, "A control transfer was allowed to leave finally.");

            const string replacement = "using System; public static class Program { private static int Value() { try { return 1; } finally { throw new Exception(\"replacement\"); } } [EntryPoint] public static void Main() { try { Console.WriteLine(Value()); } catch (Exception value) { Console.WriteLine(value.Message); } } }";
            var result = CompileAndRun(replacement);
            Assert(result.ExitCode == 0 && Normalize(result.StandardOutput) == "replacement\n", result.StandardOutput + result.StandardError);

            const string exceptionalRead = "using System; public static class Program { private static void MayThrow() { } [EntryPoint] public static void Main() { int value; try { MayThrow(); value = 1; } finally { Console.WriteLine(value); } } }";
            Assert(Compile(exceptionalRead).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT3108"), "A finally block used the normal try assignment state.");

            const string assignments = "public static class Program { [EntryPoint] public static void Main() { int fromTry; int fromFinally; try { fromTry = 1; } finally { fromFinally = 2; } int first = fromTry; int second = fromFinally; } }";
            Assert(!Compile(assignments).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT3108"), "Assignments from normal try and finally completion were not preserved.");

            const string readonlyMerge = "public static class Program { [EntryPoint] public static void Main() { readonly int value; try { value = 1; } finally { value = 2; } } }";
            Assert(Compile(readonlyMerge).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT3130"), "Readonly assignments in try and finally were not merged.");

            const string readonlyFieldMerge = "public class Value { public readonly int Data; public Value() { try { Data = 1; } finally { Data = 2; } } } public static class Program { [EntryPoint] public static void Main() { } }";
            Assert(Compile(readonlyFieldMerge).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT3131"), "Constructor readonly-field assignments in try and finally were not merged.");
        });

        suite.Run("ARC ABI and ownership annotations", () =>
        {
            var generated = Emit("public class Item { public Item Next; } public static class Program { [EntryPoint] public static void Main() { Item value = new Item(); } }");
            Assert(generated.Contains("ct_atomic_u32 RefCount;", StringComparison.Ordinal), "The object header does not contain an atomic reference count.");
            Assert(generated.Contains("ct_object* ReleaseNext;", StringComparison.Ordinal), "The object header does not contain the intrusive release link.");
            Assert(generated.Contains("void (*Drop)(ct_object*);", StringComparison.Ordinal), "Type descriptors do not contain drop callbacks.");
            Assert(generated.Contains("void ct_retain(ct_object* object)", StringComparison.Ordinal), "ct_retain was not exported.");
            Assert(generated.Contains("void ct_release(ct_object* object)", StringComparison.Ordinal), "ct_release was not exported.");
            Assert(generated.Contains("ct_cleanup_record", StringComparison.Ordinal), "Automatic cleanup records were not emitted.");

            const string valid = "public static class Native { [Extern(\"keep\")] public static void Keep([Retained] object value); [Extern(\"borrow\")] [ReturnsBorrowed] public static object Borrow(); } public static class Program { [EntryPoint] public static void Main() { } }";
            Assert(!Compile(valid).GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "Valid extern ownership annotations were rejected.");

            const string invalidRetained = "public static class Native { public static void Managed([Retained] object value) { } [Extern(\"bad\")] public static void Bad([Retained(1)] int value); } public static class Program { [EntryPoint] public static void Main() { } }";
            Assert(Compile(invalidRetained).GetDiagnostics().Count(diagnostic => diagnostic.Code == "CT1234") == 2, "Invalid Retained targets or arguments did not produce CT1234.");

            const string invalidBorrowed = "public static class Native { [ReturnsBorrowed] public static object Managed() { return null; } [Extern(\"bad\")] [ReturnsBorrowed(1)] public static int Bad(); } public static class Program { [EntryPoint] public static void Main() { } }";
            Assert(Compile(invalidBorrowed).GetDiagnostics().Count(diagnostic => diagnostic.Code == "CT1235") == 2, "Invalid ReturnsBorrowed targets or arguments did not produce CT1235.");

            const string unsafeCall = "using System.Runtime; public static class Program { [EntryPoint] public static void Main() { Memory.Retain(null); } }";
            Assert(Compile(unsafeCall).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2139"), "Calling unsafe Memory.Retain outside an unsafe context was allowed.");

            const string conflictingOwnership = "public static class A { [Extern(\"native_argument\")] public static void First(object value); [Extern(\"native_result\")] public static object Result(); } public static class B { [Extern(\"native_argument\")] public static void Second([Retained] object value); [Extern(\"native_result\")] [ReturnsBorrowed] public static object Result(); } public static class Program { [EntryPoint] public static void Main() { } }";
            Assert(Compile(conflictingOwnership).GetDiagnostics().Count(diagnostic => diagnostic.Code == "CT4102") == 2, "Conflicting extern ownership contracts were accepted for one native symbol.");

            const string reservedOwnershipHelper = "public static class Native { [Extern(\"ct_drop_object_fake\")] public static void Drop(); } public static class Program { [EntryPoint] public static void Main() { } }";
            Assert(Compile(reservedOwnershipHelper).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4101"), "A generated ARC helper prefix was available to user externs.");
        });

        suite.Run("ARC deterministic lifetime", () =>
        {
            const string source = """
                using System;
                public class Node { public Node Next; public string Name; }
                public struct Holder { public Node Value; }
                public static class Diagnostics
                {
                    [Extern("ct_memory_diagnostic_live_allocations")]
                    [NoAlloc]
                    public static uint LiveAllocations();
                }
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        uint baseline = Diagnostics.LiveAllocations();
                        {
                            Node first = new Node();
                            Node alias = first;
                            alias = alias;
                            Node second = new Node();
                            first.Next = second;
                            first.Next = null;
        
                            Node[] values = new Node[2];
                            values[0] = first;
                            values[0] = null;
        
                            Holder holder = new Holder();
                            holder.Value = first;
                            object boxed = holder;
                            boxed = null;
                        }
                        Console.WriteLine(Diagnostics.LiveAllocations() == baseline);
                    }
                }
                """;
            var result = CompileAndRun(source, memoryDiagnostics: true);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "True\n", result.StandardOutput);
        });

        suite.Run("ARC properties arrays virtual calls and defer captures", () =>
        {
            const string source = """
                using System;
                public class Item { }
                public struct Holder { public Item Value; }
                public class Owner
                {
                    private Item value;
                    public Item Automatic { get; set; }
                    public Item Explicit { get { return value; } set { this.value = value; } }
                }
                public class Base
                {
                    public virtual Item Create() { return new Item(); }
                    public virtual Item Value { get { return new Item(); } set { } }
                }
                public class Derived : Base
                {
                    public override Item Create() { return new Item(); }
                    public override Item Value { get { return new Item(); } set { } }
                }
                public static class Diagnostics
                {
                    [Extern("ct_memory_diagnostic_live_allocations")]
                    [NoAlloc]
                    public static uint LiveAllocations();
                }
                public static class Program
                {
                    private static void Observe(Item value) { }
        
                    [EntryPoint]
                    public static void Main()
                    {
                        uint baseline = Diagnostics.LiveAllocations();
                        {
                            Item item = new Item();
                            Holder[] holders = new Holder[2];
                            holders[0].Value = item;
                            holders[0].Value = null;
        
                            Owner owner = new Owner();
                            owner.Automatic = item;
                            Item automatic = owner.Automatic;
                            owner.Automatic = null;
                            owner.Explicit = item;
                            Item explicitValue = owner.Explicit;
                            owner.Explicit = null;
        
                            Base polymorphic = new Derived();
                            Item created = polymorphic.Create();
                            Item property = polymorphic.Value;
                            polymorphic.Value = item;
                            defer Observe(item);
                        }
                        Console.WriteLine(Diagnostics.LiveAllocations() == baseline);
                    }
                }
                """;
            var result = CompileAndRun(source, memoryDiagnostics: true);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "True\n", result.StandardOutput);
        });

        suite.Run("ARC returns exceptions and iterative destruction", () =>
        {
            const string source = """
                using System;
                public class Node { public Node Next; }
                public class Broken
                {
                    public Node Value = new Node();
                    public Broken() { throw new Exception("broken"); }
                }
                public static class Diagnostics
                {
                    [Extern("ct_memory_diagnostic_live_allocations")]
                    [NoAlloc]
                    public static uint LiveAllocations();
                }
                public static class Program
                {
                    private static Node Echo(Node value) { return value; }
                    private static Node Make() { return new Node(); }
        
                    [EntryPoint]
                    public static void Main()
                    {
                        uint baseline = Diagnostics.LiveAllocations();
                        {
                            Node made = Make();
                            Node echoed = Echo(made);
                            try { Broken value = new Broken(); }
                            catch (Exception) { }
        
                            Node head = null;
                            int index = 0;
                            while (index < 10000)
                            {
                                Node next = new Node();
                                next.Next = head;
                                head = next;
                                index++;
                            }
                        }
                        Console.WriteLine(Diagnostics.LiveAllocations() == baseline);
                    }
                }
                """;
            var result = CompileAndRun(source, memoryDiagnostics: true);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "True\n", result.StandardOutput);
        });

        suite.Run("ARC native ownership and unsafe manual counts", () =>
        {
            const string source = """
                using System;
                using System.Runtime;
                public class Item { }
                public static class Native
                {
                    [Extern("native_keep")]
                    [NoAlloc]
                    public static void Keep([Retained] object value);
        
                    [Extern("native_borrow")]
                    [ReturnsBorrowed]
                    [NoAlloc]
                    public static object Borrow();
        
                    [Extern("native_clear")]
                    [NoAlloc]
                    public static void Clear();
                }
                public static class Diagnostics
                {
                    [Extern("ct_memory_diagnostic_live_allocations")]
                    [NoAlloc]
                    public static uint LiveAllocations();
                }
                public static class Program
                {
                    [EntryPoint]
                    public static unsafe void Main()
                    {
                        uint baseline = Diagnostics.LiveAllocations();
                        {
                            Item value = new Item();
                            Memory.Retain(value);
                            Memory.Release(value);
                            Native.Keep(value);
                            object borrowed = Native.Borrow();
                            Native.Clear();
                        }
                        Console.WriteLine(Diagnostics.LiveAllocations() == baseline);
                    }
                }
                """;
            const string nativeSuffix = """
        
                static ct_managed_object* native_saved = NULL;
                void native_keep(ct_managed_object* value) { native_saved = value; }
                ct_managed_object* native_borrow(void) { return native_saved; }
                void native_clear(void) { ct_release((ct_object*)(void*)native_saved); native_saved = NULL; }
                """;
            var result = CompileAndRun(source, memoryDiagnostics: true, nativeSuffix: nativeSuffix);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "True\n", result.StandardOutput);
        });

        suite.Run("ARC cycle limitation", () =>
        {
            const string source = """
                using System;
                public delegate int Reader();
                public class Node { public Node Next; public Reader Callback; public int Read() { return 1; } }
                public static class Diagnostics
                {
                    [Extern("ct_memory_diagnostic_live_allocations")]
                    [NoAlloc]
                    public static uint LiveAllocations();
                }
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        uint baseline = Diagnostics.LiveAllocations();
                        {
                            Node left = new Node();
                            Node right = new Node();
                            left.Next = right;
                            right.Next = left;
                        }
                        {
                            Node target = new Node();
                            target.Callback = target.Read;
                        }
                        Console.WriteLine(Diagnostics.LiveAllocations() == baseline + 4u);
                    }
                }
                """;
            var result = CompileAndRun(source, memoryDiagnostics: true);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "True\n", result.StandardOutput);
        });

    }
}
