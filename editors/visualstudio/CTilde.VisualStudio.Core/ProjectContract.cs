using System.Xml;
using System.Xml.Linq;

namespace CTilde.VisualStudio.Core;

public sealed record CTildeProjectContract(string ProjectPath, Guid ProjectGuid, string ManifestPath)
{
    public static CTildeProjectContract Load(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            throw new ArgumentException("A .ctproj path is required.", nameof(projectPath));
        var fullProjectPath = Path.GetFullPath(projectPath);
        if (!Path.GetExtension(fullProjectPath).Equals(".ctproj", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"'{fullProjectPath}' is not a .ctproj file.");
        if (!File.Exists(fullProjectPath))
            throw new FileNotFoundException("The C~ project file does not exist.", fullProjectPath);

        XDocument document;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using (var reader = XmlReader.Create(fullProjectPath, settings))
            document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root;
        if (root is null || root.Name.LocalName != "Project")
            throw new InvalidDataException("A .ctproj file must have a Project root element.");

        var properties = root.Descendants().Where(element => element.Parent?.Name.LocalName == "PropertyGroup").ToArray();
        var projectGuidText = SingleProperty(properties, "ProjectGuid", required: true)!;
        if (!Guid.TryParse(projectGuidText, out var projectGuid) || projectGuid == Guid.Empty)
            throw new InvalidDataException("ProjectGuid must be a non-empty GUID.");
        var manifestValue = SingleProperty(properties, "CTildeManifest", required: false) ?? "ctilde.json";
        if (string.IsNullOrWhiteSpace(manifestValue) || Path.IsPathRooted(manifestValue))
            throw new InvalidDataException("CTildeManifest must be a non-empty project-relative path.");

        var projectDirectory = Path.GetDirectoryName(fullProjectPath)!;
        var manifestPath = Path.GetFullPath(Path.Combine(projectDirectory, manifestValue));
        if (!IsAtOrBelow(projectDirectory, manifestPath))
            throw new InvalidDataException("CTildeManifest must stay inside the project directory.");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("The C~ manifest referenced by CTildeManifest does not exist.", manifestPath);
        return new CTildeProjectContract(fullProjectPath, projectGuid, manifestPath);
    }

    public static void Create(string projectPath, string manifestPath, Guid projectGuid)
    {
        var fullProjectPath = Path.GetFullPath(projectPath);
        var fullManifestPath = Path.GetFullPath(manifestPath);
        var projectDirectory = Path.GetDirectoryName(fullProjectPath)!;
        if (File.Exists(fullProjectPath))
            throw new IOException($"The project file '{fullProjectPath}' already exists.");
        if (!File.Exists(fullManifestPath))
            throw new FileNotFoundException("The selected C~ manifest does not exist.", fullManifestPath);
        if (!IsAtOrBelow(projectDirectory, fullManifestPath))
            throw new InvalidDataException("The manifest must be inside the new project directory.");
        Directory.CreateDirectory(projectDirectory);
        var relative = RelativePath(projectDirectory, fullManifestPath).Replace('\\', '/');
        var escapedManifest = System.Security.SecurityElement.Escape(relative);
        var text = $"<Project ToolsVersion=\"Current\" DefaultTargets=\"Build\">{Environment.NewLine}" +
                   $"  <Import Project=\"$(MSBuildExtensionsPath)\\$(MSBuildToolsVersion)\\Microsoft.Common.props\" />{Environment.NewLine}" +
                   $"  <PropertyGroup>{Environment.NewLine}" +
                   $"    <ProjectGuid>{{{projectGuid.ToString().ToUpperInvariant()}}}</ProjectGuid>{Environment.NewLine}" +
                   $"    <CTildeManifest>{escapedManifest}</CTildeManifest>{Environment.NewLine}" +
                   $"    <Configuration Condition=\"'$(Configuration)' == ''\">Debug</Configuration>{Environment.NewLine}" +
                   $"    <Platform Condition=\"'$(Platform)' == ''\">AnyCPU</Platform>{Environment.NewLine}" +
                   $"    <OutputPath>$(MSBuildProjectDirectory)\\obj\\$(Configuration)\\</OutputPath>{Environment.NewLine}" +
                   $"    <DisableFastUpToDateCheck>true</DisableFastUpToDateCheck>{Environment.NewLine}" +
                   $"    <DefineCommonItemSchemas>true</DefineCommonItemSchemas>{Environment.NewLine}" +
                   $"    <CTildeProjectItemExcludes>$(DefaultItemExcludes);$(DefaultExcludesInProjectFolder);.git\\**;**\\.git\\**;.vs\\**;**\\.vs\\**;.ctilde\\**;**\\.ctilde\\**;.ctilde-cache\\**;**\\.ctilde-cache\\**;bin\\**;**\\bin\\**;obj\\**;**\\obj\\**;build\\**;**\\build\\**;node_modules\\**;**\\node_modules\\**;main\\generated\\**;**\\main\\generated\\**;managed_components\\**;**\\managed_components\\**;**\\*.ctproj</CTildeProjectItemExcludes>{Environment.NewLine}" +
                   $"  </PropertyGroup>{Environment.NewLine}" +
                   $"  <ItemGroup Label=\"ProjectConfigurations\">{Environment.NewLine}" +
                   $"    <ProjectConfiguration Include=\"Debug|AnyCPU\"><Configuration>Debug</Configuration><Platform>AnyCPU</Platform></ProjectConfiguration>{Environment.NewLine}" +
                   $"    <ProjectConfiguration Include=\"Release|AnyCPU\"><Configuration>Release</Configuration><Platform>AnyCPU</Platform></ProjectConfiguration>{Environment.NewLine}" +
                   $"  </ItemGroup>{Environment.NewLine}" +
                   $"  <ItemGroup>{Environment.NewLine}" +
                   $"    <ProjectCapability Include=\"CTilde\" />{Environment.NewLine}" +
                   $"    <ProjectCapability Include=\"UseFileGlobs\" />{Environment.NewLine}" +
                   $"    <ProjectCapability Include=\"OpenProjectFile\" />{Environment.NewLine}" +
                   $"    <ProjectCapability Include=\"HandlesOwnReload\" />{Environment.NewLine}" +
                   $"    <ProjectCapability Include=\"ProjectConfigurationsDeclaredAsItems\" />{Environment.NewLine}" +
                   $"  </ItemGroup>{Environment.NewLine}" +
                   $"  <ItemGroup>{Environment.NewLine}" +
                   $"    <None Include=\"**\\*\" Exclude=\"$(CTildeProjectItemExcludes)\" />{Environment.NewLine}" +
                   $"  </ItemGroup>{Environment.NewLine}" +
                   $"  <Import Project=\"$(MSBuildToolsPath)\\Microsoft.Common.targets\" />{Environment.NewLine}" +
                   $"  <Import Project=\"$(CTildeProjectSystemPath)\\CTilde.targets\" Condition=\"Exists('$(CTildeProjectSystemPath)\\CTilde.targets')\" />{Environment.NewLine}" +
                   $"</Project>{Environment.NewLine}";
        using var stream = new FileStream(fullProjectPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.Write(text);
    }

    private static string? SingleProperty(IEnumerable<XElement> properties, string name, bool required)
    {
        var values = properties.Where(element => element.Name.LocalName == name).Select(element => element.Value.Trim()).ToArray();
        if (values.Length > 1)
            throw new InvalidDataException($"{name} must appear at most once.");
        if (required && values.Length == 0)
            throw new InvalidDataException($"{name} is required.");
        return values.SingleOrDefault();
    }

    private static bool IsAtOrBelow(string directory, string path)
    {
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        var comparison = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return fullPath.Equals(fullDirectory, comparison) || fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, comparison);
    }

    private static string RelativePath(string directory, string path)
    {
        var basePath = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Uri.UnescapeDataString(new Uri(basePath).MakeRelativeUri(new Uri(Path.GetFullPath(path))).ToString()).Replace('/', Path.DirectorySeparatorChar);
    }
}
