namespace CTilde;

internal sealed record DirectDeferThunk(string Name, string Code);

internal interface ILoweringServices
{
    CompilationModel Model { get; }
    DiagnosticBag Diagnostics { get; }
    EffectRegistry Effects { get; }
    IEnumerable<(MethodSymbol Method, SyntaxNode Syntax)> ExternUses { get; }
    IEnumerable<string> DynamicGeneratedSymbols { get; }
    bool EmitDebugInformation { get; }
    bool EmitDebugInstrumentation { get; }
    CompilationTarget Target { get; }
    CompilationArchitecture Architecture { get; }
    bool HasCpuFeature(CpuFeature feature);

    string CTypeName(CType type);
    string CDeclaration(CType type, string name);
    string CParameterDeclaration(ParameterSymbol parameter, string name);
    string CCastType(CType type);
    string DefaultValue(CType type);
    string RetainValueStatement(CType type, string address);
    string DropValueStatement(CType type, string address);
    string DescriptorExpression(CType type);
    string RegisterString(string value);
    string SourceArgument(SyntaxNode syntax);
    string DebugSourceDirective(SyntaxNode syntax);
    string DebugGeneratedDirective();
    void RegisterDebugExecutable(MethodSymbol method, SyntaxNode syntax);
    void RegisterDebugLocal(MethodSymbol method, LocalSymbol local, int liveStart, int? liveEnd);
    int RegisterDebugSite(MethodSymbol method, SyntaxNode syntax, string kind);
    string RegisterDelegateThunk(TypeSymbol delegateType, MethodSymbol method, bool virtualDispatch);
    string RegisterFunctionPointerTrampoline(CType type, MethodSymbol method);
    string DirectDeferThunkName(MethodSymbol method, int id);
    string DurableStateTypeName(MethodSymbol method);
    void RegisterDirectDeferState(MethodSymbol method, IReadOnlyDictionary<string, CType> fields, IReadOnlyList<DirectDeferThunk> thunks);
    string SynchronousCallbackAdapterName(TypeSymbol delegateType);
    string MethodSignature(MethodSymbol method, string? name = null, bool prototype = false);
    MethodSymbol GetAccessorMethod(PropertySymbol property, bool getter);
    void RegisterExceptions();
    void RegisterExternUse(MethodSymbol method, SyntaxNode syntax);
    void RegisterType(CType type);
    void RegisterBox(CType type);
}
