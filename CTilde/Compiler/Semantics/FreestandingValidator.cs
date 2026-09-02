using System.Collections.Immutable;

namespace CTilde;

internal static class FreestandingValidator
{
    public static void Validate(CompilationModel model, ImmutableArray<BoundBody>.Builder bodies, CompilationTarget target)
    {
        if (target is not (CompilationTarget.Freestanding or CompilationTarget.EspIdf))
            return;

        var allBodies = bodies.ToImmutable();
        var bodyByMethod = allBodies.GroupBy(body => body.Method).ToDictionary(group => group.Key, group => group.First());
        if (target == CompilationTarget.EspIdf)
        {
            foreach (var implementation in model.RuntimeImplementations.Values)
                if (bodyByMethod.ContainsKey(implementation))
                    ValidateBootstrapBody(model, implementation);
            RequireCompleteOverrideGroup(model, [RuntimeImplementationRole.Allocate, RuntimeImplementationRole.Free]);
            RequireCompleteOverrideGroup(model, [RuntimeImplementationRole.ConsoleWrite, RuntimeImplementationRole.ConsoleRead, RuntimeImplementationRole.ConsoleFlush]);
            RequireCompleteOverrideGroup(model, [RuntimeImplementationRole.FileOpen, RuntimeImplementationRole.FileRead, RuntimeImplementationRole.FileWrite,
                RuntimeImplementationRole.FileSeek, RuntimeImplementationRole.FileLength, RuntimeImplementationRole.FileSetLength,
                RuntimeImplementationRole.FileFlush, RuntimeImplementationRole.FileClose]);
            RequireCompleteOverrideGroup(model, [RuntimeImplementationRole.PathSeparator, RuntimeImplementationRole.PathMetadata,
                RuntimeImplementationRole.FileDelete, RuntimeImplementationRole.PathMove, RuntimeImplementationRole.DirectoryCreate,
                RuntimeImplementationRole.DirectoryDelete, RuntimeImplementationRole.DirectoryOpen, RuntimeImplementationRole.DirectoryRead,
                RuntimeImplementationRole.DirectoryClose, RuntimeImplementationRole.CurrentDirectoryGet, RuntimeImplementationRole.CurrentDirectorySet]);
            RequireCompleteOverrideGroup(model, [RuntimeImplementationRole.ThreadCreate, RuntimeImplementationRole.ThreadJoin,
                RuntimeImplementationRole.ThreadClose, RuntimeImplementationRole.ThreadSleep, RuntimeImplementationRole.ThreadYield,
                RuntimeImplementationRole.ThreadStateGet, RuntimeImplementationRole.ThreadStateSet]);
            RequireCompleteOverrideGroup(model, [RuntimeImplementationRole.MutexCreate, RuntimeImplementationRole.MutexEnter,
                RuntimeImplementationRole.MutexTryEnter, RuntimeImplementationRole.MutexExit, RuntimeImplementationRole.MutexClose,
                RuntimeImplementationRole.ThreadStateGet, RuntimeImplementationRole.ThreadStateSet]);
            return;
        }
        bool IsUserSource(SyntaxNode? syntax) => syntax is not null &&
            model.UserSyntaxTrees.Any(tree => ReferenceEquals(tree.Text, syntax.Source));
        var roots = model.UserTypes.SelectMany(type => type.Methods)
            .Where(method => IsUserSource(method.Syntax) && !method.IsNaked && (method.ExportName is not null || method.IsUsed))
            .Concat(allBodies.Where(body => body.Method.Name == "<module_init>" && IsUserSource(body.Method.Syntax)).Select(body => body.Method))
            .Distinct()
            .ToArray();
        model.FreestandingRuntimeRequired = roots.Length != 0 || model.UserTypes.SelectMany(type => type.Fields)
            .Any(field => IsUserSource(field.Syntax) && field.IsUsed && !field.IsConstInit);

        var reachable = model.Effects.ReachableMethods(roots);
        model.FreestandingHeapRequired = reachable.Any(method => (model.Effects.GetEffects(method) & EffectKind.Allocates) != 0);

        var required = ImmutableHashSet.CreateBuilder<RuntimeImplementationRole>();
        if (model.FreestandingRuntimeRequired)
            required.Add(RuntimeImplementationRole.Panic);
        if (model.FreestandingHeapRequired)
        {
            required.Add(RuntimeImplementationRole.Allocate);
            required.Add(RuntimeImplementationRole.Free);
        }

        foreach (var body in allBodies.Where(body => reachable.Contains(body.Method)))
        {
            if (body.Flow.ContainsThrow || body.Flow.ContainsExceptionRegion)
                ReportUnavailable(model, body.Method, "Exceptions and exception regions are unavailable in freestanding compilations.");
            foreach (var use in body.ExternUses)
            {
                var name = use.Method.ExternName!;
                foreach (var role in RolesForExtern(name))
                    required.Add(role);
            }
            foreach (var semantic in body.Semantics.Values)
            {
                if (semantic.Symbol is MethodSymbol { IsNaked: true } naked && naked != body.Method)
                    model.Diagnostics.Add("CT1302", "Naked methods cannot be invoked as ordinary C~ methods.", semantic.Syntax.Source, semantic.Syntax.Span);
            }
        }

        foreach (var method in reachable)
            foreach (var role in RolesForMethod(method))
                required.Add(role);
        if (required.Overlaps([RuntimeImplementationRole.FileOpen, RuntimeImplementationRole.FileRead, RuntimeImplementationRole.FileWrite,
                RuntimeImplementationRole.FileSeek, RuntimeImplementationRole.FileLength, RuntimeImplementationRole.FileSetLength,
                RuntimeImplementationRole.FileFlush, RuntimeImplementationRole.FileClose]))
        {
            required.UnionWith([RuntimeImplementationRole.FileOpen, RuntimeImplementationRole.FileRead, RuntimeImplementationRole.FileWrite,
                RuntimeImplementationRole.FileSeek, RuntimeImplementationRole.FileLength, RuntimeImplementationRole.FileSetLength,
                RuntimeImplementationRole.FileFlush, RuntimeImplementationRole.FileClose]);
        }
        if (required.Overlaps([RuntimeImplementationRole.PathSeparator, RuntimeImplementationRole.PathMetadata,
                RuntimeImplementationRole.FileDelete, RuntimeImplementationRole.PathMove, RuntimeImplementationRole.DirectoryCreate,
                RuntimeImplementationRole.DirectoryDelete, RuntimeImplementationRole.DirectoryOpen, RuntimeImplementationRole.DirectoryRead,
                RuntimeImplementationRole.DirectoryClose, RuntimeImplementationRole.CurrentDirectoryGet, RuntimeImplementationRole.CurrentDirectorySet]))
        {
            required.UnionWith([RuntimeImplementationRole.PathSeparator, RuntimeImplementationRole.PathMetadata,
                RuntimeImplementationRole.FileDelete, RuntimeImplementationRole.PathMove, RuntimeImplementationRole.DirectoryCreate,
                RuntimeImplementationRole.DirectoryDelete, RuntimeImplementationRole.DirectoryOpen, RuntimeImplementationRole.DirectoryRead,
                RuntimeImplementationRole.DirectoryClose, RuntimeImplementationRole.CurrentDirectoryGet, RuntimeImplementationRole.CurrentDirectorySet]);
        }
        if (required.Overlaps([RuntimeImplementationRole.ThreadCreate, RuntimeImplementationRole.ThreadJoin, RuntimeImplementationRole.ThreadClose,
                RuntimeImplementationRole.ThreadSleep, RuntimeImplementationRole.ThreadYield]))
        {
            required.UnionWith([RuntimeImplementationRole.ThreadCreate, RuntimeImplementationRole.ThreadJoin, RuntimeImplementationRole.ThreadClose,
                RuntimeImplementationRole.ThreadSleep, RuntimeImplementationRole.ThreadYield]);
            required.Add(RuntimeImplementationRole.ThreadStateGet);
            required.Add(RuntimeImplementationRole.ThreadStateSet);
            required.Add(RuntimeImplementationRole.Allocate);
            required.Add(RuntimeImplementationRole.Free);
        }
        if (required.Overlaps([RuntimeImplementationRole.MutexCreate, RuntimeImplementationRole.MutexEnter, RuntimeImplementationRole.MutexTryEnter,
                RuntimeImplementationRole.MutexExit, RuntimeImplementationRole.MutexClose]))
        {
            required.UnionWith([RuntimeImplementationRole.MutexCreate, RuntimeImplementationRole.MutexEnter, RuntimeImplementationRole.MutexTryEnter,
                RuntimeImplementationRole.MutexExit, RuntimeImplementationRole.MutexClose]);
            required.Add(RuntimeImplementationRole.ThreadStateGet);
            required.Add(RuntimeImplementationRole.ThreadStateSet);
            required.Add(RuntimeImplementationRole.Allocate);
            required.Add(RuntimeImplementationRole.Free);
        }
        model.RequiredRuntimeImplementations = required.ToImmutable();
        if (model.RequireRuntimeImplementations)
            foreach (var role in model.RequiredRuntimeImplementations.OrderBy(role => role))
                Require(model, role);

        foreach (var implementation in model.RuntimeImplementations.Values)
        {
            if (!bodyByMethod.TryGetValue(implementation, out var body))
                continue;
            ValidateBootstrapBody(model, implementation);
        }
    }

