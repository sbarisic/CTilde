using System.Text.Json;
using CTilde;

namespace CTilde.Cli;

internal sealed record StackUsageAnalysisResult(bool ContractFailure, IReadOnlyList<string> Messages);

internal static class StackUsageReporter
{
    private sealed class NativeFunction
    {
        public required string Name { get; init; }
        public long? FrameBytes { get; set; }
        public string Qualifier { get; set; } = "unknown";
        public bool HasCallgraphNode { get; set; }
        public HashSet<string> Callees { get; } = new(StringComparer.Ordinal);
    }

    private sealed record Symbol(string Name, string Identity, string Kind, string? NativeSymbol, string? Export,
        bool EntryPoint, bool Used, bool AssemblyFunction, uint? TaskStackBytes, uint? StackUsageBytes, JsonElement? Source);

    private sealed record Bound(long KnownLowerBoundBytes, long? WorstCaseBytes, bool Complete,
        IReadOnlyList<string> Path, IReadOnlyList<string> UnknownBoundaries);

    public static StackUsageAnalysisResult Analyze(BuildRequest request, NativeBuildOutcome native)
    {
        var functions = new Dictionary<string, NativeFunction>(StringComparer.Ordinal);
        foreach (var path in native.StackUsageFiles ?? [])
        {
            if (path.EndsWith(".su", StringComparison.OrdinalIgnoreCase))
                ParseStackUsage(path, functions);
            else if (path.EndsWith(".ci", StringComparison.OrdinalIgnoreCase))
                ParseCallgraph(path, functions);
        }

        using var symbolDocument = JsonDocument.Parse(File.ReadAllText(request.SymbolMapPath!));
        var symbols = symbolDocument.RootElement.GetProperty("symbols").EnumerateArray()
            .Select(ParseSymbol).Where(symbol => symbol is not null).Cast<Symbol>().ToArray();
        var symbolsByName = symbols.GroupBy(symbol => symbol.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var trusted = symbols.Where(symbol => (symbol.Kind is "extern" or "nativeImport" || symbol.AssemblyFunction) && symbol.StackUsageBytes is not null)
            .ToDictionary(symbol => symbol.Kind == "extern" ? symbol.NativeSymbol ?? symbol.Name : symbol.Name,
                symbol => (long)symbol.StackUsageBytes!.Value, StringComparer.Ordinal);

        var roots = new List<(string NativeName, Symbol Symbol, string Kind)>();
        foreach (var symbol in symbols.Where(symbol => symbol.Kind is "method" or "getter" or "setter"))
        {
            if (symbol.EntryPoint)
                roots.Add((request.Target == CompilationTarget.EspIdf ? "app_main" : "main", symbol, "entryPoint"));
            if (symbol.Export is not null)
                roots.Add((symbol.Export, symbol, symbol.TaskStackBytes is null ? "export" : "taskEntry"));
            if (symbol.Used)
                roots.Add((symbol.Name, symbol, "used"));
            if (symbol.StackUsageBytes is not null)
                roots.Add((symbol.Name, symbol, "contract"));
        }
        roots = roots.DistinctBy(root => (root.NativeName, root.Kind, root.Symbol.Identity)).
            OrderBy(root => root.NativeName, StringComparer.Ordinal).ThenBy(root => root.Kind, StringComparer.Ordinal).ToList();

        var messages = new List<string>();
        var rootReports = new List<Dictionary<string, object?>>();
        foreach (var root in roots)
        {
            var bound = ComputeBound(root.NativeName, functions, trusted, new HashSet<string>(StringComparer.Ordinal));
            var declared = root.Kind == "contract" ? root.Symbol.StackUsageBytes : null;
            string status;
            if (declared is not null)
            {
                if (!bound.Complete)
                {
                    status = "unverified";
                    messages.Add($"CT2226: StackUsage({declared.Value}) for '{root.Symbol.Identity}' could not be verified because the native call graph is incomplete.");
                }
                else if (bound.WorstCaseBytes > declared.Value)
                {
                    status = "exceeded";
                    messages.Add($"CT2226: StackUsage({declared.Value}) for '{root.Symbol.Identity}' is smaller than the computed {bound.WorstCaseBytes} byte bound.");
                }
                else
                    status = "verified";
            }
            else if (root.Symbol.TaskStackBytes is not null && bound.Complete && bound.WorstCaseBytes > root.Symbol.TaskStackBytes.Value)
            {
                status = "exceeded";
                messages.Add($"CT2226: TaskEntry '{root.Symbol.Identity}' requires {bound.WorstCaseBytes} bytes but StackSize is {root.Symbol.TaskStackBytes.Value} bytes.");
            }
            else
                status = bound.Complete ? "verified" : "unverified";

            rootReports.Add(new Dictionary<string, object?>
            {
                ["nativeName"] = root.NativeName,
                ["identity"] = root.Symbol.Identity,
                ["kind"] = root.Kind,
                ["configuredTaskStackBytes"] = root.Symbol.TaskStackBytes,
                ["declaredStackUsageBytes"] = declared,
                ["knownLowerBoundBytes"] = bound.KnownLowerBoundBytes,
                ["worstCaseBytes"] = bound.WorstCaseBytes,
                ["headroomBytes"] = bound.Complete && root.Symbol.TaskStackBytes is not null
                    ? (long)root.Symbol.TaskStackBytes.Value - bound.WorstCaseBytes : null,
                ["complete"] = bound.Complete,
                ["status"] = status,
                ["worstCasePath"] = bound.Path,
                ["unknownBoundaries"] = bound.UnknownBoundaries,
            });
        }

        var functionReports = functions.Values.OrderBy(function => function.Name, StringComparer.Ordinal)
            .Select(function => new Dictionary<string, object?>
            {
                ["name"] = function.Name,
                ["identity"] = symbolsByName.GetValueOrDefault(function.Name)?.Identity,
                ["frameBytes"] = function.FrameBytes,
                ["qualifier"] = function.Qualifier,
                ["callgraphNode"] = function.HasCallgraphNode,
                ["directCallees"] = function.Callees.Order(StringComparer.Ordinal).ToArray(),
                ["declaredStackUsageBytes"] = symbolsByName.GetValueOrDefault(function.Name)?.StackUsageBytes,
            }).ToArray();
        var report = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["generator"] = $"C~ draft {CompilerContract.DraftVersion}",
            ["runtimeAbi"] = CompilerContract.RuntimeAbiVersion,
            ["target"] = request.Target.ToString(),
            ["architecture"] = request.Architecture.ToString(),
            ["compiler"] = new Dictionary<string, object?>
            {
                ["family"] = "gcc",
                ["backend"] = native.Backend,
                ["command"] = native.CompilerCommand,
                ["wslCompiler"] = native.WslCompiler,
                ["lto"] = request.Lto,
                ["instrumentationFlags"] = new[] { "-fstack-usage", "-fcallgraph-info=su" },
            },
            ["complete"] = rootReports.All(root => (bool)root["complete"]!),
            ["roots"] = rootReports,
            ["functions"] = functionReports,
        };
        AtomicFile.WriteTextIfChanged(request.StackReportPath!, JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        return new StackUsageAnalysisResult(messages.Count != 0, messages);
    }

    private static Symbol? ParseSymbol(JsonElement element)
    {
        var kind = element.GetProperty("kind").GetString()!;
        if (kind is not ("method" or "getter" or "setter" or "extern" or "nativeImport"))
            return null;
        return new Symbol(element.GetProperty("name").GetString()!, element.GetProperty("identity").GetString()!, kind,
            OptionalString(element, "nativeSymbol"), OptionalString(element, "export"), OptionalBoolean(element, "entryPoint"), OptionalBoolean(element, "used"),
            OptionalBoolean(element, "assemblyFunction"),
            OptionalUInt(element, "taskStackBytes"), OptionalUInt(element, "stackUsageBytes"),
            element.TryGetProperty("source", out var source) && source.ValueKind != JsonValueKind.Null ? source.Clone() : null);
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool OptionalBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static uint? OptionalUInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetUInt32() : null;

    private static void ParseStackUsage(string path, Dictionary<string, NativeFunction> functions)
    {
        foreach (var line in File.ReadLines(path))
        {
            var fields = line.Split('\t');
            if (fields.Length < 3 || !long.TryParse(fields[1], out var bytes))
                continue;
            var locationAndName = fields[0];
            var separator = locationAndName.LastIndexOf(':');
            if (separator < 0 || separator + 1 == locationAndName.Length)
                continue;
            var name = locationAndName[(separator + 1)..];
            var function = GetFunction(functions, name);
            if (function.FrameBytes is null || bytes > function.FrameBytes)
            {
                function.FrameBytes = bytes;
                function.Qualifier = fields[2].Trim();
            }
        }
    }

    private static void ParseCallgraph(string path, Dictionary<string, NativeFunction> functions)
    {
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("node:", StringComparison.Ordinal))
            {
                var name = Attribute(trimmed, "title");
                if (name is not null)
                    GetFunction(functions, NormalizeNativeName(name)).HasCallgraphNode = true;
            }
            else if (trimmed.StartsWith("edge:", StringComparison.Ordinal))
            {
                var source = Attribute(trimmed, "sourcename");
                var target = Attribute(trimmed, "targetname");
                if (source is not null && target is not null)
                    GetFunction(functions, NormalizeNativeName(source)).Callees.Add(NormalizeNativeName(target));
            }
        }
    }

