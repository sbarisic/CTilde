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
    var solution = File.ReadAllText(Path.Combine(root, "CTilde.sln"));
    foreach (var contract in contracts)
    {
        var guid = contract.ProjectGuid.ToString().ToUpperInvariant();
        var relative = Path.GetRelativePath(root, contract.ProjectPath).Replace('/', '\\');
        True(solution.Contains($"\"{relative}\", \"{{{guid}}}\"", StringComparison.Ordinal));
        True(!solution.Contains($"{{{guid}}}.Release|Any CPU.Build.0", StringComparison.Ordinal));
        True(!solution.Contains($"{{{guid}}}.Debug|Any CPU.Build.0", StringComparison.Ordinal));
    }
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
    var first = StandardLibraryUri.CachePath(Path.GetTempPath(), "0.11.0", "ctilde-stdlib:/System.Console.ct");
    var second = StandardLibraryUri.CachePath(Path.GetTempPath(), "0.11.0", "ctilde-stdlib:/System.Console.ct");
    Equal(first, second);
    True(first.Contains($"{Path.DirectorySeparatorChar}0.11.0{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
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
Run("Visual Studio no-debug launch registration", () =>
{
    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
    var provider = File.ReadAllText(Path.Combine(root, "editors", "visualstudio", "CTilde.VisualStudio", "ProjectSystem", "CTildeLaunchProvider.cs"));
    True(provider.Contains("[ExportDebugger(DebuggerName)]", StringComparison.Ordinal));
    True(provider.Contains("CanLaunchAsync(DebugLaunchOptions launchOptions) => Task.FromResult(true)", StringComparison.Ordinal));
    True(provider.Contains("DebugLaunchOptions.NoDebug", StringComparison.Ordinal));
    True(provider.Contains("CTildeRunManager.DebuggingUnavailableMessage", StringComparison.Ordinal));
    var debuggerRulePath = Path.Combine(root, "editors", "visualstudio", "CTilde.VisualStudio", "ProjectSystem", "CTildeDebugger.xaml");
    var debuggerRule = System.Xml.Linq.XDocument.Load(debuggerRulePath).Root!;
    Equal("CTilde", debuggerRule.Attribute("Name")?.Value);
    Equal("debugger", debuggerRule.Attribute("PageTemplate")?.Value);
    var project = File.ReadAllText(Path.Combine(root, "editors", "visualstudio", "CTilde.VisualStudio", "CTilde.VisualStudio.csproj"));
    True(project.Contains("<VSIXSourceItem Include=\"ProjectSystem/CTildeDebugger.xaml\"", StringComparison.Ordinal));
    True(project.Contains("<AssemblyVersion>0.11.0.0</AssemblyVersion>", StringComparison.Ordinal));
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
