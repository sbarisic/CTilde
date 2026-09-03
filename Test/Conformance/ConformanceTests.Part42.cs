using System.Text.Json;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart42(ConformanceSuite suite)
    {
        suite.Run("draft 0.46 managed-module native source identity", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "ctilde-managed-native-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(directory, "main"));
            try
            {
                const string program = "public class NativeApi { public int Read() { return 1; } }";
                File.WriteAllText(Path.Combine(directory, "Program.ct"), program);
                File.WriteAllText(Path.Combine(directory, "main", "native.h"), "#define NATIVE_VALUE 1\n");
                File.WriteAllText(Path.Combine(directory, "main", "native.c"), "#include \"native.h\"\nint native_value(void) { return NATIVE_VALUE; }\n");
                File.WriteAllText(Path.Combine(directory, "ctilde.json"), Manifest("main/native.c"));

                var firstProject = CTildeProjectFile.Load(Path.Combine(directory, "ctilde.json"));
                Assert(firstProject.Configuration.ManagedModule is { NativeSources.Length: 1 } &&
                    firstProject.Configuration.ManagedModule.NativeSources[0].EndsWith("native.c", StringComparison.Ordinal),
                    "Managed-module native sources were not preserved.");
                var sourceFragment = CEmitter.BuildCMakeFragment(["ctilde_entry.c"], firstProject.Configuration.ManagedModule);
                Assert(sourceFragment.Contains("\"${COMPONENT_DIR}/native.c\"", StringComparison.Ordinal),
                    "Managed-module native sources were not added to the generated ESP-IDF source list.");
                var first = Metadata(program, firstProject.Configuration.ManagedModule!);

                File.WriteAllText(Path.Combine(directory, "main", "native.h"), "#define NATIVE_VALUE 2\n");
                var headerProject = CTildeProjectFile.Load(Path.Combine(directory, "ctilde.json"));
                var headerChanged = Metadata(program, headerProject.Configuration.ManagedModule!);
                Assert(first.BuildIdentity != headerChanged.BuildIdentity && first.ApiHash == headerChanged.ApiHash,
                    $"A project-local native header change did not affect only the managed build identity: {first.BuildIdentity} -> {headerChanged.BuildIdentity}, API {first.ApiHash} -> {headerChanged.ApiHash}.");

                File.WriteAllText(Path.Combine(directory, "main", "native.c"), "#include \"native.h\"\nint native_value(void) { return NATIVE_VALUE + 1; }\n");
                var sourceProject = CTildeProjectFile.Load(Path.Combine(directory, "ctilde.json"));
                var sourceChanged = Metadata(program, sourceProject.Configuration.ManagedModule!);
                Assert(headerChanged.BuildIdentity != sourceChanged.BuildIdentity &&
                    headerChanged.ApiHash == sourceChanged.ApiHash,
                    "A native C change did not affect only the managed build identity.");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });

        suite.Run("draft 0.46 managed-module native source validation", () =>
        {
            ExpectRejected("missing", "main/missing.c", createSource: false, generated: false, undeclared: false);
            ExpectRejected("outside-main", "native.c", createSource: true, generated: false, undeclared: false);
            ExpectRejected("generated", "main/generated/native.c", createSource: true, generated: true, undeclared: false);
            ExpectRejected("undeclared", null, createSource: true, generated: false, undeclared: true);
            ExpectDuplicateRejected();
        });
    }

    private static void ExpectDuplicateRejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ctilde-managed-native-invalid-duplicate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "main"));
        try
        {
            File.WriteAllText(Path.Combine(directory, "Program.ct"),
                "public static class Program { [EntryPoint] public static int Main(string[] args) { return 0; } }");
            File.WriteAllText(Path.Combine(directory, "main", "native.c"), "int native_value(void) { return 1; }\n");
            File.WriteAllText(Path.Combine(directory, "ctilde.json"), Manifest("main/native.c")
                .Replace("[\"main/native.c\"]", "[\"main/native.c\", \"main/native.c\"]", StringComparison.Ordinal));
            try
            {
                _ = CTildeProjectFile.Load(Path.Combine(directory, "ctilde.json"));
                throw new InvalidOperationException("Duplicate managed native source was accepted.");
            }
            catch (CTildeProjectException exception)
            {
                Assert(exception.Code == "CT6001", $"Duplicate managed native source reported {exception.Code}.");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ManagedModuleMetadata Metadata(string source, ManagedModuleConfiguration configuration)
    {
        var options = new CompilationOptions(
            CompilationTarget.EspIdf,
            Architecture: CompilationArchitecture.Xtensa,
            ManagedModuleKind: configuration.Kind,
            ManagedModule: configuration);
        var compilation = Compilation.CreateStandardLibrary([SyntaxTree.ParseText(source)], options);
        using var writer = new StringWriter();
        var result = compilation.EmitManagedModuleMetadata(writer, configuration);
        Assert(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return JsonSerializer.Deserialize<ManagedModuleMetadata>(writer.ToString(), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ??
            throw new InvalidOperationException("Managed metadata could not be read.");
    }

    private static void ExpectRejected(
        string name,
        string? declaredSource,
        bool createSource,
        bool generated,
        bool undeclared)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ctilde-managed-native-invalid-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "main"));
        try
        {
            File.WriteAllText(Path.Combine(directory, "Program.ct"),
                "public static class Program { [EntryPoint] public static int Main(string[] args) { return 0; } }");
            var source = undeclared ? "main/native.c" : declaredSource!;
            if (createSource)
            {
                var sourcePath = Path.Combine(directory, source.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
                File.WriteAllText(sourcePath, "int native_value(void) { return 1; }\n");
            }
            File.WriteAllText(Path.Combine(directory, "ctilde.json"), Manifest(undeclared ? null : declaredSource));
            try
            {
                _ = CTildeProjectFile.Load(Path.Combine(directory, "ctilde.json"));
                throw new InvalidOperationException($"Invalid managed native-source case '{name}' was accepted.");
            }
            catch (CTildeProjectException exception)
            {
                Assert(exception.Code == "CT6202" || name == "missing",
                    $"Invalid managed native-source case '{name}' reported {exception.Code}.");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Manifest(string? nativeSource)
    {
        var native = nativeSource is null ? string.Empty : $", \"nativeSources\": [\"{nativeSource}\"]";
        return $$"""
            {
              "target": "esp-idf",
              "architecture": "xtensa",
              "sources": ["**/*.ct"],
              "espIdf": { "artifact": "managed-module" },
              "managedModule": {
                "kind": "application",
                "name": "Demo.Native",
                "version": "1.0.0"{{native}}
              },
              "build": {
                "cLayout": "modules",
                "generatedDirectory": "main/generated",
                "espIdfProjectDirectory": "."
              }
            }
            """;
    }
}
