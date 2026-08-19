# Hosted ray tracer

This hosted C~ project renders a deterministic 256×144 ray-traced image to `image.ppm`. The scene contains a normal-shaded sphere above a ground sphere, with a white-to-blue sky behind them.

The implementation is an original C~ adaptation of the introductory camera, ray, sphere, normal, and PPM concepts in [Ray Tracing in One Weekend](https://raytracing.github.io/books/RayTracingInOneWeekend.html). It uses value-type vectors and rays, `System.Math`, nested render loops, automatic reference counting for temporary strings, and an owned file handle closed through `defer`.

Build and run from the example directory so the image is written beside this README:

```powershell
Push-Location .\examples\HostedIo
dotnet run --project ..\..\CTilde.Cli -c Release --no-launch-profile -- --project .\ctilde.json --build --configuration release
.\build\HostedIo.exe
Pop-Location
```

Expected console output:

```text
Rendering image.ppm...
Done: 256x144.
```

`image.ppm` uses the plain-text P3 format. Open it with an image editor or viewer that supports PPM, such as GIMP or ImageMagick. The file starts with:

```text
P3
256 144
255
```

The renderer sends one ray through the center of each pixel. It selects the closest intersection with either sphere, colors hits from their surface normals, and blends white and blue for rays that miss. It intentionally omits random sampling, antialiasing, materials, recursive bounces, gamma correction, and multithreading so the complete example remains approachable.

`FileHandle` is move-only. The successful `File.Open` result is immediately reserved by `defer File.Close(image);`, which closes the image on every normal C~ control-flow exit.

Emit-only and manual native compilation remain available. Run these commands from the same example directory:

```powershell
dotnet run --project ..\..\CTilde.Cli -c Release --no-launch-profile -- --project .\ctilde.json -o .\build\generated\ctilde_program.c
cl /nologo /std:clatest /O2 /W4 /WX /Fe:.\build\HostedIo.exe .\build\generated\ctilde_program.c
.\build\HostedIo.exe
```
