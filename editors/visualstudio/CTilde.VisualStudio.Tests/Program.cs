using CTilde.VisualStudio.Core;

var failures = new List<string>();
Run("ctproj creation and validation", () =>
{
    WithProject(root =>
    {
        var manifest = Path.Combine(root, "ctilde.json");
        File.WriteAllText(manifest, "{}");
        var project = Path.Combine(root, "Sample.ctproj");
        var guid = Guid.NewGuid();
        CTildeProjectContract.Create(project, manifest, guid);
        var loaded = CTildeProjectContract.Load(project);
        Equal(guid, loaded.ProjectGuid);
        Equal(manifest, loaded.ManifestPath);
        var projectText = File.ReadAllText(project);
        True(projectText.Contains("Microsoft.Common.props", StringComparison.Ordinal));
        True(projectText.Contains("Microsoft.Common.targets", StringComparison.Ordinal));
        True(projectText.Contains("<DefineCommonItemSchemas>true</DefineCommonItemSchemas>", StringComparison.Ordinal));
        True(projectText.Contains("<ProjectCapability Include=\"CTilde\" />", StringComparison.Ordinal));
        Throws<IOException>(() => CTildeProjectContract.Create(project, manifest, Guid.NewGuid()));
    });
});
Run("ctproj defaults and rejects escapes", () =>
{
    WithProject(root =>
    {
        File.WriteAllText(Path.Combine(root, "ctilde.json"), "{}");
        var project = Path.Combine(root, "Default.ctproj");
        File.WriteAllText(project, $"<Project><PropertyGroup><ProjectGuid>{{{Guid.NewGuid()}}}</ProjectGuid></PropertyGroup></Project>");
        Equal(Path.Combine(root, "ctilde.json"), CTildeProjectContract.Load(project).ManifestPath);
        File.WriteAllText(project, $"<Project><PropertyGroup><ProjectGuid>{{{Guid.NewGuid()}}}</ProjectGuid><CTildeManifest>../ctilde.json</CTildeManifest></PropertyGroup></Project>");
        Throws<InvalidDataException>(() => CTildeProjectContract.Load(project));
    });
});
Run("filesystem hierarchy exclusions", () =>
{
    WithProject(root =>
    {
        Write(Path.Combine(root, "Program.ct"));
        Write(Path.Combine(root, "ctilde.json"));
        Write(Path.Combine(root, "Bindings", "api.bindings.json"));
        Write(Path.Combine(root, "native", "driver.c"));
        Write(Path.Combine(root, "build", "generated.c"));
        Write(Path.Combine(root, ".git", "hidden.ct"));
        Write(Path.Combine(root, "notes.md"));
        var files = ProjectFiles.Enumerate(root);
        True(files.Any(path => path.EndsWith("Program.ct", StringComparison.Ordinal)));
        True(files.Any(path => path.EndsWith("api.bindings.json", StringComparison.Ordinal)));
        True(files.Any(path => path.EndsWith("driver.c", StringComparison.Ordinal)));
        True(!files.Any(path => path.Contains($"{Path.DirectorySeparatorChar}build{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)));
        True(!files.Any(path => path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)));
        True(!files.Any(path => path.EndsWith("notes.md", StringComparison.Ordinal)));
    });
});
Run("CLI arguments and Windows quoting", () =>
{
    var arguments = CommandContracts.Arguments(CTildeCommandKind.Build, @"C:\Program Files\C~\ctilde.dll", @"C:\Source Folder\ctilde.json");
    Equal("--build", arguments[^1]);
    Equal("\"C:\\Program Files\\C~\\ctilde.dll\" --project \"C:\\Source Folder\\ctilde.json\" --build", CommandContracts.JoinWindowsArguments(arguments));
    Equal("\"a\\\\\\\"b\"", CommandContracts.QuoteWindowsArgument("a\\\"b"));
});
Run("diagnostic parsing and non-diagnostics", () =>
{
    True(DiagnosticParser.TryParse(@"C:\work\Program.ct(12,7): error CT1234: Broken value", out var diagnostic));
    Equal(@"C:\work\Program.ct", diagnostic!.File);
    Equal(12, diagnostic.Line);
    Equal("CT1234", diagnostic.Code);
    True(!DiagnosticParser.TryParse("native compiler output", out _));
});
Run("command enablement", () =>
{
    True(CommandEnablement.ProjectCommandsEnabled(true, false));
    True(!CommandEnablement.ProjectCommandsEnabled(false, false));
    True(!CommandEnablement.ProjectCommandsEnabled(true, true));
    True(CommandEnablement.RunProjectEnabled(true, false, true));
    True(!CommandEnablement.RunProjectEnabled(true, false, false));
    True(CommandEnablement.RestartLanguageServerEnabled(true));
    True(RunSupport.IsSupported(null, null, false));
    True(RunSupport.IsSupported("application", "hosted", false));
    True(RunSupport.IsSupported("application", "cosmopolitan", false));
    True(RunSupport.IsSupported("application", "freestanding", true));
    True(!RunSupport.IsSupported("application", "freestanding", false));
    True(!RunSupport.IsSupported("standard-library", "hosted", true));
});
Run("debug compiler precedence and preparation", () =>
{
    WithProject(root =>
    {
        var manifest = Path.Combine(root, "ctilde.json");
        File.WriteAllText(manifest, "{\"target\":\"hosted\",\"sources\":[\"Program.ct\"],\"build\":{\"compiler\":\"gcc\"}}");
        var preparation = DebugLaunchContracts.CreatePreparation(Path.Combine(root, "ctilde.dll"), manifest,
            string.Empty, "clang", CTildeDebugMemoryMode.Objects);
        Equal("gcc", preparation.Compiler);
        Equal("hosted", preparation.Target);
        True(preparation.Arguments.Contains("--prepare-debug"));
        True(preparation.Arguments.Contains("--compiler"));
        True(preparation.Arguments.Contains("objects"));
        Equal("clang", DebugLaunchContracts.ResolveCompiler("clang", "gcc", null));
        Equal("gcc", DebugLaunchContracts.ResolveCompiler(null, "gcc", "clang"));
        Equal("wsl:gcc", DebugLaunchContracts.ResolveCompiler(null, "auto", "wsl:gcc"));
        Throws<InvalidOperationException>(() => DebugLaunchContracts.ResolveCompiler(null, "auto", null));
        Throws<InvalidOperationException>(() => DebugLaunchContracts.ValidateGdbCompiler("cl.exe"));
        Throws<InvalidOperationException>(() => DebugLaunchContracts.ValidateGdbCompiler("clang-cl.exe"));
    });
});
Run("QEMU debug preparation and manifest isolation", () =>
{
    WithProject(root =>
    {
        var esp32 = Path.Combine(root, "ctilde.esp32_qemu.json");
        var esp32c3 = Path.Combine(root, "ctilde.esp32c3_qemu.json");
        File.WriteAllText(esp32, "{\"target\":\"esp32_qemu\",\"sources\":[\"Program.ct\"]}");
        File.WriteAllText(esp32c3, "{\"target\":\"esp32c3_qemu\",\"sources\":[\"Program.ct\"]}");
        var first = DebugLaunchContracts.CreatePreparation(Path.Combine(root, "ctilde.dll"), esp32,
            "gcc", "clang", CTildeDebugMemoryMode.Objects, Path.Combine(root, "idf"), Path.Combine(root, "esp-clang.exe"));
        var second = DebugLaunchContracts.CreatePreparation(Path.Combine(root, "ctilde.dll"), esp32c3,
            "gcc", "clang", CTildeDebugMemoryMode.Objects);
        Equal("esp32_qemu", first.Target);
        Equal("esp32c3_qemu", second.Target);
        True(first.Arguments.Contains("--idf-path"));
        True(first.Arguments.Contains("--esp-clang"));
        True(!first.Arguments.Contains("--compiler"));
        True(!second.Arguments.Contains("--compiler"));
        True(!first.DescriptorPath.Equals(second.DescriptorPath, StringComparison.OrdinalIgnoreCase));
        Equal(DebugLaunchContracts.ManifestIdentity(esp32), Path.GetFileNameWithoutExtension(first.DescriptorPath));
        var physical = Path.Combine(root, "physical.json");
        File.WriteAllText(physical, "{\"target\":\"esp-idf\",\"sources\":[\"Program.ct\"]}");
        Throws<InvalidOperationException>(() => DebugLaunchContracts.CreatePreparation(Path.Combine(root, "ctilde.dll"), physical,
            null, null, CTildeDebugMemoryMode.Objects));
    });
});
Run("selected project routing and ambiguity", () =>
{
    WithProject(root =>
    {
        var outer = Path.Combine(root, "Outer.ctproj");
        var nestedDirectory = Path.Combine(root, "nested");
        Directory.CreateDirectory(nestedDirectory);
        var nested = Path.Combine(nestedDirectory, "Nested.ctproj");
        var variant = Path.Combine(nestedDirectory, "Variant.ctproj");
        var document = Path.Combine(nestedDirectory, "Program.ct");
        Equal(nested, ProjectSelection.Resolve(nested, null, document, [outer, nested, variant]));
        Equal(nested, ProjectSelection.Resolve(null, nested, document, [outer, nested, variant]));
        Equal(variant, ProjectSelection.Resolve(null, null, variant, [outer, nested]));
        Equal<string?>(null, ProjectSelection.Resolve(null, null, document, [outer, nested, variant]));
        Equal(outer, ProjectSelection.Resolve(null, null, Path.Combine(root, "Loose.ct"), [outer]));
        Equal<string?>(null, ProjectSelection.Resolve(null, null, null, [outer, nested]));
    });
});
Run("repository C~ project contracts", () =>
{
    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
    var relativeProjects = new[]
    {
        "CTilde/StandardLibrary/CTilde.StandardLibrary.ctproj",
        "examples/Hello/Hello.ctproj",
        "examples/Exceptions/Exceptions.ctproj",
        "examples/Features/Features.ctproj",
        "examples/InlineAssemblyWindows/InlineAssemblyWindows.ctproj",
        "examples/ObjectModel/ObjectModel.ctproj",
        "examples/Cosmopolitan/Cosmopolitan.ctproj",
        "examples/Freestanding/Freestanding.ctproj",
        "examples/HostedIo/HostedIo.ctproj",
        "examples/QemuFreestanding/QemuFreestanding.ctproj",
        "examples/TCan485/TCan485.Hardware.ctproj",
        "examples/TCan485/TCan485.QemuEsp32.ctproj",
        "examples/TCan485/TCan485.QemuEsp32C3.ctproj",
    };
    var contracts = relativeProjects.Select(path => CTildeProjectContract.Load(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)))).ToArray();
    Equal(13, contracts.Length);
    Equal(13, contracts.Select(contract => contract.ProjectGuid).Distinct().Count());
    True(contracts.All(contract => File.Exists(contract.ManifestPath)));
    foreach (var contract in contracts)
    {
        var projectText = File.ReadAllText(contract.ProjectPath);
        True(projectText.Contains("Microsoft.Common.props", StringComparison.Ordinal));
        True(projectText.Contains("Microsoft.Common.targets", StringComparison.Ordinal));
        True(projectText.Contains("<DefineCommonItemSchemas>true</DefineCommonItemSchemas>", StringComparison.Ordinal));
        True(projectText.Contains("<ProjectCapability Include=\"CTilde\" />", StringComparison.Ordinal));
        True(projectText.Contains("<ProjectConfiguration Include=\"Debug|AnyCPU\">", StringComparison.Ordinal));
        True(projectText.Contains("<ProjectConfiguration Include=\"Release|AnyCPU\">", StringComparison.Ordinal));
        True(projectText.Contains("<OutputPath>$(MSBuildProjectDirectory)\\obj\\$(Configuration)\\</OutputPath>", StringComparison.Ordinal));
        True(projectText.Contains("<DisableFastUpToDateCheck>true</DisableFastUpToDateCheck>", StringComparison.Ordinal));
        True(projectText.Contains("<None Include=\"**\\*\" Exclude=\"$(CTildeProjectItemExcludes)\" />", StringComparison.Ordinal));
        True(projectText.Contains("$(CTildeProjectSystemPath)\\CTilde.targets", StringComparison.Ordinal));
    }
    AssertSolutionProjects(root, "CTilde.sln",
    [
        "CTilde/CTilde.csproj",
        "CTilde.Cli/CTilde.Cli.csproj",
        "Test/Test.csproj",
        "CTilde.LanguageServer/CTilde.LanguageServer.csproj",
        "CTilde.DebugAdapter/CTilde.DebugAdapter.csproj",
        "CTilde.DebugAdapter.Tests/CTilde.DebugAdapter.Tests.csproj",
    ]);
    AssertSolutionProjects(root, "Editors.sln",
    [
        "editors/visualstudio/CTilde.VisualStudio.Core/CTilde.VisualStudio.Core.csproj",
        "editors/visualstudio/CTilde.VisualStudio/CTilde.VisualStudio.csproj",
        "editors/visualstudio/CTilde.VisualStudio.Tests/CTilde.VisualStudio.Tests.csproj",
    ]);
    AssertSolutionProjects(root, "Examples.sln", relativeProjects[1..]);
    AssertSolutionProjects(root, "CTilde.StandardLibrary.sln", relativeProjects[..1]);

    var examplesSolution = File.ReadAllText(Path.Combine(root, "Examples.sln"));
    var standardLibrarySolution = File.ReadAllText(Path.Combine(root, "CTilde.StandardLibrary.sln"));
    foreach (var contract in contracts[1..])
    {
        var guid = contract.ProjectGuid.ToString().ToUpperInvariant();
        var relative = Path.GetRelativePath(root, contract.ProjectPath).Replace('/', '\\');
        True(examplesSolution.Contains($"\"{relative}\", \"{{{guid}}}\"", StringComparison.Ordinal));
        AssertNoBuildMapping(examplesSolution, guid);
    }
    var standardLibraryGuid = contracts[0].ProjectGuid.ToString().ToUpperInvariant();
    AssertNoBuildMapping(standardLibrarySolution, standardLibraryGuid);
    True(examplesSolution.Contains("{4CD54149-3858-41D8-82BC-D49F144A6B90} = {4E0784B2-C9B9-4420-889F-14B231242281}", StringComparison.Ordinal));
    True(examplesSolution.Contains("{61011E83-E222-434D-9F4B-175DEAE2F1F3} = {4E0784B2-C9B9-4420-889F-14B231242281}", StringComparison.Ordinal));
    True(examplesSolution.Contains("{6CFF1E49-AC3D-43B9-A008-35A85FC530DB} = {4E0784B2-C9B9-4420-889F-14B231242281}", StringComparison.Ordinal));
});
Run("cancellation and nonzero CLI outcomes", () =>
{
    True(CommandOutcomes.Succeeded(0));
    True(!CommandOutcomes.Succeeded(1));
    True(!CommandOutcomes.Succeeded(CommandOutcomes.CanceledExitCode));
    Equal(130, CommandOutcomes.CanceledExitCode);
});
Run("missing dotnet guidance", () =>
{
    var message = CommandOutcomes.MissingDotNetMessage("The system cannot find the file specified.");
    True(message.Contains(".NET 10", StringComparison.Ordinal));
    True(message.Contains("https://dotnet.microsoft.com/download/dotnet/10.0", StringComparison.Ordinal));
    True(message.Contains("Tools > Options > C~", StringComparison.Ordinal));
});
Run("standard-library URI mapping", () =>
{
    True(StandardLibraryUri.TryGetDocumentId("ctilde-stdlib:/System.Console.ct", out var document));
    Equal("System.Console.ct", document);
    True(!StandardLibraryUri.TryGetDocumentId("ctilde-stdlib:/../secret.ct", out _));
    var first = StandardLibraryUri.CachePath(Path.GetTempPath(), "0.12.0", "ctilde-stdlib:/System.Console.ct");
    var second = StandardLibraryUri.CachePath(Path.GetTempPath(), "0.12.0", "ctilde-stdlib:/System.Console.ct");
    Equal(first, second);
    True(first.Contains($"{Path.DirectorySeparatorChar}0.12.0{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    Equal(new Uri(Path.GetFullPath(first)), StandardLibraryUri.FileUri(first));
});
Run("Visual Studio TextMate registration", () =>
{
    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
    var grammar = File.ReadAllText(Path.Combine(root, "editors", "vscode", "syntaxes", "ctilde.tmLanguage.json"));
    True(grammar.Contains("\"scopeName\": \"source.ctilde\"", StringComparison.Ordinal));
    True(grammar.Contains("\"fileTypes\":", StringComparison.Ordinal));
    True(grammar.Contains("\"ct\"", StringComparison.Ordinal));
    var registration = File.ReadAllText(Path.Combine(root, "editors", "visualstudio", "CTilde.VisualStudio", "languages.pkgdef"));
    True(registration.Contains("TextMate\\Repositories", StringComparison.Ordinal));
    True(registration.Contains("LanguageConfiguration\\GrammarMapping", StringComparison.Ordinal));
    True(registration.Contains("\"source.ctilde\"", StringComparison.Ordinal));
});
Run("Visual Studio TextMate classifications", () =>
{
    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
    var grammarName = "ctilde.tmLanguage.json";
    var themeName = Path.ChangeExtension(grammarName, ".tmTheme");
    Equal("ctilde.tmLanguage.tmTheme", themeName);
    var themePath = Path.Combine(root, "editors", "visualstudio", "CTilde.VisualStudio", "Grammars", themeName);
    True(!File.Exists(Path.Combine(Path.GetDirectoryName(themePath)!, "ctilde.tmTheme")));
    var themeText = File.ReadAllText(themePath);
    var theme = System.Xml.Linq.XDocument.Load(themePath);
    var mappings = TextMateClassificationMappings(theme);

    True(!themeText.Contains("<key>foreground</key>", StringComparison.OrdinalIgnoreCase));
    True(!themeText.Contains("<key>background</key>", StringComparison.OrdinalIgnoreCase));
    Equal("method name", mappings["entity.name.function"]);
    Equal("type", mappings["entity.name.type"]);
    Equal("keyword", mappings["storage.type.builtin"]);
    Equal("keyword - control", mappings["keyword.control"]);
    Equal("parameter name", mappings["variable.parameter"]);
    Equal("local name", mappings["variable.other"]);
    Equal("string", mappings["string"]);
    Equal("number", mappings["constant.numeric"]);
    Equal("operator", mappings["keyword.operator"]);
    Equal("comment", mappings["comment"]);
    Equal("punctuation", mappings["punctuation"]);
    Equal("identifier", mappings["meta.embedded.block.asm"]);

    var project = File.ReadAllText(Path.Combine(root, "editors", "visualstudio", "CTilde.VisualStudio", "CTilde.VisualStudio.csproj"));
    True(project.Contains("Grammars/ctilde.tmLanguage.tmTheme", StringComparison.Ordinal));
    True(project.Contains("VSIXSubPath=\"Grammars\"", StringComparison.Ordinal));
});
Run("Visual Studio debug launch registration", () =>
{
    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
    var provider = File.ReadAllText(Path.Combine(root, "editors", "visualstudio", "CTilde.VisualStudio", "ProjectSystem", "CTildeLaunchProvider.cs"));
    True(provider.Contains("[ExportDebugger(DebuggerName)]", StringComparison.Ordinal));
    True(provider.Contains("CanLaunchAsync(DebugLaunchOptions launchOptions) => Task.FromResult(true)", StringComparison.Ordinal));
    True(provider.Contains("DebugLaunchOptions.NoDebug", StringComparison.Ordinal));
    True(provider.Contains("DebugLaunchProviderBase", StringComparison.Ordinal));
    True(provider.Contains("QueryDebugTargetsAsync", StringComparison.Ordinal));
    True(provider.Contains("DebugLaunchContracts.EngineGuid", StringComparison.Ordinal));
    var debuggerRulePath = Path.Combine(root, "editors", "visualstudio", "CTilde.VisualStudio", "ProjectSystem", "CTildeDebugger.xaml");
    var debuggerRule = System.Xml.Linq.XDocument.Load(debuggerRulePath).Root!;
    Equal("CTilde", debuggerRule.Attribute("Name")?.Value);
    Equal("debugger", debuggerRule.Attribute("PageTemplate")?.Value);
    var project = File.ReadAllText(Path.Combine(root, "editors", "visualstudio", "CTilde.VisualStudio", "CTilde.VisualStudio.csproj"));
    True(project.Contains("<VSIXSourceItem Include=\"ProjectSystem/CTildeDebugger.xaml\"", StringComparison.Ordinal));
    True(project.Contains("CTilde.DebugAdapter.csproj", StringComparison.Ordinal));
    True(project.Contains("Tools\\DebugAdapter", StringComparison.Ordinal));
    True(project.Contains("<AssemblyVersion>0.12.0.0</AssemblyVersion>", StringComparison.Ordinal));
    var registration = File.ReadAllText(Path.Combine(root, "editors", "visualstudio", "CTilde.VisualStudio", "debug-adapter.pkgdef"));
    var normalizedRegistration = registration.Replace("\r\n", "\n", StringComparison.Ordinal);
    True(registration.Contains("{A8D3FECE-E5AE-4BB9-9483-23B1951FD115}", StringComparison.OrdinalIgnoreCase));
    True(registration.Contains("{0CF710B9-7DB1-473B-8CEB-1F981ABA01E2}", StringComparison.OrdinalIgnoreCase));
    True(registration.Contains("Tools\\DebugAdapter\\CTilde.DebugAdapter.exe", StringComparison.Ordinal));
    True(registration.Contains("\"Attach\"=dword:00000000", StringComparison.Ordinal));
    True(normalizedRegistration.Contains("C~ thrown exceptions]\n\"State\"=dword:00010000", StringComparison.Ordinal));
    var props = File.ReadAllText(Path.Combine(root, "editors", "visualstudio", "CTilde.VisualStudio", "ProjectSystem", "CTilde.props"));
    var targets = File.ReadAllText(Path.Combine(root, "editors", "visualstudio", "CTilde.VisualStudio", "ProjectSystem", "CTilde.targets"));
    True(props.Contains("<DebuggerFlavor>CTilde</DebuggerFlavor>", StringComparison.Ordinal));
    True(targets.Contains("<DebuggerFlavor Condition=", StringComparison.Ordinal));
    True(targets.Contains("<PropertyPageSchema Include=\"$(MSBuildThisFileDirectory)CTildeDebugger.xaml\">", StringComparison.Ordinal));
});

if (failures.Count == 0)
{
    Console.WriteLine("Visual Studio unit tests: all tests passed.");
    return 0;
}
foreach (var failure in failures)
    Console.Error.WriteLine(failure);
return 1;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {name}: {exception.Message}");
    }
}

