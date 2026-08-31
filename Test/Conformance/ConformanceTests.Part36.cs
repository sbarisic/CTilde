using CTilde;
using CTilde.Cli;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart36(ConformanceSuite suite)
    {
        suite.Run("project diagnostics identify exact manifest values", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "ctilde-project-diagnostics", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "Program.ct"), "public static class Program {}");
                var manifest = Path.Combine(directory, "ctilde.json");
                File.WriteAllText(manifest, """
                    {
                      "sources": ["*.ct"],
                      "build": {
                        "configuration": "debug",
                        "lto": true
                      }
                    }
                    """);
                try
                {
                    CTildeProjectFile.Load(manifest);
                    Assert(false, "Invalid LTO configuration was accepted.");
                }
                catch (CTildeProjectException exception)
                {
                    Assert(exception.Code == "CT6001", exception.Code);
                    Assert(exception.Location is { Line: 5 }, exception.Location?.ToString() ?? "missing location");
                    var location = exception.Location.GetValueOrDefault();
                    Assert(location.Span.Length == 4, $"Expected true value span, got {location.Span}.");
                }

                File.WriteAllText(manifest, "{\n  \"sources\": [\"*.ct\"],\n  \"target\": \n}");
                try
                {
                    CTildeProjectFile.Load(manifest);
                    Assert(false, "Malformed JSON was accepted.");
                }
                catch (CTildeProjectException exception)
                {
                    Assert(exception.Code == "CT6000", exception.Code);
                    Assert(exception.Location is { Line: 4 }, exception.Location?.ToString() ?? "missing location");
                }
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });

        suite.Run("CLI build reporting receipts and lock waiting", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "ctilde-build-reporting", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "Program.ct"), "public static class Program { [EntryPoint] public static void Main() {} }");
                var manifest = Path.Combine(directory, "ctilde.json");
                File.WriteAllText(manifest, """
                    {
                      "sources": ["*.ct"],
                      "build": {
                        "configuration": "debug",
                        "generatedC": "build/generated/program.c",
                        "generatedHeader": "build/generated/exports.h",
                        "executable": "build/program.exe"
                      }
                    }
                    """);
                var cli = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CTilde.Cli", "bin",
                    AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug",
                    "net10.0", "ctilde.dll"));
                var normal = RunProcess("dotnet", [cli, "--project", manifest, "--check", "--verbosity", "normal"], workingDirectory: directory);
                Assert(normal.ExitCode == 0, normal.StandardOutput + normal.StandardError);
                Assert(normal.StandardOutput.Contains("Sources: 1 C~ file(s)", StringComparison.Ordinal), normal.StandardOutput);
                Assert(normal.StandardOutput.Contains("Build succeeded in ", StringComparison.Ordinal), normal.StandardOutput);
                Assert(System.Text.RegularExpressions.Regex.IsMatch(normal.StandardOutput, @"Build succeeded in \d+\.\d{3}s with 0 warning\(s\) and 0 error\(s\)\."), normal.StandardOutput);
                var quiet = RunProcess("dotnet", [cli, "--project", manifest, "--check", "--verbosity", "quiet"], workingDirectory: directory);
                Assert(quiet.ExitCode == 0 && !quiet.StandardOutput.Contains("Sources:", StringComparison.Ordinal), quiet.StandardOutput);
                var minimal = RunProcess("dotnet", [cli, "--project", manifest, "--check", "--verbosity", "minimal"], workingDirectory: directory);
                Assert(minimal.ExitCode == 0 && !minimal.StandardOutput.Contains("Project:", StringComparison.Ordinal), minimal.StandardOutput);
                var detailed = RunProcess("dotnet", [cli, "--project", manifest, "--check", "--verbosity", "detailed"], workingDirectory: directory);
                Assert(detailed.ExitCode == 0 && detailed.StandardOutput.Contains(Path.Combine(directory, "Program.ct"), StringComparison.Ordinal), detailed.StandardOutput);
                var traced = RunProcess("dotnet", [cli, "--project", manifest, "--check", "--trace", "--verbosity", "quiet"], workingDirectory: directory);
                Assert(traced.ExitCode == 0 && traced.StandardError.Contains("trace: C~ compile phase", StringComparison.Ordinal), traced.StandardError);

                File.WriteAllText(Path.Combine(directory, "Program.ct"), "public static class Program { broken }");
                var failed = RunProcess("dotnet", [cli, "--project", manifest, "--check", "--verbosity", "normal"], workingDirectory: directory);
                Assert(failed.ExitCode == 1 && failed.StandardError.Contains(": error CT", StringComparison.Ordinal), failed.StandardError);
                Assert(System.Text.RegularExpressions.Regex.IsMatch(failed.StandardOutput, @"Build failed in \d+\.\d{3}s with \d+ warning\(s\) and [1-9]\d* error\(s\)\."), failed.StandardOutput);
                var receipt = File.ReadAllText(Path.Combine(directory, ".ctilde", "build-diagnostics.json"));
                Assert(receipt.Contains("\"completionState\": \"failed\"", StringComparison.Ordinal) && receipt.Contains("\"kind\": \"source\"", StringComparison.Ordinal), receipt);

                var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
                var targets = Path.Combine(repositoryRoot, "editors", "visualstudio", "CTilde.VisualStudio", "ProjectSystem", "CTilde.targets");
                var project = Path.Combine(directory, "Diagnostic.ctproj");
                File.WriteAllText(project, $"""
                    <Project ToolsVersion="Current" DefaultTargets="Build">
                      <Import Project="$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props" />
                      <PropertyGroup><CTildeManifest>ctilde.json</CTildeManifest><Configuration>Debug</Configuration><Platform>AnyCPU</Platform><OutputPath>obj\</OutputPath></PropertyGroup>
                      <Import Project="$(MSBuildToolsPath)\Microsoft.Common.targets" />
                      <Import Project="{System.Security.SecurityElement.Escape(targets)}" />
                    </Project>
                    """);
                var msbuild = RunProcess("dotnet", ["msbuild", project, "-t:Build", "-v:minimal", $"-p:CTildeCompilerPath={cli}"], workingDirectory: directory);
                Assert(msbuild.ExitCode != 0, "MSBuild accepted an invalid C~ source.");
                Assert((msbuild.StandardOutput + msbuild.StandardError).Contains(": error CT", StringComparison.Ordinal), msbuild.StandardOutput + msbuild.StandardError);
                Assert(!(msbuild.StandardOutput + msbuild.StandardError).Contains("MSB3073", StringComparison.Ordinal), msbuild.StandardOutput + msbuild.StandardError);

                File.WriteAllText(Path.Combine(directory, "Program.ct"), "public static class Program { [EntryPoint] public static void Main() {} }");
                var buildDirectory = Path.Combine(directory, "build");
                Directory.CreateDirectory(buildDirectory);
                var lockPath = Path.Combine(buildDirectory, ".ctilde-build.lock");
                var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                var owner = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { ProcessId = Environment.ProcessId, Operation = "test", Manifest = manifest, StartedAtUtc = DateTimeOffset.UtcNow });
                held.Write(owner);
                held.Flush();
                var release = Task.Run(async () => { await Task.Delay(350); held.Dispose(); });
                var build = RunProcess("dotnet", [cli, "--project", manifest, "--build"], workingDirectory: directory);
                release.GetAwaiter().GetResult();
                Assert(build.ExitCode == 0, build.StandardOutput + build.StandardError);
                Assert(build.StandardOutput.Contains("Waiting for another C~ operation", StringComparison.Ordinal), build.StandardOutput);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });

        suite.Run("build lock timeout cancellation and stale files", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "ctilde-lock-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, ".ctilde-build.lock");
                using (var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                {
                    var owner = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new BuildLockOwner(42, "Build", "manifest.json", DateTimeOffset.UnixEpoch));
                    held.Write(owner);
                    held.Flush();
                    try
                    {
                        BuildLock.AcquireAsync(directory, "Check", "manifest.json", TimeSpan.FromSeconds(30),
                            new AdvancingTimeProvider(), CancellationToken.None).GetAwaiter().GetResult();
                        Assert(false, "Contended build lock did not time out.");
                    }
                    catch (BuildLockException exception)
                    {
                        Assert(exception.Message.Contains("PID 42", StringComparison.Ordinal), exception.Message);
                    }
                }

                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                try
                {
                    BuildLock.AcquireAsync(directory, "Build", "manifest.json", cancellation.Token).GetAwaiter().GetResult();
                    Assert(false, "Canceled lock acquisition succeeded.");
                }
                catch (OperationCanceledException)
                {
                }

                File.WriteAllText(path, "stale metadata");
                var acquired = BuildLock.AcquireAsync(directory, "Build", "manifest.json", CancellationToken.None).GetAwaiter().GetResult();
                acquired.DisposeAsync().GetAwaiter().GetResult();
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private int reads;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(Interlocked.Increment(ref reads) == 1 ? 0 : 31);
    }
}
