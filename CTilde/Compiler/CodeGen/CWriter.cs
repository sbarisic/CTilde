using System.Text;

namespace CTilde;

internal sealed class CWriter
{
    private readonly StringBuilder _builder = new();
    private int _indent;

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
