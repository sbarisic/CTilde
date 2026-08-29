using System.Globalization;

namespace CTilde.DebugAdapter;

internal sealed class DebugControlImage
{
    internal const ulong Magic = 0x43544432;
    internal const uint InactiveSite = uint.MaxValue;

    private readonly byte[] _bytes;

    internal DebugControlImage(DebugMemoryLayout layout, ReadOnlySpan<byte> bytes)
    {
        Layout = layout;
        if (layout.Size <= 0 || bytes.Length < layout.Size)
            throw new InvalidDataException("The C~ debug-control image is shorter than its advertised layout.");
        _bytes = bytes[..layout.Size].ToArray();
    }

    internal DebugMemoryLayout Layout { get; }
    internal ReadOnlyMemory<byte> Bytes => _bytes;

    internal ulong Read(string field)
    {
        var definition = Field(field);
        ulong result = 0;
        for (var index = definition.Width - 1; index >= 0; index--)
            result = result << 8 | _bytes[definition.Offset + index];
        return result;
    }

    internal void Write(string field, ulong value)
    {
        var definition = Field(field);
        var remaining = value;
        for (var index = 0; index < definition.Width; index++)
        {
            _bytes[definition.Offset + index] = (byte)(remaining & 0xff);
            remaining >>= 8;
        }
        if (remaining != 0)
            throw new InvalidDataException($"The C~ debug-control value for '{field}' does not fit in {definition.Width} bytes.");
    }

    internal void WriteEnabledSites(int siteCount, IEnumerable<int> siteIds)
    {
        var offset = Layout.EnabledOffset ?? throw new InvalidDataException("The C~ debug-control layout has no enabled-site bitmap.");
        var words = BuildEnabledSiteWords(siteCount, siteIds);
        if (offset < 0 || offset + words.Length * sizeof(uint) > _bytes.Length)
            throw new InvalidDataException("The C~ enabled-site bitmap is outside the debug-control block.");
        Array.Clear(_bytes, offset, _bytes.Length - offset);
        for (var index = 0; index < words.Length; index++)
        {
            var value = words[index];
            for (var octet = 0; octet < sizeof(uint); octet++)
                _bytes[offset + index * sizeof(uint) + octet] = (byte)(value >> (octet * 8));
        }
    }

    internal DebugControlSnapshot Snapshot() => new(
        checked((uint)Read("CurrentReason")),
        checked((uint)Read("CurrentSite")),
        Read("CurrentThread"),
        Read("CurrentActivation"),
        checked((uint)Read("CurrentValue")),
        Read("CurrentObject"),
        Read("CurrentCode"),
        Read("CurrentFile"),
        unchecked((int)(uint)Read("CurrentLine")));

    internal void ValidateHeader(int minimumSiteCount)
    {
        if (Read("Magic") != Magic)
            throw new InvalidDataException("The C~ debug-control magic does not match the prepared debug map.");
        var siteCount = checked((int)Read("SiteCount"));
        if (siteCount < minimumSiteCount)
            throw new InvalidDataException($"The C~ runtime exposes {siteCount} logical sites but the debug map requires at least {minimumSiteCount}.");
        var enabledOffset = Layout.EnabledOffset ?? throw new InvalidDataException("The C~ debug-control layout has no enabled-site bitmap.");
        if (enabledOffset < 0 || enabledOffset + Math.Max(1, (siteCount + 31) / 32) * sizeof(uint) > _bytes.Length)
            throw new InvalidDataException($"The C~ debug-control layout cannot store its {siteCount} enabled-site bits.");
    }

    internal string ToHex() => Convert.ToHexString(_bytes).ToLowerInvariant();

    internal static byte[] FromHex(string text)
    {
        try { return Convert.FromHexString(text); }
        catch (FormatException exception) { throw new InvalidDataException("GDB returned malformed C~ debug-control memory.", exception); }
    }

