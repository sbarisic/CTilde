using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CTilde;

namespace CTilde.Cli;

internal static class ManagedOverlayPackager
{
    private static readonly byte[] DirectoryMagic = "CTOVLD3\0"u8.ToArray();
    private static readonly byte[] FooterMagic = "CTOVLF3\0"u8.ToArray();
    private const uint RelocationWindow = 1;
    private const uint RelocationResident = 2;
    private const uint RelocationResidentIndirect = 3;

    public static ManagedModuleMetadata Package(BuildRequest request, string linkedModule, string output)
    {
        var metadata = ManagedModuleMetadata.Load(request.ManagedModuleMetadataPath!);
        if (!metadata.HasOverlays)
        {
            File.Copy(linkedModule, output, overwrite: false);
            return metadata;
        }
        if (request.Architecture != CompilationArchitecture.Xtensa)
            throw new NativeBuildException("Managed overlays can be packaged only for ESP32/Xtensa.");

        var objectDirectory = Path.Combine(request.EspIdfBuildDirectory, "so_objs");
        var objectPaths = Directory.Exists(objectDirectory)
            ? Directory.EnumerateFiles(objectDirectory, "*.o", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray()
            : [];
        if (objectPaths.Length == 0)
            throw new NativeBuildException("The ESP-IDF managed-module build did not retain relocatable objects for overlay packaging.");
        var objects = objectPaths.Select(path => ElfFile.Read(path, requireRelocatable: true)).ToArray();
        var overlaySections = objects.SelectMany(file => file.Sections
                .Where(section => TryOverlayName(section.Name, out _)).Select(section => (File: file, Section: section)))
            .ToArray();
        if (overlaySections.Length == 0)
            throw new NativeBuildException("Overlay metadata was emitted, but no overlay machine-code sections were produced.");

        var tools = DiscoverTools(request.EspIdfBuildDirectory);
        var working = Path.Combine(request.EspIdfBuildDirectory, "ctilde-overlays-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(working);
        try
        {
            var linkedResident = Path.Combine(working, "resident-linked.so");
            var placementScript = Path.Combine(working, "overlay-placement.ld");
            WriteOverlayPlacementScript(placementScript, overlaySections.Select(item => item.Section.Name));
            var packagingObjects = PrepareOverlayObjects(tools.Objcopy, objects, working);
            Link(tools.Compiler, packagingObjects, linkedResident, stripAtLink: false, garbageCollect: true, placementScript);
            var linkedImage = ElfFile.Read(linkedResident, requireRelocatable: false);
            var residentImportSlots = ResidentImportSlots(linkedImage);
            var descriptorSymbol = linkedImage.NamedSymbols.SingleOrDefault(symbol =>
                symbol.Name == "ct_managed_module_v3" && symbol.SectionIndex != 0)
                ?? throw new NativeBuildException("Resident ELF does not retain the Module ABI 3 descriptor symbol.");
            var residentElf = Path.Combine(working, "resident.so");
            FilterOverlayRelocations(linkedImage, linkedResident, working, tools.Objcopy);
            var removable = linkedImage.Sections.Where(section => TryOverlayName(section.Name, out _) ||
                    section.Name is ".xt.lit" or ".xt.prop" or ".rela.xt.lit" or ".rela.xt.prop")
                .OrderByDescending(section => section.Name.StartsWith(".rela", StringComparison.Ordinal))
                .Select(section => section.Name).Distinct(StringComparer.Ordinal).ToArray();
            Run(tools.Objcopy, removable.SelectMany(name => new[] { "--remove-section", name })
                .Append(linkedResident).Append(residentElf));
            ValidateNoOverlayLoadSections(residentElf);
            Run(tools.Strip,
                ["--strip-unneeded", "--remove-section=.comment", "--remove-section=.got.loc", "--remove-section=.dynamic",
                 "--remove-section=.xt.lit", "--remove-section=.xt.prop", "--remove-section=.xtensa.info", residentElf]);
            ValidateNoOverlayLoadSections(residentElf);

            var layouts = BuildLayouts(metadata, linkedImage, residentImportSlots);
            var maximumOverlayBytes = checked((uint)layouts.Max(item => item.Payload.Length));
            PatchDescriptorMaximumOverlayBytes(residentElf, descriptorSymbol.Value,
                descriptorSymbol.Size, maximumOverlayBytes);
            WriteContainer(residentElf, output, layouts, descriptorSymbol.Value);
            BuildReporter.Current?.Detail($"Resident executable memory: {ExecutableLoadBytes(residentElf)} bytes; " +
                $"resident ELF file: {new FileInfo(residentElf).Length} bytes");
            foreach (var layout in layouts)
                BuildReporter.Current?.Detail($"Overlay '{layout.Name}': {layout.Payload.Length} bytes, " +
                    $"{layout.Relocations.Count} relocation(s), {layout.Functions.Count} function(s), alignment 16");
            BuildReporter.Current?.Detail($"Packaged managed module: {new FileInfo(output).Length} bytes");
            var updatedOverlays = layouts.Select(layout => new ManagedModuleOverlayMetadata(layout.Name,
                checked((int)layout.Payload.Length), 16, metadata.Overlays.First(item => item.Name == layout.Name).Functions)).ToImmutableArray();
            return metadata with
            {
                MaximumOverlayBytes = updatedOverlays.Max(item => item.PayloadBytes),
                Overlays = updatedOverlays,
            };
        }
        finally
        {
            try { Directory.Delete(working, recursive: true); } catch { }
        }
    }

    private static void PatchDescriptorMaximumOverlayBytes(string residentElf, uint descriptorVma,
        uint descriptorSize, uint maximumOverlayBytes)
    {
        const uint maximumOverlayBytesOffset = 112u;
        if (descriptorSize < maximumOverlayBytesOffset + sizeof(uint))
            throw new NativeBuildException("Module ABI 3 descriptor is smaller than its overlay capability fields.");
        var elf = ElfFile.Read(residentElf, requireRelocatable: false);
        var section = elf.Sections.SingleOrDefault(candidate => candidate.Type == 1 &&
            descriptorVma >= candidate.Address && descriptorVma - candidate.Address <= candidate.Size &&
            candidate.Size - (descriptorVma - candidate.Address) >= descriptorSize)
            ?? throw new NativeBuildException("Module ABI 3 descriptor does not map to resident ELF storage.");
        var fileOffset = checked((int)(section.Offset + descriptorVma - section.Address + maximumOverlayBytesOffset));
        var bytes = File.ReadAllBytes(residentElf);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fileOffset, sizeof(uint)), maximumOverlayBytes);
        File.WriteAllBytes(residentElf, bytes);
    }

