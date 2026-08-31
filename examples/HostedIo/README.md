# Hosted path tracer

This hosted C~ project renders the final randomized sphere scene from the first [Ray Tracing in One Weekend](https://raytracing.github.io/books/RayTracingInOneWeekend.html) book into a progressively updated Raylib 6.0 window. It is an original single-precision C~ adaptation with deterministic random sampling.

The renderer demonstrates the object model, managed threads and atomics, scalar-layout geometry, Draft 0.39 native imports, hosted runtime-file staging, and the opt-in hosted-x64 SIMD pipeline. Production rendering divides the image into twelve horizontal rectangles, one per worker thread. Each worker fills its rectangle linearly from left to right and top to bottom while tracing four independently positioned pixels at a time through `System.Simd.Vec3x4`; `RenderScalar` retains the independent one-ray, in-memory implementation for benchmarks and focused correctness comparisons.

## Build and render

From the repository root:

```powershell
dotnet run --project .\CTilde.Cli -c Release --no-launch-profile -- --project .\examples\HostedIo\ctilde.json --run
```

The manifest selects and stages `raylib.dll` beside the executable for native Windows builds. WSL GCC and Clang builds instead stage the official `libraylib.so.6.0.0` payload as `libraylib.so` and link the executable with an `$ORIGIN` runtime search path. No system Raylib installation is required.

`--run` rebuilds before it launches the renderer. Use `--build` when you only want to produce the executable and its selected runtime library.

The default is the book-quality profile: 1200×675 pixels, 500 samples per pixel, and at most 50 reflected or refracted bounces. This is intentionally expensive and can take a long time. Workers publish completed pixels through release/acquire counters; the main thread alone writes them to Raylib's CPU image, uploads dirty regions, and pumps window events at no more than 60 frames per second. Closing the window atomically cancels the shared render state and joins all workers before native resources are released. When rendering completes, the final image remains visible until the window is closed.

Console progress reports every newly reached whole percentage without duplicates:

```text
Rendering image...
Progress: 1%.
Progress: 2%.
...
Progress: 100%.
Done: 1200x675.
```

## Performance and determinism

The Draft 0.38 performance gate uses modular MSVC Release with LTO, `CreateFinal`, width 320, 16 samples, and depth 16. After two warmups, nine interleaved pairs measured 8,069.56 ms for `RenderScalar` and 3,041.98 ms for packet rendering: a 2.65x median speedup, with packets faster in all nine pairs. Those figures predate the Raylib window refactor; the benchmark now hashes an in-memory RGBA buffer and excludes file and presentation work. Run `Test/Test-HostedSimd.ps1` from the repository root to produce a current machine-specific report.

The conformance runner uses the same sources with small odd-width cameras. Its checked RGBA buffer hashes cover deterministic packet output and repeated rendering. Focused checks cover tail masks, list/BVH hit behavior, AABB edge cases, divergent child order and materials, rejection sampling, and scalar formulas without running the full production profile during every compiler test.

## Renderer structure

- `RandomGenerator.ct` supplies scalar xorshift32 and masked `U32x4` packet generators. Every `(column, row, sample)` lane is reseeded through the same documented 32-bit avalanche mix, so output does not depend on row scheduling. It is not a cryptographic generator.
- `Hittables.ct` contains scalar and structure-of-arrays hit records, robust slab-tested bounds, spheres, the fixed-capacity 512-object reference world, and a deterministic midpoint BVH. Packet traversal keeps per-lane closest distances and prunes children without scalar fallback.
- `Materials.ct` implements scalar and masked packet Lambertian, fuzzy-metal, and dielectric scattering, including divergent reflection, refraction, total internal reflection, and Schlick reflectance.
- `Camera.ct` constructs rays directly from packed columns, jitter, and defocus samples. It implements recursive masked color evaluation, gamma-2 correction, a positionable camera, thin-lens defocus blur, `PixelPacket` output, and complete in-memory scalar and packet renders.
- `ParallelRenderer.ct` gives each of twelve managed workers one horizontal rectangle to fill in scanline order, publishes completed pixel indices with release/acquire atomics, and keeps Raylib access on the primary thread.
- `Pixels.ct` defines the exact four-byte RGBA pixel and deterministic buffer checksum.
- `Raylib.ct` contains the minimal natural-layout Raylib ABI, `[NativeImport("raylib")]` declarations, CPU-image ownership, dirty-region uploads, and window lifecycle.
- `Scene.ct` constructs the randomized small spheres, ground, and three large feature spheres, then builds the BVH once.

Raylib 6.0 runtime files, the public header, license, release URLs, and SHA-256 provenance are recorded under `third_party/raylib/6.0`. Only the native functions reached by HostedIo are emitted into its import table.
