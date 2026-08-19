namespace CTilde;

internal interface ILoweringServices
{
    CompilationModel Model { get; }
    DiagnosticBag Diagnostics { get; }
    AllocationEffectRegistry AllocationEffects { get; }
    IEnumerable<(MethodSymbol Method, SyntaxNode Syntax)> ExternUses { get; }
    IEnumerable<string> DynamicGeneratedSymbols { get; }

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
    string RegisterDelegateThunk(TypeSymbol delegateType, MethodSymbol method, bool virtualDispatch);
    string RegisterFunctionPointerTrampoline(CType type, MethodSymbol method);
    string MethodSignature(MethodSymbol method, string? name = null, bool prototype = false);
    MethodSymbol GetAccessorMethod(PropertySymbol property, bool getter);
    void RegisterExceptions();
    void RegisterExternUse(MethodSymbol method, SyntaxNode syntax);
    void RegisterType(CType type);
    void RegisterBox(CType type);
}