    internal static uint[] BuildEnabledSiteWords(int siteCount, IEnumerable<int> siteIds)
    {
        var words = new uint[Math.Max(1, (siteCount + 31) / 32)];
        foreach (var siteId in siteIds.Distinct())
        {
            if (siteId < 0 || siteId >= siteCount)
                throw new InvalidDataException($"Invalid C~ debug site {siteId.ToString(CultureInfo.InvariantCulture)}.");
            words[siteId / 32] |= 1u << (siteId % 32);
        }
        return words;
    }

    private DebugMemoryField Field(string name)
    {
        if (!Layout.Fields.TryGetValue(name, out var field))
            throw new InvalidDataException($"The C~ debug-control layout does not define '{name}'.");
        if (field.Offset < 0 || field.Width is <= 0 or > sizeof(ulong) || field.Offset + field.Width > _bytes.Length)
            throw new InvalidDataException($"The C~ debug-control field '{name}' is outside the returned target memory.");
        return field;
    }
}

internal sealed record DebugControlSnapshot(
    uint Reason,
    uint Site,
    ulong Thread,
    ulong Activation,
    uint Value,
    ulong Object,
    ulong Code,
    ulong File,
    int Line);

internal sealed class LogicalBreakpoint
{
    internal required int Id { get; init; }
    internal required int SiteId { get; init; }
    internal required DebugFunction Function { get; init; }
    internal string? Condition { get; init; }
    internal int? HitCondition { get; init; }
    internal string? LogMessage { get; init; }
    internal bool Temporary { get; init; }
    internal int Hits { get; set; }
}

internal static class LogicalDebugModel
{
    internal static int? ParseHitCondition(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result > 0 ? result : null;

    internal static (DebugFunction Function, DebugSite Site)? FindExecutableSite(
        IEnumerable<DebugFunction> functions, string sourceRoot, string source, int line, int column)
    {
        var fullSource = Path.IsPathFullyQualified(source) ? Path.GetFullPath(source) : Path.GetFullPath(Path.Combine(sourceRoot, source));
        var function = functions.Where(candidate => candidate.Source is not null &&
            AbsoluteSource(sourceRoot, candidate.Source.File).Equals(fullSource, StringComparison.OrdinalIgnoreCase) &&
            candidate.Source.Line <= line && candidate.Sites.Any(site =>
                AbsoluteSource(sourceRoot, site.Source.File).Equals(fullSource, StringComparison.OrdinalIgnoreCase) && site.Source.Line >= line))
            .OrderByDescending(candidate => candidate.Source!.Line).FirstOrDefault();
        if (function is null) return null;
        var match = function.Sites.Where(candidate => AbsoluteSource(sourceRoot, candidate.Source.File).Equals(fullSource, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.Source.Line).ThenBy(candidate => candidate.Source.Column).FirstOrDefault(candidate => candidate.Source.Line > line ||
            candidate.Source.Line == line && candidate.Source.Column >= column);
        return match is null ? null : (function, match);
    }

    internal static DebugVariable[] LiveLocals(DebugFunction function, DebugSite? site)
    {
        var position = site?.Source.SpanStart;
        bool IsLive(DebugVariable variable) => position is null ||
            (variable.LiveStart is null || position >= variable.LiveStart) && (variable.LiveEnd is null || position < variable.LiveEnd);
        int ScopeLength(DebugVariable variable) => function.Scopes.FirstOrDefault(scope => scope.Id == variable.ScopeId)?.Source.SpanLength ?? int.MaxValue;
        var visible = new Dictionary<string, DebugVariable>(StringComparer.Ordinal);
        foreach (var variable in function.Locals.Where(IsLive).OrderByDescending(ScopeLength)) visible[variable.Name] = variable;
        return visible.Values.OrderBy(variable => variable.LiveStart ?? 0).ToArray();
    }

    private static string AbsoluteSource(string sourceRoot, string source) => Path.IsPathFullyQualified(source)
        ? Path.GetFullPath(source)
        : Path.GetFullPath(Path.Combine(sourceRoot, source));
}
