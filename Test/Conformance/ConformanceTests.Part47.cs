using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using CTilde.Cli;
using CTilde.LanguageServer;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart47(ConformanceSuite suite)
    {
        suite.Run("draft 0.51 managed memory section accounting", () =>
        {
            var image = new byte[512];
            new byte[] { 127, 69, 76, 70, 1, 1 }.CopyTo(image, 0);
            void U32(int offset, uint value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), value);
            void U16(int offset, ushort value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset), value);
            U32(32, 52); U16(46, 40); U16(48, 5); U16(50, 1);
            var names = System.Text.Encoding.UTF8.GetBytes("\0.shstrtab\0.text\0.data\0.rodata\0");
            names.CopyTo(image, 300);
            U32(92 + 16, 300); U32(92 + 20, (uint)names.Length);
            U32(132, 11); U32(132 + 8, 6); U32(132 + 20, 101);
            U32(172, 17); U32(172 + 8, 3); U32(172 + 20, 20);
            U32(212, 23); U32(212 + 8, 2); U32(212 + 20, 30); U32(212 + 32, 16);
            var sections = ManagedMemoryReporter.ReadSections(image);
            Assert(sections.Count == 3 && sections[0].Name == ".text" && sections[0].Executable && sections[0].Bytes == 101 &&
                sections[1].Writable && !sections[1].Executable && sections[1].Bytes == 20 &&
                !sections[2].Writable && !sections[2].Executable && sections[2].Bytes == 30,
                "Linked section memory classes were not preserved.");
            var costs = ManagedMemoryReporter.CalculateResidentCosts(sections);
            Assert(costs == new ManagedMemoryReporter.ResidentCosts(101, 20, 30, 15) && costs.Total == 166,
                "Loader allocation sizes must include code rounding and inter-section padding.");
            U32(212 + 32, 3);
            try { _ = ManagedMemoryReporter.CalculateResidentCosts(ManagedMemoryReporter.ReadSections(image)); throw new InvalidOperationException("Invalid alignment accepted."); }
            catch (NativeBuildException) { }
            U32(32, uint.MaxValue);
            try { _ = ManagedMemoryReporter.ReadSections(image); throw new InvalidOperationException("Invalid section table accepted."); }
            catch (NativeBuildException) { }
        });

        suite.Run("draft 0.51 managed memory project limits", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ctilde-memory-limits-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "Program.ct"), "public static class Program { [EntryPoint] public static int Main(string[] args) { return 0; } }");
                var manifest = Path.Combine(root, "ctilde.json");
                var json = """
                    { "target":"esp-idf", "sources":["Program.ct"], "espIdf":{"artifact":"managed-module"},
                      "managedModule":{"kind":"application","name":"tests.memory","version":"1.0.0",
                      "mainTaskStackBytes":4096,"memoryLimits":{"residentRamBytes":1000,"overlayRamBytes":0,"processStackBytes":4096}},
                      "build":{"cLayout":"modules"} }
                    """;
                File.WriteAllText(manifest, json);
                var module = CTildeProjectFile.Load(manifest).Configuration.ManagedModule!;
                Assert(module.MemoryLimits == new ManagedMemoryLimits(1000, 0, 4096), "Memory limits were not loaded.");
                File.WriteAllText(manifest, json.Replace("\"processStackBytes\":4096", "\"processStackBytes\":2048"));
                try { _ = CTildeProjectFile.Load(manifest); throw new InvalidOperationException("Stack budget violation accepted."); }
                catch (CTildeProjectException exception) { Assert(exception.Message.Contains("stack", StringComparison.OrdinalIgnoreCase), "Stack diagnostic missing."); }
            }
            finally { Directory.Delete(root, true); }
        });

        suite.Run("draft 0.50 overlay callable residency", () =>
        {
            var module = new ManagedModuleConfiguration(ManagedModuleKind.Application, "Tests.Residency", "1.0.0", [], 4096, 16384);
            var options = new CompilationOptions(CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                ManagedModuleKind: module.Kind, ManagedModule: module);
            foreach (var source in new[]
            {
                """
                public delegate int Action(int value);
                public static class Program {
                    [Overlay("work")] private static int Work(int value) { return value + 1; }
                    private static Action callback = Work;
                    [EntryPoint] public static int Main(string[] args) { return callback(41); }
                }
                """,
                """
                public delegate int Action(int value);
                public sealed class Worker {
                    [Overlay("work")] private int Work(int value) { return value + 1; }
                    public Action Create() { return Work; }
                }
                public static class Program {
                    [EntryPoint] public static int Main(string[] args) { Worker worker = new Worker(); Action callback = worker.Create(); return callback(41); }
                }
                """,
                """
                public static class Program {
                    [Overlay("work")] private static int Value { get { return 42; } }
                    [Overlay("work")] private static int Work() { return Value; }
                    [EntryPoint] public static int Main(string[] args) { return Work(); }
                }
                """,
                """
                public delegate int Action(int value);
                internal static class Identity<T> { public static T Echo(T value) { return value; } }
                public static class Program {
                    private static Action callback = Identity<int>.Echo;
                    [Overlay("work")] private static Action Work() { return [] value => value + 1; }
                    [EntryPoint] public static int Main(string[] args) { Action next = Work(); return next(callback(41)); }
                }
                """,
                """
                public static class Program {
                    private static int Helper(int value) { return value + 1; }
                    [Overlay("work")] private static int Work(int value) { return Helper(value); }
                    [EntryPoint] public static unsafe int Main(string[] args) {
                        delegate* unmanaged<int, int> pointer = &Helper;
                        return Work(pointer(1));
                    }
                }
                """
            })
            {
                var compilation = Compile(source, options);
                var first = new StringWriter();
                Assert(compilation.EmitC(first).Success, string.Join('\n', compilation.GetDiagnostics()));
                var again = new StringWriter();
                compilation.EmitC(again);
                Assert(first.ToString() == again.ToString(), "Repeated emission changed callable residency.");
                var bound = (BoundProgram)typeof(Compilation).GetField("_boundProgram", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(compilation)!;
                foreach (var method in bound.Model.DelegateTargets)
                    Assert(!method.IsOverlay || method.RequiresOverlayEntry, "A delegate lost its resident entry.");
                foreach (var method in bound.Model.UnmanagedAddressTargets)
                    Assert(!method.IsOverlay, "An unmanaged address target moved into an overlay.");
                var bundle = compilation.EmitCBundle();
                Assert(bundle.Success, "Modular callable emission failed.");
                var modularC = string.Join('\n', bundle.Artifacts.Select(a => a.Content));
                foreach (var method in bound.Model.DelegateTargets.Where(m => m.IsOverlay))
                {
                    var definition = @"\b" + Regex.Escape(method.CName) + @"\([^;{}]*\)\s*\{";
                    Assert(Regex.IsMatch(first.ToString(), definition) && Regex.IsMatch(modularC, definition), "Callable entry has no definition.");
                }
            }
        });

        suite.Run("draft 0.50 workspace membership and syntax reuse", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ctilde workspace " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var manifest = Path.Combine(root, "ctilde.json");
                var main = Path.Combine(root, "Main.ct");
                var added = Path.Combine(root, "Added.ct");
                File.WriteAllText(manifest, "{\"sources\":[\"*.ct\"]}");
                File.WriteAllText(main, "public static class Program { [EntryPoint] public static void Main() {} }");
                var workspace = new WorkspaceState();
                workspace.Initialize(UriHelpers.ToUri(root), null);
                var before = workspace.GetProject(UriHelpers.ToUri(main));
                File.WriteAllText(added, "public class Added {}");
                workspace.FilesChanged([new FileEvent(UriHelpers.ToUri(added), 1)]);
                var after = workspace.GetProject(UriHelpers.ToUri(main));
                Assert(before.SourceFiles.Length == 1 && after.SourceFiles.Length == 2 && !ReferenceEquals(before, after), "New source did not join project.");
                before.LanguageService.TryGetSourceText(main, out var beforeText);
                after.LanguageService.TryGetSourceText(main, out var afterText);
                Assert(ReferenceEquals(beforeText, afterText), "Membership changes reparsed an unchanged source tree.");
                var text = SourceText.FromFile(main);
                var firstTree = workspace.ParseCached(text, false, workspace.Revision);
                Assert(ReferenceEquals(firstTree, workspace.ParseCached(SourceText.From(text.Text, main), false, workspace.Revision + 1)), "Unchanged syntax was reparsed.");
                Assert(!ReferenceEquals(firstTree, workspace.ParseCached(SourceText.From(text.Text + " ", main), false, workspace.Revision + 2)), "Changed syntax was reused.");
                Assert(!ReferenceEquals(firstTree, workspace.ParseCached(text, true, workspace.Revision + 3)), "Binding origin reused an ordinary tree.");
                workspace.Open(new TextDocumentItem(UriHelpers.ToUri(main), "ctilde", 1, text.Text));
                File.Delete(added);
                workspace.FilesChanged([new FileEvent(UriHelpers.ToUri(added), 3)]);
                Assert(workspace.GetProject(UriHelpers.ToUri(main)).SourceFiles.Length == 1, "Deleted source stayed in project.");
                var alternate = Path.Combine(root, "alternate.json");
                File.WriteAllText(alternate, "{\"sources\":[\"*.ct\"]}");
                workspace.SetProjectContexts(new CTildeProjectContextsParams([
                    new CTildeProjectContext(UriHelpers.ToUri(Path.Combine(root, "one.ctproj")), UriHelpers.ToUri(manifest)),
                    new CTildeProjectContext(UriHelpers.ToUri(Path.Combine(root, "two.ctproj")), UriHelpers.ToUri(alternate))
                ], UriHelpers.ToUri(manifest)));
                var current = workspace.GetProject(UriHelpers.ToUri(main));
                var open = workspace.OpenDocuments.Single();
                Assert(workspace.IsCurrent(open, current), "Fresh snapshot was rejected.");
                File.WriteAllText(main, "invalid disk text");
                File.WriteAllText(added, "public class Added {}");
                workspace.FilesChanged([new FileEvent(UriHelpers.ToUri(added), 1)]);
                Assert(!workspace.IsCurrent(open, current), "Obsolete diagnostics could be published after membership changed.");
                var overlapping = workspace.GetWorkspaceProjects();
                Assert(overlapping.Length == 2 && overlapping.All(project => project.SourceFiles.Length == 2), "Overlapping projects did not both refresh.");
                Assert(overlapping.All(project => project.LanguageService.TryGetSourceText(main, out var source) && source.Text == text.Text), "Disk content replaced an open buffer.");
                var renamed = Path.Combine(root, "Renamed.ct");
                File.Move(added, renamed);
                workspace.FilesChanged([new FileEvent(UriHelpers.ToUri(added), 3), new FileEvent(UriHelpers.ToUri(renamed), 1)]);
                Assert(workspace.GetWorkspaceProjects().All(project => project.SourceFiles.Contains(renamed) && !project.SourceFiles.Contains(added)), "Rename left stale membership.");
                foreach (var path in new[] { Path.Combine(root, "a # % café.ct"), main })
                    Assert(UriHelpers.ToPath(UriHelpers.ToUri(path)) == path, "File URI did not round-trip.");
                if (OperatingSystem.IsWindows())
                    Assert(UriHelpers.ToPath("file://server/share/project/Main.ct") == @"\\server\share\project\Main.ct", "UNC server was lost.");
            }
            finally { Directory.Delete(root, true); }
        });

        suite.Run("draft 0.50 subprocess output and cancellation", () =>
        {
            ProcessStartInfo Start(string mode)
            {
                var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                start.ArgumentList.Add("--capture-child"); start.ArgumentList.Add(mode);
                return start;
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = RepositoryModules.CaptureProcessAsync(Start("flood"), deadline.Token).GetAwaiter().GetResult();
            Assert(result.ExitCode == 0 && result.Output.Length > 2_000_000 && result.Error.Length > 2_000_000, "Subprocess output was lost or blocked.");
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            try { RepositoryModules.CaptureProcessAsync(Start("wait"), cancel.Token).GetAwaiter().GetResult(); throw new Exception("Cancellation was ignored."); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        });

        suite.Run("draft 0.50 native dependency parsing", () =>
        {
            var paths = NativeObjectCache.ParseDependencies("ctilde_object: /tmp/source.c /tmp/a\\ b.h \\\n /tmp/hash\\#.h /tmp/dollar$$.h\n");
            Assert(paths.SequenceEqual(new[] { "/tmp/source.c", "/tmp/a b.h", "/tmp/hash#.h", "/tmp/dollar$.h" }), "Escaped dependencies were parsed incorrectly.");
        });

        suite.Run("draft 0.50 native header cache invalidation", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ctilde header cache " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "Program.ct"), "public static class Program { [Extern(\"review_value\")] private static int Value(); [EntryPoint] public static void Main() { System.Console.WriteLine(Value()); } }");
                File.WriteAllText(Path.Combine(root, "native.c"), "#include \"outer.h\"\nint review_value(void) { return REVIEW_VALUE; }\n");
                File.WriteAllText(Path.Combine(root, "outer.h"), "#include \"value with spaces.h\"\n");
                var header = Path.Combine(root, "value with spaces.h");
                var manifest = Path.Combine(root, "ctilde.json");
                foreach (var layout in new[] { "unity", "modules" })
                {
                    File.WriteAllText(header, "#define REVIEW_VALUE 11\n");
                    var compiler = Environment.GetEnvironmentVariable("CTILDE_CC") ?? "auto";
                    File.WriteAllText(manifest, System.Text.Json.JsonSerializer.Serialize(new
                    {
                        target = "hosted", architecture = "x64", sources = new[] { "Program.ct" },
                        hosted = new { nativeSources = new[] { "native.c" } },
                        build = new { cLayout = layout, generatedC = "out/" + layout + "/generated/program.c", generatedDirectory = "out/" + layout + "/generated", generatedHeader = "out/" + layout + "/generated/exports.h", configuration = "release", compiler, executable = "out/" + layout + "/cache.exe" }
                    }));
                    var first = RunNativeProfileCli(root, manifest, "--run", "--trace");
                    Assert(first.ExitCode == 0 && first.StandardOutput.Split(['\r', '\n']).Contains("11"), first.StandardOutput + first.StandardError);
                    var reused = RunNativeProfileCli(root, manifest, "--build", "--trace");
                    Assert(reused.ExitCode == 0 && (reused.StandardOutput + reused.StandardError).Contains("reused native object", StringComparison.Ordinal), "Unchanged objects were not reused.\n" + reused.StandardOutput + reused.StandardError);
                    File.WriteAllText(header, "#define REVIEW_VALUE 22\n");
                    var changed = RunNativeProfileCli(root, manifest, "--run");
                    Assert(changed.ExitCode == 0 && changed.StandardOutput.Split(['\r', '\n']).Contains("22"), changed.StandardOutput + changed.StandardError);
                    File.Delete(header);
                    var missing = RunNativeProfileCli(root, manifest, "--build");
                    Assert(missing.ExitCode != 0, "Deleted header reused a stale object.");
                    Assert(!Directory.EnumerateFiles(Path.Combine(root, "out", layout, ".ctilde-cache"), "pending-*").Any(), "Failed compilation left cache candidates.");
                }
            }
            finally { Directory.Delete(root, true); }
        });

        suite.Run("draft 0.50 native cache dependency matrix", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ctilde dependency matrix " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var source = Path.Combine(root, "native.c");
                var first = Directory.CreateDirectory(Path.Combine(root, "first include")).FullName;
                var second = Directory.CreateDirectory(Path.Combine(root, "second include")).FullName;
                var forced = Path.Combine(root, "forced.h");
                var nested = Path.Combine(root, "nested.h");
                File.WriteAllText(source, "#include <choice.h>\nint value(void) { return CHOICE + FORCED; }\n");
                File.WriteAllText(Path.Combine(second, "choice.h"), "#define CHOICE 11\n");
                File.WriteAllText(forced, "#include \"nested.h\"\n");
                File.WriteAllText(nested, "#define FORCED 1\n");
                string ToolPath(string path) => OperatingSystem.IsWindows() ? WslPath(path) : path;
                var command = NativeToolDiscovery.FindOnPath(OperatingSystem.IsWindows() ? "wsl" : "gcc")!;
                var prefix = OperatingSystem.IsWindows() ? new[] { "--exec", "gcc" } : Array.Empty<string>();
                NativeObjectCache Cache(string working) => new(command, prefix, null, false, OperatingSystem.IsWindows() ? "gcc" : null, working);
                var cache = Cache(root);
                var request = new BuildRequest([], CompilationTarget.Hosted, CompilationArchitecture.X64, null, root, null,
                    null, null, false, false, true, false, CTildeNativeBuildConfiguration.Release, "gcc", Path.Combine(root, "program"),
                    null, null, GeneratedCLayout.Unity, null, null, null, false);
                string[] flags = ["-std=gnu2x", "-I" + ToolPath(first), "-I" + ToolPath(second), "-include", ToolPath(forced)];
                string Key(NativeObjectCache context, string input, IReadOnlyList<string> options)
                {
                    using var entry = context.PrepareAsync(request, input, options, ".o", CancellationToken.None).GetAwaiter().GetResult();
                    return Path.GetFileName(entry.ObjectPath);
                }
                var original = Key(cache, source, flags);
                Assert(!original.StartsWith("uncached-") && original == Key(cache, source, flags), "Valid dependency scans were not reusable.");
                File.WriteAllText(nested, "#define FORCED 2\n");
                var forcedChanged = Key(cache, source, flags);
                Assert(forcedChanged != original, "A transitive forced include did not invalidate the cache.");
                File.WriteAllText(Path.Combine(first, "choice.h"), "#define CHOICE 22\n");
                var priorityChanged = Key(cache, source, flags);
                Assert(priorityChanged != forcedChanged, "A newly preferred include did not invalidate the cache.");
                Assert(Key(cache, source, [.. flags, "-O1"]) != priorityChanged, "Effective flag changes did not invalidate the cache.");
                Assert(Key(Cache(first), source, flags) != priorityChanged, "Working-directory changes did not invalidate the cache.");
                var wrapper = Path.Combine(root, "compiler-wrapper");
                var wrapperText = "#!/bin/sh\n# identity 1\nexec gcc \"$@\"\n";
                File.WriteAllText(wrapper, wrapperText);
                var wrapperToolPath = ToolPath(wrapper);
                var permission = OperatingSystem.IsWindows()
                    ? RunProcess(command, ["--exec", "chmod", "+x", wrapperToolPath])
                    : RunProcess("chmod", ["+x", wrapper]);
                Assert(permission.ExitCode == 0, permission.StandardError);
                NativeObjectCache WrapperCache() => OperatingSystem.IsWindows()
                    ? new(command, ["--exec", wrapperToolPath], null, false, wrapperToolPath, root)
                    : new(wrapper, [], null, false, null, root);
                var compilerKey = Key(WrapperCache(), source, flags);
                File.WriteAllText(wrapper, wrapperText.Replace("identity 1", "identity 2"));
                var replacedCompilerKey = Key(WrapperCache(), source, flags);
                Assert(!compilerKey.StartsWith("uncached-") && !replacedCompilerKey.StartsWith("uncached-") && compilerKey != replacedCompilerKey,
                    "A changed compiler binary with the same version text reused its old cache identity.");
                var assembly = Path.Combine(root, "assembly.S");
                File.WriteAllText(assembly, "#include \"nested.h\"\n.text\n");
                var assemblyKey = Key(cache, assembly, []);
                File.WriteAllText(nested, "#define FORCED 3\n");
                Assert(!assemblyKey.StartsWith("uncached-") && Key(cache, assembly, []) != assemblyKey, "Preprocessed assembly dependencies were ignored.");
                File.Delete(nested);
                Assert(Key(cache, source, flags).StartsWith("uncached-"), "An unusable dependency scan reused an object.");
            }
            finally
            {
                for (var attempt = 0; ; attempt++)
                {
                    try { Directory.Delete(root, true); break; }
                    catch (IOException) when (attempt < 20) { Thread.Sleep(100); }
                }
            }
        });

        suite.Run("draft 0.50 streaming and stable sorting runtime", () =>
        {
            var result = CompileAndRun("""
                using System;
                using System.IO;
                using System.Text;
                using System.Collections;
                public sealed class FragmentStream : Stream {
                    private byte[] data; private int position; public bool Closed; public bool StopAfterLine;
                    public FragmentStream(byte[] data) { this.data = data; }
                    public override bool CanRead { get { return true; } }
                    public override bool CanWrite { get { return false; } }
                    public override bool CanSeek { get { return false; } }
                    public override long Length { get { throw new InvalidOperationException(); } }
                    public override long Position { get { return position; } set { throw new InvalidOperationException(); } }
                    public override int Read(byte[] buffer, int offset, int count) {
                        if (StopAfterLine && position >= 2) throw new InvalidOperationException();
                        if (position == data.Length) return 0;
                        buffer[offset] = data[position]; position++; return 1;
                    }
                    public override void Write(byte[] b, int o, int c) { throw new InvalidOperationException(); }
                    public override long Seek(long o, SeekOrigin origin) { throw new InvalidOperationException(); }
                    public override void SetLength(long value) { throw new InvalidOperationException(); }
                    public override void Flush() { }
                    public override void Dispose() { Closed = true; }
                }
                public sealed class Item { public int Key; public int Index; public Item(int key, int index) { Key = key; Index = index; } }
                public static class Program {
                    private static int comparisons;
                    private static int Compare(Item a, Item b) { comparisons++; return a.Key - b.Key; }
                    private static int Fail(Item a, Item b) { throw new InvalidOperationException(); }
                    [EntryPoint] public static void Main() {
                        FragmentStream live = new FragmentStream(Encoding.UTF8.GetBytes("a\n")); live.StopAfterLine = true;
                        StreamReader first = new StreamReader(live, Encoding.UTF8, true);
                        Console.WriteLine(first.ReadLine() == "a"); first.Dispose(); Console.WriteLine(!live.Closed);
                        live.StopAfterLine = false;
                        FragmentStream fragmented = new FragmentStream(Encoding.UTF8.GetBytes("﻿café 🐟\r\ntail"));
                        StreamReader reader = new StreamReader(fragmented);
                        Console.WriteLine(reader.ReadLine() == "café 🐟" && reader.ReadToEnd() == "tail" && reader.EndOfStream);
                        reader.Dispose(); Console.WriteLine(fragmented.Closed);
                        try { reader.ReadLine(); } catch (ObjectDisposedException) { Console.WriteLine("disposed"); }
                        byte[] invalid = new byte[3]; invalid[0] = (byte)97; invalid[1] = (byte)10; invalid[2] = (byte)255;
                        StreamReader bad = new StreamReader(new FragmentStream(invalid));
                        Console.WriteLine(bad.ReadLine() == "a");
                        try { bad.ReadToEnd(); } catch (Exception) { Console.WriteLine("invalid"); }
                        bad.Dispose();
                        StringBuilder longLine = new StringBuilder(); for (int i = 0; i < 5000; i++) longLine.Append('x');
                        StreamReader longReader = new StreamReader(new FragmentStream(Encoding.UTF8.GetBytes(longLine.ToString())));
                        Console.WriteLine(longReader.ReadLine().Length == 5000 && longReader.ReadLine() == null); longReader.Dispose();
                        Item[] values = new Item[257]; for (int i = 0; i < values.Length; i++) values[i] = new Item((256 - i) / 3, i);
                        Item[] sorted = ArrayAlgorithms.Sorted<Item>(values, Compare);
                        bool valid = sorted.Length == values.Length && comparisons < 6000;
                        for (int i = 1; i < sorted.Length; i++) valid = valid && sorted[i - 1].Key <= sorted[i].Key && (sorted[i - 1].Key != sorted[i].Key || sorted[i - 1].Index < sorted[i].Index);
                        for (int i = 0; i < values.Length; i++) valid = valid && values[i].Index == i;
                        Console.WriteLine(valid);
                        try { ArrayAlgorithms.Sorted<Item>(values, Fail); } catch (InvalidOperationException) { Console.WriteLine("comparer"); }
                    }
                }
                """, memoryDiagnostics: true);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(result.StandardOutput.Replace("\r", "").Trim() == "True\nTrue\nTrue\nTrue\ndisposed\nTrue\ninvalid\nTrue\nTrue\ncomparer", result.StandardOutput + result.StandardError);
        });
    }
}