static void WithProject(Action<string> action)
{
    var root = Path.Combine(Path.GetTempPath(), "ctilde-vs-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try { action(root); }
    finally { Directory.Delete(root, recursive: true); }
}

static void Write(string path)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, string.Empty);
}

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static void AssertSolutionProjects(string root, string solutionName, IReadOnlyCollection<string> expectedProjects)
{
    var actualProjects = File.ReadLines(Path.Combine(root, solutionName))
        .Where(line => line.StartsWith("Project(", StringComparison.Ordinal))
        .Select(line => line.Split("\", \"", StringSplitOptions.None))
        .Where(fields => fields.Length == 3)
        .Select(fields => fields[1].Replace('\\', '/'))
        .Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".ctproj", StringComparison.OrdinalIgnoreCase))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    var expected = expectedProjects.OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    Equal(string.Join("|", expected), string.Join("|", actualProjects));
}

static void AssertNoBuildMapping(string solution, string projectGuid)
{
    True(!solution.Split('\n').Any(line => line.Contains($"{{{projectGuid}}}.", StringComparison.Ordinal)
        && line.Contains(".Build.0 =", StringComparison.Ordinal)));
}

static IReadOnlyDictionary<string, string> TextMateClassificationMappings(System.Xml.Linq.XDocument theme)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    var settingsArray = theme.Descendants("key")
        .First(key => key.Value == "settings" && key.ElementsAfterSelf().FirstOrDefault()?.Name.LocalName == "array")
        .ElementsAfterSelf()
        .First(element => element.Name.LocalName == "array");

    foreach (var entry in settingsArray.Elements("dict"))
    {
        var elements = entry.Elements().ToArray();
        var scopeIndex = Array.FindIndex(elements, element => element.Name.LocalName == "key" && element.Value == "scope");
        var settingsIndex = Array.FindIndex(elements, element => element.Name.LocalName == "key" && element.Value == "settings");
        if (scopeIndex < 0 || settingsIndex < 0)
            continue;

        var scopes = elements[scopeIndex + 1].Value.Split(',').Select(scope => scope.Trim());
        var settings = elements[settingsIndex + 1];
        var settingElements = settings.Elements().ToArray();
        var classificationIndex = Array.FindIndex(settingElements, element => element.Name.LocalName == "key" && element.Value == "vsclassificationtype");
        True(classificationIndex >= 0);
        var classification = settingElements[classificationIndex + 1].Value;
        foreach (var scope in scopes)
            result.Add(scope, classification);
    }

    return result;
}