    private static ImmutableArray<OverlayLayout> BuildLayouts(ManagedModuleMetadata metadata, ElfFile linkedImage,
        IReadOnlyDictionary<string, uint> residentImportSlots)
    {
        var layouts = new List<OverlayLayout>();
        foreach (var (overlay, overlayIndex) in metadata.Overlays.Select((value, index) => (value, index)))
        {
            var sections = linkedImage.Sections
                .Where(section => TryOverlayName(section.Name, out var name) && name == overlay.Name)
                .OrderBy(section => section.Address).ToArray();
            if (sections.Length == 0)
                throw new NativeBuildException($"Overlay '{overlay.Name}' has no retained linked sections.");
            if (sections.Any(section => section.Type != 1))
                throw new NativeBuildException($"Overlay '{overlay.Name}' contains a non-PROGBITS linked section.");
            var start = sections.Min(section => section.Address);
            var end = sections.Max(section => checked(section.Address + section.Size));
            var payloadBytes = new byte[checked((int)Align(end - start, 16u))];
            foreach (var section in sections)
                linkedImage.Bytes.AsSpan(checked((int)section.Offset), checked((int)section.Size))
                    .CopyTo(payloadBytes.AsSpan(checked((int)(section.Address - start))));
            var payload = new MemoryStream(payloadBytes.Length);
            payload.Write(payloadBytes);
            var linkedRanges = sections.Select(section =>
                new LinkedRange(section.Address, checked(section.Address + section.Size))).ToImmutableArray();
            layouts.Add(new OverlayLayout((uint)overlayIndex + 1u, overlay.Name, payload, [], [], start, end, linkedRanges));
        }

        foreach (var relocationSection in linkedImage.Sections.Where(section => section.Type == 4 && section.Link < linkedImage.Sections.Length))
        {
            var symbols = linkedImage.SymbolsFor(relocationSection.Link);
            foreach (var relocation in linkedImage.ReadRelocations(relocationSection))
            {
                var layout = layouts.SingleOrDefault(candidate => candidate.ContainsLinkedAddress(relocation.Offset));
                if (layout is null) continue;
                if (relocation.SymbolIndex >= symbols.Length || relocation.Type is not (1u or 4u or 5u))
                    throw new NativeBuildException($"Unsupported or malformed linked Xtensa overlay relocation {relocation.Type}.");
                var patchOffset = checked(relocation.Offset - layout.LinkedStart);
                var symbol = symbols[checked((int)relocation.SymbolIndex)];
                uint targetAddress;
                uint kind;
                var addend = relocation.Addend;
                if (symbol.SectionIndex == 0 && symbol.Name.Length != 0)
                {
                    if (!residentImportSlots.TryGetValue(symbol.Name, out targetAddress))
                        throw new NativeBuildException($"Overlay import '{symbol.Name}' has no resident relocation slot.");
                    kind = RelocationResidentIndirect;
                }
                else
                {
                    targetAddress = symbol.SectionIndex == 0
                        ? ReadPayloadU32(layout, patchOffset)
                        : checked((uint)((long)symbol.Value + addend));
                    addend = 0;
                    var targetLayout = layouts.SingleOrDefault(candidate => candidate.ContainsLinkedAddress(targetAddress));
                    if (targetLayout is not null)
                    {
                        if (targetLayout != layout)
                            throw new NativeBuildException($"Overlay '{layout.Name}' directly references overlay '{targetLayout.Name}'.");
                        kind = RelocationWindow;
                        targetAddress -= layout.LinkedStart;
                    }
                    else
                        kind = RelocationResident;
                }
                layout.Relocations.Add(new OverlayRelocation(patchOffset, kind, targetAddress, addend));
            }
        }

        foreach (var overlay in metadata.Overlays.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            var layout = layouts.Single(item => item.Name == overlay.Name);
            foreach (var function in overlay.Functions.OrderBy(item => item.Identity, StringComparer.Ordinal))
            {
                var matches = linkedImage.NamedSymbols.Where(symbol => symbol.Name == function.BodySymbol && symbol.SectionIndex != 0)
                    .DistinctBy(symbol => (symbol.SectionIndex, symbol.Value, symbol.Size)).ToArray();
                if (matches.Length != 1 || matches[0].Value < layout.LinkedStart || matches[0].Value >= layout.LinkedEnd)
                    throw new NativeBuildException($"Overlay body symbol '{function.BodySymbol}' was not uniquely retained in '{overlay.Name}'.");
                layout.Functions.Add(new OverlayFunction(checked((uint)function.TargetIndex), layout.Id,
                    matches[0].Value - layout.LinkedStart));
            }
        }
        return [.. layouts.OrderBy(layout => layout.Name, StringComparer.Ordinal)];
    }

