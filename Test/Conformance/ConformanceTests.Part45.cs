using System.Text.RegularExpressions;
using CTilde;
using CTilde.Cli;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart45(ConformanceSuite suite)
    {
        suite.Run("draft 0.51 SSH receive allocation shortage closes cleanly", () =>
        {
            var transport = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Examples", "ManagedShell", "SystemSsh", "Transport.ct"));
            var start = transport.IndexOf("    internal byte[] ReceivePacket(", StringComparison.Ordinal);
            var end = transport.IndexOf("    internal void SendPacket(", start, StringComparison.Ordinal);
            var source = "using System; using System.Runtime; namespace System.Ssh;\n" + """
                internal sealed class Receiver {
                    private byte[] receiveHeader = new byte[4];
                    private byte[] receiveTag = new byte[16];
                    private byte[] receiveNonce = new byte[12];
                    private byte[] inboundIv = new byte[12];
                    private Exception receiveAllocationFailure = new InvalidOperationException();
                    private int maximumPacket = 35000;
                    private bool encrypted;
                    private ulong inboundInvocation;
                    private uint inboundCipher;
                    private uint inboundSequence;
                    internal bool Closed;
                    private void Close() { Closed = true; }
                    private void ReceiveExact(byte[] data, int offset, int count, uint timeout) {
                        if (data == receiveHeader) data[3] = (byte)12;
                        else data[0] = (byte)4;
                    }
                    private static void WriteNonce(byte[] basis, ulong invocation, byte[] result) {}
                """ + transport[start..end] + "}\n" + """
                internal static class SshWire {
                    internal static uint ReadUInt32(byte[] data, int offset) { return (uint)data[3]; }
                }
                internal static class SshNative {
                    internal static void Require(int result) {}
                    internal static int AesOpen(uint cipher, byte[] nonce, byte[] header,
                        byte[] body, byte[] tag, byte[] output) { return 0; }
                }
                public static class Program {
                    [EntryPoint]
                    public static void Main() {
                        Check(0); Check(1);
                        Receiver receiver = new Receiver();
                        byte[] packet = receiver.ReceivePacket(0u);
                        if (receiver.Closed || packet.Length != 7) throw new InvalidOperationException();
                        Console.WriteLine("SSH_RECEIVE_SHORTAGE_OK");
                    }
                    private static void Check(int successfulAllocations) {
                        Receiver receiver = new Receiver();
                        bool caught = false;
                        Memory.TestFailAllocationAfter(successfulAllocations);
                        try { receiver.ReceivePacket(0u); }
                        catch (InvalidOperationException) { caught = true; }
                        finally { Memory.TestFailAllocationAfter(-1); }
                        if (!caught || !receiver.Closed) throw new InvalidOperationException();
                    }
                }
                """;
            var result = CompileAndRun(source, conformance: true, memoryDiagnostics: true);
            Assert(result.ExitCode == 0 && result.StandardOutput.Contains("SSH_RECEIVE_SHORTAGE_OK", StringComparison.Ordinal),
                result.StandardOutput + result.StandardError);
        });

        suite.Run("draft 0.51 SSH borrowed field ranges", () =>
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Examples", "ManagedShell", "SystemSsh");
            var transport = File.ReadAllText(Path.Combine(directory, "Transport.ct"));
            var start = transport.IndexOf("internal sealed class SshPacketReader", StringComparison.Ordinal);
            var end = transport.IndexOf("internal sealed class SshPacketWriter", start, StringComparison.Ordinal);
            var source = "using System; using System.Text; namespace System.Ssh;\n" + transport[start..end] + """
                internal static class SshWire {
                    internal static uint ReadUInt32(byte[] data, int offset) {
                        return (uint)data[offset] << 24 | (uint)data[offset + 1] << 16 |
                            (uint)data[offset + 2] << 8 | (uint)data[offset + 3];
                    }
                }
                public static class Program {
                    [EntryPoint]
                    public static void Main() {
                        byte[] packet = new byte[11];
                        packet[3] = (byte)3; packet[4] = (byte)65; packet[5] = (byte)66; packet[6] = (byte)67;
                        SshPacketReader reader = new SshPacketReader(packet);
                        int offset; int count;
                        System.Runtime.Memory.TestFailAllocationAfter(0);
                        reader.ReadBytesRange(out offset, out count);
                        System.Runtime.Memory.TestFailAllocationAfter(-1);
                        if (offset != 4 || count != 3 || reader.Position != 7 || reader.Remaining != 4)
                            throw new InvalidOperationException();
                        reader.ReadBytesRange(out offset, out count);
                        reader.RequireEnd();
                        if (offset != 11 || count != 0) throw new InvalidOperationException();
                        reader = new SshPacketReader(packet);
                        if (reader.ReadString() != "ABC") throw new InvalidOperationException();
                        packet[3] = (byte)12;
                        bool rejected = false;
                        try { new SshPacketReader(packet).ReadBytesRange(out offset, out count); }
                        catch (InvalidOperationException) { rejected = true; }
                        if (!rejected) throw new InvalidOperationException();
                        Console.WriteLine("SSH_RANGES_OK");
                    }
                }
                """;
            var result = CompileAndRun(source, conformance: true, memoryDiagnostics: true);
            Assert(result.ExitCode == 0 && result.StandardOutput.Contains("SSH_RANGES_OK", StringComparison.Ordinal),
                result.StandardOutput + result.StandardError);
        });

        suite.Run("draft 0.51 fallible byte arrays support guarded allocations", () =>
        {
            const string source = """
                using System;
                using System.Runtime;
                public static class Program {
                    [EntryPoint]
                    public static void Main() {
                        for (int index = 0; index < 40; index++) Use(index);
                        Console.WriteLine("TRY_BYTES_GUARDS_OK");
                    }
                    private static void Use(int index) {
                        byte[] bytes = Memory.TryAllocateBytes(128);
                        if (bytes == null || bytes[127] != 0) throw new InvalidOperationException();
                        bytes[127] = (byte)index;
                    }
                }
                """;
            var result = CompileAndRun(source, new CompilationOptions(
                DebugInformation: DebugInformationMode.Instrumented, DebugMemory: DebugMemoryMode.Guarded));
            Assert(result.ExitCode == 0 && result.StandardOutput.Contains("TRY_BYTES_GUARDS_OK", StringComparison.Ordinal),
                result.StandardOutput + result.StandardError);
        });

        suite.Run("draft 0.51 fallible byte arrays preserve allocation accounting", () =>
        {
            const string source = """
                using System;
                using System.Runtime;
                public static class Program {
                    [Extern("ct_memory_diagnostic_live_allocations")]
                    public static uint LiveAllocations();
                    [Extern("ct_memory_diagnostic_live_objects")]
                    public static uint LiveObjects();
                    [EntryPoint]
                    public static void Main() {
                        uint allocations = LiveAllocations();
                        uint objects = LiveObjects();
                        Run();
                        if (LiveAllocations() != allocations || LiveObjects() != objects)
                            throw new InvalidOperationException();
                        Console.WriteLine("TRY_BYTES_OK");
                    }
                    private static void Run() {
                        if (Memory.TryAllocateBytes(-1) != null) throw new InvalidOperationException();
                        Memory.TestFailAllocationAfter(0);
                        byte[] missing = Memory.TryAllocateBytes(35000);
                        Memory.TestFailAllocationAfter(-1);
                        if (missing != null) throw new InvalidOperationException();
                        byte[] empty = Memory.TryAllocateBytes(0);
                        if (empty == null || empty.Length != 0) throw new InvalidOperationException();
                        byte[] bytes = Memory.TryAllocateBytes(128);
                        if (bytes == null || bytes.Length != 128) throw new InvalidOperationException();
                        for (int index = 0; index < bytes.Length; index++) {
                            if (bytes[index] != 0) throw new InvalidOperationException();
                            bytes[index] = (byte)index;
                        }
                        try {
                            byte[] temporary = Memory.TryAllocateBytes(17);
                            temporary[0] = (byte)7;
                            throw new InvalidOperationException();
                        } catch (InvalidOperationException) { }
                    }
                }
                """;
            var result = CompileAndRun(source, memoryDiagnostics: true, conformance: true);
            Assert(result.ExitCode == 0 && result.StandardOutput.Contains("TRY_BYTES_OK", StringComparison.Ordinal),
                result.StandardOutput + result.StandardError);
        });

        suite.Run("draft 0.51 shell distinguishes empty polls and EOF", () =>
        {
            var editor = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Examples", "ManagedShell", "Shell", "ShellEditor.ct"));
            var start = editor.IndexOf("    private static int ReadInput()", StringComparison.Ordinal);
            var end = editor.IndexOf("    private static bool ReadEscape(", start, StringComparison.Ordinal);
            var source = "using System; public static class Program {\n" + editor[start..end] + """
                [EntryPoint]
                public static void Main() {
                    if (ReadInput() != 'X' || ShellHost.Calls != 3 || ReadInput() != -1 || ShellHost.Calls != 4)
                        throw new InvalidOperationException();
                    Process.IsCancellationRequested = true;
                    if (ReadInput() != -1 || ShellHost.Calls != 4) throw new InvalidOperationException();
                    Console.WriteLine("SHELL_INPUT_EOF_OK");
                }
                }
                internal static class Process { internal static bool IsCancellationRequested; }
                internal static class ShellHost {
                    internal static int Calls;
                    internal static int ReadInput(out bool eof) {
                        Calls++;
                        eof = Calls == 4;
                        if (Calls == 3) return 'X';
                        return -1;
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0 && result.StandardOutput.Contains("SHELL_INPUT_EOF_OK", StringComparison.Ordinal),
                result.StandardOutput + result.StandardError);
        });

        suite.Run("draft 0.51 shell consumes complete CSI input", () =>
        {
            var editor = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Examples", "ManagedShell", "Shell", "ShellEditor.ct"));
            var start = editor.IndexOf("    private static bool ReadEscape(", StringComparison.Ordinal);
            var end = editor.IndexOf("    public string ReadLine(", start, StringComparison.Ordinal);
            var source = "using System; public static class Program {\n" +
                "private static int ReadInput() { return Console.Read(); }\n" + editor[start..end] + """
                [EntryPoint]
                public static void Main() {
                    Check(false, 0); Check(false, 0); Check(false, 0);
                    Check(true, 456); Check(true, 68);
                    Console.WriteLine("SHELL_CSI_OK");
                }
                private static void Check(bool expected, int expectedCode) {
                    if (Console.Read() != 27) throw new InvalidOperationException();
                    int code;
                    bool result = ReadEscape(out code);
                    if (result != expected || code != expectedCode || Console.Read() != 'X')
                        throw new InvalidOperationException();
                }
                }
                """;
            var result = CompileAndRun(source,
                standardInput: "\u001b[8;24;80tX\u001b[?25lX\u001b[999999999999999999999999~X\u001b[200~X\u001b[1;5DX");
            Assert(result.ExitCode == 0 && result.StandardOutput.Contains("SHELL_CSI_OK", StringComparison.Ordinal),
                result.StandardOutput + result.StandardError);
        });

        suite.Run("draft 0.51 SSH zero terminal dimensions", () =>
        {
            var server = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Examples", "ManagedShell", "SystemSsh", "Server.ct"));
            var start = server.IndexOf("internal sealed class SshChannelState", StringComparison.Ordinal);
            var end = server.IndexOf("[Overlay(\"channels\")]", start, StringComparison.Ordinal);
            var source = "using System; using System.Diagnostics; namespace System.Ssh;\n" +
                "internal interface ISshSubsystem {}\n" + server[start..end] + """
                public static class Program {
                    [EntryPoint]
                    public static void Main() {
                        SshChannelState channel = new SshChannelState();
                        channel.UpdateTerminalSize(0u, 0u);
                        Check(channel, 80u, 24u);
                        channel.UpdateTerminalSize(120u, 40u);
                        Check(channel, 120u, 40u);
                        channel.UpdateTerminalSize(0u, 50u);
                        Check(channel, 120u, 50u);
                        channel.UpdateTerminalSize(90u, 0u);
                        Check(channel, 90u, 50u);
                        Console.WriteLine("SSH_TERMINAL_SIZE_OK");
                    }
                    private static void Check(SshChannelState channel, uint columns, uint rows) {
                        if (channel.Columns != columns || channel.Rows != rows)
                            throw new InvalidOperationException();
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode == 0 && result.StandardOutput.Contains("SSH_TERMINAL_SIZE_OK", StringComparison.Ordinal),
                result.StandardOutput + result.StandardError);
        });

        suite.Run("draft 0.51 SSH exit-status packet", () =>
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Examples", "ManagedShell", "SystemSsh");
            var transport = File.ReadAllText(Path.Combine(directory, "Transport.ct"));
            var server = File.ReadAllText(Path.Combine(directory, "Server.ct"));
            var protocol = File.ReadAllText(Path.Combine(directory, "Protocol.ct"));
            var writerStart = transport.IndexOf("internal sealed class SshPacketWriter", StringComparison.Ordinal);
            var writerEnd = transport.IndexOf("internal sealed class SshTransport", writerStart, StringComparison.Ordinal);
            var statusStart = server.IndexOf("    private void SendExitStatus(", StringComparison.Ordinal);
            var statusEnd = server.IndexOf("    private void CloseChannel(", statusStart, StringComparison.Ordinal);
            var wireStart = protocol.IndexOf("    internal static uint ReadUInt32(", StringComparison.Ordinal);
            var wireEnd = protocol.IndexOf("    internal static bool IsSupportedAlgorithm(", wireStart, StringComparison.Ordinal);
            var requireStart = protocol.LastIndexOf("    private static void Require(", StringComparison.Ordinal);
            var helpers = "using System; using System.Text; namespace System.Ssh;\n" +
                transport[writerStart..writerEnd] + "internal static class SshWire {\n" +
                protocol[wireStart..wireEnd] + protocol[requireStart..];
            helpers = Regex.Replace(helpers, "\\[Overlay\\(\"[^\"]+\"\\)\\]", "");
            var source = "using System; namespace System.Ssh;\n" + """
                internal sealed class SshChannelState { internal uint RemoteId; }
                internal sealed class CaptureTransport {
                    internal byte[] Packet;
                    internal void SendPacket(byte[] packet) { Packet = packet; }
                }
                internal sealed class Connection {
                    private CaptureTransport transport = new CaptureTransport();
                    internal byte[] Encode(int code) {
                        SshChannelState channel = new SshChannelState();
                        channel.RemoteId = 42u;
                        SendExitStatus(channel, code);
                        return transport.Packet;
                    }
                """ + server[statusStart..statusEnd] + "}\n" + """
                public static class Program {
                    [EntryPoint]
                    public static void Main() {
                        Check(0); Check(7); Check(-1);
                        Console.WriteLine("SSH_EXIT_STATUS_OK");
                    }
                    private static void Check(int code) {
                        byte[] packet = new Connection().Encode(code);
                        if (packet.Length != 25 || packet[0] != 98 ||
                            SshWire.ReadUInt32(packet, 1) != 42u ||
                            SshWire.ReadUInt32(packet, 5) != 11u || packet[20] != 0 ||
                            System.Text.Encoding.UTF8.GetString(packet, 9, 11) != "exit-status" ||
                            SshWire.ReadUInt32(packet, 21) != (uint)code)
                            throw new InvalidOperationException();
                    }
                }
                """;
            var result = CompileAndRun([SyntaxTree.ParseText(helpers, "Transport.ct"),
                SyntaxTree.ParseText(source, "test.ct")], memoryDiagnostics: true);
            Assert(result.ExitCode == 0 && result.StandardOutput.Contains("SSH_EXIT_STATUS_OK", StringComparison.Ordinal),
                result.StandardOutput + result.StandardError);
        });

        suite.Run("draft 0.51 overlay packaging drains both tool streams", () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            ManagedOverlayPackager.Run("dotnet",
                [System.Reflection.Assembly.GetExecutingAssembly().Location, "--capture-child", "flood"], deadline.Token);
        });

        suite.Run("draft 0.51 overlay object manifest excludes stale profiles", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "ctilde-object-manifest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var current = Path.Combine(directory, "current.o");
                var stale = Path.Combine(directory, "stale.o");
                var manifest = Path.Combine(directory, "objects.txt");
                File.WriteAllText(current, "current");
                File.WriteAllText(stale, "stale");
                File.WriteAllText(manifest, current + "\n");
                Assert(ManagedOverlayPackager.ReadObjectManifest(manifest, directory).SequenceEqual([current]),
                    "A stale profile object entered the overlay link.");
                File.WriteAllText(manifest, current + "\n" + current + "\n");
                bool rejected = false;
                try { ManagedOverlayPackager.ReadObjectManifest(manifest, directory); }
                catch (NativeBuildException) { rejected = true; }
                Assert(rejected, "Duplicate link inputs were accepted.");
                File.WriteAllText(manifest, Path.Combine(directory, "missing.o") + "\n");
                rejected = false;
                try { ManagedOverlayPackager.ReadObjectManifest(manifest, directory); }
                catch (NativeBuildException) { rejected = true; }
                Assert(rejected, "A missing link input was accepted.");
            }
            finally { Directory.Delete(directory, recursive: true); }
        });

        suite.Run("draft 0.51 SFTP rooted relative paths", () =>
        {
            var protocol = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Examples", "ManagedShell", "SystemSsh", "Protocol.ct"));
            var start = protocol.IndexOf("    internal static bool TryNormalizeSftpPath(", StringComparison.Ordinal);
            var end = protocol.IndexOf("    internal static bool IsAuthorizedP256Line(", start, StringComparison.Ordinal);
            Assert(start >= 0 && end > start, "Production path normalizer was not found.");
            var helper = "using System; internal static class SshWire {\n" + protocol[start..end] + "\n}";
            const string source = """
                using System;
                public static class Program
                {
                    private static void Check(string input, string expected)
                    {
                        string actual;
                        bool valid = SshWire.TryNormalizeSftpPath(input, out actual);
                        if (valid != (expected != null) || actual != expected)
                            throw new InvalidOperationException();
                    }
                    [EntryPoint]
                    public static void Main()
                    {
                        Check(".", "/sftp");
                        Check("", "/sftp");
                        Check("/", "/sftp");
                        Check("./docs//readme.txt", "/sftp/docs/readme.txt");
                        Check("/docs/./readme.txt", "/sftp/docs/readme.txt");
                        Check("../storage/ssh", null);
                        Check("/docs/../escape", null);
                        Check("docs\\escape", null);
                        Check("docs\0escape", null);
                        Check(null, null);
                        Console.WriteLine("SFTP_PATH_OK");
                    }
                }
                """;
            var result = CompileAndRun([SyntaxTree.ParseText(helper, "paths.ct"), SyntaxTree.ParseText(source, "test.ct")]);
            Assert(result.ExitCode == 0 && result.StandardOutput.Contains("SFTP_PATH_OK", StringComparison.Ordinal),
                result.StandardOutput + result.StandardError);
        });

        suite.Run("draft 0.51 SFTP incremental request framing", () =>
        {
            var framer = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Examples", "ManagedShell", "SystemSsh", "SftpFramer.ct")).Replace("[Overlay(\"sftp-core\")]", "", StringComparison.Ordinal);
            const string source = """
                using System;
                using System.Ssh;
                public static class Program
                {
                    [Extern("ct_memory_diagnostic_live_objects")]
                    [NoAlloc]
                    public static uint LiveObjects();
                    private static void Check(bool value)
                    {
                        if (!value) throw new InvalidOperationException();
                    }
                    [EntryPoint]
                    public static void Main()
                    {
                        uint baseline = LiveObjects();
                        Run();
                        Check(LiveObjects() == baseline);
                        Console.WriteLine("SFTP_FRAMER_OK");
                    }
                    private static void Run()
                    {
                        SftpFramer framer = new SftpFramer();
                        byte[] wire = new byte[35004];
                        wire[2] = (byte)136; wire[3] = (byte)184;
                        for (int index = 4; index < wire.Length; index++)
                            wire[index] = (byte)index;
                        for (int split = 0; split <= 4; split++)
                        {
                            byte[] prefix = new byte[split];
                            byte[] suffix = new byte[wire.Length - split];
                            for (int index = 0; index < split; index++) prefix[index] = wire[index];
                            for (int index = split; index < wire.Length; index++) suffix[index - split] = wire[index];
                            int position = 0;
                            Check(framer.Read(prefix, ref position) == null && position == split);
                            position = 0;
                            byte[] packet = framer.Read(suffix, ref position);
                            Check(packet.Length == 35000 && position == suffix.Length);
                            for (int index = 0; index < packet.Length; index++) Check(packet[index] == (byte)(index + 4));
                        }
                        byte[] bytewise = new byte[1];
                        for (int index = 0; index < wire.Length; index++)
                        {
                            bytewise[0] = wire[index];
                            int position = 0;
                            byte[] packet = framer.Read(bytewise, ref position);
                            Check(position == 1);
                            Check((packet != null) == (index == wire.Length - 1));
                        }
                        byte[] joined = new byte[70008];
                        for (int index = 0; index < joined.Length; index++) joined[index] = wire[index % wire.Length];
                        int offset = 0;
                        Check(framer.Read(joined, ref offset).Length == 35000 && offset == 35004);
                        Check(framer.Read(joined, ref offset).Length == 35000 && offset == joined.Length);
                        byte[] invalid = new byte[4];
                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            if (attempt == 1) { invalid[2] = (byte)136; invalid[3] = (byte)185; }
                            if (attempt == 2) { invalid[0] = (byte)255; invalid[1] = (byte)255; invalid[2] = (byte)255; invalid[3] = (byte)255; }
                            offset = 0;
                            bool rejected = false;
                            try { framer.Read(invalid, ref offset); }
                            catch (InvalidOperationException) { rejected = true; }
                            Check(rejected);
                            framer.Reset();
                        }
                        byte[] partial = new byte[5];
                        partial[3] = (byte)2; partial[4] = (byte)1;
                        offset = 0;
                        Check(framer.Read(partial, ref offset) == null);
                        framer.Reset();
                        partial[3] = (byte)1;
                        offset = 0;
                        Check(framer.Read(partial, ref offset)[0] == 1);
                    }
                }
                """;
            var result = CompileAndRun([
                SyntaxTree.ParseText(framer, "SftpFramer.ct"),
                SyntaxTree.ParseText(source, "test.ct")], memoryDiagnostics: true);
            Assert(result.ExitCode == 0 && result.StandardOutput.Contains("SFTP_FRAMER_OK", StringComparison.Ordinal),
                result.StandardOutput + result.StandardError);
        });

        suite.Run("draft 0.49 overlay placement and resident call stubs", () =>
        {
            const string source = """
                [Overlay("render")]
                public class Renderer
                {
                    public static int Trace(int value) { return Shade(value) + 1; }
                    private static int Shade(int value) { return value * 2; }
                    [Resident]
                    public static int Report(int value) { return value; }
                }

                public static class Program
                {
                    [Overlay("unused")]
                    private static void Unused() { }

                    [EntryPoint]
                    public static int Main(string[] args) { return Renderer.Trace(20); }
                }
                """;
            var module = new ManagedModuleConfiguration(
                ManagedModuleKind.Application, "Demo.Overlay", "1.0.0", [], 4096, 16384);
            var compilation = Compile(source, new CompilationOptions(
                CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                ManagedModuleKind: module.Kind, ManagedModule: module));
            var diagnostics = compilation.GetDiagnostics();
            Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                string.Join(Environment.NewLine, diagnostics));
            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var generated = string.Join('\n', bundle.Artifacts.Select(artifact => artifact.Content));
            Assert(generated.Contains("CT_OVERLAY_BODY(\"render\")", StringComparison.Ordinal) &&
                generated.Contains("ct_managed_call_target_v4", StringComparison.Ordinal) &&
                generated.Contains("EnterManagedCall", StringComparison.Ordinal) &&
                generated.Contains("ct_leave_managed_call_cleanup", StringComparison.Ordinal) &&
                generated.Contains("ct_cleanup_push(&ct_call_cleanup", StringComparison.Ordinal) &&
                generated.Contains("ct_managed_module_text_anchor", StringComparison.Ordinal),
                "Overlay bodies were not separated behind cleanup-safe resident stubs.");

            using var metadataWriter = new StringWriter();
            Assert(compilation.EmitManagedModuleMetadata(metadataWriter, module).Success,
                "Overlay metadata emission failed.");
            var metadataText = metadataWriter.ToString();
            Assert(metadataText.Contains("\"hasOverlays\": true", StringComparison.Ordinal) &&
                metadataText.Contains("\"name\": \"render\"", StringComparison.Ordinal) &&
                metadataText.Contains("\"targetIndex\": 0", StringComparison.Ordinal) &&
                !metadataText.Contains("\"name\": \"unused\"", StringComparison.Ordinal),
                "Schema-3 metadata omitted deterministic reachable placement or retained an unreachable overlay.");
            var bodySymbols = Regex.Matches(metadataText, "\\\"bodySymbol\\\": \\\"([^\\\"]+)\\\"")
                .Select(match => match.Groups[1].Value).ToArray();
            var bound = (BoundProgram)typeof(Compilation).GetField("_boundProgram",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(compilation)!;
            var shade = bound.Model.ProjectTypes.Single(type => type.Name == "Renderer").Methods.Single(method => method.Name == "Shade");
            var shadeBody = CEmitter.OverlayBodyName(shade, shade.CName);
            Assert(bodySymbols.Length == 1 && !shade.RequiresOverlayEntry &&
                    !bodySymbols.Contains(shadeBody) && Regex.Matches(generated, $@"\b{Regex.Escape(shadeBody)}\s*\(").Count >= 3,
                "A proven same-overlay call did not target its typed overlay body directly.");
        });

        suite.Run("draft 0.49 Xtensa overlay instruction relocation audit", () =>
        {
            Assert(ManagedOverlayPackager.IsAuditedXtensaInstructionRelocation(8u) &&
                ManagedOverlayPackager.IsAuditedXtensaInstructionRelocation(10u) &&
                ManagedOverlayPackager.IsAuditedXtensaInstructionRelocation(14u) &&
                ManagedOverlayPackager.IsAuditedXtensaInstructionRelocation(20u) &&
                ManagedOverlayPackager.IsAuditedXtensaInstructionRelocation(49u) &&
                !ManagedOverlayPackager.IsAuditedXtensaInstructionRelocation(11u) &&
                !ManagedOverlayPackager.IsAuditedXtensaInstructionRelocation(50u),
                "The audited Xtensa instruction-relocation allowlist changed unexpectedly.");
            ManagedOverlayPackager.ValidateAuditedXtensaInstructionRelocation(
                "render", 20u, true, true, "render", true);
            AssertRejected(20u, true, true, "other", true);
            AssertRejected(20u, true, true, null, true);
            AssertRejected(20u, true, false, null, false);
            AssertRejected(11u, true, true, "render", true);
            AssertRejected(20u, false, true, "render", true);

            static void AssertRejected(uint type, bool originContains, bool targetDefined,
                string? targetOverlay, bool targetContains)
            {
                try
                {
                    ManagedOverlayPackager.ValidateAuditedXtensaInstructionRelocation(
                        "render", type, originContains, targetDefined, targetOverlay, targetContains);
                    throw new InvalidOperationException("Unsafe Xtensa overlay relocation was accepted.");
                }
                catch (NativeBuildException)
                {
                }
            }
        });

        suite.Run("draft 0.49 overlay constructors properties and delegates use stable stubs", () =>
        {
            const string source = """
                public delegate int Transform(int value);

                [Overlay("outer")]
                public class Worker
                {
                    private int number;

                    [Overlay("construction")]
                    public Worker() { number = 20; }

                    public int Value { get { return number; } set { number = value; } }

                    public static int Twice(int value) { return value * 2; }

                    [Resident]
                    public int ResidentValue() { return 2; }
                }

                public static class Program
                {
                    [EntryPoint]
                    public static int Main(string[] args)
                    {
                        Worker worker = new Worker();
                        Transform transform = Worker.Twice;
                        return worker.Value + transform(worker.ResidentValue());
                    }
                }
                """;
            var module = new ManagedModuleConfiguration(
                ManagedModuleKind.Application, "Demo.OverlayMembers", "1.0.0", [], 4096, 16384);
            var compilation = Compile(source, new CompilationOptions(
                CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                ManagedModuleKind: module.Kind, ManagedModule: module));
            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var generated = string.Join('\n', bundle.Artifacts.Select(artifact => artifact.Content));
            Assert(generated.Contains("CT_OVERLAY_BODY(\"construction\")", StringComparison.Ordinal),
                "The overlay constructor body was not separated.");
            Assert(generated.Contains("CT_OVERLAY_BODY(\"outer\")", StringComparison.Ordinal),
                "The inherited property or method body was not separated.");
            Assert(generated.Contains("EnterManagedCall", StringComparison.Ordinal) &&
                generated.Contains("ct_managed_call_targets_v3", StringComparison.Ordinal),
                "Constructors, properties, or managed delegates bypassed stable overlay stubs.");
        });

        suite.Run("draft 0.49 overlay placement rejects invalid and unsafe boundaries", () =>
        {
            var module = new ManagedModuleConfiguration(
                ManagedModuleKind.Application, "Demo.InvalidOverlay", "1.0.0", [], 4096, 16384);
            var invalid = Compile("""
                [Overlay("1invalid")]
                public class InvalidName { }

                [Overlay("outer")]
                public class Worker
                {
                    [Overlay("inner")]
                    [Resident]
                    public static void Conflict() { }

                    [Overlay("native")]
                    [Extern("native_work")]
                    public static void Native();
                }

                public static class Program
                {
                    [EntryPoint]
                    public static int Main(string[] args) { return 0; }
                }
                """, new CompilationOptions(
                    CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                    ManagedModuleKind: module.Kind, ManagedModule: module));
            var invalidDiagnostics = invalid.GetDiagnostics();
            Assert(invalidDiagnostics.Any(diagnostic => diagnostic.Code == "CT6230") &&
                invalidDiagnostics.Any(diagnostic => diagnostic.Code == "CT6231"),
                "Invalid overlay names, conflicts, or native boundaries were accepted.");

            var pointer = Compile("""
                public static class Program
                {
                    [Overlay("work")]
                    public static int Work(int value) { return value; }

                    [EntryPoint]
                    public static unsafe int Main(string[] args)
                    {
                        delegate* unmanaged<int, int> address = &Work;
                        return address(1);
                    }
                }
                """, new CompilationOptions(
                    CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                    ManagedModuleKind: module.Kind, ManagedModule: module));
            Assert(pointer.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT6234"),
                "An overlay body exposed a raw unmanaged function pointer.");

            var interrupt = Compile("""
                public static class Program
                {
                    [Overlay("work")]
                    public static void Work() { }

                    [Interrupt]
                    [Export("irq")]
                    public static unsafe void Handler(void* context) { Work(); }

                    [EntryPoint]
                    public static int Main(string[] args) { return 0; }
                }
                """, new CompilationOptions(
                    CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                    ManagedModuleKind: module.Kind, ManagedModule: module));
            Assert(interrupt.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT6235"),
                "An interrupt call closure was allowed to enter overlay code.");
        });

        suite.Run("draft 0.49 overlay target and thread restrictions", () =>
        {
            const string source = """
                using System.Threading;
                public static class Program
                {
                    [Overlay("work")]
                    public static void Work() { }

                    [EntryPoint]
                    public static int Main(string[] args)
                    {
                        Thread thread = new Thread(Work);
                        thread.Start();
                        return 0;
                    }
                }
                """;
            var module = new ManagedModuleConfiguration(
                ManagedModuleKind.Application, "Demo.ThreadedOverlay", "1.0.0", [], 4096, 16384);
            var xtensa = Compile(source, new CompilationOptions(
                CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                ManagedModuleKind: module.Kind, ManagedModule: module));
            Assert(xtensa.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT6233"),
                "Overlay-enabled dependency closure accepted Thread.Start.");

            var riscV = Compile("""
                public static class Program
                {
                    [Overlay("work")] public static void Work() { }
                    [EntryPoint] public static int Main(string[] args) { Work(); return 0; }
                }
                """, new CompilationOptions(CompilationTarget.EspIdf,
                    Architecture: CompilationArchitecture.RiscV32,
                    ManagedModuleKind: module.Kind, ManagedModule: module));
            Assert(riscV.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT6232"),
                "ESP32-C3 did not receive the dedicated overlay target diagnostic.");

            var providerConfiguration = new ManagedModuleConfiguration(
                ManagedModuleKind.Library, "Demo.OverlayDependency", "1.0.0", [], 4096, 16384);
            var provider = Compile("""
                namespace Demo.OverlayDependency;
                public static class Work
                {
                    [Overlay("dependency")] public static void Run() { }
                }
                """, new CompilationOptions(
                    CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                    ManagedModuleKind: providerConfiguration.Kind, ManagedModule: providerConfiguration));
            using var metadataWriter = new StringWriter();
            Assert(provider.EmitManagedModuleMetadata(metadataWriter, providerConfiguration).Success,
                "Overlay dependency metadata emission failed.");
            var metadataPath = Path.Combine(Path.GetTempPath(), $"ctilde-overlay-{Guid.NewGuid():N}.ctmeta.json");
            File.WriteAllText(metadataPath, metadataWriter.ToString());
            try
            {
                var metadata = ManagedModuleMetadata.Load(metadataPath);
                Assert(metadata.HasOverlays, "Provider metadata did not publish overlay capability.");
                var reference = new ManagedModuleReference(metadataPath, metadata.Name, metadata.Version,
                    metadata.BuildIdentity, metadata.ApiHash, metadata);
                var consumerConfiguration = new ManagedModuleConfiguration(
                    ManagedModuleKind.Application, "Demo.ThreadedConsumer", "1.0.0", [reference], 4096, 16384);
                var owner = new SourceOwnerIdentity(metadata.Name, Path.GetTempPath(), Path.GetTempPath(), false,
                    metadata.BuildIdentity);
                var trees = metadata.Declarations.Select((declaration, index) => SyntaxTree.ParseManagedModuleReference(
                        SourceText.From(declaration.Source, Path.Combine(Path.GetTempPath(), $"overlay-reference-{index}.ct")), owner))
                    .Append(SyntaxTree.ParseText("""
                        using System.Threading;
                        using Demo.OverlayDependency;
                        public static class Program
                        {
                            private static void Worker() { Work.Run(); }
                            [EntryPoint]
                            public static int Main(string[] args)
                            {
                                Thread thread = new Thread(Worker);
                                thread.Start();
                                return 0;
                            }
                        }
                        """, Path.Combine(Path.GetTempPath(), "overlay-consumer.ct"), SourceOwnerIdentity.ImplicitRoot));
                var consumer = Compilation.Create(trees, new CompilationOptions(
                    CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                    ManagedModuleKind: consumerConfiguration.Kind, ManagedModule: consumerConfiguration));
                Assert(consumer.GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT6233"),
                    "A consumer of an overlay-enabled dependency accepted Thread.Start.");
            }
            finally
            {
                File.Delete(metadataPath);
            }
        });

        suite.Run("draft 0.49 managed application exception boundary", () =>
        {
            const string source = """
                public static class Program
                {
                    [EntryPoint]
                    public static int Main(string[] args)
                    {
                        throw new System.InvalidOperationException();
                    }
                }
                """;
            var module = new ManagedModuleConfiguration(
                ManagedModuleKind.Application, "Demo.ExceptionBoundary", "1.0.0", [], 4096, 16384);
            var compilation = Compile(source, new CompilationOptions(
                CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                ManagedModuleKind: module.Kind, ManagedModule: module));
            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var generated = string.Join('\n', bundle.Artifacts.Select(artifact => artifact.Content));
            Assert(generated.Contains("C~ unhandled module exception\\n", StringComparison.Ordinal) &&
                generated.Contains("return -2;", StringComparison.Ordinal) &&
                generated.Contains("setjmp(ct_main_target)", StringComparison.Ordinal),
                "Managed application Main lacks its process-level exception boundary.");
        });

        suite.Run("draft 0.49 managed child tasks inherit process context", () =>
        {
            const string source = """
                using System.Threading;
                public static class Program
                {
                    private static void Worker() { Thread.Yield(); }
                    [EntryPoint]
                    public static int Main(string[] args)
                    {
                        Thread thread = new Thread(Worker);
                        thread.Start();
                        thread.Join();
                        return 0;
                    }
                }
                """;
            var module = new ManagedModuleConfiguration(
                ManagedModuleKind.Application, "Demo.ChildTask", "1.0.0", [], 4096, 16384);
            var compilation = Compile(source, new CompilationOptions(
                CompilationTarget.EspIdf, Architecture: CompilationArchitecture.Xtensa,
                ManagedModuleKind: module.Kind, ManagedModule: module));
            var bundle = compilation.EmitCBundle();
            Assert(bundle.Success, string.Join(Environment.NewLine, bundle.Diagnostics));
            var generated = string.Join('\n', bundle.Artifacts.Select(artifact => artifact.Content));
            Assert(generated.Contains("ct_runtime_thread_attach_v23", StringComparison.Ordinal) &&
                generated.Contains("payload->Process = ct_runtime_api->CurrentProcess()", StringComparison.Ordinal) &&
                generated.Contains("ct_thread_attach_to(payload->Process)", StringComparison.Ordinal),
                "Generated managed workers do not inherit the creating process context.");
        });
    }
}