    private static IEnumerable<RuntimeImplementationRole> RolesForExtern(string name)
    {
        if (name.StartsWith("ct_write_", StringComparison.Ordinal))
        {
            yield return RuntimeImplementationRole.ConsoleWrite;
            if (name == "ct_write_line")
                yield return RuntimeImplementationRole.ConsoleFlush;
            yield break;
        }
        if (name is "ct_console_read" or "ct_console_read_line") { yield return RuntimeImplementationRole.ConsoleRead; yield break; }
        if (name == "ct_console_read_line_prompt")
        {
            yield return RuntimeImplementationRole.ConsoleWrite;
            yield return RuntimeImplementationRole.ConsoleRead;
            yield return RuntimeImplementationRole.ConsoleFlush;
            yield break;
        }
        if (name == "ct_environment_exit") { yield return RuntimeImplementationRole.Exit; yield break; }
        if (name == "ct_monotonic_nanoseconds") { yield return RuntimeImplementationRole.MonotonicNanoseconds; yield break; }
        if (name == "ct_host_path_separator") { yield return RuntimeImplementationRole.PathSeparator; yield break; }
        if (name.StartsWith("ct_math_", StringComparison.Ordinal))
        {
            var binary = name.Contains("min", StringComparison.Ordinal) || name.Contains("max", StringComparison.Ordinal) ||
                         name.Contains("atan2", StringComparison.Ordinal) || name.Contains("pow", StringComparison.Ordinal);
            var doublePrecision = name.EndsWith("_double", StringComparison.Ordinal);
            yield return (doublePrecision, binary) switch
            {
                (false, false) => RuntimeImplementationRole.MathFloatUnary,
                (false, true) => RuntimeImplementationRole.MathFloatBinary,
                (true, false) => RuntimeImplementationRole.MathDoubleUnary,
                _ => RuntimeImplementationRole.MathDoubleBinary,
            };
            yield break;
        }
        var role = name switch
        {
            "ct_host_file_open" or "ct_host_stream_open" => RuntimeImplementationRole.FileOpen,
            "ct_host_file_read" or "ct_host_stream_read" => RuntimeImplementationRole.FileRead,
            "ct_host_file_write_buffer" or "ct_host_file_write_string" or "ct_host_stream_write" => RuntimeImplementationRole.FileWrite,
            "ct_host_file_seek" or "ct_host_file_position" or "ct_host_stream_seek" or "ct_host_stream_position" => RuntimeImplementationRole.FileSeek,
            "ct_host_file_length" or "ct_host_stream_length" => RuntimeImplementationRole.FileLength,
            "ct_host_file_set_length" or "ct_host_stream_set_length" => RuntimeImplementationRole.FileSetLength,
            "ct_host_file_flush" or "ct_host_stream_flush" => RuntimeImplementationRole.FileFlush,
            "ct_host_file_close" or "ct_host_stream_close" => RuntimeImplementationRole.FileClose,
            "ct_host_file_exists" or "ct_host_file_metadata" or "ct_host_directory_exists" => RuntimeImplementationRole.PathMetadata,
            "ct_host_file_delete" => RuntimeImplementationRole.FileDelete,
            "ct_host_file_move" or "ct_host_directory_move" => RuntimeImplementationRole.PathMove,
            "ct_host_directory_create" => RuntimeImplementationRole.DirectoryCreate,
            "ct_host_directory_delete" => RuntimeImplementationRole.DirectoryDelete,
            "ct_host_directory_get_current" => RuntimeImplementationRole.CurrentDirectoryGet,
            "ct_host_directory_set_current" => RuntimeImplementationRole.CurrentDirectorySet,
            "ct_host_directory_entries" => RuntimeImplementationRole.DirectoryOpen,
            _ => (RuntimeImplementationRole?)null,
        };
        if (role is { } value)
            yield return value;
        if (name == "ct_host_directory_entries")
        {
            yield return RuntimeImplementationRole.DirectoryRead;
            yield return RuntimeImplementationRole.DirectoryClose;
        }
    }

