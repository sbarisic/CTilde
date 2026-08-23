using System.Diagnostics;
using System.Globalization;
using System.Text;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart1(ConformanceSuite suite)
    {
        suite.Run("deterministic C emission", () =>
        {
            const string source = """
                using System;
                namespace Tests;
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        int value = 2 + 3 * 4;
                        Console.WriteLine(value);
                    }
                }
                """;
            var first = Emit(source);
            var second = Emit(source);
            Assert(first == second, "Repeated compilation did not produce byte-identical C.");
            Assert(first.Contains("int main(void)", StringComparison.Ordinal), "C entry point was not emitted.");
            Assert(first.Contains("for GNU C23", StringComparison.Ordinal), "Generated C does not identify the default GNU C23 dialect.");
            Assert(first.Contains("static_assert(CHAR_BIT == 8", StringComparison.Ordinal), "Generated C does not use the C23 static_assert spelling.");
        });

        suite.Run("bound analysis and typed IR-only emission", () =>
        {
            const string source = "public static class Program { private static void Done() { } private static void Unused() { } [EntryPoint] public static void Main() { int[] values = new int[1]; int value = 40 + 2; if (values[0] == 0) { values[0] = value; } defer Done(); return; } }";
            var compilation = Compile(source);
            var compilerAssembly = typeof(Compilation).Assembly;
            var cWriterType = compilerAssembly.GetType("CTilde.CWriter", throwOnError: true)!;
            var constructionCount = cWriterType.GetProperty("ConstructionCount", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            var writersBeforeAnalysis = (int)constructionCount.GetValue(null)!;
            var before = compilation.GetDiagnostics();
            Assert(!before.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, before));
            Assert((int)constructionCount.GetValue(null)! == writersBeforeAnalysis, "GetDiagnostics constructed a C writer.");

            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var generatedField = typeof(Compilation).GetField("_generatedC", flags)!;
            var boundField = typeof(Compilation).GetField("_boundProgram", flags)!;
            Assert(generatedField.GetValue(compilation) is null, "GetDiagnostics initialized generated C.");
            var bound = (BoundProgram?)boundField.GetValue(compilation);
            Assert(bound is not null, "GetDiagnostics did not retain an immutable bound program.");
            Assert(bound!.SemanticMap.Count > 0, "The bound program did not contain expression semantics.");
            var mainBody = bound.Bodies.Single(body => body.Method.IsEntryPoint);
            Assert(mainBody.Root.Kind == BoundStatementKind.Block && mainBody.Flow.ContainsReturn && mainBody.Flow.ContainsDefer, "The bound body did not preserve structured flow and cleanup.");
            Assert(mainBody.Semantics.Values.Any(entry => entry.Type == CType.Int && entry.ConstantValue is int), "The bound body did not preserve typed constants.");

            var instructionType = compilerAssembly.GetType("CTilde.IrInstruction", throwOnError: true)!;
            var lowererType = compilerAssembly.GetType("CTilde.TypedIrLowerer", throwOnError: true)!;
            Assert(instructionType.GetProperty("Text") is null, "Typed IR retained rendered C text.");
            Assert(lowererType.GetMethod("Classify", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic) is null, "Typed IR retained the line classifier.");
            Assert(compilerAssembly.GetType("CTilde.BodyPipeline", throwOnError: false) is null, "The syntax-driven BodyPipeline transition layer still exists.");
            Assert(compilerAssembly.GetType("CTilde.CBodyLowerer", throwOnError: false) is null, "CEmitter can still replay the CBodyLowerer transition layer.");
            Assert(compilerAssembly.GetType("CTilde.LoweredExpression", throwOnError: false) is null, "The LoweredExpression transition type still exists.");

            var typedIr = new TypedIrLowerer(bound).Lower();
            Assert(typeof(TypedIrProgram).GetProperty("Emission") is null, "Typed IR retained a program-wide rendered C emission plan.");
            Assert(typedIr.Functions.All(function => function.Emission is null), "Semantic typed IR eagerly rendered C function bodies.");
            var mainIr = typedIr.Functions.Single(function => function.Method.IsEntryPoint);
            Assert(mainIr.Blocks.SelectMany(block => block.Instructions).Any(instruction => instruction is IrBinary), "Typed IR did not contain the bound binary operation.");
            Assert(mainIr.Blocks.SelectMany(block => block.Instructions).Any(instruction => instruction is IrCheck { Kind: IrCheckKind.Bounds }), "Typed IR did not contain a bounds check.");
            Assert(mainIr.Blocks.SelectMany(block => block.Instructions).Any(instruction => instruction is IrCleanupAction { Kind: IrCleanupActionKind.RunDefer }), "Typed IR did not contain a defer cleanup action.");
            Assert(mainIr.Blocks.Any(block => block.Terminator is IrConditionalTerminator), "Typed IR did not contain structured conditional control flow.");

            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            var result = compilation.EmitC(writer);
            Assert(result.Success && writer.ToString().Contains("return", StringComparison.Ordinal), "Lazy emission did not produce C.");
            Assert(before.SequenceEqual(compilation.GetDiagnostics()), "Emission changed the analyzed diagnostics.");

            var optimizedIr = new TypedIrOptimizer(bound).Optimize(typedIr);
            var emissionEmitter = new CEmitter(bound.Model, CompilationTarget.Hosted, null);
            var emissionIr = new TypedIrEmissionLowerer(emissionEmitter).Lower(optimizedIr);
            Assert(emissionIr.Functions.All(function => function.Emission is not null), "Retained typed-IR functions did not receive immutable emission plans.");
            Assert(emissionIr.Functions.Length < typedIr.Functions.Length, "Function-body emission ran before whole-program reachability pruning.");
        });

        suite.Run("ESP-IDF target profile", () =>
        {
            const string source = "using Esp.Idf; public static class Program { [EntryPoint] public static void Main() { int[] values = new int[1]; long now = EspTimer.GetTimeMicroseconds(); FreeRtos.DelayMilliseconds(1u); Gpio.ConfigureOutput(32); Gpio.Write(32, true); Ws2812.Configure(4, 1u); Ws2812.SetPixel(0u, 0u, 16u, 0u); Ws2812.Refresh(); Ws2812.Clear(); } }";
            var options = new CompilationOptions(CompilationTarget.EspIdf);
            var first = Emit(source, options, @"E:\private\firmware\Program.ct");
            var second = Emit(source, options, @"E:\private\firmware\Program.ct");
            Assert(first == second, "Repeated ESP-IDF emission was not byte-identical.");
            Assert(first.Contains("for ESP-IDF GNU C23", StringComparison.Ordinal), "ESP-IDF banner was not emitted.");
            Assert(first.Contains("void app_main(void)", StringComparison.Ordinal), "ESP-IDF app_main was not emitted.");
            Assert(!first.Contains("int main(void)", StringComparison.Ordinal), "Hosted main was emitted for ESP-IDF.");
            Assert(!first.Contains("ct_keep_symbols", StringComparison.Ordinal), "ESP-IDF output retained ct_keep_symbols.");
            Assert(!first.Contains("ct_environment_exit", StringComparison.Ordinal), "Hosted Environment.Exit runtime was emitted for ESP-IDF.");
            Assert(first.Contains("static_assert(sizeof(void*) == 4", StringComparison.Ordinal), "ESP-IDF pointer-width assertion was not emitted.");
            Assert(first.Contains("\"ctilde_esp_shim.h\"", StringComparison.Ordinal), "ESP-IDF shim header was not included.");
            Assert(first.Contains("\"Program.ct\"", StringComparison.Ordinal) && !first.Contains("E:/private", StringComparison.Ordinal), "ESP-IDF source locations were not compacted.");
            Assert(first.Contains("CT_UNUSED", StringComparison.Ordinal), "ESP-IDF unused-definition marker was not emitted.");
            Assert(first.Contains("extern esp_err_t ct_esp_ws2812_configure(int32_t", StringComparison.Ordinal), "WS2812 configure ABI was not emitted.");
            Assert(first.Contains("extern esp_err_t ct_esp_ws2812_set_pixel(uint32_t", StringComparison.Ordinal), "WS2812 pixel ABI was not emitted.");
            Assert(first.Contains("extern esp_err_t ct_esp_ws2812_refresh(void);", StringComparison.Ordinal), "WS2812 refresh ABI was not emitted.");
            Assert(first.Contains("extern esp_err_t ct_esp_ws2812_clear(void);", StringComparison.Ordinal), "WS2812 clear ABI was not emitted.");
            Assert(first.Contains("extern int64_t ct_esp_timer_get_time_us(void);", StringComparison.Ordinal), "ESP timer ABI was not emitted.");

            var hostedDiagnostics = Compile(source).GetDiagnostics();
            Assert(hostedDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "ESP-IDF declarations were available to the hosted target.");
        });

        suite.Run("ESP-IDF target diagnostics", () =>
        {
            var exit = Compile("public static class Program { [EntryPoint] public static void Main() { System.Environment.Exit(1); } }", new CompilationOptions(CompilationTarget.EspIdf));
            Assert(exit.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4105"), "Environment.Exit was not rejected for ESP-IDF.");

            var reserved = Compile("public static class Native { [Extern(\"app_main\")] public static void Start(); [Extern(\"ct_esp_restart\")] public static void Restart(); [Extern(\"ct_esp_ws2812_configure\")] public static bool Configure(int pin, uint count); [Extern(\"ct_esp_ws2812_set_pixel\")] public static bool SetPixel(uint index, uint red, uint green, uint blue); [Extern(\"ct_esp_ws2812_refresh\")] public static bool Refresh(); [Extern(\"ct_esp_ws2812_clear\")] public static bool Clear(); [Extern(\"ct_esp_timer_get_time_us\")] public static long Time(); [Extern(\"ct_esp_error_name\")] public static string ErrorName(int code); [Extern(\"ct_esp_current_task\")] public static unsafe void* CurrentTask(); } public static class Program { [EntryPoint] public static void Main() { } }", new CompilationOptions(CompilationTarget.EspIdf));
            Assert(reserved.GetDiagnostics().Count(diagnostic => diagnostic.Code == "CT4101") == 9, "ESP-IDF target symbols were not reserved.");

            var invalid = Compile("public static class Program { [EntryPoint] public static void Main() { } }", new CompilationOptions((CompilationTarget)99));
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            var result = invalid.EmitC(writer);
            Assert(!result.Success && result.Diagnostics.Any(diagnostic => diagnostic.Code == "CT4104"), "Invalid API target did not produce CT4104.");
            Assert(writer.GetStringBuilder().Length == 0, "Invalid target emitted C output.");

            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
            var cliDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CTilde.Cli", "bin", configuration, "net10.0", "ctilde.dll"));
            var cli = RunProcess("dotnet", [cliDll, "--target", "unknown"]);
            Assert(cli.ExitCode == 2 && cli.StandardError.Contains("Unknown target 'unknown'", StringComparison.Ordinal), "Unknown CLI target was not a usage error.");
        });

        suite.Run("ESP GCC exception formatting", () =>
        {
            const string source = "using System; public static class Program { [EntryPoint] public static void Main() { throw new Exception(\"failure\"); } }";
            var generated = Emit(source, new CompilationOptions(CompilationTarget.EspIdf));
            Assert(generated.Contains("%.*s\", (int)message->Length", StringComparison.Ordinal), "Exception message precision was not converted to int for ESP GCC.");
        });

        suite.Run("structured syntax diagnostic", () =>
        {
            var tree = SyntaxTree.ParseText("public class Broken {", "broken.ct");
            Assert(tree.Diagnostics.Any(diagnostic => diagnostic.Code.StartsWith("CT0", StringComparison.Ordinal)), "Expected a syntax diagnostic.");
        });

        suite.Run("language service completion and navigation", () =>
        {
            const string source = "using System; public static class Program { [EntryPoint] public static void Main() { Console. } }";
            var tree = SyntaxTree.ParseText(source, "editor.ct");
            var service = LanguageServiceSnapshot.Create([tree]);
            var completionPosition = source.IndexOf("Console.", StringComparison.Ordinal) + "Console.".Length;
            var completions = service.GetCompletions("editor.ct", completionPosition);
            Assert(completions.Any(item => item.Label == "WriteLine" && item.Kind == LanguageCompletionKind.Method), "Console member completion did not include WriteLine.");
            Assert(completions.All(item => item.ReplacementSpan.Start == completionPosition), "Empty member completion used the wrong replacement span.");

            const string deferSource = "public static class Program { [EntryPoint] public static void Main() { de } }";
            var deferService = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(deferSource, "defer-completion.ct")]);
            var deferPosition = deferSource.IndexOf("de }", StringComparison.Ordinal) + 2;
            Assert(deferService.GetCompletions("defer-completion.ct", deferPosition).Any(item => item.Label == "defer" && item.Kind == LanguageCompletionKind.Keyword), "Statement completion did not include defer.");

            var consolePosition = source.IndexOf("Console", StringComparison.Ordinal) + 1;
            var hover = service.GetHover("editor.ct", consolePosition);
            Assert(hover?.Contents.Contains("System.Console", StringComparison.Ordinal) == true, "Hover did not resolve System.Console.");
            var definition = service.GetDefinition("editor.ct", consolePosition);
            Assert(definition?.FilePath == "stdlib/System/Console.ct", "Definition did not resolve the embedded Console declaration.");
            Assert(service.GetDocumentSymbols("editor.ct").Single().Name == "Program", "Document symbols did not include Program.");
            Assert(service.GetWorkspaceSymbols("Prog").Any(symbol => symbol.Name == "Program"), "Workspace symbols did not include Program.");

            var library = SyntaxTree.ParseText("namespace Demo; public class Widget { public int Value; }", "library.ct");
            const string programSource = "using Demo; public static class Program { [EntryPoint] public static void Main() { Widget value = new Widget(); } }";
            var program = SyntaxTree.ParseText(programSource, "program.ct");
            var multiFile = LanguageServiceSnapshot.Create([library, program]);
            var widgetDefinition = multiFile.GetDefinition("program.ct", programSource.IndexOf("Widget", StringComparison.Ordinal) + 1);
            Assert(widgetDefinition?.FilePath == "library.ct", "Cross-file type definition did not resolve to its declaration.");

            var unicode = SourceText.From("α\r\nβ", "unicode.ct");
            var unicodePosition = unicode.GetPosition(1, 1);
            var unicodeLocation = unicode.GetLocation(new TextSpan(unicodePosition, 0));
            Assert(unicodePosition == 4 && unicodeLocation.Line == 2 && unicodeLocation.Column == 2, "UTF-16 CRLF position conversion was incorrect.");
        });

        suite.Run("language service scopes and targets", () =>
        {
            const string source = "public class Device { public int Value; private int Secret; public void Run(int count) { } } public static class Program { [EntryPoint] public static void Main() { Device device = new Device(); device. } }";
            var service = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(source, "scopes.ct")]);
            var position = source.LastIndexOf("device.", StringComparison.Ordinal) + "device.".Length;
            var completions = service.GetCompletions("scopes.ct", position);
            Assert(completions.Any(item => item.Label == "Value"), "Instance completion did not include an accessible field.");
            Assert(completions.Any(item => item.Label == "Run"), "Instance completion did not include an accessible method.");
            Assert(completions.All(item => item.Label != "Secret"), "Instance completion exposed an inaccessible private field.");

            const string targetSource = "using Esp.Idf;\n\npublic static class Program { [EntryPoint] public static void Main() { } }";
            var targetPosition = targetSource.IndexOf("\n\n", StringComparison.Ordinal) + 1;
            var hosted = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(targetSource, "hosted.ct")]);
            var esp = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(targetSource, "esp.ct")], new CompilationOptions(CompilationTarget.EspIdf));
            Assert(hosted.GetCompletions("hosted.ct", targetPosition).All(item => item.Label != "Ws2812"), "ESP type leaked into the hosted language service.");
            Assert(esp.GetCompletions("esp.ct", targetPosition).Any(item => item.Label == "Ws2812"), "ESP target completion did not include Ws2812.");
        });

        suite.Run("language service semantic tokens", () =>
        {
            const string source = """
                using System;
                namespace Demo;
                public enum Mode { Off, On }
                public class Device
                {
                    public static readonly int Count;
                    public int Value;
                    public void Run(int parameter)
                    {
                        readonly int local = parameter;
                        foreach (int item in new int[1]) { Value = item; }
                        try { Console.WriteLine(local); }
                        catch (Exception error) { Console.WriteLine(error); }
                        Unknown;
                    }
                }
                """;
            var service = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(source, "semantic.ct")]);
            var tokens = service.GetSemanticTokens("semantic.ct");

            LanguageSemanticToken TokenAt(string text, int occurrence = 0)
            {
                var position = -1;
                for (var index = 0; index <= occurrence; index++)
                    position = source.IndexOf(text, position + 1, StringComparison.Ordinal);
                Assert(position >= 0, $"Semantic-token fixture did not contain '{text}'.");
                return tokens.Single(token => token.Span.Start == position);
            }

            Assert(TokenAt("System").Kind == LanguageSemanticTokenKind.Namespace && TokenAt("System").Modifiers.HasFlag(LanguageSemanticTokenModifiers.DefaultLibrary), "System namespace was not classified as default-library namespace.");
            Assert(TokenAt("Demo").Kind == LanguageSemanticTokenKind.Namespace, "Namespace declaration was not classified.");
            Assert(TokenAt("Mode").Kind == LanguageSemanticTokenKind.Enum && TokenAt("Mode").Modifiers.HasFlag(LanguageSemanticTokenModifiers.Declaration), "Enum declaration was not classified.");
            Assert(TokenAt("Off").Kind == LanguageSemanticTokenKind.EnumMember && TokenAt("Off").Modifiers.HasFlag(LanguageSemanticTokenModifiers.Readonly), "Enum member modifiers were not classified.");
            Assert(TokenAt("Device").Kind == LanguageSemanticTokenKind.Class && TokenAt("Device").Modifiers.HasFlag(LanguageSemanticTokenModifiers.Declaration), "Class declaration was not classified.");
            Assert(TokenAt("Count").Kind == LanguageSemanticTokenKind.Property && TokenAt("Count").Modifiers.HasFlag(LanguageSemanticTokenModifiers.Static | LanguageSemanticTokenModifiers.Readonly), "Static readonly field modifiers were not classified.");
            Assert(TokenAt("Run").Kind == LanguageSemanticTokenKind.Method, "Method declaration was not classified.");
            Assert(TokenAt("parameter").Kind == LanguageSemanticTokenKind.Parameter && TokenAt("parameter", 1).Kind == LanguageSemanticTokenKind.Parameter, "Parameter declaration or reference was not classified.");
            Assert(TokenAt("local").Kind == LanguageSemanticTokenKind.Variable && TokenAt("local").Modifiers.HasFlag(LanguageSemanticTokenModifiers.Readonly), "Readonly local declaration was not classified.");
            Assert(TokenAt("item").Kind == LanguageSemanticTokenKind.Variable && TokenAt("item", 1).Kind == LanguageSemanticTokenKind.Variable, "Foreach variable declaration or reference was not classified.");
            Assert(TokenAt("error").Kind == LanguageSemanticTokenKind.Variable && TokenAt("error", 1).Kind == LanguageSemanticTokenKind.Variable, "Catch variable declaration or reference was not classified.");
            Assert(TokenAt("Value", 1).Kind == LanguageSemanticTokenKind.Property && !TokenAt("Value", 1).Modifiers.HasFlag(LanguageSemanticTokenModifiers.Declaration), "Field reference was not classified separately from its declaration.");
            Assert(TokenAt("Console").Kind == LanguageSemanticTokenKind.Class && TokenAt("Console").Modifiers.HasFlag(LanguageSemanticTokenModifiers.DefaultLibrary), "Standard-library type reference was not classified.");
            Assert(TokenAt("WriteLine").Kind == LanguageSemanticTokenKind.Method && TokenAt("WriteLine").Modifiers.HasFlag(LanguageSemanticTokenModifiers.DefaultLibrary | LanguageSemanticTokenModifiers.Static), "Standard-library method reference was not classified.");
            Assert(tokens.All(token => source.AsSpan(token.Span.Start, token.Span.Length).IndexOfAny('\r', '\n') < 0), "A semantic token crossed a line boundary.");
            Assert(tokens.Zip(tokens.Skip(1)).All(pair => pair.First.Span.End <= pair.Second.Span.Start), "Semantic tokens were not sorted or overlapped.");
            Assert(!tokens.Any(token => token.Span.Start == source.IndexOf("Unknown", StringComparison.Ordinal)), "Unresolved identifier received a semantic token.");

            const string accessSource = "public class Base { protected int State; private int Secret; public static int Count; public void Run(int value) { } public void Run(string value) { } } public class Derived : Base { public void Test() { State = 1; Run(1); Base item = new Base(); item.Secret = 1; item.Count = 1; Base.Count = 1; } }";
            var accessTokens = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(accessSource, "semantic-access.ct")]).GetSemanticTokens("semantic-access.ct");
            int AccessPosition(string text, int occurrence)
            {
                var position = -1;
                for (var index = 0; index <= occurrence; index++)
                    position = accessSource.IndexOf(text, position + 1, StringComparison.Ordinal);
                return position;
            }
            Assert(accessTokens.Any(token => token.Span.Start == AccessPosition("State", 1) && token.Kind == LanguageSemanticTokenKind.Property), "Accessible inherited field was not classified.");
            Assert(accessTokens.Any(token => token.Span.Start == AccessPosition("Run", 2) && token.Kind == LanguageSemanticTokenKind.Method), "Overloaded method reference was not classified.");
            Assert(!accessTokens.Any(token => token.Span.Start == AccessPosition("Secret", 1)), "Inaccessible private member received a semantic token.");
            Assert(!accessTokens.Any(token => token.Span.Start == AccessPosition("Count", 1)), "Static member accessed through an instance received a semantic token.");
            Assert(accessTokens.Any(token => token.Span.Start == AccessPosition("Count", 2) && token.Kind == LanguageSemanticTokenKind.Property && token.Modifiers.HasFlag(LanguageSemanticTokenModifiers.Static)), "Static member accessed through its type was not classified.");

            const string escaped = "public class @class { public int π; public void M() { @class value = new @class(); value.π = 1; } }";
            var escapedTokens = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(escaped, "escaped-semantic.ct")]).GetSemanticTokens("escaped-semantic.ct");
            var escapedDeclaration = escapedTokens.Single(token => token.Span.Start == escaped.IndexOf("@class", StringComparison.Ordinal));
            Assert(escapedDeclaration.Kind == LanguageSemanticTokenKind.Class && escapedDeclaration.Span.Length == "@class".Length, "Escaped declaration did not retain its full source span.");
            Assert(escapedTokens.Count(token => token.Kind == LanguageSemanticTokenKind.Property && escaped.AsSpan(token.Span.Start, token.Span.Length).SequenceEqual("π")) == 2, "Unicode field declaration and reference were not classified.");

            const string targetSource = "using Esp.Idf; public static class Program { [EntryPoint] public static void Main() { Ws2812.Configure(4, 1u); } }";
            var hosted = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(targetSource, "semantic-hosted.ct")]);
            var esp = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(targetSource, "semantic-esp.ct")], new CompilationOptions(CompilationTarget.EspIdf));
            var wsPosition = targetSource.IndexOf("Ws2812", StringComparison.Ordinal);
            Assert(!hosted.GetSemanticTokens("semantic-hosted.ct").Any(token => token.Span.Start == wsPosition), "ESP semantic type leaked into hosted analysis.");
            Assert(esp.GetSemanticTokens("semantic-esp.ct").Any(token => token.Span.Start == wsPosition && token.Kind == LanguageSemanticTokenKind.Class && token.Modifiers.HasFlag(LanguageSemanticTokenModifiers.DefaultLibrary)), "ESP semantic type was not classified.");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var canceled = false;
            try { _ = service.GetSemanticTokens("semantic.ct", cancellation.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Assert(canceled, "Semantic token classification ignored cancellation.");
        });

        suite.Run("language service draft 0.7 types", () =>
        {
            const string source = "public delegate long Transformer(long value); public static class Program { private static long Double(long value) { return value * 2L; } [EntryPoint] public static unsafe void Main() { Transformer managed = Double; delegate* unmanaged<long, long> native = &Double; long result = managed(21L) + native(21L); } }";
            var service = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(source, "draft07-editor.ct")]);
            Assert(!service.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, service.Diagnostics));

            var delegateReference = source.IndexOf("Transformer managed", StringComparison.Ordinal) + 1;
            var delegateHover = service.GetHover("draft07-editor.ct", delegateReference);
            Assert(delegateHover?.Contents.Contains("delegate long Transformer(long value)", StringComparison.Ordinal) == true, "Delegate hover did not expose its signature.");
            var delegateDefinition = service.GetDefinition("draft07-editor.ct", delegateReference);
            Assert(delegateDefinition?.Span.Start == source.IndexOf("Transformer(long", StringComparison.Ordinal), "Delegate reference did not navigate to its declaration.");

            var nativeUse = source.LastIndexOf("native(21L)", StringComparison.Ordinal) + 1;
            var pointerHover = service.GetHover("draft07-editor.ct", nativeUse);
            Assert(pointerHover?.Contents.Contains("delegate* unmanaged<long, long>", StringComparison.Ordinal) == true, "Function-pointer hover did not expose its structural signature.");

            var tokens = service.GetSemanticTokens("draft07-editor.ct");
            var delegateDeclarationStart = source.IndexOf("Transformer(long", StringComparison.Ordinal);
            Assert(tokens.Any(token => token.Span.Start == delegateDeclarationStart && token.Kind == LanguageSemanticTokenKind.Class && token.Modifiers.HasFlag(LanguageSemanticTokenModifiers.Declaration)), "Delegate declaration was not semantically classified.");
            Assert(tokens.Any(token => token.Span.Start == source.IndexOf("Double; delegate", StringComparison.Ordinal) && token.Kind == LanguageSemanticTokenKind.Method), "Delegate method group was not classified as a method.");
        });

        suite.Run("project manifest and CLI", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "ctilde-project-tests", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(Path.Combine(directory, "src", "generated"));
            try
            {
                File.WriteAllText(Path.Combine(directory, "ctilde.json"), "{\"target\":\"hosted\",\"sources\":[\"src/**/*.ct\"],\"exclude\":[\"src/generated/**\"],\"build\":{\"generatedC\":\"out/program.c\",\"generatedHeader\":\"out/exports.h\",\"configuration\":\"release\",\"compiler\":\"auto\",\"executable\":\"out/program.exe\"}}");
                File.WriteAllText(Path.Combine(directory, "src", "Program.ct"), "public static class Program { [EntryPoint] public static void Main() { Console.WriteLine(Math.Sqrt(9.0f)); } }");
                File.WriteAllText(Path.Combine(directory, "src", "Library.ct"), "public class Library { }");
                File.WriteAllText(Path.Combine(directory, "src", "generated", "Ignored.ct"), "public class Ignored { }");
                var project = CTildeProjectFile.Load(Path.Combine(directory, "ctilde.json"));
                Assert(project.SourceFiles.Length == 2, "Project source globs or exclusions were not applied.");
                Assert(project.SourceFiles.SequenceEqual(project.SourceFiles.OrderBy(path => path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)), "Project sources were not deterministic.");
                Assert(CTildeProjectFile.FindNearest(Path.Combine(directory, "src", "Program.ct")) == project.ManifestPath, "Nearest project discovery failed.");
                Assert(project.Configuration.Build.Configuration == CTildeNativeBuildConfiguration.Release, "Project native configuration was not loaded.");
                Assert(project.Configuration.Build.GeneratedCPath == Path.Combine(directory, "out", "program.c"), "Project generated-C path was not resolved.");

                var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
                var cliDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CTilde.Cli", "bin", configuration, "net10.0", "ctilde.dll"));
                var check = RunProcess("dotnet", [cliDll, "--project", project.ManifestPath, "--check"]);
                Assert(check.ExitCode == 0, $"Project CLI check failed: {check.StandardError}");
                var build = RunProcess("dotnet", [cliDll, "--project", project.ManifestPath, "--build"]);
                Assert(build.ExitCode == 0, $"Project native build failed: {build.StandardOutput}{build.StandardError}");
                Assert(File.Exists(Path.Combine(directory, "out", "program.c")) && File.Exists(Path.Combine(directory, "out", "exports.h")) && File.Exists(Path.Combine(directory, "out", "program.exe")), "Project native build outputs were missing.");
                var executable = Path.Combine(directory, "out", "program.exe");
                var configuredCompiler = Environment.GetEnvironmentVariable("CTILDE_CC");
                var builtProgram = configuredCompiler?.StartsWith("wsl:", StringComparison.OrdinalIgnoreCase) == true
                    ? RunProcess("wsl", ["--exec", WslPath(executable)])
                    : RunProcess(executable, []);
                Assert(builtProgram.ExitCode == 0 && Normalize(builtProgram.StandardOutput) == "3\n", $"Project native math executable failed: {builtProgram.StandardOutput}{builtProgram.StandardError}");
                using (var buildLock = new FileStream(Path.Combine(directory, "out", ".ctilde-build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    var overlapping = RunProcess("dotnet", [cliDll, "--project", project.ManifestPath, "--build"]);
                    Assert(overlapping.ExitCode == 1 && overlapping.StandardError.Contains("Another C~ project build", StringComparison.Ordinal), "An overlapping project native build was not rejected.");
                }
                var conflict = RunProcess("dotnet", [cliDll, "--project", project.ManifestPath, "--target", "hosted", "--check"]);
                Assert(conflict.ExitCode == 2, "Project and target were not rejected as conflicting CLI inputs.");
                var incompatible = RunProcess("dotnet", [cliDll, "--project", project.ManifestPath, "--check", "--build"]);
                Assert(incompatible.ExitCode == 2, "Check and native build were not rejected as conflicting CLI modes.");

                File.WriteAllText(Path.Combine(directory, "invalid.json"), "{\"sources\":[\"src/**/*.ct\"],\"build\":{\"generatedC\":\"../outside.c\"}}");
                var invalidRejected = false;
                try { CTildeProjectFile.Load(Path.Combine(directory, "invalid.json")); }
                catch (CTildeProjectException) { invalidRejected = true; }
                Assert(invalidRejected, "A project build output escaping the project directory was accepted.");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });

        suite.Run("ESP-IDF binding manifest model", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "ctilde-binding-manifest-tests", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(Path.Combine(directory, "Bindings", "Generated"));
            try
            {
                File.WriteAllText(Path.Combine(directory, "Program.ct"), "public static class Program { [EntryPoint] public static void Main() { } }");
                File.WriteAllText(Path.Combine(directory, "Bindings", "Generated", "Api.g.ct"), "namespace Test.Bindings; public static class Clock { }");
                File.WriteAllText(Path.Combine(directory, "ctilde.json"), "{\"target\":\"esp-idf\",\"sources\":[\"Program.ct\"],\"espIdf\":{\"bindings\":[\"Bindings/api.bindings.json\"]}}");
                const string manifestText = """
                    {
                      "schemaVersion": 1,
                      "namespace": "Test.Bindings",
                      "declarations": "Bindings/Generated/Api.g.ct",
                      "adapterSource": "Bindings/Generated/Api.g.c",
                      "imports": [{
                        "component": "esp_timer",
                        "header": "esp_timer.h",
                        "container": "Clock",
                        "opaqueTypes": [{ "symbol": "esp_timer_handle_t", "name": "TimerHandle" }],
                        "delegates": [{ "symbol": "timer_cb_t", "name": "TimerCallback", "returnType": "void", "parameters": [] }],
                        "functions": [
                          { "symbol": "esp_timer_get_time", "name": "Now", "returnType": "long", "parameters": [], "noAlloc": true },
                          { "symbol": "esp_timer_get_handle", "name": "GetHandle", "returnType": "TimerHandle", "parameters": [], "returnOwnership": "borrowed", "returnNullable": true }
                        ],
                        "constants": [{ "symbol": "ESP_TIMER_TASK", "name": "TaskDispatch", "type": "int" }],
                        "configAdapters": [{
                          "function": "esp_timer_configure",
                          "struct": "esp_timer_config_t",
                          "structParameter": "config",
                          "name": "Configure",
                          "returnType": "EspError",
                          "initializer": "ESP_TIMER_CONFIG_DEFAULT",
                          "parameters": [{ "name": "handle", "type": "TimerHandle", "nativeNames": ["handle"] }],
                          "fields": [{ "field": "options.name", "name": "name", "type": "NativeUtf8String", "mapping": "fixedUtf8", "maxBytes": 16 }],
                          "noAlloc": true
                        }],
                        "outputAdapters": [{
                          "function": "esp_timer_get_info",
                          "struct": "esp_timer_info_t",
                          "structParameter": "info",
                          "name": "GetInfo",
                          "returnType": "EspError",
                          "parameters": [{ "name": "handle", "type": "TimerHandle", "nativeNames": ["handle"] }],
                          "fields": [{ "field": "stats.invocations", "name": "invocations", "type": "uint" }],
                          "noAlloc": true
                        }]
                      }]
                    }
                    """;
                var manifestPath = Path.Combine(directory, "Bindings", "api.bindings.json");
                File.WriteAllText(manifestPath, manifestText);
                var project = CTildeProjectFile.Load(Path.Combine(directory, "ctilde.json"));
                Assert(project.Configuration.BindingManifests.Length == 1, "ESP-IDF binding manifest was not loaded.");
                var binding = project.Configuration.BindingManifests[0];
                Assert(project.SourceFiles.Contains(binding.DeclarationsPath), "Tracked generated declarations were not added outside the project source glob.");
                Assert(binding.Imports[0].OpaqueTypes.Length == 1 && binding.Imports[0].Delegates.Length == 1, "Opaque and callback selections were not preserved.");
                Assert(binding.Imports[0].Functions[1].ReturnOwnership == "borrowed" && binding.Imports[0].Functions[1].ReturnNullable, "Opaque return ownership metadata was not preserved.");
                Assert(binding.Imports[0].ConfigAdapters[0].Initializer == "ESP_TIMER_CONFIG_DEFAULT", "Configuration initializer metadata was not preserved.");
                Assert(binding.Imports[0].ConfigAdapters[0].Fields[0].Field == "options.name" && binding.Imports[0].ConfigAdapters[0].Fields[0].Mapping == "fixedUtf8" && binding.Imports[0].ConfigAdapters[0].Fields[0].MaxBytes == 16, "Nested fixed UTF-8 configuration metadata was not preserved.");
                Assert(binding.Imports[0].OutputAdapters[0].Fields[0].Field == "stats.invocations", "Output-structure adapter metadata was not preserved.");
                Assert(binding.CanonicalText() == EspIdfBindingManifest.Load("Bindings/api.bindings.json", directory).CanonicalText(), "Binding manifest canonicalization was not deterministic.");
                Assert(binding.ManifestFingerprint.Length == 64, "Binding manifest fingerprint was not a SHA-256 value.");
                const string reservedExtern = "public static class Native { [Extern(\"ct_idf_fake\")] public static int Call(); } public static class Program { [EntryPoint] public static void Main() { } }";
                var userDiagnostics = Compilation.Create([SyntaxTree.ParseText(reservedExtern, "user-binding.ct")], new CompilationOptions(CompilationTarget.EspIdf)).GetDiagnostics();
                Assert(userDiagnostics.Any(diagnostic => diagnostic.Code == "CT4101"), "User source impersonated the reserved generated-binding symbol prefix.");
                var trustedDiagnostics = Compilation.Create([SyntaxTree.ParseEspIdfBinding(SourceText.From(reservedExtern, "generated-binding.ct"))], new CompilationOptions(CompilationTarget.EspIdf)).GetDiagnostics();
                Assert(!trustedDiagnostics.Any(diagnostic => diagnostic.Code == "CT4101"), "A compiler-origin binding declaration was rejected as user impersonation.");

                File.WriteAllText(manifestPath, manifestText.Replace("esp_timer_get_time", "esp_timer_get_time()", StringComparison.Ordinal));
                var rawExpressionRejected = false;
                try { CTildeProjectFile.Load(Path.Combine(directory, "ctilde.json")); }
                catch (CTildeProjectException) { rawExpressionRejected = true; }
                Assert(rawExpressionRejected, "A raw native expression was accepted as a binding symbol.");
                File.WriteAllText(manifestPath, manifestText.Replace("Bindings/Generated/Api.g.c", "../Api.g.c", StringComparison.Ordinal));
                var escapingOutputRejected = false;
                try { CTildeProjectFile.Load(Path.Combine(directory, "ctilde.json")); }
                catch (CTildeProjectException) { escapingOutputRejected = true; }
                Assert(escapingOutputRejected, "A binding output outside the project root was accepted.");
                File.WriteAllText(manifestPath, manifestText.Replace("\"maxBytes\": 16", "\"maxBytes\": 0", StringComparison.Ordinal));
                var invalidFixedUtf8Rejected = false;
                try { CTildeProjectFile.Load(Path.Combine(directory, "ctilde.json")); }
                catch (CTildeProjectException) { invalidFixedUtf8Rejected = true; }
                Assert(invalidFixedUtf8Rejected, "A fixed UTF-8 field with an invalid bound was accepted.");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });

        suite.Run("full fidelity syntax round trip", () =>
        {
            const string valid = "// lead\r\npublic static class Program { /* body */ [EntryPoint] public static void Main() { } }\r\n";
            var validTree = SyntaxTree.ParseText(valid, "valid.ct");
            Assert(validTree.ToFullString() == valid, "Valid syntax did not round-trip exactly.");
            Assert(validTree.Root.ToFullString() == valid, "The compilation-unit node did not round-trip exactly.");
            Assert(validTree.Root.ChildNodesAndTokens().Any(item => item.IsNode) && validTree.Root.ChildNodesAndTokens().Any(item => item.IsToken), "Node/token traversal did not expose both child forms.");
            Assert(validTree.Tokens.Any(token => token.LeadingTrivia.Concat(token.TrailingTrivia).Any(trivia => trivia.Kind == SyntaxTriviaKind.SingleLineComment)), "Single-line comment trivia was not retained.");
            Assert(validTree.Tokens.Any(token => token.LeadingTrivia.Concat(token.TrailingTrivia).Any(trivia => trivia.Kind == SyntaxTriviaKind.BlockComment)), "Block comment trivia was not retained.");
            Assert(validTree.Tokens.Any(token => token.TrailingTrivia.Length > 0), "Trailing trivia was not retained.");

            const string parameterAttribute = "public static class Native { [Extern(\"keep\")] public static void Keep([Retained] object value); }";
            var attributeTree = SyntaxTree.ParseText(parameterAttribute, "parameter-attribute.ct");
            var method = attributeTree.Root.Types.Single().Members.OfType<MethodDeclarationSyntax>().Single();
            Assert(attributeTree.ToFullString() == parameterAttribute, "Parameter attributes did not round-trip exactly.");
            Assert(method.Parameters.Single().Attributes.Single().Name == "Retained", "Parameter attributes were not preserved in the syntax tree.");

            const string invalid = "public static class Program { @ [EntryPoint] public static void Main( { } }";
            var invalidTree = SyntaxTree.ParseText(invalid, "invalid.ct");
            Assert(invalidTree.ToFullString() == invalid, "Invalid syntax did not round-trip exactly.");
            Assert(invalidTree.Tokens.Any(token => token.IsMissing), "Parser recovery did not retain a missing token.");
            Assert(invalidTree.SkippedTokens.Length > 0, "Parser recovery did not retain skipped tokens.");
        });

        suite.Run("conversion and recursive unsafe safety", () =>
        {
            const string source = """
                public class A { }
                public class B { }
                public struct Holder { public unsafe int* Pointer; }
                public static class Program
                {
                    public static int*[] Expose(int*[] value) { return value; }
                    public static Holder Echo(Holder value) { return value; }
                    [EntryPoint]
                    public static void Main()
                    {
                        A a = new A();
                        B b = (B)a;
                        string text = "x";
                        int[] values = (int[])text;
                        int*[] pointers = new int*[1];
                    }
                }
                """;
            var diagnostics = Compile(source).GetDiagnostics();
            Assert(diagnostics.Count(diagnostic => diagnostic.Code == "CT2137") >= 2, "Unrelated reference casts were not rejected.");
            Assert(diagnostics.Count(diagnostic => diagnostic.Code == "CT2141") >= 4, "Pointer-containing public signatures were not recursively unsafe-checked.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2139"), "Pointer-containing local uses were not recursively unsafe-checked.");

            const string valid = "public static class Program { public static unsafe int** Convert(int** value) { return (int**)value; } [EntryPoint] public static void Main() { unsafe { int*[] values = new int*[1]; } } }";
            Assert(!Compile(valid).GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "Valid pointer casts and pointer arrays were rejected in unsafe contexts.");
        });

        suite.Run("integral only operators", () =>
        {
            const string source = "public static class Program { [EntryPoint] public static void Main() { float a = 5.0; float b = a % 2.0; a %= 2.0; float c = ~a; } }";
            var diagnostics = Compile(source).GetDiagnostics();
            Assert(diagnostics.Count(diagnostic => diagnostic.Code == "CT2149") == 2, "float remainder forms were not rejected.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2148"), "Bitwise complement on float was not rejected.");
        });

        suite.Run("C float literal formatting", () =>
        {
            const string source = "using System; public static class Program { [EntryPoint] public static void Main() { float whole = 5.0; float negativeZero = -0.0; float infinity = 1.0 / 0.0; float notANumber = 0.0 / 0.0; Console.WriteLine((0.0 / 0.0) == (0.0 / 0.0)); } }";
            var generated = Emit(source);
            Assert(generated.Contains("5.0f", StringComparison.Ordinal), "An integral-valued float literal was not emitted with a decimal point.");
            Assert(generated.Contains("-0.0f", StringComparison.Ordinal), "Negative zero was not preserved.");
            Assert(generated.Contains("INFINITY", StringComparison.Ordinal), "A folded infinity was not emitted with the C macro.");
            Assert(generated.Contains("NAN", StringComparison.Ordinal), "A folded NaN was not emitted with the C macro.");
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0 && Normalize(result.StandardOutput) == "False\n", "Folded NaN equality did not use IEEE semantics.");
        });

        suite.Run("pairwise overload ambiguity", () =>
        {
            const string source = "public static class Program { private static void Pick(short a, float b) { } private static void Pick(int a, uint b) { } [EntryPoint] public static void Main() { byte a = 1; ushort b = 2; Pick(a, b); } }";
            Assert(Compile(source).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT2123"), "Cross-argument overload preferences were not reported as ambiguous.");
        });

        suite.Run("do and switch control flow", () =>
        {
            const string assigned = "public static class Program { [EntryPoint] public static void Main() { int value; do { value = 1; } while (false); int copy = value; } }";
            Assert(!Compile(assigned).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT3108"), "Assignment through a do body was not preserved.");

            const string broken = "public static class Program { [EntryPoint] public static void Main() { int value; do { if (true) break; value = 1; } while (false); int copy = value; } }";
            Assert(Compile(broken).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT3108"), "An early do break incorrectly assigned a local.");

            const string returning = "public static class Program { private static int Pick(int value) { switch (value) { case 0: return 1; default: return 2; } } [EntryPoint] public static void Main() { } }";
            Assert(!Compile(returning).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT3100"), "A fully returning switch was rejected.");

            const string incomplete = "public static class Program { private static int Pick(int value) { switch (value) { case 0: break; default: return 2; } } [EntryPoint] public static void Main() { } }";
            Assert(Compile(incomplete).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT3100"), "A switch break incorrectly completed a non-void return.");
        });

        suite.Run("switch case conversion", () =>
        {
            const string duplicates = "public static class Program { [EntryPoint] public static void Main() { byte value = 0; switch (value) { case 1: break; case (byte)1: break; case 300: break; default: break; } } }";
            var diagnostics = Compile(duplicates).GetDiagnostics();
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT3109"), "Duplicate converted case labels were not rejected.");
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT2108"), "Out-of-range case label was not rejected.");
        });

        suite.Run("extern ABI validation", () =>
        {
            const string identical = "public static class A { [Extern(\"native_value\")] public static int Get(int value); } public static class B { [Extern(\"native_value\")] public static int Read(int value); [EntryPoint] public static void Main() { } }";
            var emitted = Emit(identical);
            Assert(emitted.Split("extern int32_t native_value", StringSplitOptions.None).Length == 2, "Identical extern aliases did not emit exactly one prototype.");

            const string incompatible = "public static class A { [Extern(\"native_value\")] public static int Get(int value); } public static class B { [Extern(\"native_value\")] public static uint Read(uint value); [EntryPoint] public static void Main() { } }";
            Assert(Compile(incompatible).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4102" && diagnostic.RelatedLocation is not null), "Incompatible extern aliases did not report the earlier declaration.");

            const string reserved = "public static class Program { [Extern(\"main\")] public static int Native(); [EntryPoint] public static void Main() { } }";
            Assert(Compile(reserved).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4101"), "Reserved external main was not rejected.");

            const string runtime = "public static class Program { [Extern(\"ct_alloc\")] public static int Native(); [EntryPoint] public static void Main() { } }";
            Assert(Compile(runtime).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4101"), "A runtime external collision was not rejected.");

            const string generatedBaseline = "public static class Program { private static void Helper() { } [EntryPoint] public static void Main() { Helper(); } }";
            var generatedCompilation = Compile(generatedBaseline);
            using var generatedMap = new StringWriter();
            Assert(generatedCompilation.EmitSymbolMap(generatedMap).Success, "Could not emit the generated-symbol collision map.");
            using var generatedDocument = System.Text.Json.JsonDocument.Parse(generatedMap.ToString());
            var helperName = generatedDocument.RootElement.GetProperty("symbols").EnumerateArray()
                .Single(symbol => symbol.GetProperty("identity").GetString()!.Contains("Program::Helper", StringComparison.Ordinal))
                .GetProperty("name").GetString()!;
            var generated = $"public static class Program {{ private static void Helper() {{ }} [Extern(\"{helperName}\")] public static int Native(); [EntryPoint] public static void Main() {{ Helper(); }} }}";
            Assert(Compile(generated).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4101"), "A generated external collision was not rejected.");

            const string dynamicGenerated = "public static class Program { [Extern(\"ct_new_ct_a_i32\")] public static int Native(); [EntryPoint] public static void Main() { int[] values = new int[1]; } }";
            Assert(Compile(dynamicGenerated).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4101"), "A generated array-allocator collision was not rejected.");

            const string objectGenerated = "public class Value { public virtual int Read() { return 1; } } public static class Program { [Extern(\"ct_v_adf7d9ba8d8122f0c937620e\")] public static int Native(); [EntryPoint] public static void Main() { object value = new Value(); } }";
            Assert(Compile(objectGenerated).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4101"), "A generated object-vtable collision was not rejected.");

            const string boxGenerated = "public static class Program { [Extern(\"ct_box_value_i32\")] public static int Native(); [EntryPoint] public static void Main() { object value = 1; } }";
            Assert(Compile(boxGenerated).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4101"), "A generated boxing-helper collision was not rejected.");

            const string exceptionGenerated = "public static class Program { [Extern(\"ct_eh_0_catch\")] public static int Native(); [EntryPoint] public static void Main() { } }";
            Assert(Compile(exceptionGenerated).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4101"), "An exception-lowering symbol collision was not rejected.");

            const string currentException = "public static class Program { [Extern(\"ct_current_exception\")] public static int Native(); [EntryPoint] public static void Main() { } }";
            Assert(Compile(currentException).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4101"), "The current-exception runtime symbol was accepted as an extern name.");
        });

        suite.Run("target validation precedes output", () =>
        {
            const string source = "public struct Recursive { public Recursive Value; } public static class Program { [EntryPoint] public static void Main() { } }";
            var compilation = Compile(source);
            Assert(compilation.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4100"), "A recursive value layout was not rejected during analysis.");
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            var result = compilation.EmitC(writer);
            Assert(!result.Success && writer.GetStringBuilder().Length == 0, "Target validation wrote partial C output.");
        });

        suite.Run("directory mode output safety", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "ctilde-directory-tests", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "valid.ct"), "public static class Program { [EntryPoint] public static void Main() { } }");
                File.WriteAllText(Path.Combine(directory, "generated.ct"), "public static class Broken {");
                File.WriteAllText(Path.Combine(directory, "generated.c"), "/* Generated by C~ old output. Do not edit. */\nold");
                File.WriteAllText(Path.Combine(directory, "handwritten.ct"), "public static class Broken {");
                File.WriteAllText(Path.Combine(directory, "handwritten.c"), "/* handwritten */\nint value;");
                var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
                var cliDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CTilde.Cli", "bin", configuration, "net10.0", "ctilde.dll"));
                Assert(File.Exists(cliDll), $"CLI test dependency was not found at {cliDll}.");
                var result = RunProcess("dotnet", [cliDll, "--compile-directory", directory]);
                Assert(result.ExitCode == 1, "Directory mode did not report invalid siblings.");
                Assert(File.Exists(Path.Combine(directory, "valid.c")), "A valid sibling did not produce C output.");
                Assert(!File.Exists(Path.Combine(directory, "generated.c")), "Stale generated output was not removed.");
                Assert(File.ReadAllText(Path.Combine(directory, "handwritten.c")) == "/* handwritten */\nint value;", "Handwritten C output was modified.");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });

        suite.Run("semantic diagnostics", () =>
        {
            const string source = """
                using System;
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        int value;
                        Console.WriteLine(value);
                    }
                }
                """;
            var diagnostics = Compile(source).GetDiagnostics();
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT3108"), "Expected a definite-assignment diagnostic.");
        });

        suite.Run("multi-file namespaces and using", () =>
        {
            var library = SyntaxTree.ParseText("namespace Library; public static class Numbers { public static int Add(int left, int right) { return left + right; } }", "library.ct");
            var program = SyntaxTree.ParseText("using System; using Library; namespace Application; public static class Program { [EntryPoint] public static void Main() { Console.WriteLine(Numbers.Add(2, 3)); } }", "program.ct");
            var compilation = Compilation.Create([program, library]);
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            var result = compilation.EmitC(writer);
            Assert(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        });

        suite.Run("access and unsafe diagnostics", () =>
        {
            const string source = """
                public sealed class Box
                {
                    public int Value { get; private set; }
                }
                public static class Program
                {
                    public static int* Expose(int* value) { return value; }
                    [EntryPoint]
                    public static void Main()
                    {
                        Box box = new Box();
                        box.Value = 4;
                    }
                }
                """;
            var diagnostics = Compile(source).GetDiagnostics();
            Assert(diagnostics.Any(diagnostic => diagnostic.Code == "CT1110"), "Expected a private-setter diagnostic.");
            Assert(diagnostics.Count(diagnostic => diagnostic.Code == "CT2141") >= 2, "Expected unsafe pointer-signature diagnostics.");
        });
    }
}
