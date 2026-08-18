using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace CTilde;

internal sealed class CompilationModel
{
    private static readonly Regex CIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> CKeywords = new(StringComparer.Ordinal)
    {
        "alignas", "alignof", "auto", "bool", "break", "case", "char", "const", "constexpr", "continue",
        "default", "do", "double", "else", "enum", "extern", "false", "float", "for", "goto", "if", "inline",
        "int", "long", "nullptr", "register", "restrict", "return", "short", "signed", "sizeof", "static",
        "static_assert", "struct", "switch", "thread_local", "true", "typedef", "typeof", "typeof_unqual", "union",
        "unsigned", "void", "volatile", "while",
    };
    private readonly Dictionary<SyntaxTree, string> _namespaces = [];
    private readonly Dictionary<SyntaxTree, ImmutableArray<string>> _usings = [];

    public CompilationModel(ImmutableArray<SyntaxTree> syntaxTrees, ImmutableArray<SyntaxTree> userSyntaxTrees, DiagnosticBag diagnostics)
    {
        SyntaxTrees = syntaxTrees;
        UserSyntaxTrees = userSyntaxTrees;
        Diagnostics = diagnostics;
        Types = new Dictionary<string, TypeSymbol>(StringComparer.Ordinal);
        DeclareTypes();
        ValidateUsings();
        ResolveBaseTypes();
        DeclareMembers();
        ValidateInheritanceMembers();
        ValidateRecursivePointerExposure();
        ValidateExternalSymbols();
        ValidateEntryPoint();
    }

    public ImmutableArray<SyntaxTree> SyntaxTrees { get; }
    public ImmutableArray<SyntaxTree> UserSyntaxTrees { get; }
    public DiagnosticBag Diagnostics { get; }
    public Dictionary<string, TypeSymbol> Types { get; }
    public IEnumerable<TypeSymbol> UserTypes => Types.Values.Where(type => type.Syntax is not null).OrderBy(type => type.FullName, StringComparer.Ordinal);
    public MethodSymbol? EntryPoint { get; private set; }

    public CType ResolveType(TypeSyntax syntax, SyntaxTree tree, bool report = true)
    {
        var baseType = syntax.Name == "object" && Types.TryGetValue("System.Object", out var objectType)
            ? objectType.Type
            : TypeFacts.BuiltIn(syntax.Name);
        if (baseType is null)
        {
            var candidates = ResolveNamedTypeCandidates(syntax.Name, tree).ToArray();
            if (candidates.Length > 1)
            {
                if (report)
                    Diagnostics.Add("CT1112", $"Type name '{syntax.Name}' is ambiguous between {string.Join(", ", candidates.Select(candidate => $"'{candidate.FullName}'"))}.", syntax.Source, syntax.Span);
                return CType.Error;
            }
            var type = candidates.SingleOrDefault();
            if (type is null)
            {
                if (report)
                    Diagnostics.Add("CT1101", $"The type '{syntax.Name}' could not be found.", syntax.Source, syntax.Span);
                return CType.Error;
            }
            baseType = type.Type;
        }

        if (baseType == CType.Void && (syntax.PointerDepth > 0 || syntax.IsArray))
        {
            Diagnostics.Add("CT2101", "void cannot be used as an element or pointed type.", syntax.Source, syntax.Span);
            return CType.Error;
        }

        for (var i = 0; i < syntax.PointerDepth; i++)
            baseType = new CType(CTypeKind.Pointer, ElementType: baseType);
        if (syntax.IsArray)
            baseType = new CType(CTypeKind.Array, ElementType: baseType);
        return baseType;
    }

