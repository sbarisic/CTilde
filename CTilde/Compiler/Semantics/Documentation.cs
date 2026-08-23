using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CTilde;

public sealed record LanguageParameterDocumentation(string Name, string Text);

public sealed record LanguageExceptionDocumentation(string TypeName, string Text);

public sealed record LanguageDocumentation(
    string Summary,
    ImmutableArray<LanguageParameterDocumentation> Parameters,
    string? Returns,
    string? Remarks,
    ImmutableArray<LanguageExceptionDocumentation> Exceptions)
{
    public bool IsEmpty => string.IsNullOrEmpty(Summary) && Parameters.IsDefaultOrEmpty &&
        string.IsNullOrEmpty(Returns) && string.IsNullOrEmpty(Remarks) && Exceptions.IsDefaultOrEmpty;
}

internal sealed class DocumentationIndex
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);
    private readonly ImmutableDictionary<string, LanguageDocumentation> _documentation;
    private readonly Dictionary<object, string> _ids;

    private DocumentationIndex(ImmutableDictionary<string, LanguageDocumentation> documentation, Dictionary<object, string> ids)
    {
        _documentation = documentation;
        _ids = ids;
    }

    public static DocumentationIndex Build(CompilationModel model, CompilationTarget target)
    {
        var ids = CreateSymbolIds(model);
        var symbolsById = ids.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
        var documentation = ImmutableDictionary.CreateBuilder<string, LanguageDocumentation>(StringComparer.Ordinal);
        LoadSidecars(model, target, symbolsById, documentation);

        var pendingInheritance = new Dictionary<string, PendingInheritance>(StringComparer.Ordinal);
        var consumedTrivia = new HashSet<(SourceText Source, int Start)>();
        foreach (var (symbol, syntax) in DocumentableSymbols(model))
        {
            if (!ids.TryGetValue(symbol, out var id))
                continue;
            var tree = model.SyntaxTrees.First(candidate => ReferenceEquals(candidate.Text, syntax.Source));
            var block = GetDocumentationBlock(tree, syntax);
            if (block.IsDefaultOrEmpty)
                continue;
            foreach (var trivia in block)
                consumedTrivia.Add((trivia.Source, trivia.Span.Start));
            var span = TextSpan.FromBounds(block[0].Span.Start, block[^1].Span.End);
            var parsed = ParseSourceBlock(model, symbol, tree, block, span);
            if (parsed.Inherit)
                pendingInheritance[id] = new PendingInheritance(symbol, syntax.Source, span);
            else if (parsed.Documentation is { IsEmpty: false } value)
                documentation[id] = value;
        }

        ResolveInheritance(model, ids, documentation, pendingInheritance);
        ReportOrphans(model, consumedTrivia);
        return new DocumentationIndex(documentation.ToImmutable(), ids);
    }

    public string? GetId(object symbol) => _ids.GetValueOrDefault(symbol);

    public LanguageDocumentation? GetDocumentation(object symbol) =>
        GetId(symbol) is { } id ? GetDocumentation(id) : null;

    public LanguageDocumentation? GetDocumentation(string id) => _documentation.GetValueOrDefault(id);

    private static Dictionary<object, string> CreateSymbolIds(CompilationModel model)
    {
        var result = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        foreach (var type in model.Types.Values.Where(type => !type.IsOpenConstructed).Distinct())
        {
            result[type] = $"T:{type.FullName}";
            foreach (var field in type.Fields.Where(field => field.Syntax is FieldDeclarationSyntax))
                result[field] = $"F:{type.FullName}.{field.Name}";
            foreach (var property in type.Properties)
                result[property] = $"P:{type.FullName}.{property.Name}";
            foreach (var method in type.Constructors.Concat(type.Methods))
                result[method] = MethodId(method);
            foreach (var value in type.EnumValues)
                result[value] = $"E:{type.FullName}.{value.Name}";
        }
        return result;
    }

    private static string MethodId(MethodSymbol method)
    {
        var name = method.IsConstructor
            ? "#ctor"
            : method.IsOperator
                ? $"op_{OperatorFacts.MetadataName(method.OperatorKind, method.Parameters.Length)}"
                : method.Name;
        if (method.IsGenericDefinition)
            name += $"``{method.TypeParameters.Length}";
        else if (!method.TypeArguments.IsDefaultOrEmpty)
            name += $"<{string.Join(",", method.TypeArguments.Select(argument => argument.DisplayName))}>";
        return $"M:{method.ContainingType.FullName}.{name}({string.Join(",", method.Parameters.Select(ParameterId))})";
    }

    private static string ParameterId(ParameterSymbol parameter) =>
        PassingPrefix(parameter.PassingKind) + parameter.Type.DisplayName;

    private static string PassingPrefix(ParameterPassingKind kind) => kind switch
    {
        ParameterPassingKind.Ref => "ref ",
        ParameterPassingKind.In => "in ",
        ParameterPassingKind.Out => "out ",
        _ => string.Empty,
    };

    private static IEnumerable<(object Symbol, SyntaxNode Syntax)> DocumentableSymbols(CompilationModel model)
    {
        foreach (var type in model.Types.Values.Where(type => type.Syntax is not null && !type.IsOpenConstructed).Distinct())
        {
            yield return (type, type.Syntax!);
            foreach (var field in type.Fields.Where(field => field.Syntax is FieldDeclarationSyntax))
                yield return (field, field.Syntax!);
            foreach (var property in type.Properties)
                yield return (property, property.Syntax!);
            foreach (var method in type.Constructors.Concat(type.Methods).Where(method => method.Syntax is not null))
                yield return (method, method.Syntax!);
            foreach (var value in type.EnumValues)
                yield return (value, value.Syntax);
        }
    }

    private static ImmutableArray<SyntaxTrivia> GetDocumentationBlock(SyntaxTree tree, SyntaxNode syntax)
    {
        var token = tree.Tokens.FirstOrDefault(candidate => candidate.Span.Start >= syntax.Span.Start && candidate.Span.End <= syntax.Span.End);
        if (token is null)
            return [];
        var trivia = tree.Tokens
            .SelectMany(candidate => candidate.LeadingTrivia.Concat(candidate.TrailingTrivia))
            .Where(item => item.Span.End <= token.Span.Start)
            .DistinctBy(item => (item.Span.Start, item.Span.Length, item.Kind))
            .OrderBy(item => item.Span.Start)
            .ToImmutableArray();
        if (trivia.IsDefaultOrEmpty)
            return [];
        var index = trivia.Length - 1;
        SkipWhitespace();
        if (!ConsumeSingleEndOfLine())
            return [];
        SkipWhitespace();
        if (index < 0 || trivia[index].Kind != SyntaxTriviaKind.DocumentationComment)
            return [];

        var result = ImmutableArray.CreateBuilder<SyntaxTrivia>();
        while (index >= 0 && trivia[index].Kind == SyntaxTriviaKind.DocumentationComment)
        {
            result.Add(trivia[index--]);
            SkipWhitespace();
            if (!ConsumeSingleEndOfLine())
                break;
            SkipWhitespace();
        }
        return result.ToImmutable().Reverse().ToImmutableArray();

        void SkipWhitespace()
        {
            while (index >= 0 && trivia[index].Kind == SyntaxTriviaKind.Whitespace)
                index--;
        }

        bool ConsumeSingleEndOfLine()
        {
            if (index < 0 || trivia[index].Kind != SyntaxTriviaKind.EndOfLine)
                return false;
            index--;
            return index < 0 || trivia[index].Kind != SyntaxTriviaKind.EndOfLine;
        }
    }

    private static ParsedDocumentation ParseSourceBlock(
        CompilationModel model,
        object symbol,
        SyntaxTree tree,
        ImmutableArray<SyntaxTrivia> block,
        TextSpan span)
    {
        var content = string.Join('\n', block.Select(trivia =>
        {
            var text = trivia.Text[3..];
            return text.StartsWith(' ') ? text[1..] : text;
        }));
        XDocument document;
        try
        {
            document = ParseXml($"<doc>{content}</doc>");
        }
        catch (XmlException exception)
        {
            model.Diagnostics.AddWarning("CT5000", $"Malformed XML documentation: {exception.Message}", block[0].Source, span);
            return default;
        }

        var root = document.Root!;
        var inherit = root.Elements().Where(element => element.Name.LocalName == "inheritdoc").ToArray();
        if (inherit.Length != 0)
        {
            var soleElement = root.Elements().Count() == 1 && root.Nodes().OfType<XText>().All(text => string.IsNullOrWhiteSpace(text.Value));
            var valid = inherit.Length == 1 && soleElement && !inherit[0].HasAttributes && !inherit[0].Nodes().Any();
            if (!valid)
            {
                model.Diagnostics.AddWarning("CT5001", "inheritdoc must be the sole empty documentation element.", block[0].Source, span);
                return default;
            }
            return new ParsedDocumentation(null, true);
        }

        return new ParsedDocumentation(ParseElements(model, symbol, tree, root, block[0].Source, span), false);
    }

    private static LanguageDocumentation ParseElements(
        CompilationModel? model,
        object? symbol,
        SyntaxTree? tree,
        XElement root,
        SourceText? source,
        TextSpan span)
    {
        string summary = string.Empty;
        string? returns = null;
        string? remarks = null;
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        var exceptions = new List<LanguageExceptionDocumentation>();
        var parameterNames = ParameterNames(symbol);
        var topText = new StringBuilder();

        foreach (var node in root.Nodes())
        {
            if (node is XText text)
            {
                if (!string.IsNullOrWhiteSpace(text.Value))
                    topText.Append(' ').Append(RenderText(model, symbol, tree, text.Parent!, source, span, [text]));
                continue;
            }
            if (node is not XElement element)
            {
                Warning("CT5001", "Only text and supported XML elements are allowed in documentation comments.");
                continue;
            }
            switch (element.Name.LocalName)
            {
                case "summary":
                    RequireNoAttributes(element);
                    AssignSingle(ref summary, RenderText(model, symbol, tree, element, source, span, element.Nodes()), "summary");
                    break;
                case "returns":
                    RequireNoAttributes(element);
                    AssignOptional(ref returns, RenderText(model, symbol, tree, element, source, span, element.Nodes()), "returns");
                    break;
                case "remarks":
                    RequireNoAttributes(element);
                    AssignOptional(ref remarks, RenderText(model, symbol, tree, element, source, span, element.Nodes()), "remarks");
                    break;
                case "param":
                    {
                        if (!HasOnlyAttribute(element, "name", out var name))
                        {
                            Warning("CT5001", "param requires exactly one name attribute.");
                            break;
                        }
                        if (!parameterNames.Contains(name, StringComparer.Ordinal))
                            Warning("CT5003", $"Documentation parameter '{name}' does not match a declared parameter.");
                        var value = RenderText(model, symbol, tree, element, source, span, element.Nodes());
                        if (!parameters.TryAdd(name, value))
                            Warning("CT5002", $"Documentation parameter '{name}' is duplicated; the first entry is used.");
                        break;
                    }
                case "exception":
                    {
                        if (!HasOnlyAttribute(element, "cref", out var cref))
                        {
                            Warning("CT5001", "exception requires exactly one cref attribute.");
                            break;
                        }
                        var resolved = ResolveCref(model, symbol, tree, cref, source, span, requireException: true);
                        exceptions.Add(new LanguageExceptionDocumentation(resolved, RenderText(model, symbol, tree, element, source, span, element.Nodes())));
                        break;
                    }
                case "see" or "paramref":
                    topText.Append(' ').Append(RenderInlineElement(model, symbol, tree, element, source, span));
                    break;
                default:
                    Warning("CT5001", $"Documentation element '{element.Name.LocalName}' is not supported.");
                    topText.Append(' ').Append(EscapeMarkdown(Normalize(element.Value)));
                    break;
            }
        }

        if (string.IsNullOrEmpty(summary) && topText.Length != 0)
            summary = Normalize(topText.ToString());
        else if (topText.Length != 0)
            Warning("CT5001", "Top-level documentation text cannot be combined with summary.");

        var orderedParameters = parameterNames
            .Where(parameters.ContainsKey)
            .Select(name => new LanguageParameterDocumentation(name, parameters[name]))
            .Concat(parameters.Where(pair => !parameterNames.Contains(pair.Key, StringComparer.Ordinal)).Select(pair => new LanguageParameterDocumentation(pair.Key, pair.Value)))
            .ToImmutableArray();
        return new LanguageDocumentation(summary, orderedParameters, returns, remarks, [.. exceptions]);

        void Warning(string code, string message)
        {
            if (model is not null && source is not null)
                model.Diagnostics.AddWarning(code, message, source, span);
        }

        void RequireNoAttributes(XElement element)
        {
            if (element.HasAttributes)
                Warning("CT5001", $"Documentation element '{element.Name.LocalName}' does not accept attributes.");
        }

        void AssignSingle(ref string target, string value, string section)
        {
            if (!string.IsNullOrEmpty(target))
                Warning("CT5002", $"Documentation section '{section}' is duplicated; the first entry is used.");
            else
                target = value;
        }

        void AssignOptional(ref string? target, string value, string section)
        {
            if (target is not null)
                Warning("CT5002", $"Documentation section '{section}' is duplicated; the first entry is used.");
            else
                target = value;
        }
    }

    private static string RenderText(
        CompilationModel? model,
        object? symbol,
        SyntaxTree? tree,
        XElement parent,
        SourceText? source,
        TextSpan span,
        IEnumerable<XNode> nodes)
    {
        var result = new StringBuilder();
        foreach (var node in nodes)
        {
            if (node is XText text)
                result.Append(EscapeMarkdown(text.Value));
            else if (node is XElement element && element.Name.LocalName is "see" or "paramref")
                result.Append(RenderInlineElement(model, symbol, tree, element, source, span));
            else if (node is XElement unsupported)
            {
                if (model is not null && source is not null)
                    model.Diagnostics.AddWarning("CT5001", $"Documentation element '{unsupported.Name.LocalName}' is not valid inside '{parent.Name.LocalName}'.", source, span);
                result.Append(EscapeMarkdown(unsupported.Value));
            }
        }
        return Normalize(result.ToString());
    }

    private static string RenderInlineElement(
        CompilationModel? model,
        object? symbol,
        SyntaxTree? tree,
        XElement element,
        SourceText? source,
        TextSpan span)
    {
        if (element.Name.LocalName == "paramref")
        {
            if (!HasOnlyAttribute(element, "name", out var name) || element.Nodes().Any())
            {
                if (model is not null && source is not null)
                    model.Diagnostics.AddWarning("CT5001", "paramref must be empty and have exactly one name attribute.", source, span);
                return string.Empty;
            }
            if (!ParameterNames(symbol).Contains(name, StringComparer.Ordinal) && model is not null && source is not null)
                model.Diagnostics.AddWarning("CT5003", $"Documentation parameter '{name}' does not match a declared parameter.", source, span);
            return $"`{name}`";
        }
        if (!HasOnlyAttribute(element, "cref", out var cref) || element.Nodes().Any())
        {
            if (model is not null && source is not null)
                model.Diagnostics.AddWarning("CT5001", "see must be empty and have exactly one cref attribute.", source, span);
            return string.Empty;
        }
        return $"`{ResolveCref(model, symbol, tree, cref, source, span, requireException: false)}`";
    }

    private static bool HasOnlyAttribute(XElement element, string name, out string value)
    {
        value = element.Attribute(name)?.Value.Trim() ?? string.Empty;
        return element.Attributes().Count() == 1 && value.Length != 0;
    }

    private static ImmutableArray<string> ParameterNames(object? symbol) => symbol switch
    {
        MethodSymbol method => [.. method.Parameters.Select(parameter => parameter.Name)],
        TypeSymbol { Kind: DeclaredTypeKind.Delegate } type => [.. type.DelegateParameters.Select(parameter => parameter.Name)],
        _ => [],
    };

    private static string ResolveCref(
        CompilationModel? model,
        object? context,
        SyntaxTree? tree,
        string cref,
        SourceText? source,
        TextSpan span,
        bool requireException)
    {
        if (model is null || tree is null)
            return cref;
        var result = TryResolveCref(model, context, tree, cref);
        if (result is null)
        {
            if (source is not null)
                model.Diagnostics.AddWarning("CT5004", $"Documentation reference '{cref}' could not be resolved unambiguously{(requireException ? " to an exception type" : string.Empty)}.", source, span);
            return cref;
        }
        if (requireException && (result.Symbol is not TypeSymbol exceptionType || !IsException(exceptionType, model)))
        {
            if (source is not null)
                model.Diagnostics.AddWarning("CT5004", $"Documentation reference '{cref}' could not be resolved unambiguously to an exception type.", source, span);
            return cref;
        }
        return result.Display;
    }

    private static CrefResult? TryResolveCref(CompilationModel model, object? context, SyntaxTree tree, string text)
    {
        text = Normalize(text);
        var parameterText = default(string);
        var open = text.LastIndexOf('(');
        if (open >= 0 && text.EndsWith(')'))
        {
            parameterText = text[(open + 1)..^1];
            text = text[..open].Trim();
        }

        var directType = ResolveTypeName(model, tree, text);
        if (directType is not null && parameterText is null)
            return new CrefResult(directType.DisplayName, directType.Symbol);

        TypeSymbol? containingType = null;
        string memberName;
        var dot = text.LastIndexOf('.');
        if (dot >= 0)
        {
            containingType = ResolveTypeName(model, tree, text[..dot])?.Symbol;
            memberName = text[(dot + 1)..];
        }
        else
        {
            containingType = context switch
            {
                TypeSymbol type => type,
                MemberSymbol member => member.ContainingType,
                EnumValueSymbol value => model.Types.Values.FirstOrDefault(type => type.EnumValues.Contains(value)),
                _ => null,
            };
            memberName = text;
        }
        if (containingType is null)
            return null;

        var members = containingType.BaseTypesAndSelf().SelectMany(type =>
            type.Methods.Cast<object>().Concat(type.Constructors).Concat(type.Properties).Concat(type.Fields.Where(field => field.Syntax is FieldDeclarationSyntax)).Concat(type.EnumValues))
            .Where(candidate => SymbolName(candidate) == memberName || candidate is MethodSymbol { IsConstructor: true } && memberName is "new" or "#ctor")
            .ToArray();
        if (parameterText is not null)
        {
            var parameters = SplitParameters(parameterText);
            members = members.OfType<MethodSymbol>().Where(method => ParametersMatch(method.Parameters, parameters)).Cast<object>().ToArray();
        }
        if (members.Length != 1)
            return null;
        return new CrefResult(CrefDisplay(members[0]), members[0]);
    }

    private static CType? ResolveTypeName(CompilationModel model, SyntaxTree tree, string name)
    {
        var type = model.ResolveType(new TypeSyntax(tree.Text, new TextSpan(0, 0), name.Trim()), tree, report: false);
        return type.IsError ? null : type;
    }

    private static bool ParametersMatch(ImmutableArray<ParameterSymbol> parameters, ImmutableArray<string> requested)
    {
        if (parameters.Length != requested.Length)
            return false;
        for (var index = 0; index < parameters.Length; index++)
        {
            var expected = ParameterId(parameters[index]);
            var actual = Normalize(requested[index]);
            if (expected == actual)
                continue;
            var shortExpected = PassingPrefix(parameters[index].PassingKind) + ShortTypeName(parameters[index].Type);
            if (shortExpected != actual)
                return false;
        }
        return true;
    }

    private static string ShortTypeName(CType type) => type.Kind switch
    {
        CTypeKind.Class or CTypeKind.Struct or CTypeKind.Enum or CTypeKind.Delegate or CTypeKind.Opaque or CTypeKind.EspError => type.Symbol!.Name,
        CTypeKind.Pointer => ShortTypeName(type.ElementType!) + "*",
        CTypeKind.Array => ShortTypeName(type.ElementType!) + "[]",
        CTypeKind.NativeBuffer => $"NativeBuffer<{ShortTypeName(type.ElementType!)}>",
        CTypeKind.ReadOnlyNativeBuffer => $"ReadOnlyNativeBuffer<{ShortTypeName(type.ElementType!)}>",
        _ => type.DisplayName,
    };

    private static ImmutableArray<string> SplitParameters(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var result = ImmutableArray.CreateBuilder<string>();
        var start = 0;
        var depth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '<')
                depth++;
            else if (text[index] == '>')
                depth--;
            else if (text[index] == ',' && depth == 0)
            {
                result.Add(text[start..index].Trim());
                start = index + 1;
            }
        }
        result.Add(text[start..].Trim());
        return result.ToImmutable();
    }

    private static string SymbolName(object symbol) => symbol switch
    {
        TypeSymbol value => value.Name,
        MemberSymbol value => value.Name,
        EnumValueSymbol value => value.Name,
        _ => string.Empty,
    };

    private static string CrefDisplay(object symbol) => symbol switch
    {
        MethodSymbol method => $"{method.ContainingType.FullName}.{(method.IsConstructor ? method.ContainingType.Name : method.Name)}({string.Join(", ", method.Parameters.Select(ParameterId))})",
        MemberSymbol member => $"{member.ContainingType.FullName}.{member.Name}",
        EnumValueSymbol value => value.Name,
        TypeSymbol type => type.FullName,
        _ => SymbolName(symbol),
    };

    private static bool IsException(TypeSymbol type, CompilationModel model) =>
        model.Types.TryGetValue("System.Exception", out var exception) && (ReferenceEquals(type, exception) || type.DerivesFrom(exception));

    private static void LoadSidecars(
        CompilationModel model,
        CompilationTarget target,
        IReadOnlyDictionary<string, object> symbolsById,
        ImmutableDictionary<string, LanguageDocumentation>.Builder documentation)
    {
        foreach (var xml in StandardLibrary.GetDocumentationXml(target))
        {
            var document = ParseXml(xml);
            foreach (var member in document.Root?.Elements("member") ?? [])
            {
                var id = member.Attribute("id")?.Value;
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidOperationException("A standard-library documentation member is missing its id.");
                symbolsById.TryGetValue(id, out var symbol);
                var tree = symbol is null ? null : TreeForSymbol(model, symbol);
                var parsed = ParseElements(null, symbol, tree, member, null, default);
                if (!parsed.IsEmpty)
                    documentation[id] = parsed;
            }
        }
    }

    private static SyntaxTree? TreeForSymbol(CompilationModel model, object symbol)
    {
        var syntax = symbol switch
        {
            TypeSymbol type => type.Syntax,
            MemberSymbol member => member.Syntax,
            EnumValueSymbol value => value.Syntax,
            _ => null,
        };
        return syntax is null ? null : model.SyntaxTrees.First(tree => ReferenceEquals(tree.Text, syntax.Source));
    }

    private static void ResolveInheritance(
        CompilationModel model,
        IReadOnlyDictionary<object, string> ids,
        ImmutableDictionary<string, LanguageDocumentation>.Builder documentation,
        IReadOnlyDictionary<string, PendingInheritance> pending)
    {
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in pending.Keys)
            Resolve(id);

        LanguageDocumentation? Resolve(string id)
        {
            if (documentation.TryGetValue(id, out var existing))
                return existing;
            if (!pending.TryGetValue(id, out var item))
                return null;
            if (!active.Add(id))
            {
                model.Diagnostics.AddWarning("CT5005", "Documentation inheritance contains a cycle.", item.Source, item.Span);
                return null;
            }
            object? target = item.Symbol switch
            {
                TypeSymbol type => type.BaseType,
                MethodSymbol method => method.OverriddenMethod,
                PropertySymbol property => property.OverriddenProperty,
                _ => null,
            };
            if (target is null || !ids.TryGetValue(target, out var targetId))
            {
                model.Diagnostics.AddWarning("CT5005", "inheritdoc requires an overridden member or base type with documentation.", item.Source, item.Span);
                active.Remove(id);
                return null;
            }
            var inherited = Resolve(targetId) ?? documentation.GetValueOrDefault(targetId);
            if (inherited is null)
                model.Diagnostics.AddWarning("CT5005", "The inherited declaration has no documentation.", item.Source, item.Span);
            else
                documentation[id] = RemapParameters(inherited, target, item.Symbol);
            active.Remove(id);
            return documentation.GetValueOrDefault(id);
        }
    }

    private static LanguageDocumentation RemapParameters(LanguageDocumentation documentation, object source, object target)
    {
        if (source is not MethodSymbol sourceMethod || target is not MethodSymbol targetMethod)
            return documentation;
        var byName = documentation.Parameters.ToDictionary(parameter => parameter.Name, StringComparer.Ordinal);
        var parameters = ImmutableArray.CreateBuilder<LanguageParameterDocumentation>();
        for (var index = 0; index < Math.Min(sourceMethod.Parameters.Length, targetMethod.Parameters.Length); index++)
            if (byName.TryGetValue(sourceMethod.Parameters[index].Name, out var value))
                parameters.Add(new LanguageParameterDocumentation(targetMethod.Parameters[index].Name, value.Text));
        return documentation with { Parameters = parameters.ToImmutable() };
    }

    private static void ReportOrphans(CompilationModel model, HashSet<(SourceText Source, int Start)> consumed)
    {
        foreach (var tree in model.SyntaxTrees)
        {
            var orphaned = tree.Tokens
                .SelectMany(token => token.LeadingTrivia.Concat(token.TrailingTrivia))
                .Where(trivia => trivia.Kind == SyntaxTriviaKind.DocumentationComment && !consumed.Contains((trivia.Source, trivia.Span.Start)))
                .OrderBy(trivia => trivia.Span.Start)
                .ToArray();
            for (var index = 0; index < orphaned.Length;)
            {
                var start = orphaned[index].Span.Start;
                var end = orphaned[index].Span.End;
                index++;
                while (index < orphaned.Length && tree.Text.Text[end..orphaned[index].Span.Start].All(char.IsWhiteSpace))
                    end = orphaned[index++].Span.End;
                model.Diagnostics.AddWarning("CT5006", "XML documentation comment is not attached to a supported declaration.", tree.Text, TextSpan.FromBounds(start, end));
            }
        }
    }

    private static XDocument ParseXml(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static string Normalize(string value) => Whitespace.Replace(value, " ").Trim();

    private static string EscapeMarkdown(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal)
        .Replace("*", "\\*", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal)
        .Replace("]", "\\]", StringComparison.Ordinal);

    private sealed record PendingInheritance(object Symbol, SourceText Source, TextSpan Span);
    private readonly record struct ParsedDocumentation(LanguageDocumentation? Documentation, bool Inherit);
    private sealed record CrefResult(string Display, object? Symbol);
}
