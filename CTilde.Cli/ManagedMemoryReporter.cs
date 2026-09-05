using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using CTilde;

namespace CTilde.Cli;

/// <summary>Reports linked section costs without presenting unknown dynamic costs as zero.</summary>
internal static class ManagedMemoryReporter
{
    internal sealed record Section(string Name, ulong Bytes, bool Writable, bool Executable, uint Alignment = 1);

    internal sealed record ResidentCosts(ulong Executable, ulong Mutable, ulong Constants, ulong Padding)
    {
        public ulong Total => Executable + Mutable + Constants + Padding;
    }

    internal static ResidentCosts CalculateResidentCosts(IReadOnlyList<Section> sections)
    {
        ulong executable = 0, mutable = 0, constants = 0, padding = 0, dataSize = 0;
        foreach (var name in new[] { ".text", ".data", ".rodata", ".data.rel.ro", ".bss" })
        {
            var matches = sections.Where(section => section.Name == name).ToArray();
            if (matches.Length > 1) throw new NativeBuildException($"Duplicate retained ELF section '{name}'.");
            if (matches.Length == 0 || matches[0].Bytes == 0) continue;
            var section = matches[0];
            var alignment = section.Alignment == 0 ? 1UL : section.Alignment;
            if ((alignment & (alignment - 1)) != 0 || alignment > 4096)
                throw new NativeBuildException($"Unsupported alignment for ELF section '{name}'.");
            if (name == ".text")
            {
                executable += section.Bytes;
                padding += (4 - section.Bytes % 4) % 4;
                continue;
            }
            var gap = (alignment - dataSize % alignment) % alignment;
            padding += gap;
            dataSize += gap + section.Bytes;
            if (section.Writable) mutable += section.Bytes;
            else constants += section.Bytes;
        }
        return new ResidentCosts(executable, mutable, constants, padding);
    }

    internal static IReadOnlyList<Section> ReadSections(byte[] image)
    {
        if (image.Length < 52 || !image.AsSpan(0, 6).SequenceEqual(new byte[] { 127, 69, 76, 70, 1, 1 }))
            throw new NativeBuildException("Memory reporting requires a little-endian ELF32 module.");
        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset, 4));
        ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset, 2));
        var table = U32(32);
        var stride = U16(46);
        var count = U16(48);
        var stringsIndex = U16(50);
        if (stride < 40 || stringsIndex >= count || (ulong)table + (ulong)stride * count > (ulong)image.Length)
            throw new NativeBuildException("Invalid ELF section table in memory report input.");
        var stringsHeader = checked((int)(table + (uint)stride * stringsIndex));
        var stringsOffset = U32(stringsHeader + 16);
        var stringsSize = U32(stringsHeader + 20);
        if ((ulong)stringsOffset + stringsSize > (ulong)image.Length)
            throw new NativeBuildException("Invalid ELF section names in memory report input.");
        var result = new List<Section>();
        for (var index = 0; index < count; index++)
        {
            var header = checked((int)(table + (uint)stride * (uint)index));
            var flags = U32(header + 8);
            if ((flags & 2) == 0) continue;
            var nameOffset = U32(header);
            if (nameOffset >= stringsSize)
                throw new NativeBuildException("Invalid ELF section name offset in memory report input.");
            var nameBytes = image.AsSpan(checked((int)(stringsOffset + nameOffset)), checked((int)(stringsSize - nameOffset)));
            var end = nameBytes.IndexOf((byte)0);
            if (end < 0) throw new NativeBuildException("Unterminated ELF section name in memory report input.");
            result.Add(new Section(Encoding.UTF8.GetString(nameBytes[..end]), U32(header + 20),
                (flags & 1) != 0, (flags & 4) != 0, U32(header + 32)));
        }
        return result;
    }

    internal static void Write(BuildRequest request, string artifact, int overlayBytes)
    {
        var sections = ReadSections(File.ReadAllBytes(artifact));
        // The streamed loader retains these sections. Dynamic linking tables are
        // temporary inputs, not persistent copies of all ELF SHF_ALLOC sections.
        var costs = CalculateResidentCosts(sections);
        var executable = costs.Executable;
        var mutable = costs.Mutable;
        var constants = costs.Constants;
        var resident = costs.Total;
        var module = request.ManagedModule!;
        var failures = new List<string>();
        if (module.MemoryLimits?.ResidentRamBytes is ulong residentLimit && resident > residentLimit)
            failures.Add($"Resident linked sections require {resident} bytes, exceeding the configured {residentLimit}-byte limit.");
        if (module.MemoryLimits?.OverlayRamBytes is ulong overlayLimit && (ulong)overlayBytes > overlayLimit)
            failures.Add($"Overlay window requires {overlayBytes} bytes, exceeding the configured {overlayLimit}-byte limit.");
        var report = new
        {
            schemaVersion = 1,
            module = module.Name,
            draftVersion = CompilerContract.DraftVersion,
            sharedModule = new
            {
                executableRamBytes = executable,
                mutableDataBytes = mutable,
                ramResidentConstantsBytes = constants,
                linkedResidentBytes = resident,
                sectionPaddingBytes = costs.Padding,
                loaderMetadataBytes = (ulong?)null,
                mappedFlashBytes = 0UL,
            },
            perProcess = new
            {
                stackBytes = module.Kind == ManagedModuleKind.Application ? (uint?)module.MainTaskStackBytes : null,
                stackApplicable = module.Kind == ManagedModuleKind.Application,
                overlayRamBytes = overlayBytes,
                managedPayloadLimitBytes = module.HeapLimitBytes,
                processStateBytes = (ulong?)null,
                peakManagedAllocationBytes = (ulong?)null,
                peakNativeScratchBytes = (ulong?)null,
            },
            unknownCosts = new[] { "allocator overhead", "loader metadata", "per-process state", "dynamic allocation peaks", "native scratch peaks" },
            sections,
            staticLimitsPassed = failures.Count == 0,
            failures,
        };
        var path = Path.Combine(request.ManagedModuleOutputDirectory!, module.Name + ".memory.json");
        AtomicFile.WriteTextIfChanged(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        var stackDescription = module.Kind == ManagedModuleKind.Application ? module.MainTaskStackBytes.ToString() : "not applicable (library)";
        BuildReporter.Current?.Phase($"Memory: shared linked RAM {resident} bytes ({executable} code, {mutable} mutable, {constants} constants, {costs.Padding} padding); " +
            $"per-process stack {stackDescription}, overlay {overlayBytes}; dynamic costs unknown.");
        if (failures.Count != 0) throw new NativeBuildException(string.Join(" ", failures));
    }
}
