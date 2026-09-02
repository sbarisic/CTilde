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

        suite.Run("hosted native compiler reporting", () =>
        {
            Assert(CTilde.Cli.HostedBuildDriver.ClassifyBackend(true, @"C:\tools\cl.exe", null) == "msvc",
                "MSVC was not reported as the MSVC native toolchain.");
            Assert(CTilde.Cli.HostedBuildDriver.ClassifyBackend(false, @"C:\Windows\System32\wsl.exe", "clang-18") == "clang",
                "An explicit WSL Clang compiler was mislabeled as GCC.");
            Assert(CTilde.Cli.HostedBuildDriver.ClassifyBackend(false, "/usr/bin/gcc", null) == "gcc",
                "A native GCC compiler was not reported as GCC.");
        });
    }
}
