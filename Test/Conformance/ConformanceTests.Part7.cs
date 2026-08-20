using System.Globalization;
using System.Text;
using CTilde;

namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart7(ConformanceSuite suite)
    {
        suite.Run("hosted I/O target and documentation", () =>
        {
            const string source = "using System; using System.IO; public static class Program { [EntryPoint] public static void Main() { Console.ReadLine(); } }";
            var hosted = Compile(source);
            Assert(!hosted.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, hosted.GetDiagnostics()));
            var importOnly = Compile("using System.IO; public static class Program { [EntryPoint] public static void Main() { } }");
            Assert(!importOnly.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "An unused hosted System.IO import was rejected.");
            var esp = Compile(source, new CompilationOptions(CompilationTarget.EspIdf));
            Assert(esp.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "Hosted I/O was available to ESP-IDF.");

            var service = LanguageServiceSnapshot.Create([SyntaxTree.ParseText("using System.IO; public static class P { public void M() { File. } }", "hosted-io.ct")]);
            var text = "using System.IO; public static class P { public void M() { File. } }";
            var completions = service.GetCompletions("hosted-io.ct", text.IndexOf("File.", StringComparison.Ordinal) + "File.".Length);
            var open = completions.Single(completion => completion.Label == "Open");
            Assert(open.DocumentationId is not null && service.GetDocumentation(open.DocumentationId)?.Summary.Contains("Opens", StringComparison.Ordinal) == true, "Hosted File.Open documentation was unavailable.");

            var espService = LanguageServiceSnapshot.Create([SyntaxTree.ParseText(text, "esp-io.ct")], new CompilationOptions(CompilationTarget.EspIdf));
            Assert(!espService.GetCompletions("esp-io.ct", text.IndexOf("File.", StringComparison.Ordinal) + "File.".Length).Any(completion => completion.Label == "Open"), "Hosted File completion appeared for ESP-IDF.");
        });

        suite.Run("hosted I/O ownership and reserved symbols", () =>
        {
            const string valid = """
                using System.IO;
                public static class Program
                {
                    [EntryPoint] public static void Main()
                    {
                        FileHandle file = File.Open("owned.bin", FileMode.Create, FileAccess.Write);
                        defer File.Close(file);
                        File.Write(file, "ok");
                    }
                }
                """;
            Assert(!Compile(valid).GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "A deferred hosted file handle was rejected.");

            const string leak = "using System.IO; public static class P { [EntryPoint] public static void Main() { FileHandle file = File.Open(\"x\", FileMode.Open, FileAccess.Read); } }";
            Assert(Compile(leak).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT1258"), "An unclosed hosted file handle was not diagnosed.");

            const string reserved = "public static class Native { [Extern(\"ct_host_file_open\")] public static int Call(); } public static class P { [EntryPoint] public static void Main() { } }";
            Assert(Compile(reserved).GetDiagnostics().Any(diagnostic => diagnostic.Code == "CT4101"), "A hosted runtime symbol conflict was not diagnosed.");
        });

        suite.Run("hosted path tracer model and deterministic sampling", () =>
        {
            const string harness = """
                using System;
                namespace HostedIoExample;

                public static class MemoryDiagnostics
                {
                    [Extern("ct_memory_diagnostic_live_objects")]
                    [NoAlloc]
                    public static uint LiveObjects();
                }

                public sealed class CountingHittable : Hittable
                {
                    public static int Calls;
                    private Hittable inner;

                    public CountingHittable(Hittable inner)
                    {
                        this.inner = inner;
                    }

                    [NoAlloc]
                    public override bool Hit(Ray ray, Interval interval, out HitRecord hit)
                    {
                        Calls++;
                        HitRecord result;
                        bool found = inner.Hit(ray, interval, out result);
                        hit = result;
                        return found;
                    }

                    [NoAlloc]
                    public override Aabb BoundingBox()
                    {
                        return inner.BoundingBox();
                    }
                }

                public static class TestProgram
                {
                    private static bool Close(float left, float right)
                    {
                        return Math.Abs(left - right) < 0.0001f;
                    }

                    [EntryPoint]
                    public static void Main()
                    {
                        uint baseline = MemoryDiagnostics.LiveObjects();
                        {
                            RandomGenerator sequence = new RandomGenerator(RandomGenerator.DefaultSeed);
                            Console.WriteLine(sequence.NextUInt() == 4170923632u);
                            Console.WriteLine(sequence.NextUInt() == 1979402906u);
                            RandomGenerator zeroSeed = new RandomGenerator(0u);
                            Console.WriteLine(zeroSeed.NextUInt() == 4170923632u);

                            RandomGenerator samples = new RandomGenerator(123u);
                            float scalar = samples.NextFloat();
                            Vec3 sphereSample = samples.InUnitSphere();
                            Vec3 unitSample = samples.UnitVector();
                            Vec3 diskSample = samples.InUnitDisk();
                            Console.WriteLine(scalar >= 0.0f && scalar < 1.0f);
                            Console.WriteLine(sphereSample.LengthSquared() < 1.0f);
                            Console.WriteLine(Close(unitSample.Length(), 1.0f));
                            Console.WriteLine(diskSample.Z == 0.0f && diskSample.LengthSquared() < 1.0f);

                            Material matte = new Lambertian(new Vec3(0.8f, 0.3f, 0.2f));
                            Sphere sphere = new Sphere(new Vec3(0.0f, 0.0f, -1.0f), 0.5f, matte);
                            HitRecord outsideHit;
                            bool hitOutside = sphere.Hit(new Ray(Vec3.Zero, new Vec3(0.0f, 0.0f, -1.0f)), new Interval(0.001f, 100.0f), out outsideHit);
                            Console.WriteLine(hitOutside && Close(outsideHit.Distance, 0.5f) && outsideHit.FrontFace && Close(outsideHit.Normal.Z, 1.0f));
                            HitRecord insideHit;
                            bool hitInside = sphere.Hit(new Ray(new Vec3(0.0f, 0.0f, -1.0f), Vec3.UnitX), new Interval(0.001f, 100.0f), out insideHit);
                            Console.WriteLine(hitInside && !insideHit.FrontFace && Close(insideHit.Normal.X, -1.0f));

                            HittableList closest = new HittableList();
                            closest.Add(new Sphere(new Vec3(0.0f, 0.0f, -3.0f), 0.5f, matte));
                            closest.Add(sphere);
                            HitRecord closestHit;
                            bool foundClosest = closest.Hit(new Ray(Vec3.Zero, new Vec3(0.0f, 0.0f, -1.0f)), new Interval(0.001f, 100.0f), out closestHit);
                            Console.WriteLine(foundClosest && Close(closestHit.Distance, 0.5f));

                            Hittable accelerated = closest.BuildBvh();
                            HitRecord acceleratedHit;
                            bool foundAccelerated = accelerated.Hit(new Ray(Vec3.Zero, new Vec3(0.0f, 0.0f, -1.0f)), new Interval(0.001f, 100.0f), out acceleratedHit);
                            Console.WriteLine(foundAccelerated == foundClosest && Close(acceleratedHit.Distance, closestHit.Distance));

                            Aabb box = new Aabb(new Vec3(-1.0f, -1.0f, -1.0f), Vec3.One);
                            Console.WriteLine(box.Hit(new Ray(new Vec3(0.0f, 0.0f, 2.0f), new Vec3(0.0f, 0.0f, -1.0f)), new Interval(0.0f, 100.0f)));
                            Console.WriteLine(!box.Hit(new Ray(new Vec3(2.0f, 0.0f, 0.0f), Vec3.UnitY), new Interval(0.0f, 100.0f)));

                            RandomGenerator scheduled = new RandomGenerator(1u);
                            uint sampleSeed = RandomGenerator.SampleSeed(RandomGenerator.DefaultRenderSeed, 7, 3, 2);
                            scheduled.Reseed(sampleSeed);
                            uint scheduledFirst = scheduled.NextUInt();
                            scheduled.Reseed(RandomGenerator.SampleSeed(RandomGenerator.DefaultRenderSeed, 1, 9, 4));
                            scheduled.NextUInt();
                            scheduled.Reseed(sampleSeed);
                            Console.WriteLine(scheduled.NextUInt() == scheduledFirst);

                            HittableList measured = new HittableList();
                            int measuredIndex = 0;
                            while (measuredIndex < 32)
                            {
                                measured.Add(new CountingHittable(new Sphere(new Vec3(0.0f, 0.0f, -2.0f - (float)measuredIndex * 2.0f), 0.25f, matte)));
                                measuredIndex++;
                            }
                            Hittable measuredBvh = measured.BuildBvh();
                            CountingHittable.Calls = 0;
                            HitRecord measuredListHit;
                            bool measuredListFound = measured.Hit(new Ray(Vec3.Zero, new Vec3(0.0f, 0.0f, -1.0f)), new Interval(0.001f, 100.0f), out measuredListHit);
                            int listCalls = CountingHittable.Calls;
                            CountingHittable.Calls = 0;
                            HitRecord measuredBvhHit;
                            bool measuredBvhFound = measuredBvh.Hit(new Ray(Vec3.Zero, new Vec3(0.0f, 0.0f, -1.0f)), new Interval(0.001f, 100.0f), out measuredBvhHit);
                            int bvhCalls = CountingHittable.Calls;
                            Console.WriteLine(measuredListFound == measuredBvhFound && Close(measuredListHit.Distance, measuredBvhHit.Distance) && bvhCalls * 4 <= listCalls);

                            bool capacityFailed = false;
                            try
                            {
                                HittableList limited = new HittableList();
                                int fill = 0;
                                while (fill < 512)
                                {
                                    limited.Add(sphere);
                                    fill++;
                                }
                                limited.Add(sphere);
                            }
                            catch (Exception error)
                            {
                                capacityFailed = error.Message == "HittableList capacity exceeded.";
                            }
                            Console.WriteLine(capacityFailed);

                            Vec3 reflected = VectorMath.Reflect(new Vec3(1.0f, -1.0f, 0.0f).Normalize(), Vec3.UnitY);
                            Console.WriteLine(reflected.X > 0.0f && reflected.Y > 0.0f);
                            Vec3 refracted = VectorMath.Refract(new Vec3(0.0f, -1.0f, 0.0f), Vec3.UnitY, 1.0f / 1.5f);
                            Console.WriteLine(Close(refracted.Length(), 1.0f) && refracted.Y < 0.0f);

                            HitRecord materialHit = new HitRecord();
                            materialHit.Point = Vec3.Zero;
                            materialHit.Normal = Vec3.UnitY;
                            materialHit.FrontFace = true;
                            Vec3 attenuation;
                            Ray scattered;
                            bool matteScattered = matte.Scatter(new Ray(Vec3.Zero, -Vec3.UnitY), materialHit, samples, out attenuation, out scattered);
                            Console.WriteLine(matteScattered && attenuation.X == 0.8f && scattered.Direction.Dot(Vec3.UnitY) > -1.0f);
                            Material metal = new Metal(new Vec3(0.7f, 0.6f, 0.5f), 0.0f);
                            bool metalScattered = metal.Scatter(new Ray(Vec3.Zero, -Vec3.UnitY), materialHit, samples, out attenuation, out scattered);
                            Console.WriteLine(metalScattered && scattered.Direction.Y > 0.0f);
                            Material glass = new Dielectric(1.5f);
                            bool glassScattered = glass.Scatter(new Ray(Vec3.Zero, -Vec3.UnitY), materialHit, samples, out attenuation, out scattered);
                            Console.WriteLine(glassScattered && attenuation.X == 1.0f && Close(scattered.Direction.Length(), 1.0f));
                            materialHit.FrontFace = false;
                            materialHit.Normal = -Vec3.UnitY;
                            Vec3 grazing = new Vec3(0.9f, 0.4358899f, 0.0f).Normalize();
                            bool internallyReflected = glass.Scatter(new Ray(Vec3.Zero, grazing), materialHit, samples, out attenuation, out scattered);
                            Console.WriteLine(internallyReflected && scattered.Direction.X > 0.0f && scattered.Direction.Y < 0.0f);

                            HittableList finalScene = Scene.CreateFinal(RandomGenerator.DefaultSceneSeed);
                            Console.WriteLine(finalScene.Count > 400 && finalScene.Count <= 488);
                        }
                        Console.WriteLine(MemoryDiagnostics.LiveObjects() == baseline);
                    }
                }
                """;
            var result = CompileAndRun(HostedIoSources(harness), memoryDiagnostics: true);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == string.Concat(Enumerable.Repeat("True\n", 24)), result.StandardOutput);
        });

        suite.Run("hosted path tracer deterministic native render", () =>
        {
            var production = Compile(HostedIoSources(includeProgram: true));
            Assert(!production.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, production.GetDiagnostics()));

            const string harness = """
                using System;
                using System.IO;
                namespace HostedIoExample;

                public static class TestProgram
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        FileHandle image = File.Open("image.ppm", FileMode.Create, FileAccess.Write);
                        defer File.Close(image);
                        Console.WriteLine("Rendering test image...");
                        HittableList world = Scene.CreateFinal(RandomGenerator.DefaultSceneSeed);
                        Hittable accelerated = world.BuildBvh();
                        Camera camera = Scene.CreateBookCamera();
                        camera.ImageWidth = 256;
                        camera.SamplesPerPixel = 4;
                        camera.MaxDepth = 8;
                        camera.ProgressRows = 48;
                        camera.Render(image, accelerated, RandomGenerator.DefaultRenderSeed);
                        Console.WriteLine("Done: 256x144.");
                    }
                }
                """;
            var sources = HostedIoSources(harness);
            var generated = Emit(sources);
            Assert(generated.Contains("ct_math_sqrt(", StringComparison.Ordinal) && generated.Contains("ct_math_tan(", StringComparison.Ordinal), "The path tracer omitted its camera or scattering math dependencies.");
            Assert(generated.Contains("ct_h_", StringComparison.Ordinal), "The path tracer did not emit virtual hittable/material dispatch.");
            Assert(generated.Contains("ct_host_file_open(", StringComparison.Ordinal) && generated.Contains("ct_host_file_write_string(", StringComparison.Ordinal), "The path tracer did not emit hosted file output.");
            Assert(!generated.Contains("ct_console_read()", StringComparison.Ordinal) && !generated.Contains("ct_console_read_line()", StringComparison.Ordinal), "The path tracer unexpectedly reads console input.");

            var first = CompileAndRun(sources, captureFile: "image.ppm");
            var second = CompileAndRun(sources, captureFile: "image.ppm");
            Assert(first.ExitCode == 0, first.StandardError);
            Assert(second.ExitCode == 0, second.StandardError);
            Assert(Normalize(first.StandardOutput) == "Rendering test image...\nProgress: 33%.\nProgress: 66%.\nProgress: 100%.\nDone: 256x144.\n", first.StandardOutput);
            Assert(Normalize(second.StandardOutput) == Normalize(first.StandardOutput), second.StandardOutput);
            var firstImage = first.CapturedFile ?? throw new InvalidOperationException("The first path-tracer run did not produce image.ppm.");
            var secondImage = second.CapturedFile ?? throw new InvalidOperationException("The second path-tracer run did not produce image.ppm.");
            Assert(firstImage.SequenceEqual(secondImage), "Repeated seeded path-tracer runs were not byte-identical.");

            const int width = 256;
            const int height = 144;
            var imageHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(firstImage));
            Assert(imageHash == "5709717E43C2752ECE14180A8B5E424B96638D7E34FA726CC60248DDEAB121DF", $"The deterministic 256x144 PPM hash changed: {imageHash}.");
            var tokens = Encoding.ASCII.GetString(firstImage).Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert(tokens.Length == 4 + width * height * 3, $"The PPM contained {tokens.Length} tokens instead of {4 + width * height * 3}.");
            Assert(tokens[0] == "P3" && tokens[1] == "256" && tokens[2] == "144" && tokens[3] == "255", "The acceptance-render PPM header was incorrect.");

            var components = new int[width * height * 3];
            var colors = new HashSet<(int Red, int Green, int Blue)>();
            var darkPixels = 0;
            var nonSkyBottomPixels = 0;
            long topRed = 0;
            long topGreen = 0;
            long topBlue = 0;
            for (var index = 0; index < components.Length; index++)
            {
                Assert(int.TryParse(tokens[index + 4], NumberStyles.None, CultureInfo.InvariantCulture, out components[index]), $"PPM component {index} was not an integer.");
                Assert(components[index] is >= 0 and <= 255, $"PPM component {index} was outside 0 through 255.");
            }
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var pixel = ReadPixel(components, width, x, y);
                    colors.Add(pixel);
                    if (pixel.Red + pixel.Green + pixel.Blue < 240)
                        darkPixels++;
                    if (y < height / 3)
                    {
                        topRed += pixel.Red;
                        topGreen += pixel.Green;
                        topBlue += pixel.Blue;
                    }
                    if (y >= height * 2 / 3 && !(pixel.Blue > pixel.Green && pixel.Green > pixel.Red))
                        nonSkyBottomPixels++;
                }
            }
            Assert(colors.Count > 64, $"The acceptance render contained only {colors.Count} distinct colors.");
            Assert(darkPixels > width, "The acceptance render did not contain shaded object pixels.");
            Assert(topBlue > topGreen && topGreen > topRed, "The upper image region was not the expected blue-biased sky.");
            Assert(nonSkyBottomPixels > width, "The lower image region did not contain enough ground or object pixels.");
        });

        suite.Run("hosted console EOF and I/O exceptions", () =>
        {
            const string source = """
                using System;
                using System.IO;
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        Console.WriteLine(Console.Read());
                        Console.WriteLine(Console.Read());
                        try
                        {
                            FileHandle file = File.Open("missing-file.bin", FileMode.Open, FileAccess.Read);
                            defer File.Close(file);
                        }
                        catch (IOException error)
                        {
                            Console.WriteLine(error.ErrorCode != 0);
                        }
                        try
                        {
                            FileHandle file = File.Open("bad.bin", FileMode.Append, FileAccess.ReadWrite);
                            defer File.Close(file);
                        }
                        catch (IOException error)
                        {
                            Console.WriteLine(error.ErrorCode != 0);
                        }
                    }
                }
                """;
            var result = CompileAndRun(source, standardInput: "A");
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "65\n-1\nTrue\nTrue\n", result.StandardOutput);
        });

        suite.Run("hosted console line edge cases", () =>
        {
            const string lines = """
                using System;
                public static class Program
                {
                    [EntryPoint] public static void Main()
                    {
                        string first = Console.ReadLine();
                        string second = Console.ReadLine();
                        string third = Console.ReadLine();
                        string fourth = Console.ReadLine();
                        Console.WriteLine(first);
                        Console.WriteLine(second.Length);
                        Console.WriteLine(third);
                        Console.WriteLine(fourth == null);
                    }
                }
                """;
            var lineResult = CompileAndRun(lines, standardInput: "alpha\r\n\nlast");
            Assert(lineResult.ExitCode == 0, lineResult.StandardError);
            Assert(Normalize(lineResult.StandardOutput) == "alpha\n0\nlast\nTrue\n", lineResult.StandardOutput);

            const string invalid = """
                using System;
                using System.IO;
                public static class Program
                {
                    [EntryPoint] public static void Main()
                    {
                        try { Console.ReadLine(); }
                        catch (IOException error) { Console.WriteLine(error.ErrorCode != 0); }
                    }
                }
                """;
            var invalidResult = CompileAndRun(invalid, standardInputBytes: [0xf0, 0x28, 0x8c, 0x28, 0x0a]);
            Assert(invalidResult.ExitCode == 0, invalidResult.StandardError);
            Assert(Normalize(invalidResult.StandardOutput) == "True\n", invalidResult.StandardOutput);
        });

        suite.Run("hosted file mode and buffer writes", () =>
        {
            const string source = """
                using System;
                using System.IO;
                using System.Runtime;
                public static class Program
                {
                    private static unsafe void WriteByte(FileMode mode, FileAccess access, byte value)
                    {
                        FileHandle file = File.Open("modes.bin", mode, access);
                        defer File.Close(file);
                        NativeBuffer<byte> buffer = stackalloc byte[1];
                        buffer[0u] = value;
                        File.Write(file, buffer);
                    }
                    private static unsafe void Print()
                    {
                        FileHandle file = File.Open("modes.bin", FileMode.Open, FileAccess.Read);
                        defer File.Close(file);
                        NativeBuffer<byte> buffer = stackalloc byte[4];
                        nuint count = File.Read(file, buffer);
                        nuint index = 0u;
                        while (index < count) { Console.Write((char)buffer[index]); index++; }
                        Console.WriteLine();
                    }
                    private static void Invalid(FileMode mode, FileAccess access)
                    {
                        try
                        {
                            FileHandle file = File.Open("modes.bin", mode, access);
                            defer File.Close(file);
                        }
                        catch (IOException error) { Console.WriteLine(error.ErrorCode != 0); }
                    }
                    private static void InvalidPath()
                    {
                        try
                        {
                            FileHandle file = File.Open("bad\0name.bin", FileMode.Create, FileAccess.Write);
                            defer File.Close(file);
                        }
                        catch (IOException error) { Console.WriteLine(error.ErrorCode != 0); }
                    }
                    [EntryPoint] public static unsafe void Main()
                    {
                        WriteByte(FileMode.Create, FileAccess.Write, (byte)'A');
                        WriteByte(FileMode.Open, FileAccess.Write, (byte)'B');
                        WriteByte(FileMode.Append, FileAccess.Write, (byte)'C');
                        Print();
                        WriteByte(FileMode.Create, FileAccess.ReadWrite, (byte)'D');
                        WriteByte(FileMode.Open, FileAccess.ReadWrite, (byte)'E');
                        Print();
                        Invalid(FileMode.Create, FileAccess.Read);
                        Invalid(FileMode.Append, FileAccess.Read);
                        Invalid(FileMode.Append, FileAccess.ReadWrite);
                        InvalidPath();
                    }
                }
                """;
            var result = CompileAndRun(source, standardInput: string.Empty);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "BC\nE\nTrue\nTrue\nTrue\nTrue\n", result.StandardOutput);
        });

        suite.Run("hosted I/O emission isolation", () =>
        {
            const string source = "using System; public static class Program { [EntryPoint] public static void Main() { Console.WriteLine(42); } }";
            var hosted = Emit(source);
            var esp = Emit(source, new CompilationOptions(CompilationTarget.EspIdf));
            foreach (var symbol in new[] { "ct_console_read", "ct_console_read_line", "ct_host_file_open", "ct_host_file_read" })
            {
                Assert(!hosted.Contains(symbol, StringComparison.Ordinal), $"Unused hosted I/O symbol '{symbol}' changed hosted output.");
                Assert(!esp.Contains(symbol, StringComparison.Ordinal), $"Hosted I/O symbol '{symbol}' changed ESP output.");
            }
        });
    }

    private static SyntaxTree[] HostedIoSources(string? harness = null, bool includeProgram = false)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Examples", "HostedIo");
        var sources = Directory.GetFiles(directory, "*.ct", SearchOption.TopDirectoryOnly)
            .Where(path => includeProgram || !Path.GetFileName(path).Equals("Program.ct", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => SyntaxTree.ParseText(File.ReadAllText(path), path))
            .ToList();
        if (harness is not null)
            sources.Add(SyntaxTree.ParseText(harness, "HostedIo.TestProgram.ct"));
        return [.. sources];
    }

    private static (int Red, int Green, int Blue) ReadPixel(int[] components, int width, int x, int y)
    {
        var offset = (y * width + x) * 3;
        return (components[offset], components[offset + 1], components[offset + 2]);
    }
}
