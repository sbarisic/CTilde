using CTilde.Tests;

var crossToolchainOnly = false;
string? commandLineFilter = null;
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--cross-toolchain-only":
            crossToolchainOnly = true;
            break;
        case "--filter" when index + 1 < args.Length:
            commandLineFilter = args[++index];
            break;
        case "--filter":
            Console.Error.WriteLine("Missing value after --filter.");
            return 2;
        default:
            Console.Error.WriteLine($"Unknown conformance argument '{args[index]}'.");
            return 2;
    }
}

var suite = new ConformanceSuite(crossToolchainOnly, commandLineFilter);
ConformanceTests.RegisterPart1(suite);
ConformanceTests.RegisterPart2(suite);
ConformanceTests.RegisterPart3(suite);
ConformanceTests.RegisterPart4(suite);
ConformanceTests.RegisterPart5(suite);
ConformanceTests.RegisterPart6(suite);
ConformanceTests.RegisterPart7(suite);
ConformanceTests.RegisterPart8(suite);
ConformanceTests.RegisterPart9(suite);
ConformanceTests.RegisterPart10(suite);
ConformanceTests.RegisterPart11(suite);
ConformanceTests.RegisterPart12(suite);
ConformanceTests.RegisterPart13(suite);
ConformanceTests.RegisterPart14(suite);
ConformanceTests.RegisterPart15(suite);
ConformanceTests.RegisterPart16(suite);
ConformanceTests.RegisterPart17(suite);
ConformanceTests.RegisterPart18(suite);
ConformanceTests.RegisterPart19(suite);
ConformanceTests.RegisterPart20(suite);
ConformanceTests.RegisterPart21(suite);
ConformanceTests.RegisterPart22(suite);
ConformanceTests.RegisterPart23(suite);
ConformanceTests.RegisterPart24(suite);
ConformanceTests.RegisterPart25(suite);
ConformanceTests.RegisterPart26(suite);
ConformanceTests.RegisterPart27(suite);
ConformanceTests.RegisterPart28(suite);
ConformanceTests.RegisterPart29(suite);
ConformanceTests.RegisterPart30(suite);
ConformanceTests.RegisterPart31(suite);
ConformanceTests.RegisterPart32(suite);
return suite.Complete();

internal sealed class ConformanceSuite
{
    private readonly List<string> _failures = [];
    private readonly HashSet<string> _registered = new(StringComparer.Ordinal);
    private readonly bool _crossToolchainOnly;
    private readonly string? _filter;
    private int _passed;
    private int _skipped;

    public ConformanceSuite(bool crossToolchainOnly, string? commandLineFilter)
    {
        _crossToolchainOnly = crossToolchainOnly;
        _filter = commandLineFilter ?? Environment.GetEnvironmentVariable("CTILDE_TEST_FILTER");
    }

    public void Run(string name, Action test)
    {
        if (!_registered.Add(name))
        {
            _failures.Add($"FAIL duplicate conformance registration: {name}");
            return;
        }
        if (_crossToolchainOnly && !ConformanceTestCatalog.CrossToolchain.Contains(name))
        {
            _skipped++;
            return;
        }
        if (_filter is not null && !name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
        {
            _skipped++;
            return;
        }
        try
        {
            test();
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            _failures.Add($"FAIL {name}: {exception}");
        }
    }

    public int Complete()
    {
        var unknownTags = ConformanceTestCatalog.CrossToolchain.Except(_registered, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (unknownTags.Length != 0)
            _failures.Add($"FAIL stale cross-toolchain tags: {string.Join(", ", unknownTags)}");
        if (_failures.Count == 0)
        {
            Console.WriteLine("Conformance: all tests passed.");
            Console.WriteLine($"Conformance summary: {_passed} passed, {_skipped} skipped, {_registered.Count} registered.");
            return 0;
        }
        foreach (var failure in _failures)
            Console.Error.WriteLine(failure);
        return 1;
    }
}
