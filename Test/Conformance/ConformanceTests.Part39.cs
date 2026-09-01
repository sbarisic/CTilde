namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart39(ConformanceSuite suite)
    {
        suite.Run("draft 0.44 debugger backend descriptors", () =>
        {
            Assert(CTilde.Cli.DebugPreparation.SelectDebuggerBackend(
                    new CTilde.Cli.NativeBuildOutcome(0, "msvc", "cl")) == "msvc",
                "MSVC did not select the debugger fallback backend.");
            Assert(CTilde.Cli.DebugPreparation.SelectDebuggerBackend(
                    new CTilde.Cli.NativeBuildOutcome(0, "gcc", "gcc")) == "gdb",
                "Hosted GNU compilation did not select GDB.");
            Assert(CTilde.Cli.DebugPreparation.SelectDebuggerBackend(
                    new CTilde.Cli.NativeBuildOutcome(0, "esp-idf-gcc", "ESP-IDF GCC")) == "gdb",
                "ESP-IDF GNU compilation leaked its native build label into the debugger descriptor.");
        });
    }
}
