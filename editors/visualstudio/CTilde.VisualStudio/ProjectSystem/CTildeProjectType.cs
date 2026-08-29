using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Reflection;
using System.Threading.Tasks.Dataflow;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Build;
using Microsoft.VisualStudio.ProjectSystem.VS;

namespace CTilde.VisualStudio.ProjectSystem;

[Export]
[AppliesTo(Capability)]
[ProjectTypeRegistration(ProjectTypeGuid, "C~", "C~ Project Files (*.ctproj);*.ctproj", "ctproj", "CTilde", CTildePackage.PackageGuid,
    Capabilities = Capability + ";UseFileGlobs;OpenProjectFile;HandlesOwnReload;ProjectConfigurationsDeclaredAsItems",
    PossibleProjectExtensions = "ctproj")]
internal sealed class CTildeProjectType
{
    public const string Capability = "CTilde";
    public const string ProjectTypeGuid = "97222c8d-c7c1-4f63-8c70-0798e354f6f0";

    [ImportingConstructor]
    public CTildeProjectType(UnconfiguredProject project) => Project = project;

    internal UnconfiguredProject Project { get; }
}

[Export(typeof(IProjectConfigurationDimensionsProvider))]
[AppliesTo(CTildeProjectType.Capability)]
[ConfigurationDimensionDescription("Configuration", false)]
[ConfigurationDimensionDescription("Platform", false)]
internal sealed class CTildeConfigurationDimensions : IProjectConfigurationDimensionsProvider
{
    public Task<IEnumerable<KeyValuePair<string, IEnumerable<string>>>> GetProjectConfigurationDimensionsAsync(UnconfiguredProject project) =>
        Task.FromResult<IEnumerable<KeyValuePair<string, IEnumerable<string>>>>(
        [
            new("Configuration", new[] { "Debug", "Release" }),
            new("Platform", new[] { "AnyCPU" }),
        ]);

    public Task<IEnumerable<KeyValuePair<string, string>>> GetDefaultValuesForDimensionsAsync(UnconfiguredProject project) =>
        Task.FromResult<IEnumerable<KeyValuePair<string, string>>>(
        [
            new("Configuration", "Debug"),
            new("Platform", "AnyCPU"),
        ]);
}

[Export(typeof(IProjectGlobalPropertiesProvider))]
[AppliesTo(CTildeProjectType.Capability)]
internal sealed class CTildeBuildPropertiesProvider :
    ProjectValueDataSourceBase<IImmutableDictionary<string, string>>,
    IProjectGlobalPropertiesProvider
{
    private ITargetBlock<IProjectVersionedValue<IImmutableDictionary<string, string>>>? _targetBlock;
    private IReceivableSourceBlock<IProjectVersionedValue<IImmutableDictionary<string, string>>>? _sourceBlock;
    private long _version;

    [ImportingConstructor]
    public CTildeBuildPropertiesProvider(IProjectService projectService) : base(projectService.Services) { }

    public override NamedIdentity DataSourceKey { get; } = new("CTildeBuildProperties");
    public override IComparable DataSourceVersion => Interlocked.Read(ref _version);
    public override IReceivableSourceBlock<IProjectVersionedValue<IImmutableDictionary<string, string>>> SourceBlock
    {
        get
        {
            EnsureInitialized();
            return _sourceBlock!;
        }
    }

    public Task<IImmutableDictionary<string, string>> GetGlobalPropertiesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Properties());

    protected override void Initialize()
    {
        base.Initialize();
        var broadcastBlock = new BroadcastBlock<IProjectVersionedValue<IImmutableDictionary<string, string>>>(
            value => value,
            new DataflowBlockOptions { NameFormat = "CTildeBuildProperties: {1}" });
        _sourceBlock = broadcastBlock.SafePublicize();
        _targetBlock = broadcastBlock;
        CTildeToolPaths.Changed += Publish;
        Publish();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CTildeToolPaths.Changed -= Publish;
            _targetBlock?.Complete();
        }
        base.Dispose(disposing);
    }

    private void Publish()
    {
        var version = Interlocked.Increment(ref _version);
        _targetBlock?.Post(new ProjectVersionedValue<IImmutableDictionary<string, string>>(
            Properties(),
            ImmutableDictionary<NamedIdentity, IComparable>.Empty.Add(DataSourceKey, version)));
    }

    private static IImmutableDictionary<string, string> Properties()
    {
        var extensionDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var options = CTildeToolPaths.Current;
        var compiler = string.IsNullOrWhiteSpace(options.CompilerPath)
            ? Path.Combine(extensionDirectory, "Tools", "Compiler", "ctilde.dll")
            : Path.GetFullPath(options.CompilerPath);
        var dotnet = string.IsNullOrWhiteSpace(options.DotNetPath) ? "dotnet" : options.DotNetPath;
        IImmutableDictionary<string, string> properties = ImmutableDictionary<string, string>.Empty
            .Add("CTildeProjectSystemPath", Path.Combine(extensionDirectory, "ProjectSystem"))
            .Add("CTildeCompilerPath", compiler)
            .Add("CTildeDotNetPath", dotnet)
            .Add("DebuggerFlavor", CTildeLaunchProvider.DebuggerName);
        return properties;
    }
}
