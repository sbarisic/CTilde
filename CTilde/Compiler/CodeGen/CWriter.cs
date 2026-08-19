using System.Text;

namespace CTilde;

internal interface ILoweringWriter
{
    void WriteLine(string text = "");
    void WriteBlock(IEnumerable<string> lines);
    IDisposable Block(string header);
    IDisposable Block();
}

internal sealed class CWriter : ILoweringWriter
{
    private static int _constructionCount;
    private readonly StringBuilder _builder = new();
    private int _indent;

    public CWriter() => Interlocked.Increment(ref _constructionCount);

    internal static int ConstructionCount => Volatile.Read(ref _constructionCount);

    public void WriteLine(string text = "")
    {
        if (text.Length > 0)
            _builder.Append(' ', _indent * 4).Append(text);
        _builder.Append('\n');
    }

    public void WriteBlock(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.Length == 0)
                WriteLine();
            else
                WriteLine(line);
        }
    }

    public IDisposable Block(string header)
    {
        WriteLine(header);
        WriteLine("{");
        _indent++;
        return new BlockScope(this);
    }

    public IDisposable Block()
    {
        WriteLine("{");
        _indent++;
        return new BlockScope(this);
    }

    public override string ToString() => _builder.ToString();

    private sealed class BlockScope(CWriter writer) : IDisposable
    {
        public void Dispose()
        {
            writer._indent--;
            writer.WriteLine("}");
        }
    }
}

internal sealed class NullLoweringWriter : ILoweringWriter
{
    private NullLoweringWriter() { }

    public static NullLoweringWriter Instance { get; } = new();

    public void WriteLine(string text = "") { }
    public void WriteBlock(IEnumerable<string> lines) { }
    public IDisposable Block(string header) => NullScope.Instance;
    public IDisposable Block() => NullScope.Instance;
    public override string ToString() => string.Empty;

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();
        public void Dispose() { }
    }
}