    public TypeSymbol? ResolveNamedType(string name, SyntaxTree tree)
    {
        var candidates = ResolveNamedTypeCandidates(name, tree).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private IEnumerable<TypeSymbol> ResolveNamedTypeCandidates(string name, SyntaxTree tree)
    {
        if (name.Contains('.', StringComparison.Ordinal))
        {
            if (Types.TryGetValue(name, out var qualified))
                yield return qualified;
            yield break;
        }
        var currentNamespace = _namespaces.GetValueOrDefault(tree, string.Empty);
        if (!string.IsNullOrEmpty(currentNamespace) && Types.TryGetValue($"{currentNamespace}.{name}", out var local))
        {
            yield return local;
            yield break;
        }
        var emitted = new HashSet<TypeSymbol>();
        foreach (var imported in _usings.GetValueOrDefault(tree, []))
        {
            if (Types.TryGetValue($"{imported}.{name}", out var importedType) && emitted.Add(importedType))
                yield return importedType;
        }
        if (Types.TryGetValue(name, out var global) && emitted.Add(global))
            yield return global;
    }

    private void ValidateUsings()
    {
        foreach (var tree in SyntaxTrees)
        {
            foreach (var directive in tree.Root.Usings)
            {
                if (!Types.Values.Any(type => type.Namespace == directive.Name || type.Namespace.StartsWith(directive.Name + ".", StringComparison.Ordinal)))
                    Diagnostics.Add("CT1111", $"Namespace '{directive.Name}' does not exist in this compilation.", directive.Source, directive.Span);
            }
        }
    }

    private void DeclareTypes()
    {
        foreach (var tree in SyntaxTrees)
        {
            var namespaceName = tree.Root.Namespace?.Name ?? string.Empty;
            _namespaces[tree] = namespaceName;
            _usings[tree] = tree.Root.Usings.Select(@using => @using.Name).Append("System").Distinct(StringComparer.Ordinal).ToImmutableArray();
            foreach (var declaration in tree.Root.Types)
            {
                var fullName = string.IsNullOrEmpty(namespaceName) ? declaration.Name : $"{namespaceName}.{declaration.Name}";
                if (Types.TryGetValue(fullName, out var existing))
                {
                    Diagnostics.Add("CT1100", $"The type '{fullName}' is already declared.", declaration.Source, declaration.Span, existing.Syntax?.Source.GetLocation(existing.Syntax.Span));
                    continue;
                }
                var kind = declaration.Kind switch
                {
                    TypeDeclarationKind.Struct => DeclaredTypeKind.Struct,
                    TypeDeclarationKind.Enum => DeclaredTypeKind.Enum,
                    _ when declaration.Modifiers.Contains("static", StringComparer.Ordinal) => DeclaredTypeKind.StaticClass,
                    _ => DeclaredTypeKind.Class,
                };
                ValidateModifiers(declaration.Modifiers, declaration);
                var typeAccessibility = GetAccessibility(declaration.Modifiers, declaration, Accessibility.Internal);
                if (typeAccessibility is Accessibility.Private or Accessibility.Protected)
                    Diagnostics.Add("CT1216", "A namespace type can be only public or internal.", declaration.Source, declaration.Span);
                if (declaration.Kind != TypeDeclarationKind.Class && declaration.Modifiers.Contains("static", StringComparer.Ordinal))
                    Diagnostics.Add("CT1217", "Only a class can be static.", declaration.Source, declaration.Span);
                if (declaration.Kind != TypeDeclarationKind.Class && declaration.Modifiers.Contains("sealed", StringComparer.Ordinal))
                    Diagnostics.Add("CT1218", "sealed applies only to classes.", declaration.Source, declaration.Span);
                foreach (var invalidModifier in declaration.Modifiers.Where(modifier => modifier is "const" or "readonly" or "unsafe" or "virtual" or "override"))
                    Diagnostics.Add("CT1219", $"Modifier '{invalidModifier}' is not valid on a type declaration.", declaration.Source, declaration.Span);
                ValidateAttributes(declaration.Attributes, declaration, []);
                Types.Add(fullName, new TypeSymbol
                {
                    Namespace = namespaceName,
                    Name = declaration.Name,
                    Kind = kind,
                    Syntax = declaration,
                    IsSealed = declaration.Modifiers.Contains("sealed", StringComparer.Ordinal) || kind == DeclaredTypeKind.StaticClass,
                });
            }
        }
    }

    private void ResolveBaseTypes()
    {
        Types.TryGetValue("System.Object", out var objectType);
        foreach (var tree in SyntaxTrees)
        {
            foreach (var declaration in tree.Root.Types)
            {
                var fullName = string.IsNullOrEmpty(_namespaces[tree]) ? declaration.Name : $"{_namespaces[tree]}.{declaration.Name}";
                if (!Types.TryGetValue(fullName, out var type) || type.Kind != DeclaredTypeKind.Class)
                    continue;
                if (type.IsObject)
                {
                    if (declaration.BaseType is not null)
                        Diagnostics.Add("CT1225", "System.Object cannot declare a base type.", declaration.BaseType.Source, declaration.BaseType.Span);
                    continue;
                }
                if (declaration.BaseType is null)
                {
                    type.BaseType = objectType;
                    continue;
                }
                var resolved = ResolveType(declaration.BaseType, tree);
                if (resolved.Kind != CTypeKind.Class || resolved.Symbol is null || resolved.Symbol.IsStatic)
                {
                    Diagnostics.Add("CT1225", $"Class '{type.FullName}' requires a non-static class base type.", declaration.BaseType.Source, declaration.BaseType.Span);
                    continue;
                }
                type.BaseType = resolved.Symbol;
                if (resolved.Symbol.IsSealed)
                    Diagnostics.Add("CT1227", $"Class '{type.FullName}' cannot derive from sealed class '{resolved.Symbol.FullName}'.", declaration.BaseType.Source, declaration.BaseType.Span);
            }
        }

        var complete = new HashSet<TypeSymbol>();
        var active = new HashSet<TypeSymbol>();
        foreach (var type in Types.Values.Where(type => type.Kind == DeclaredTypeKind.Class))
            Visit(type);

        void Visit(TypeSymbol type)
        {
            if (complete.Contains(type))
                return;
            if (!active.Add(type))
            {
                if (type.Syntax is not null)
                    Diagnostics.Add("CT1226", $"Class '{type.FullName}' participates in an inheritance cycle.", type.Syntax.Source, type.Syntax.Span);
                return;
            }
            if (type.BaseType is not null)
                Visit(type.BaseType);
            active.Remove(type);
            complete.Add(type);
        }
    }

    private void DeclareMembers()
    {
        foreach (var tree in SyntaxTrees)
        {
            foreach (var declaration in tree.Root.Types)
            {
                var fullName = string.IsNullOrEmpty(_namespaces[tree]) ? declaration.Name : $"{_namespaces[tree]}.{declaration.Name}";
                if (!Types.TryGetValue(fullName, out var type) || type.Syntax != declaration)
                    continue;
                if (type.Kind == DeclaredTypeKind.Enum)
                {
                    DeclareEnum(type, declaration, tree);
                    continue;
                }
                foreach (var member in declaration.Members)
                    DeclareMember(type, member, tree);
                if (!type.IsStatic && type.Constructors.Count == 0)
                {
                    type.Constructors.Add(new MethodSymbol
                    {
                        Name = type.Name,
                        ContainingType = type,
                        Accessibility = Accessibility.Public,
                        IsStatic = false,
                        Syntax = null,
                        ReturnType = type.Type,
                        Parameters = [],
                        Body = null,
                        IsConstructor = true,
                    });
                }
            }
        }
    }

    private void DeclareMember(TypeSymbol type, MemberDeclarationSyntax declaration, SyntaxTree tree)
    {
        ValidateModifiers(declaration.Modifiers, declaration);
        var accessibility = GetAccessibility(declaration.Modifiers, declaration, Accessibility.Private);
        var isStatic = declaration.Modifiers.Contains("static", StringComparer.Ordinal) ||
            declaration is FieldDeclarationSyntax { Modifiers: var fieldModifiers } && fieldModifiers.Contains("const", StringComparer.Ordinal);
        if (type.IsStatic && !isStatic)
            Diagnostics.Add("CT1201", "A static class can contain only static members.", declaration.Source, declaration.Span);

        switch (declaration)
        {
            case FieldDeclarationSyntax field:
                {
                    ValidateAllowedModifiers(field.Modifiers, ["public", "internal", "protected", "private", "static", "const", "readonly", "unsafe"], field);
                    ValidateAttributes(field.Attributes, field, []);
                    var symbol = new FieldSymbol
                    {
                        Name = field.Name,
                        ContainingType = type,
                        Accessibility = accessibility,
                        IsStatic = isStatic,
                        Syntax = field,
                        Type = ResolveType(field.Type, tree),
                        IsReadonly = field.Modifiers.Contains("readonly", StringComparer.Ordinal),
                        IsConst = field.Modifiers.Contains("const", StringComparer.Ordinal),
                        Initializer = field.Initializer,
                    };
                    if (symbol.IsConst && field.Initializer is null)
                        Diagnostics.Add("CT1202", "A const field requires an initializer.", field.Source, field.Span);
                    if (symbol.IsConst && symbol.IsReadonly)
                        Diagnostics.Add("CT1220", "A field cannot be both const and readonly.", field.Source, field.Span);
                    AddUnique(type, symbol);
                    break;
                }
            case PropertyDeclarationSyntax property:
                {
                    ValidateAllowedModifiers(property.Modifiers, ["public", "internal", "protected", "private", "static", "unsafe", "virtual", "override", "sealed"], property);
                    ValidateAttributes(property.Attributes, property, []);
                    if (property.Getter is null && property.Setter is null)
                        Diagnostics.Add("CT1224", "A property requires a getter, a setter, or both.", property.Source, property.Span);
                    var propertyType = ResolveType(property.Type, tree);
                    if (property.Getter is not null)
                        ValidateAllowedModifiers(property.Getter.Modifiers, ["public", "internal", "protected", "private"], property.Getter);
                    if (property.Setter is not null)
                        ValidateAllowedModifiers(property.Setter.Modifiers, ["public", "internal", "protected", "private"], property.Setter);
                    FieldSymbol? backing = null;
                    if (property.Getter is { Body: null } || property.Setter is { Body: null })
                    {
                        backing = new FieldSymbol
                        {
                            Name = $"<{property.Name}>k__BackingField",
                            ContainingType = type,
                            Accessibility = Accessibility.Private,
                            IsStatic = isStatic,
                            Syntax = property,
                            Type = propertyType,
                            IsReadonly = false,
                            IsConst = false,
                        };
                        type.Fields.Add(backing);
                    }
                    var symbol = new PropertySymbol
                    {
                        Name = property.Name,
                        ContainingType = type,
                        Accessibility = accessibility,
                        IsStatic = isStatic,
                        Syntax = property,
                        Type = propertyType,
                        Getter = property.Getter,
                        Setter = property.Setter,
                        BackingField = backing,
                        GetterAccessibility = property.Getter is null ? Accessibility.Private : GetAccessibility(property.Getter.Modifiers, property.Getter, accessibility),
                        SetterAccessibility = property.Setter is null ? Accessibility.Private : GetAccessibility(property.Setter.Modifiers, property.Setter, accessibility),
                        IsVirtual = property.Modifiers.Contains("virtual", StringComparer.Ordinal) || property.Modifiers.Contains("override", StringComparer.Ordinal),
                        IsOverride = property.Modifiers.Contains("override", StringComparer.Ordinal),
                        IsSealedOverride = property.Modifiers.Contains("sealed", StringComparer.Ordinal),
                    };
                    if (AccessRank(symbol.GetterAccessibility) > AccessRank(accessibility) || AccessRank(symbol.SetterAccessibility) > AccessRank(accessibility))
                        Diagnostics.Add("CT1222", "An accessor cannot be more accessible than its property.", property.Source, property.Span);
                    AddUnique(type, symbol);
                    break;
                }
            case ConstructorDeclarationSyntax constructor:
                {
                    ValidateAllowedModifiers(constructor.Modifiers, ["public", "internal", "protected", "private", "unsafe"], constructor);
                    ValidateAttributes(constructor.Attributes, constructor, []);
                    if (isStatic)
                        Diagnostics.Add("CT1203", "Static constructors are not part of draft 0.4.", constructor.Source, constructor.Span);
                    var parameters = DeclareParameters(constructor.Parameters, tree);
                    var symbol = new MethodSymbol
                    {
                        Name = constructor.Name,
                        ContainingType = type,
                        Accessibility = accessibility,
                        IsStatic = false,
                        Syntax = constructor,
                        ReturnType = type.Type,
                        Parameters = parameters,
                        Body = constructor.Body,
                        IsConstructor = true,
                        ConstructorInitializer = constructor.Initializer,
                    };
                    AddMethod(type.Constructors, symbol);
                    break;
                }
            case MethodDeclarationSyntax method:
                {
                    ValidateAllowedModifiers(method.Modifiers, ["public", "internal", "protected", "private", "static", "unsafe", "virtual", "override", "sealed"], method);
                    ValidateAttributes(method.Attributes, method, ["EntryPoint", "Extern"]);
                    var entry = FindAttribute(method.Attributes, "EntryPoint");
                    var external = FindAttribute(method.Attributes, "Extern");
                    if (entry is not null && entry.Arguments.Length != 0)
                        Diagnostics.Add("CT1223", "EntryPoint does not accept arguments.", entry.Source, entry.Span);
                    string? externalName = null;
                    if (external is not null)
                    {
                        if (external.Arguments is [LiteralExpressionSyntax { LiteralKind: SyntaxKind.StringToken, Value: string value }] && IsPortableExternalIdentifier(value))
                            externalName = value;
                        else
                            Diagnostics.Add("CT1204", "Extern requires one string containing a portable C identifier.", external.Source, external.Span);
                        if (!isStatic || method.Body is not null)
                            Diagnostics.Add("CT1205", "An Extern method must be static and bodyless.", method.Source, method.Span);
                    }
                    else if (method.Body is null)
                        Diagnostics.Add("CT1206", "A bodyless method requires Extern.", method.Source, method.Span);
                    var returnType = ResolveType(method.ReturnType, tree);
                    var methodParameters = DeclareParameters(method.Parameters, tree);
                    var symbol = new MethodSymbol
                    {
                        Name = method.Name,
                        ContainingType = type,
                        Accessibility = accessibility,
                        IsStatic = isStatic,
                        Syntax = method,
                        ReturnType = returnType,
                        Parameters = methodParameters,
                        Body = method.Body,
                        IsEntryPoint = entry is not null,
                        ExternName = externalName,
                        IsTrustedExtern = !UserSyntaxTrees.Contains(tree),
                        IsVirtual = method.Modifiers.Contains("virtual", StringComparer.Ordinal) || method.Modifiers.Contains("override", StringComparer.Ordinal),
                        IsOverride = method.Modifiers.Contains("override", StringComparer.Ordinal),
                        IsSealedOverride = method.Modifiers.Contains("sealed", StringComparer.Ordinal),
                    };
                    if (entry is not null && (!isStatic || symbol.ReturnType != CType.Void || symbol.Parameters.Length != 0 || method.Body is null))
                        Diagnostics.Add("CT1207", "EntryPoint must mark a body-bearing static void method with no parameters.", entry.Source, entry.Span);
                    AddMethod(type.Methods, symbol);
                    break;
                }
        }
    }

    private ImmutableArray<ParameterSymbol> DeclareParameters(ImmutableArray<ParameterSyntax> parameters, SyntaxTree tree)
    {
        var result = ImmutableArray.CreateBuilder<ParameterSymbol>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            if (!names.Add(parameter.Name))
                Diagnostics.Add("CT1102", $"Parameter '{parameter.Name}' is already declared.", parameter.Source, parameter.Span);
            result.Add(new ParameterSymbol { Name = parameter.Name, Type = ResolveType(parameter.Type, tree), Syntax = parameter });
        }
        return result.ToImmutable();
    }

