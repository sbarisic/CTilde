using CTilde.DebugAdapter;

Console.InputEncoding = System.Text.Encoding.UTF8;
Console.OutputEncoding = System.Text.Encoding.UTF8;
var adapter = new CTildeDebugAdapter();
adapter.Run(Console.OpenStandardInput(), Console.OpenStandardOutput());
