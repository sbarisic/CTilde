# Hosted path tracer

This hosted C~ project renders the final randomized sphere scene from the first [Ray Tracing in One Weekend](https://raytracing.github.io/books/RayTracingInOneWeekend.html) book to `image.ppm`. It is an original single-precision C~ adaptation with deterministic random sampling.

The renderer demonstrates the draft 0.14 object model rather than adding special graphics intrinsics. It uses `System.Vec3`, operator overloads, virtual hittable and material methods, arrays of managed references, automatic reference counting, recursive ray scattering, `System.Math`, and an owned file handle closed through `defer`.

## Build and render

Run from the example directory so the image is written beside this README:

```powershell
Push-Location .\examples\HostedIo
dotnet run --project ..\..\CTilde.Cli -c Release --no-launch-profile -- --project .\ctilde.json --build
.\build\HostedIo.exe
Pop-Location
```

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

The draft 0.14 acceptance run used the modular MSVC Release build with LTO on 2026-08-20. The full 1200x675, 500-sample, 50-bounce render completed in 4,420.348 seconds (1:13:40.348) and produced SHA-256 `4084366E15EACF65F73758C22C0A12589B30EC09362B9749DA690A7D71B1D5A4`. Elapsed time is machine-specific; the hash is the reproducibility record.

## Renderer structure

- `RandomGenerator.ct` supplies a project-local xorshift32 generator. Scene construction and rendering use separate seeds. Every `(column, row, sample)` reseeds its own stream through a documented 32-bit avalanche mix, so output does not depend on row scheduling. It is not a cryptographic generator.
- `Hittables.ct` contains hit records, robust slab-tested axis-aligned bounds, spheres, the fixed-capacity 512-object reference world, and a deterministic midpoint BVH. ARC owns the sphere array, BVH nodes, and material references without creating cycles.
- `Materials.ct` implements Lambertian, fuzzy metal, and dielectric scattering, including reflection, refraction, total internal reflection, and Schlick reflectance.
- `Camera.ct` implements jittered multisampling, recursive color evaluation, gamma-2 correction, a positionable camera, and thin-lens defocus blur.
- `Scene.ct` constructs the randomized small spheres, ground, and three large feature spheres, then builds the BVH once. Production rendering uses the BVH; conformance compares it with the original list traversal.

The conformance runner uses the same sources with a 256×144, four-sample, eight-bounce camera. That acceptance profile validates SHA-256 `5709717E43C2752ECE14180A8B5E424B96638D7E34FA726CC60248DDEAB121DF`, list/BVH hit equivalence, AABB edge cases, scheduling-independent sample streams, and a fixed-ray primitive-test reduction without running the full production render during every compiler test.

Emit-only and manual native compilation remain available:

```powershell
dotnet run --project ..\..\CTilde.Cli -c Release --no-launch-profile -- --project .\ctilde.json --c-layout unity -o .\build\generated\ctilde_program.c
cl /nologo /std:clatest /O2 /W4 /WX /wd4702 /Fe:.\build\HostedIo.exe .\build\generated\ctilde_program.c
```