    private static string? Attribute(string line, string name)
    {
        var marker = name + ": \"";
        var start = line.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += marker.Length;
        var end = line.IndexOf('"', start);
        return end < 0 ? null : line[start..end];
    }

    private static string NormalizeNativeName(string name)
    {
        var separator = name.LastIndexOf(':');
        return separator >= 0 && separator + 1 < name.Length ? name[(separator + 1)..] : name;
    }

    private static NativeFunction GetFunction(Dictionary<string, NativeFunction> functions, string name)
    {
        if (!functions.TryGetValue(name, out var function))
            functions[name] = function = new NativeFunction { Name = name };
        return function;
    }

    private static Bound ComputeBound(string name, IReadOnlyDictionary<string, NativeFunction> functions,
        IReadOnlyDictionary<string, long> trusted, HashSet<string> visiting)
    {
        if (trusted.TryGetValue(name, out var trustedBytes))
            return new Bound(trustedBytes, trustedBytes, true, [name], []);
        if (!functions.TryGetValue(name, out var function) || function.FrameBytes is null)
            return new Bound(0, null, false, [name], [$"{name}:missing-frame"]);
        if (!visiting.Add(name))
            return new Bound(function.FrameBytes.Value, null, false, [name], [$"{name}:recursive-cycle"]);
        var complete = function.HasCallgraphNode && (!function.Qualifier.Contains("dynamic", StringComparison.OrdinalIgnoreCase) ||
            function.Qualifier.Contains("bounded", StringComparison.OrdinalIgnoreCase));
        var unknown = new HashSet<string>(StringComparer.Ordinal);
        if (!function.HasCallgraphNode)
            unknown.Add($"{name}:missing-callgraph");
        if (function.Qualifier.Contains("dynamic", StringComparison.OrdinalIgnoreCase) &&
            !function.Qualifier.Contains("bounded", StringComparison.OrdinalIgnoreCase))
            unknown.Add($"{name}:unbounded-dynamic-frame");
        Bound? greatest = null;
        foreach (var callee in function.Callees.Order(StringComparer.Ordinal))
        {
            var child = ComputeBound(callee, functions, trusted, visiting);
            if (!child.Complete)
                complete = false;
            foreach (var boundary in child.UnknownBoundaries)
                unknown.Add(boundary);
            if (greatest is null || child.KnownLowerBoundBytes > greatest.KnownLowerBoundBytes)
                greatest = child;
        }
        visiting.Remove(name);
        var lower = function.FrameBytes.Value + (greatest?.KnownLowerBoundBytes ?? 0);
        return new Bound(lower, complete ? lower : null, complete,
            new[] { name }.Concat(greatest?.Path ?? []).ToArray(), unknown.Order(StringComparer.Ordinal).ToArray());
    }
}
