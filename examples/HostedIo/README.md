# Hosted path tracer

This hosted C~ project renders the final randomized sphere scene from the first [Ray Tracing in One Weekend](https://raytracing.github.io/books/RayTracingInOneWeekend.html) book to `image.ppm`. It is an original single-precision C~ adaptation with deterministic random sampling.

The renderer demonstrates the draft 0.12 object model rather than adding special graphics intrinsics. It uses `System.Vec3`, operator overloads, virtual hittable and material methods, arrays of managed references, automatic reference counting, recursive ray scattering, `System.Math`, and an owned file handle closed through `defer`.

## Build and render

Run from the example directory so the image is written beside this README:

```powershell
Push-Location .\examples\HostedIo
dotnet run --project ..\..\CTilde.Cli -c Release --no-launch-profile -- --project .\ctilde.json --build
.\build\HostedIo.exe
Pop-Location
```

The default is the book-quality profile: 1200×675 pixels, 500 samples per pixel, and at most 50 reflected or refracted bounces. This is intentionally expensive and can take a long time. The executable reports progress every 25 completed rows:

```text
Rendering image.ppm...
Rendered rows: 25/675.
Rendered rows: 50/675.
...
Rendered rows: 675/675.
Done: 1200x675.
```

The output is a plain-text P3 PPM:

```text
P3
1200 675
255
```

Open it with a PPM-capable viewer such as GIMP or ImageMagick.

## Renderer structure

- `RandomGenerator.ct` supplies a project-local xorshift32 generator. Seed `0x00C0FFEE` produces repeatable scene placement, antialiasing, material scattering, and defocus samples. It is not a cryptographic generator.
- `Hittables.ct` contains hit records, spheres, and the fixed-capacity 512-object world. ARC owns the sphere array and material references without creating cycles.
- `Materials.ct` implements Lambertian, fuzzy metal, and dielectric scattering, including reflection, refraction, total internal reflection, and Schlick reflectance.
- `Camera.ct` implements jittered multisampling, recursive color evaluation, gamma-2 correction, a positionable camera, and thin-lens defocus blur.
- `Scene.ct` constructs the randomized small spheres, ground, and three large feature spheres from the book's final scene.

The conformance runner uses the same sources with a 256×144, four-sample, eight-bounce camera. That acceptance profile validates an exact deterministic PPM hash and memory ownership without running the full production render during every compiler test.

Emit-only and manual native compilation remain available:

```powershell
dotnet run --project ..\..\CTilde.Cli -c Release --no-launch-profile -- --project .\ctilde.json -o .\build\generated\ctilde_program.c
cl /nologo /std:clatest /O2 /W4 /WX /wd4702 /Fe:.\build\HostedIo.exe .\build\generated\ctilde_program.c
```