    private static IEnumerable<RuntimeImplementationRole> RolesForMethod(MethodSymbol method)
    {
        if (method.ContainingType.FullName == "System.Threading.Thread")
        {
            if (method.Name == "Start") yield return RuntimeImplementationRole.ThreadCreate;
            else if (method.Name == "Join") yield return RuntimeImplementationRole.ThreadJoin;
            else if (method.Name == "Sleep") yield return RuntimeImplementationRole.ThreadSleep;
            else if (method.Name == "Yield") yield return RuntimeImplementationRole.ThreadYield;
        }
        else if (method.ContainingType.FullName == "System.Threading.Mutex")
        {
            if (method.Name == "Enter") { yield return RuntimeImplementationRole.MutexCreate; yield return RuntimeImplementationRole.MutexEnter; }
            else if (method.Name == "TryEnter") { yield return RuntimeImplementationRole.MutexCreate; yield return RuntimeImplementationRole.MutexTryEnter; }
            else if (method.Name == "Exit") yield return RuntimeImplementationRole.MutexExit;
        }
    }

    private static bool ValidateBootstrapBody(CompilationModel model, MethodSymbol method)
    {
        var invalid = (model.Effects.GetBootstrapEffects(method) &
            (EffectKind.Allocates | EffectKind.Throws | EffectKind.UsesRuntime)) != 0;
        if (invalid)
            model.Diagnostics.Add("CT2211", $"Runtime implementation '{method.ContainingType.FullName}.{method.Name}' is not bootstrap-safe.",
                method.Syntax!.Source, method.Syntax.Span);
        return !invalid;
    }

