using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart37(ConformanceSuite suite)
    {
        suite.Run("draft 0.42 invariant scalar and enum parsing", () =>
        {
            const string source = """
                using System;
                using System.Globalization;

                public enum Access : ushort { None=0, Read=1, Write=2, Alias=2, All=3 }

                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        int integer; uint unsigned; float single; Access access;
                        Console.WriteLine(bool.Parse(" true ") && !bool.Parse("FALSE"));
                        Console.WriteLine(int.Parse(" -2147483648 ") == -2147483648 &&
                            int.Parse("FFFFFFFF", NumberStyles.HexNumber) == -1);
                        Console.WriteLine(sbyte.Parse("80", NumberStyles.HexNumber) == -128 &&
                            ushort.Parse("FFFF", NumberStyles.HexNumber) == 65535);
                        Console.WriteLine(!uint.TryParse("4294967296", out unsigned) && unsigned == 0);
                        Console.WriteLine(float.TryParse("1.40129846e-45", out single) && single > 0.0f &&
                            double.Parse("-Infinity") < 0.0d && String.Format("{0:G}", double.Parse("-0")) == "-0");
                        double tooLarge; Console.WriteLine(!double.TryParse("1e9999", out tooLarge) && tooLarge == 0.0d && double.Parse("NaN") != double.Parse("NaN"));
                        Console.WriteLine(Enum.TryParse<Access>("Read, Write", out access) && access == Access.All);
                        Console.WriteLine(Enum.TryParse<Access>("write", true, out access) && access == Access.Write);
                        Console.WriteLine(Enum.TryParse<Access>("2", out access) && access == Access.Write);
                        Console.WriteLine(!Enum.TryParse<Access>("65536", out access));
                        Console.WriteLine(access == Access.None);
                        Console.WriteLine(Convert.ToInt32("42") == 42 && Convert.ToDouble("6.25") == 6.25d);
                        try { int.Parse(null); } catch (ArgumentNullException) { Console.WriteLine("null"); }
                        try { int.Parse("x"); } catch (FormatException) { Console.WriteLine("format"); }
                        try { int.Parse("2147483648"); } catch (OverflowException) { Console.WriteLine("overflow"); }
                        try { int.Parse("1", (NumberStyles)64); } catch (ArgumentException) { Console.WriteLine("style"); }
                        Console.WriteLine(!int.TryParse("x", out integer) && integer == 0);
                    }
                }
                """;
            var result = CompileAndRun(source, memoryDiagnostics: true);
            Assert(result.ExitCode == 0, result.StandardOutput + result.StandardError);
            AssertOutputLines(result.StandardOutput, "True", "True", "True", "True", "True", "True", "True", "True", "True", "True", "True", "True",
                "null", "format", "overflow", "style", "True");

            var bundle = Compile(source).EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var internalHeader = bundle.Artifacts.Single(artifact => artifact.Kind == GeneratedCArtifactKind.InternalHeader).Content;
            var runtimeSource = bundle.Artifacts.Single(artifact => artifact.Kind == GeneratedCArtifactKind.RuntimeSource).Content;
            Assert(!internalHeader.Contains("CTILDE_INTERNAL_HEADER_SKIP_BEGIN", StringComparison.Ordinal) &&
                runtimeSource.Contains("CTILDE_INTERNAL_HEADER_SKIP_BEGIN", StringComparison.Ordinal) &&
                runtimeSource.Contains("enum Status s2d_n(", StringComparison.Ordinal) &&
                runtimeSource.Contains("static inline int32_t log2pow5(", StringComparison.Ordinal),
                "The modular internal header retained Ryu parser implementation details.");
        });

        suite.Run("draft 0.42 hosted streams UTF-8 and filesystem", () =>
        {
            const string source = """
                using System;
                using System.IO;
                using System.Text;

                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        string root = "ctilde-draft-042-io";
                        if (Directory.Exists(root)) Directory.Delete(root, true);
                        Directory.CreateDirectory(Path.Combine(root, "nested"));
                        string first = Path.Combine(root, "café.txt");
                        FileStream stream = new FileStream(first, FileMode.CreateNew, FileAccess.ReadWrite);
                        StreamWriter writer = new StreamWriter(stream, Encoding.UTF8WithBom, true);
                        writer.WriteLine("café 🐟"); writer.Write("tail"); writer.Dispose();
                        Console.WriteLine(stream.Length > 3 && stream.Position == stream.Length);
                        stream.Position = 0;
                        StreamReader reader = new StreamReader(stream, Encoding.UTF8, true);
                        Console.WriteLine(reader.ReadLine() == "café 🐟" && reader.ReadToEnd() == "tail" && reader.EndOfStream);
                        reader.Dispose(); stream.SetLength(3); stream.Flush(); Console.WriteLine(stream.Length == 3); stream.Dispose(); stream.Dispose();
                        try { stream.ReadByte(); } catch (ObjectDisposedException) { Console.WriteLine("disposed"); }

                        string second = Path.Combine(root, "second.txt");
                        File.WriteAllText(second, "a\0b"); File.AppendAllText(second, "\nlast");
                        string text = File.ReadAllText(second);
                        FileMetadata metadata = File.GetMetadata(second);
                        Console.WriteLine(text.Length == 8 && text[1] == '\0' && metadata.Kind == FileSystemEntryKind.File && metadata.Length == 8);
                        string[] entries = Directory.GetFileSystemEntries(root);
                        Console.WriteLine(entries.Length == 3 && String.CompareOrdinal(entries[0], entries[1]) < 0 && String.CompareOrdinal(entries[1], entries[2]) < 0);
                        StringBuilder large = new StringBuilder(5000); for (int index = 0; index < 5000; index++) large.Append('x');
                        string largePath = Path.Combine(root, "large.txt"); File.WriteAllText(largePath, large.ToString());
                        Console.WriteLine(File.ReadAllText(largePath).Length == 5000);
                        File.Copy(second, Path.Combine(root, "copy.txt"), false);
                        File.Move(Path.Combine(root, "copy.txt"), Path.Combine(root, "moved.txt"), false);
                        Console.WriteLine(File.Exists(Path.Combine(root, "moved.txt")) && Directory.Exists(Path.Combine(root, "nested")));

                        string invalid = Path.Combine(root, "invalid.txt"); byte[] bytes = new byte[2]; bytes[0] = (byte)0xc3; bytes[1] = (byte)0x28; File.WriteAllBytes(invalid, bytes);
                        try { File.ReadAllText(invalid); } catch (DecoderFallbackException) { Console.WriteLine("invalid-utf8"); }
                        string missingPath = Path.Combine(root, "missing.txt");
                        try { FileStream missing = File.OpenRead(missingPath); missing.Dispose(); }
                        catch (IOException error) { Console.WriteLine(error.Operation == "File.Open" && error.Path == missingPath && error.ErrorCode != 0); }
                        Directory.Delete(root, true); Console.WriteLine(!Directory.Exists(root));
                    }
                }
                """;
            var result = CompileAndRun(source, memoryDiagnostics: true, standardInput: "");
            Assert(result.ExitCode == 0, result.StandardOutput + result.StandardError);
            AssertOutputLines(result.StandardOutput, "True", "True", "True", "disposed", "True", "True", "True", "True", "invalid-utf8", "True", "True");
        });

        suite.Run("draft 0.42 primitive surfaces profiles and pruning", () =>
        {
            const string completionSource = "using System; using System.Globalization; public static class P { public static void M() { int.; System.Double.; Enum. } }";
            var languageService = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(completionSource, "parsing-completion.ct")]);
            var integerPosition = completionSource.IndexOf("int.", StringComparison.Ordinal) + "int.".Length;
            var doublePosition = completionSource.IndexOf("System.Double.", StringComparison.Ordinal) + "System.Double.".Length;
            var enumPosition = completionSource.IndexOf("Enum.", StringComparison.Ordinal) + "Enum.".Length;
            Assert(languageService.GetCompletions("parsing-completion.ct", integerPosition).Any(item => item.Label == "TryParse") &&
                languageService.GetCompletions("parsing-completion.ct", doublePosition).Any(item => item.Label == "Parse") &&
                languageService.GetCompletions("parsing-completion.ct", enumPosition).Any(item => item.Label == "TryParse"),
                "Primitive or enum parsing completion was unavailable.");
            var keywordPosition = completionSource.IndexOf("int.", StringComparison.Ordinal) + 1;
            Assert(languageService.GetDefinition("parsing-completion.ct", keywordPosition)?.FilePath == "stdlib/System/Parsing.ct",
                "The int keyword did not navigate to its compiler-backed System.Int32 surface.");

            const string applicationSource = """
                using System;
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        int value; bool ok = System.Int32.TryParse("42", out value);
                        double real = System.Double.Parse("0.5");
                    }
                }
                """;
            const string librarySource = """
                using System;
                public static class Program
                {
                    public static void ParseValues()
                    {
                        int value; bool ok = System.Int32.TryParse("42", out value);
                        double real = System.Double.Parse("0.5");
                    }
                }
                """;
            foreach (var target in new[] { CompilationTarget.Hosted, CompilationTarget.Cosmopolitan, CompilationTarget.EspIdf, CompilationTarget.Freestanding })
            {
                var options = new CompilationOptions(target, Architecture: target == CompilationTarget.EspIdf
                    ? CompilationArchitecture.Xtensa : CompilationArchitecture.X64);
                var diagnostics = Compile(target == CompilationTarget.Freestanding ? librarySource : applicationSource, options).GetDiagnostics();
                Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                    $"Draft 0.42 parsing was unavailable for {target}.\n" + string.Join(Environment.NewLine, diagnostics));
            }

            var emitted = Emit("public static class Program { [EntryPoint] public static void Main() { int value = 1; } }");
            Assert(!emitted.Contains("ct_parse_", StringComparison.Ordinal) &&
                !emitted.Contains("ct_host_directory_", StringComparison.Ordinal) &&
                !emitted.Contains("s2d_n", StringComparison.Ordinal),
                "Unused parsing or filesystem support entered generated output.");

            const string invalidSurface = "namespace System; public static class Int32 { public static int Field; } public static class Program { [EntryPoint] public static void Main() {} }";
            Assert(Compile(invalidSurface).GetDiagnostics().Any(diagnostic => diagnostic.Code is "CT1321" or "CT1100"),
                "A compiler-backed primitive surface accepted storage.");

            const string freestandingIo = "using System.IO; public static class Program { [Export(\"probe\")] public static bool Probe() { return File.Exists(\"x\"); } }";
            var ioDiagnostics = Compile(freestandingIo, new CompilationOptions(CompilationTarget.Freestanding,
                Architecture: CompilationArchitecture.X64)).GetDiagnostics();
            Assert(ioDiagnostics.Any(diagnostic => diagnostic.Code == "CT4114"),
                "Reachable freestanding filesystem use did not require its provider group.");
        });
    }
}