    private static uint ReadPayloadU32(OverlayLayout layout, uint offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(layout.Payload.GetBuffer().AsSpan(checked((int)offset), sizeof(uint)));

    private static uint Align(uint value, uint alignment) => checked((value + alignment - 1u) & ~(alignment - 1u));

    private static uint ExecutableLoadBytes(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 52)
            throw new NativeBuildException("Resident overlay ELF header is truncated.");
        var programOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28, 4));
        var entrySize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(42, 2));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(44, 2));
        uint result = 0;
        for (var index = 0; index < count; ++index)
        {
            var offset = checked((int)(programOffset + (uint)index * entrySize));
            if (entrySize < 32 || offset < 0 || offset > bytes.Length - entrySize)
                throw new NativeBuildException("Resident overlay ELF program headers are malformed.");
            var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            var memorySize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 20, 4));
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 24, 4));
            if (type == 1u && (flags & 1u) != 0u)
                result = checked(result + memorySize);
        }
        return result;
    }

    private static Dictionary<string, uint> ResidentImportSlots(ElfFile linkedImage)
    {
        var result = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var relocationSection in linkedImage.Sections.Where(section => section.Type == 4 && section.Link < linkedImage.Sections.Length))
        {
            var symbols = linkedImage.SymbolsFor(relocationSection.Link);
            foreach (var relocation in linkedImage.ReadRelocations(relocationSection))
            {
                if (relocation.SymbolIndex >= symbols.Length || IsOverlayAddress(linkedImage, relocation.Offset)) continue;
                var symbol = symbols[checked((int)relocation.SymbolIndex)];
                if (symbol.SectionIndex == 0 && symbol.Name.Length != 0)
                    result.TryAdd(symbol.Name, relocation.Offset);
            }
        }
        return result;
    }

    private static void FilterOverlayRelocations(ElfFile linkedImage, string linkedResident,
        string workingDirectory, string objcopy)
    {
        foreach (var section in linkedImage.Sections.Where(section => section.Type == 4 && section.EntrySize == 12u))
        {
            using var filtered = new MemoryStream();
            for (uint offset = 0; offset + 12u <= section.Size; offset += 12u)
            {
                var source = checked((int)(section.Offset + offset));
                var target = BinaryPrimitives.ReadUInt32LittleEndian(linkedImage.Bytes.AsSpan(source, 4));
                if (!IsOverlayAddress(linkedImage, target))
                    filtered.Write(linkedImage.Bytes, source, 12);
            }
            if (filtered.Length == section.Size) continue;
            var replacement = Path.Combine(workingDirectory, "relocations-" + section.Index + ".bin");
            File.WriteAllBytes(replacement, filtered.ToArray());
            Run(objcopy, [$"--update-section={section.Name}={replacement}", linkedResident]);
        }
    }

    private static bool IsOverlayAddress(ElfFile image, uint address) => image.Sections.Any(section =>
        TryOverlayName(section.Name, out _) && address >= section.Address && address - section.Address < section.Size);

    private static void WriteContainer(string residentElf, string output, ImmutableArray<OverlayLayout> layouts, uint descriptorVma)
    {
        var resident = File.ReadAllBytes(residentElf);
        using var result = new MemoryStream();
        result.Write(resident);
        Pad(result, 16);
        foreach (var layout in layouts)
        {
            layout.FileOffset = checked((uint)result.Position);
            result.Write(layout.Payload.GetBuffer(), 0, checked((int)layout.Payload.Length));
        }
        var directoryOffset = checked((uint)result.Position);
        using var directory = new MemoryStream();
        directory.Write(DirectoryMagic);
        WriteU32(directory, 3);
        WriteU32(directory, checked((uint)layouts.Length));
        WriteU32(directory, checked((uint)layouts.Sum(item => item.Functions.Count)));
        WriteU32(directory, checked((uint)layouts.Sum(item => item.Relocations.Count)));
        WriteU32(directory, checked((uint)layouts.Max(item => item.Payload.Length)));
        WriteU32(directory, checked((uint)resident.Length));
        WriteU32(directory, descriptorVma);
        var relocationStart = 0u;
        var functionStart = 0u;
        foreach (var layout in layouts)
        {
            WriteU32(directory, layout.Id);
            WriteU32(directory, layout.FileOffset);
            WriteU32(directory, checked((uint)layout.Payload.Length));
            WriteU32(directory, checked((uint)layout.Payload.Length));
            WriteU32(directory, 16);
            WriteU32(directory, relocationStart);
            WriteU32(directory, checked((uint)layout.Relocations.Count));
            WriteU32(directory, functionStart);
            WriteU32(directory, checked((uint)layout.Functions.Count));
            WriteFixedAscii(directory, layout.Name, 32);
            directory.Write(SHA256.HashData(layout.Payload.GetBuffer().AsSpan(0, checked((int)layout.Payload.Length))));
            relocationStart += checked((uint)layout.Relocations.Count);
            functionStart += checked((uint)layout.Functions.Count);
        }
        foreach (var layout in layouts)
            foreach (var function in layout.Functions)
            {
                WriteU32(directory, function.TargetIndex);
                WriteU32(directory, function.OverlayId);
                WriteU32(directory, function.BodyOffset);
            }
        foreach (var layout in layouts)
            foreach (var relocation in layout.Relocations)
            {
                WriteU32(directory, relocation.Offset);
                WriteU32(directory, relocation.Kind);
                WriteU32(directory, relocation.Target);
                WriteI32(directory, relocation.Addend);
            }
        result.Write(directory.GetBuffer(), 0, checked((int)directory.Length));
        result.Write(FooterMagic);
        WriteU32(result, directoryOffset);
        WriteU32(result, checked((uint)directory.Length));
        WriteU32(result, checked((uint)resident.Length));
        WriteU32(result, checked((uint)layouts.Length));
        File.WriteAllBytes(output, result.ToArray());
    }

    private static Toolset DiscoverTools(string buildDirectory)
    {
        var cache = Path.Combine(buildDirectory, "CMakeCache.txt");
        var compiler = ReadCache(cache, "CMAKE_C_COMPILER");
        if (compiler is null && ReadCache(cache, "CMAKE_C_COMPILER_AR") is { } archiveTool)
            compiler = archiveTool.Replace("-gcc-ar.exe", "-gcc.exe", StringComparison.OrdinalIgnoreCase)
                .Replace("-gcc-ar", "-gcc", StringComparison.OrdinalIgnoreCase);
        if (compiler is null)
            throw new NativeBuildException("Could not locate the Xtensa compiler from CMakeCache.txt.");
        var directory = Path.GetDirectoryName(compiler)!;
        return new Toolset(compiler, Path.Combine(directory, "xtensa-esp32-elf-objcopy.exe"),
            Path.Combine(directory, "xtensa-esp32-elf-strip.exe"));
    }

    private static void WriteOverlayPlacementScript(string path, IEnumerable<string> sectionNames)
    {
        var writer = new StringBuilder("SECTIONS\n{\n");
        foreach (var section in sectionNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (section.Contains('"'))
                throw new NativeBuildException($"Overlay section '{section}' cannot be represented in the linker placement script.");
            writer.Append("  \"").Append(section).Append("\" : { KEEP(*(\"").Append(section).Append("\")) }\n");
        }
        writer.Append("}\nINSERT AFTER .bss;\n");
        File.WriteAllText(path, writer.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string[] PrepareOverlayObjects(string objcopy, IEnumerable<ElfFile> objects, string workingDirectory)
    {
        var result = new List<string>();
        foreach (var (source, index) in objects.Select((value, index) => (value, index)))
        {
            var destination = Path.Combine(workingDirectory, $"object-{index:D4}.o");
            File.Copy(source.Path, destination, overwrite: false);
            foreach (var section in source.Sections.Where(section => TryOverlayName(section.Name, out _)))
                Run(objcopy, ["--set-section-flags", $"{section.Name}=alloc,load,readonly,data,contents", destination]);
            result.Add(destination);
        }
        return [.. result];
    }

    private static string? ReadCache(string path, string name)
    {
        foreach (var line in File.ReadLines(path))
            if (line.StartsWith(name + ":", StringComparison.Ordinal) && line.IndexOf('=') is var separator && separator >= 0)
                return line[(separator + 1)..];
        return null;
    }

    private static void Link(string compiler, IEnumerable<string> objects, string output, bool stripAtLink,
        bool garbageCollect = false, string? linkerScript = null)
    {
        var arguments = new List<string> { "-shared", "-fPIC", "-static-libgcc", "-nostdlib", "-nostartfiles", "-fdata-sections", "-ffunction-sections", "-fvisibility=hidden" };
        if (garbageCollect)
            arguments.Add("-Wl,--gc-sections");
        if (stripAtLink)
            arguments.AddRange(["-Wl,--strip-all", "-Wl,--strip-debug", "-Wl,--strip-discarded"]);
        if (linkerScript is not null)
            arguments.Add($"-Wl,-T,{linkerScript}");
        arguments.AddRange(["-o", output]);
        arguments.AddRange(objects);
        arguments.Add("-Wl,--allow-shlib-undefined");
        Run(compiler, arguments);
    }

    private static void Run(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new NativeBuildException($"Could not start '{executable}'.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new NativeBuildException($"Overlay packaging tool '{Path.GetFileName(executable)}' failed: {(error + output).Trim()}");
    }

    private static void ValidateNoOverlayLoadSections(string path)
    {
        var elf = ElfFile.Read(path, requireRelocatable: false);
        if (elf.Sections.Any(section => TryOverlayName(section.Name, out _)))
            throw new NativeBuildException("Resident ELF still contains overlay sections after extraction.");
    }

    private static bool TryOverlayName(string section, out string name)
    {
        const string prefix = ".ctilde.overlay.";
        name = string.Empty;
        if (!section.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var suffix = section.AsSpan(prefix.Length);
        var separator = suffix.IndexOf('.');
        if (separator <= 0) return false;
        name = suffix[..separator].ToString();
        return ManagedModuleMetadata.IsOverlayName(name);
    }

    private static void Pad(Stream stream, uint alignment)
    {
        while ((stream.Position & (alignment - 1)) != 0) stream.WriteByte(0);
    }
    private static void WriteU32(Stream stream, uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); stream.Write(bytes); }
    private static void WriteI32(Stream stream, int value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); stream.Write(bytes); }
    private static void WriteFixedAscii(Stream stream, string value, int capacity) { var bytes = Encoding.ASCII.GetBytes(value); stream.Write(bytes); for (var i = bytes.Length; i < capacity; i++) stream.WriteByte(0); }

    private sealed record Toolset(string Compiler, string Objcopy, string Strip);
    private sealed class OverlayLayout(uint id, string name, MemoryStream payload, List<OverlayFunction> functions,
        List<OverlayRelocation> relocations, uint linkedStart, uint linkedEnd, ImmutableArray<LinkedRange> linkedRanges)
    {
        public uint Id { get; } = id;
        public string Name { get; } = name;
        public MemoryStream Payload { get; } = payload;
        public List<OverlayFunction> Functions { get; } = functions;
        public List<OverlayRelocation> Relocations { get; } = relocations;
        public uint LinkedStart { get; } = linkedStart;
        public uint LinkedEnd { get; } = linkedEnd;
        public uint FileOffset { get; set; }
        public bool ContainsLinkedAddress(uint address) => linkedRanges.Any(range => address >= range.Start && address < range.End);
    }
    private sealed record LinkedRange(uint Start, uint End);
    private sealed record OverlayFunction(uint TargetIndex, uint OverlayId, uint BodyOffset);
    private sealed record OverlayRelocation(uint Offset, uint Kind, uint Target, int Addend);

    private sealed class ElfFile
    {
        private ElfFile(string path, byte[] bytes, ImmutableArray<ElfSection> sections, ImmutableArray<ElfSymbol> symbols)
        { Path = path; Bytes = bytes; Sections = sections; NamedSymbols = symbols; }
        public string Path { get; }
        public byte[] Bytes { get; }
        public ImmutableArray<ElfSection> Sections { get; }
        public ImmutableArray<ElfSymbol> NamedSymbols { get; }

        public static ElfFile Read(string path, bool requireRelocatable)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 52 || !bytes.AsSpan(0, 6).SequenceEqual(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 1, 1 }))
                throw new NativeBuildException($"'{path}' is not a 32-bit little-endian ELF file.");
            var type = U16(bytes, 16);
            if (requireRelocatable && type != 1) throw new NativeBuildException($"'{path}' is not an ELF relocatable object.");
            var sectionOffset = U32(bytes, 32);
            var entrySize = U16(bytes, 46);
            var count = U16(bytes, 48);
            var namesIndex = U16(bytes, 50);
            if (entrySize < 40 || sectionOffset + (uint)entrySize * count > bytes.Length || namesIndex >= count)
                throw new NativeBuildException($"'{path}' has a malformed ELF section table.");
            var raw = new (uint Name, uint Type, uint Flags, uint Address, uint Offset, uint Size, uint Link, uint Info, uint Alignment, uint EntrySize)[count];
            for (var i = 0; i < count; i++)
            {
                var p = checked((int)(sectionOffset + (uint)i * entrySize));
                raw[i] = (U32(bytes, p), U32(bytes, p + 4), U32(bytes, p + 8), U32(bytes, p + 12), U32(bytes, p + 16), U32(bytes, p + 20), U32(bytes, p + 24), U32(bytes, p + 28), U32(bytes, p + 32), U32(bytes, p + 36));
            }
            var names = raw[namesIndex];
            var sections = raw.Select((item, index) => new ElfSection(index, ReadString(bytes, names.Offset, names.Size, item.Name), item.Type, item.Flags, item.Address, item.Offset, item.Size, item.Link, item.Info, item.Alignment == 0 ? 1 : item.Alignment, item.EntrySize)).ToImmutableArray();
            var allSymbols = ImmutableArray.CreateBuilder<ElfSymbol>();
            foreach (var table in sections.Where(section => section.Type is 2 or 11 && section.EntrySize >= 16 && section.Link < sections.Length))
                allSymbols.AddRange(ReadSymbols(bytes, table, sections[checked((int)table.Link)]));
            return new ElfFile(path, bytes, sections, allSymbols.ToImmutable());
        }

        public ImmutableArray<ElfSymbol> SymbolsFor(uint sectionIndex)
        {
            if (sectionIndex >= Sections.Length) return [];
            var table = Sections[checked((int)sectionIndex)];
            return table.Type is 2 or 11 && table.Link < Sections.Length ? ReadSymbols(Bytes, table, Sections[checked((int)table.Link)]) : [];
        }

        public IEnumerable<ElfRelocation> ReadRelocations(ElfSection section)
        {
            for (uint offset = 0; offset + 12 <= section.Size; offset += 12)
            {
                var position = checked((int)(section.Offset + offset));
                var info = U32(Bytes, position + 4);
                yield return new ElfRelocation(U32(Bytes, position), info >> 8, info & 0xff, I32(Bytes, position + 8));
            }
        }

        private static ImmutableArray<ElfSymbol> ReadSymbols(byte[] bytes, ElfSection table, ElfSection strings)
        {
            var result = ImmutableArray.CreateBuilder<ElfSymbol>();
            for (uint offset = 0; offset + 16 <= table.Size; offset += table.EntrySize)
            {
                var position = checked((int)(table.Offset + offset));
                result.Add(new ElfSymbol(ReadString(bytes, strings.Offset, strings.Size, U32(bytes, position)),
                    U32(bytes, position + 4), U32(bytes, position + 8), bytes[position + 12], U16(bytes, position + 14)));
            }
            return result.ToImmutable();
        }

        private static string ReadString(byte[] bytes, uint tableOffset, uint tableSize, uint offset)
        {
            if (offset >= tableSize || tableOffset + offset >= bytes.Length) return string.Empty;
            var start = checked((int)(tableOffset + offset));
            var end = start;
            var limit = checked((int)Math.Min((long)bytes.Length, (long)tableOffset + tableSize));
            while (end < limit && bytes[end] != 0) end++;
            return Encoding.ASCII.GetString(bytes, start, end - start);
        }
        private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        private static int I32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
    }

    private sealed record ElfSection(int Index, string Name, uint Type, uint Flags, uint Address, uint Offset, uint Size, uint Link, uint Info, uint Alignment, uint EntrySize);
    private sealed record ElfSymbol(string Name, uint Value, uint Size, byte Info, int SectionIndex);
    private sealed record ElfRelocation(uint Offset, uint SymbolIndex, uint Type, int Addend);
}
