using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CTilde;

namespace CTilde.Cli;

internal static class HostedRuntimeFileStager
{
    private const string ReceiptFileName = "ctilde-runtime-files.json";

    public static IReadOnlyList<HostedRuntimeFile> Select(
        BuildRequest request,
        HostedOperatingSystem operatingSystem)
    {
        var configured = request.Hosted?.RuntimeFiles ?? [];
        if (configured.Length == 0)
            return [];
        var selected = configured
            .Where(file => file.OperatingSystem == operatingSystem && file.Architecture == request.Architecture)
            .OrderBy(file => file.OutputFileName, StringComparer.Ordinal)
            .ToArray();
        if (selected.Length == 0)
            throw new NativeBuildException($"No hosted runtime files match {OperatingSystemName(operatingSystem)} {ArchitectureName(request.Architecture)}.");
        return selected;
    }

    public static void Stage(BuildRequest request, IReadOnlyList<HostedRuntimeFile> selected)
    {
        if (request.ExecutablePath is null)
            throw new NativeBuildException("Hosted runtime files require a native executable output.");
        var outputDirectory = Path.GetDirectoryName(request.ExecutablePath)!;
        Directory.CreateDirectory(outputDirectory);
        if (selected.Count == 0 && !File.Exists(ReceiptPath(outputDirectory)))
            return;
        var previous = ReadReceipt(outputDirectory);
        var selectedNames = selected.Select(file => file.OutputFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var old in previous.Where(entry => !selectedNames.Contains(entry.OutputFileName)))
        {
            var destination = ResolveReceiptDestination(outputDirectory, old.OutputFileName);
            if (!File.Exists(destination))
                continue;
            if (Hash(destination).Equals(old.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(destination);
                Trace(request, $"removed stale hosted runtime file {destination}");
            }
            else
                Console.Error.WriteLine($"ctilde: Preserving modified staged runtime file '{destination}'.");
        }

        var receipt = new List<RuntimeFileReceipt>();
        foreach (var file in selected)
        {
            var destination = Path.Combine(outputDirectory, file.OutputFileName);
            var sourceHash = Hash(file.SourcePath);
            if (File.Exists(destination))
            {
                var destinationHash = Hash(destination);
                if (destinationHash.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
                {
                    Trace(request, $"reused hosted runtime file {destination}");
                    receipt.Add(new RuntimeFileReceipt(file.OutputFileName, sourceHash));
                    continue;
                }
                var owned = previous.FirstOrDefault(entry => entry.OutputFileName.Equals(file.OutputFileName, StringComparison.OrdinalIgnoreCase));
                if (owned is null || !destinationHash.Equals(owned.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new NativeBuildException($"Hosted runtime destination '{destination}' exists and was not produced by the current project build.");
            }

            var temporary = Path.Combine(outputDirectory, $".ctilde-runtime-{Guid.NewGuid():N}.tmp");
            try
            {
                File.Copy(file.SourcePath, temporary, overwrite: false);
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            Trace(request, $"staged hosted runtime file {destination}");
            receipt.Add(new RuntimeFileReceipt(file.OutputFileName, sourceHash));
        }

        if (receipt.Count == 0)
        {
            var emptyReceipt = ReceiptPath(outputDirectory);
            if (File.Exists(emptyReceipt))
                File.Delete(emptyReceipt);
        }
        else
            WriteReceipt(outputDirectory, receipt);
    }

    public static bool Clean(string outputDirectory, bool trace)
    {
        var receiptPath = ReceiptPath(outputDirectory);
        if (!File.Exists(receiptPath))
            return true;
        IReadOnlyList<RuntimeFileReceipt> receipt;
        try
        {
            receipt = ReadReceipt(outputDirectory);
        }
        catch (NativeBuildException exception)
        {
            Console.Error.WriteLine($"ctilde: {exception.Message}");
            return false;
        }

        var succeeded = true;
        foreach (var entry in receipt)
        {
            var destination = ResolveReceiptDestination(outputDirectory, entry.OutputFileName);
            if (!File.Exists(destination))
                continue;
            if (!Hash(destination).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                if (trace)
                    Console.Error.WriteLine($"trace: skipped modified hosted runtime file {destination}");
                succeeded = false;
                continue;
            }
            File.Delete(destination);
            if (trace)
                Console.Error.WriteLine($"trace: removed hosted runtime file {destination}");
        }
        File.Delete(receiptPath);
        if (trace)
            Console.Error.WriteLine($"trace: removed hosted runtime receipt {receiptPath}");
        return succeeded;
    }

    private static IReadOnlyList<RuntimeFileReceipt> ReadReceipt(string outputDirectory)
    {
        var path = ReceiptPath(outputDirectory);
        if (!File.Exists(path))
            return [];
        try
        {
            var receipt = JsonSerializer.Deserialize<RuntimeFileReceipt[]>(File.ReadAllText(path));
            if (receipt is null || receipt.Any(entry => string.IsNullOrWhiteSpace(entry.OutputFileName) ||
                string.IsNullOrWhiteSpace(entry.Sha256)))
                throw new JsonException("The receipt is incomplete.");
            foreach (var entry in receipt)
                _ = ResolveReceiptDestination(outputDirectory, entry.OutputFileName);
            return receipt;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NativeBuildException)
        {
            throw new NativeBuildException($"Could not read hosted runtime receipt '{path}': {exception.Message}", exception);
        }
    }

    private static void WriteReceipt(string outputDirectory, IReadOnlyList<RuntimeFileReceipt> receipt)
    {
        var directory = Path.Combine(outputDirectory, ".ctilde");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ReceiptFileName);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string ResolveReceiptDestination(string outputDirectory, string outputFileName)
    {
        if (string.IsNullOrWhiteSpace(outputFileName) || Path.GetFileName(outputFileName) != outputFileName ||
            outputFileName.Contains(Path.DirectorySeparatorChar) || outputFileName.Contains(Path.AltDirectorySeparatorChar))
            throw new NativeBuildException($"Hosted runtime receipt contains invalid output file name '{outputFileName}'.");
        var destination = Path.GetFullPath(Path.Combine(outputDirectory, outputFileName));
        if (!Path.GetDirectoryName(destination)!.Equals(Path.GetFullPath(outputDirectory), PathComparison))
            throw new NativeBuildException($"Hosted runtime receipt output '{outputFileName}' escapes the executable directory.");
        return destination;
    }

    private static string ReceiptPath(string outputDirectory) => Path.Combine(outputDirectory, ".ctilde", ReceiptFileName);

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string OperatingSystemName(HostedOperatingSystem operatingSystem) => operatingSystem switch
    {
        HostedOperatingSystem.Windows => "windows",
        HostedOperatingSystem.Linux => "linux",
        _ => operatingSystem.ToString().ToLowerInvariant(),
    };

    private static string ArchitectureName(CompilationArchitecture architecture) => architecture.ToString().ToLowerInvariant();

    private static void Trace(BuildRequest request, string message)
    {
        if (request.Trace)
            Console.Error.WriteLine($"trace: {message}");
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed record RuntimeFileReceipt(string OutputFileName, string Sha256);
}