    private void DeclareEnum(TypeSymbol type, TypeDeclarationSyntax declaration, SyntaxTree tree)
    {
        var underlying = declaration.EnumUnderlyingType is null ? CType.Int : ResolveType(declaration.EnumUnderlyingType, tree);
        if (underlying.Kind is not CTypeKind.Byte and not CTypeKind.Sbyte and not CTypeKind.Short and not CTypeKind.Ushort and not CTypeKind.Int and not CTypeKind.Uint)
            Diagnostics.Add("CT1208", "An enum underlying type must be byte, sbyte, short, ushort, int, or uint.", declaration.Source, declaration.EnumUnderlyingType?.Span ?? declaration.Span);
        long value = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in declaration.EnumMembers)
        {
            if (!names.Add(member.Name))
            {
                Diagnostics.Add("CT1103", $"Enum member '{member.Name}' is already declared.", member.Source, member.Span);
                continue;
            }
            if (member.Value is LiteralExpressionSyntax { Value: NumericLiteralValue numeric, LiteralKind: SyntaxKind.NumberToken } && numeric.FloatingPoint is null && numeric.Integer >= long.MinValue && numeric.Integer <= long.MaxValue)
                value = (long)numeric.Integer;
            else if (member.Value is not null)
                Diagnostics.Add("CT1209", "An enum value must be an integral constant in draft 0.4.", member.Source, member.Value.Span);
            if (!FitsEnumValue(value, underlying))
                Diagnostics.Add("CT1215", $"Enum value {value} does not fit underlying type '{underlying.DisplayName}'.", member.Source, member.Span);
            type.EnumValues.Add(new EnumValueSymbol(member.Name, value, member));
            value++;
        }
        type.Fields.Add(new FieldSymbol
        {
            Name = "<underlying>",
            ContainingType = type,
            Accessibility = Accessibility.Private,
            IsStatic = true,
            Syntax = declaration,
            Type = underlying,
            IsReadonly = true,
            IsConst = true,
        });
    }

    private void ValidateEntryPoint()
    {
        var entries = UserTypes.SelectMany(type => type.Methods).Where(method => method.IsEntryPoint).ToArray();
        if (entries.Length == 1)
            EntryPoint = entries[0];
        else if (entries.Length == 0)
        {
            var source = UserSyntaxTrees.FirstOrDefault()?.Text ?? SourceText.From(string.Empty);
            Diagnostics.Add("CT1300", "The program must declare exactly one EntryPoint.", source, new TextSpan(0, 0));
        }
        else
        {
            foreach (var entry in entries.Skip(1))
                Diagnostics.Add("CT1301", "The program has more than one EntryPoint.", entry.Syntax!.Source, entry.Syntax.Span, entries[0].Syntax!.Source.GetLocation(entries[0].Syntax!.Span));
            EntryPoint = entries[0];
        }
    }

    private void ValidateInheritanceMembers()
    {
        var objectType = Types.GetValueOrDefault("System.Object");
        foreach (var type in Types.Values.Where(type => type.Kind is DeclaredTypeKind.Class or DeclaredTypeKind.Struct))
        {
            var baseTypes = type.Kind == DeclaredTypeKind.Class
                ? type.BaseTypesAndSelf().Skip(1).ToArray()
                : Array.Empty<TypeSymbol>();
            var inheritedMembers = baseTypes.SelectMany(baseType => baseType.Fields.Cast<MemberSymbol>().Concat(baseType.Properties).Concat(baseType.Methods)).ToArray();

            foreach (var member in type.Fields.Cast<MemberSymbol>())
            {
                if (inheritedMembers.Any(candidate => candidate.Name == member.Name))
                    Diagnostics.Add("CT1230", $"Member '{member.Name}' hides an inherited member; member hiding is not supported.", member.Syntax!.Source, member.Syntax.Span);
            }

            foreach (var property in type.Properties)
            {
                ValidateVirtualModifiers(property.IsStatic, property.IsVirtual, property.IsOverride, property.IsSealedOverride, property.Syntax!);
                var candidate = baseTypes.SelectMany(baseType => baseType.Properties).FirstOrDefault(baseProperty => baseProperty.Name == property.Name);
                if (property.IsOverride)
                {
                    if (candidate is null || !candidate.IsVirtual || candidate.IsSealedOverride || candidate.Type != property.Type ||
                        (candidate.Getter is null) != (property.Getter is null) || (candidate.Setter is null) != (property.Setter is null) ||
                        candidate.Accessibility != property.Accessibility)
                        Diagnostics.Add("CT1229", $"Property '{property.Name}' does not match an accessible unsealed virtual base property.", property.Syntax!.Source, property.Syntax.Span);
                    else
                        property.OverriddenProperty = candidate;
                }
                else if (candidate is not null)
                    Diagnostics.Add("CT1230", $"Property '{property.Name}' hides an inherited property; use override for a virtual property.", property.Syntax!.Source, property.Syntax.Span);
            }

            foreach (var method in type.Methods)
            {
                ValidateVirtualModifiers(method.IsStatic, method.IsVirtual, method.IsOverride, method.IsSealedOverride, method.Syntax!);
                var candidate = baseTypes.SelectMany(baseType => baseType.Methods)
                    .FirstOrDefault(baseMethod => HaveSameSourceSignature(baseMethod, method));
                if (type.Kind == DeclaredTypeKind.Struct && method.IsOverride && objectType is not null)
                    candidate = objectType.Methods.FirstOrDefault(baseMethod => HaveSameSourceSignature(baseMethod, method) && baseMethod.Name is "ToString" or "Equals" or "GetHashCode");
                if (method.IsOverride)
                {
                    if (candidate is null || !candidate.IsVirtual || candidate.IsSealedOverride || candidate.ReturnType != method.ReturnType || candidate.Accessibility != method.Accessibility)
                        Diagnostics.Add("CT1229", $"Method '{method.Name}' does not match an accessible unsealed virtual base method.", method.Syntax!.Source, method.Syntax.Span);
                    else
                        method.OverriddenMethod = candidate;
                }
                else if (candidate is not null && type.Kind == DeclaredTypeKind.Class)
                    Diagnostics.Add("CT1230", $"Method '{method.Name}' hides an inherited method; use override for a virtual method.", method.Syntax!.Source, method.Syntax.Span);
                if (inheritedMembers.Any(member => member.Name == method.Name && member is not MethodSymbol))
                    Diagnostics.Add("CT1230", $"Method '{method.Name}' hides an inherited member; member hiding is not supported.", method.Syntax!.Source, method.Syntax.Span);
            }
        }
    }

    private void ValidateVirtualModifiers(bool isStatic, bool isVirtual, bool isOverride, bool isSealedOverride, SyntaxNode syntax)
    {
        if (isStatic && isVirtual)
            Diagnostics.Add("CT1228", "A static member cannot be virtual or override.", syntax.Source, syntax.Span);
        if (isVirtual && !isOverride && syntax is MemberDeclarationSyntax declaration && declaration.Modifiers.Contains("sealed", StringComparer.Ordinal))
            Diagnostics.Add("CT1228", "sealed on a member requires override.", syntax.Source, syntax.Span);
        if (syntax is MemberDeclarationSyntax member && member.Modifiers.Contains("virtual", StringComparer.Ordinal) && member.Modifiers.Contains("override", StringComparer.Ordinal))
            Diagnostics.Add("CT1228", "A member cannot be both virtual and override.", syntax.Source, syntax.Span);
    }

    private static bool HaveSameSourceSignature(MethodSymbol left, MethodSymbol right) =>
        left.Name == right.Name && left.Parameters.Select(parameter => parameter.Type).SequenceEqual(right.Parameters.Select(parameter => parameter.Type));

    private void ValidateExternalSymbols()
    {
        var runtimeSymbols = new HashSet<string>(StringComparer.Ordinal)
        {
            "main", "ct_fail", "ct_require_nonnull", "ct_alloc", "ct_alloc_array", "ct_bounds", "ct_i32_bits",
            "ct_i32_add", "ct_i32_sub", "ct_i32_mul", "ct_i32_neg", "ct_i32_div", "ct_i32_mod",
            "ct_u32_div", "ct_u32_mod", "ct_i32_shl", "ct_i32_shr", "ct_string_equal", "ct_string_concat",
            "ct_string_from_bytes", "ct_string_from_format", "ct_to_string_int", "ct_to_string_uint",
            "ct_to_string_float", "ct_to_string_bool", "ct_to_string_char", "ct_write_string", "ct_write_char",
            "ct_write_int", "ct_write_uint", "ct_write_float", "ct_write_bool", "ct_write_line", "ct_environment_exit",
            "ct_module_init", "ct_keep_symbols", "ct_string", "ct_object", "ct_type_descriptor", "ct_vtable",
            "ct_init_object", "ct_object_default_to_string", "ct_object_default_equals", "ct_object_default_hash",
            "ct_object_to_string", "ct_object_hash", "ct_object_reference_equals", "ct_type_is_assignable",
            "ct_checked_cast", "ct_safe_cast", "ct_hash_bytes", "ct_hash_float", "ct_object_value_equals",
            "ct_object_value_hash", "ct_default_vtable", "ct_string_vtable", "ct_desc_string",
            "ct_string_v_to_string", "ct_string_v_equals", "ct_string_v_hash", "NAN", "INFINITY",
        };
        var generatedSymbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in Types.Values)
        {
            generatedSymbols.Add(NameMangler.Type(type));
            foreach (var field in type.Fields.Where(field => field.IsStatic && field.Name != "<underlying>"))
                generatedSymbols.Add(field.CName);
            foreach (var value in type.EnumValues)
                generatedSymbols.Add(NameMangler.Identifier(type.FullName + "." + value.Name));
            foreach (var constructor in type.Constructors)
                generatedSymbols.Add(NameMangler.Method(constructor));
            foreach (var method in type.Methods.Where(method => method.ExternName is null))
                generatedSymbols.Add(NameMangler.Method(method));
            foreach (var property in type.Properties)
            {
                if (property.Getter is not null)
                    generatedSymbols.Add(NameMangler.Getter(property));
                if (property.Setter is not null)
                    generatedSymbols.Add(NameMangler.Setter(property));
            }
        }

        var externs = Types.Values.SelectMany(type => type.Methods)
            .Where(method => method.ExternName is not null)
            .OrderBy(method => method.ExternName, StringComparer.Ordinal)
            .ThenBy(method => method.ContainingType.FullName, StringComparer.Ordinal)
            .ToArray();
        foreach (var method in externs.Where(method => !method.IsTrustedExtern))
        {
            if (runtimeSymbols.Contains(method.ExternName!) || generatedSymbols.Contains(method.ExternName!))
                Diagnostics.Add("CT4101", $"External symbol '{method.ExternName}' conflicts with a compiler-owned or generated C symbol.", method.Syntax!.Source, method.Syntax.Span);
        }

        foreach (var group in externs.GroupBy(method => method.ExternName!, StringComparer.Ordinal))
        {
            var first = group.First();
            foreach (var method in group.Skip(1))
            {
                if (HaveSameAbiSignature(first, method))
                    continue;
                Diagnostics.Add("CT4102", $"External symbol '{group.Key}' has incompatible ABI signatures.", method.Syntax!.Source, method.Syntax.Span,
                    first.Syntax?.Source.GetLocation(first.Syntax.Span));
            }
        }
    }

    private static bool HaveSameAbiSignature(MethodSymbol left, MethodSymbol right) =>
        left.ReturnType == right.ReturnType &&
        left.Parameters.Select(parameter => parameter.Type).SequenceEqual(right.Parameters.Select(parameter => parameter.Type));

    private static bool FitsEnumValue(long value, CType underlying) => underlying.Kind switch
    {
        CTypeKind.Byte => value is >= byte.MinValue and <= byte.MaxValue,
        CTypeKind.Sbyte => value is >= sbyte.MinValue and <= sbyte.MaxValue,
        CTypeKind.Short => value is >= short.MinValue and <= short.MaxValue,
        CTypeKind.Ushort => value is >= ushort.MinValue and <= ushort.MaxValue,
        CTypeKind.Int => value is >= int.MinValue and <= int.MaxValue,
        CTypeKind.Uint => value is >= uint.MinValue and <= uint.MaxValue,
        _ => false,
    };

    private static int AccessRank(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Private => 0,
        Accessibility.Protected => 1,
        Accessibility.Internal => 2,
        Accessibility.Public => 3,
        _ => 0,
    };

    private void AddUnique(TypeSymbol type, MemberSymbol member)
    {
        var existing = type.Fields.Cast<MemberSymbol>().Concat(type.Properties).FirstOrDefault(candidate => candidate.Name == member.Name);
        if (existing is not null)
        {
            Diagnostics.Add("CT1104", $"Member '{member.Name}' is already declared in '{type.FullName}'.", member.Syntax!.Source, member.Syntax.Span, existing.Syntax?.Source.GetLocation(existing.Syntax.Span));
            return;
        }
        if (member is FieldSymbol field)
            type.Fields.Add(field);
        else
            type.Properties.Add((PropertySymbol)member);
    }

    private void AddMethod(List<MethodSymbol> methods, MethodSymbol method)
    {
        var existing = methods.FirstOrDefault(candidate => candidate.Name == method.Name && candidate.Parameters.Select(parameter => parameter.Type).SequenceEqual(method.Parameters.Select(parameter => parameter.Type)));
        if (existing is not null)
        {
            Diagnostics.Add("CT1105", $"Method '{method.Name}' with the same parameter types is already declared.", method.Syntax!.Source, method.Syntax.Span, existing.Syntax?.Source.GetLocation(existing.Syntax.Span));
            return;
        }
        methods.Add(method);
    }

    private Accessibility GetAccessibility(ImmutableArray<string> modifiers, SyntaxNode syntax, Accessibility fallback)
    {
        var values = modifiers.Where(modifier => modifier is "public" or "internal" or "protected" or "private").ToArray();
        if (values.Length > 1)
            Diagnostics.Add("CT1210", "Only one access modifier is permitted.", syntax.Source, syntax.Span);
        return values.FirstOrDefault() switch
        {
            "public" => Accessibility.Public,
            "internal" => Accessibility.Internal,
            "protected" => Accessibility.Protected,
            "private" => Accessibility.Private,
            _ => fallback,
        };
    }

    private void ValidateModifiers(ImmutableArray<string> modifiers, SyntaxNode syntax)
    {
        foreach (var duplicate in modifiers.GroupBy(modifier => modifier, StringComparer.Ordinal).Where(group => group.Count() > 1))
            Diagnostics.Add("CT1212", $"Duplicate modifier '{duplicate.Key}'.", syntax.Source, syntax.Span);
    }

    private void ValidateAllowedModifiers(ImmutableArray<string> modifiers, string[] allowed, SyntaxNode syntax)
    {
        foreach (var modifier in modifiers.Where(modifier => !allowed.Contains(modifier, StringComparer.Ordinal)))
            Diagnostics.Add("CT1221", $"Modifier '{modifier}' is not valid on this declaration.", syntax.Source, syntax.Span);
    }

    private void ValidatePointerExposure(CType type, ImmutableArray<string> modifiers, SyntaxNode syntax)
    {
        if (type.ContainsPointer && !modifiers.Contains("unsafe", StringComparer.Ordinal))
            Diagnostics.Add("CT2141", "A pointer in a member signature requires the unsafe modifier.", syntax.Source, syntax.Span);
    }

    private void ValidateRecursivePointerExposure()
    {
        foreach (var type in Types.Values)
        {
            foreach (var field in type.Fields.Where(field => field.Syntax is MemberDeclarationSyntax))
                ValidatePointerExposure(field.Type, ((MemberDeclarationSyntax)field.Syntax!).Modifiers, field.Syntax!);
            foreach (var property in type.Properties)
                ValidatePointerExposure(property.Type, ((MemberDeclarationSyntax)property.Syntax!).Modifiers, property.Syntax!);
            foreach (var method in type.Constructors.Concat(type.Methods).Where(method => method.Syntax is MemberDeclarationSyntax))
            {
                var modifiers = ((MemberDeclarationSyntax)method.Syntax!).Modifiers;
                ValidatePointerExposure(method.ReturnType, modifiers, method.Syntax!);
                foreach (var parameter in method.Parameters)
                    ValidatePointerExposure(parameter.Type, modifiers, method.Syntax!);
            }
        }
    }

    private void ValidateAttributes(ImmutableArray<AttributeSyntax> attributes, SyntaxNode syntax, string[] allowed)
    {
        foreach (var attribute in attributes)
        {
            if (!allowed.Contains(attribute.Name, StringComparer.Ordinal))
                Diagnostics.Add("CT1213", $"Unknown or invalid attribute '{attribute.Name}' on this declaration.", attribute.Source, attribute.Span);
        }
        foreach (var duplicate in attributes.GroupBy(attribute => attribute.Name, StringComparer.Ordinal).Where(group => group.Count() > 1))
            Diagnostics.Add("CT1214", $"Attribute '{duplicate.Key}' cannot be applied more than once.", syntax.Source, syntax.Span);
    }

    private static AttributeSyntax? FindAttribute(ImmutableArray<AttributeSyntax> attributes, string name) => attributes.FirstOrDefault(attribute => attribute.Name == name);

    private static bool IsPortableExternalIdentifier(string value) =>
        CIdentifier.IsMatch(value) && !value.StartsWith('_') && !CKeywords.Contains(value);
}
