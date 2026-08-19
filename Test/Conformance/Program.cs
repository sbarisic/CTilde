using CTilde.Tests;

var suite = new ConformanceSuite();
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
return suite.Complete();

internal sealed class ConformanceSuite
{
    private readonly List<string> _failures = [];
    private readonly string? _filter = Environment.GetEnvironmentVariable("CTILDE_TEST_FILTER");

    public void Run(string name, Action test)
    {
        if (_filter is not null && !name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            _failures.Add($"FAIL {name}: {exception.Message}");
        }
    }

    public int Complete()
    {
        if (_failures.Count == 0)
        {
            Console.WriteLine("Conformance: all tests passed.");
            return 0;
        }
        foreach (var failure in _failures)
            Console.Error.WriteLine(failure);
        return 1;
    }
}
