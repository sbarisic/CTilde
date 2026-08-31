# Hosted path tracer

This hosted C~ project renders the final randomized sphere scene from the first [Ray Tracing in One Weekend](https://raytracing.github.io/books/RayTracingInOneWeekend.html) book to `image.ppm`. It is an original single-precision C~ adaptation with deterministic random sampling.

The renderer demonstrates the object model, scalar-layout geometry, and opt-in hosted-x64 SIMD pipeline. Production rendering traces four columns at a time through `System.Simd.Vec3x4`; `RenderScalar` retains the independent one-ray implementation for benchmarks and focused correctness comparisons. The project also uses managed references, automatic reference counting, recursive ray scattering, `System.Math`, and an owned file handle closed through `defer`.

## Build and render

Run from the example directory so the image is written beside this README:

```powershell
Push-Location .\examples\HostedIo
dotnet run --project ..\..\CTilde.Cli -c Release --no-launch-profile -- --project .\ctilde.json --run
Pop-Location
```

`--run` rebuilds before it launches the renderer. Use `--build` when you only want to produce `build/HostedIo.exe`.

The default is the book-quality profile: 1200×675 pixels, 500 samples per pixel, and at most 50 reflected or refracted bounces. This is intentionally expensive and can take a long time. The executable reports every newly reached whole percentage without printing duplicate percentages:

```text
Rendering image.ppm...
Progress: 1%.
Progress: 2%.
...
Progress: 100%.
Done: 1200x675.
```

The output is a plain-text P3 PPM:

```text
P3
1200 675
255
```

Open it with a PPM-capable viewer such as GIMP or ImageMagick.

The Draft 0.38 performance gate uses modular MSVC Release with LTO, `CreateFinal`, width 320, 16 samples, and depth 16. After two warmups, nine interleaved pairs measured 8,069.56 ms for `RenderScalar` and 3,041.98 ms for packet rendering: a 2.65x median speedup, with packets faster in all nine pairs. The optimized checksum for this profile is `3FA451E880DA69D2E30663F957053760085AA5C37D2CAF2E6454A4A4BFE7837B`. Run `Test/Test-HostedSimd.ps1` from the repository root to reproduce the machine-specific report.

The report-only full 1200x675, 500-sample, depth-50 comparison completed in 3,623.72 seconds (1:00:23.72) for `RenderScalar` and 1,495.34 seconds (24:55.34) for packets, a 2.42x speedup. The scalar checksum remains `4084366E15EACF65F73758C22C0A12589B30EC09362B9749DA690A7D71B1D5A4`; the optimized checksum is `723C4DCFEC3776326D17E04C42AB83B84FFCF847F4A4F59464C31C173D243671`. These elapsed times are machine-specific; the checksums are the reproducibility records.

## Renderer structure

- `RandomGenerator.ct` supplies both scalar xorshift32 and masked `U32x4` packet generators. Every `(column, row, sample)` lane is reseeded through the same documented 32-bit avalanche mix, so output does not depend on row scheduling. It is not a cryptographic generator.
- `Hittables.ct` contains scalar and structure-of-arrays hit records, robust slab-tested bounds, spheres, the fixed-capacity 512-object reference world, and a deterministic midpoint BVH. Packet traversal keeps per-lane closest distances and prunes children without scalar fallback. ARC owns the sphere array, BVH nodes, and material references without creating cycles.
- `Materials.ct` implements scalar and masked packet Lambertian, fuzzy-metal, and dielectric scattering, including divergent reflection, refraction, total internal reflection, and Schlick reflectance.
- `Camera.ct` constructs rays directly from packed columns, jitter, and defocus samples. It implements recursive masked color evaluation, gamma-2 correction, a positionable camera, thin-lens defocus blur, and final-lane-only extraction for PPM output.
- `Scene.ct` constructs the randomized small spheres, ground, and three large feature spheres, then builds the BVH once. Production rendering uses the BVH; conformance compares it with the original list traversal.

The conformance runner uses the same sources with a small odd-width camera. Its optimized golden is SHA-256 `799529CAE793F5C425EB3A15805991ACA7926EE66733906D940935093CAA6FB0` under MSVC, GCC 13.3, and Clang 18.1. Focused checks cover tail masks, list/BVH hit behavior, AABB edge cases, divergent child order and materials, rejection sampling, and deterministic repeated renders without running the full production profile during every compiler test.

Emit-only and manual native compilation remain available:

```powershell
dotnet run --project ..\..\CTilde.Cli -c Release --no-launch-profile -- --project .\ctilde.json --c-layout unity -o .\build\generated\ctilde_program.c
cl /nologo /std:clatest /O2 /W4 /WX /wd4702 /Fe:.\build\HostedIo.exe .\build\generated\ctilde_program.c
```