    private static void RequireCompleteOverrideGroup(CompilationModel model, RuntimeImplementationRole[] roles)
    {
        if (!roles.Any(model.RuntimeImplementations.ContainsKey) || roles.All(model.RuntimeImplementations.ContainsKey))
            return;
        var source = model.UserSyntaxTrees.FirstOrDefault()?.Text ?? SourceText.From(string.Empty);
        foreach (var role in roles.Where(role => !model.RuntimeImplementations.ContainsKey(role)))
            model.Diagnostics.Add("CT4114", $"ESP-IDF runtime override group requires one RuntimeImpl(Runtime.{role}) method.", source, new TextSpan(0, 0));
    }

    private static void Require(CompilationModel model, RuntimeImplementationRole role)
    {
        if (model.RuntimeImplementations.ContainsKey(role))
            return;
        var source = model.UserSyntaxTrees.FirstOrDefault()?.Text ?? SourceText.From(string.Empty);
        model.Diagnostics.Add("CT4114", $"Freestanding compilation requires one RuntimeImpl(Runtime.{role}) method.", source, new TextSpan(0, 0));
    }

    private static void ReportUnavailable(CompilationModel model, MethodSymbol method, string message)
    {
        var syntax = method.Syntax ?? method.ContainingType.Syntax!;
        model.Diagnostics.Add("CT4115", message, syntax.Source, syntax.Span);
    }
}
