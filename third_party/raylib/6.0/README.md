# raylib 6.0 runtime files

These files come from the official raylib 6.0 release:

- `windows-x64/raylib.dll` from `raylib-6.0_win64_msvc16.zip`
- `linux-x64/libraylib.so.6.0.0` from `raylib-6.0_linux_amd64.tar.gz`
- `include/raylib.h` and `LICENSE` from the same release packages

Release: <https://github.com/raysan5/raylib/releases/tag/6.0>

SHA-256:

- `raylib.dll`: `C62606798C3F736B479DB7721AED884102060541B743FD81BE3E687AC6DE3E67`
- `libraylib.so.6.0.0`: `1041653DD5C1CB8C67494FA398A296520D3FFEC20CF1BF71B1EAA4B96AC61AEE`
- `raylib.h`: `1842111E48260E622D0FF2E6F4AC6141508D6F369791265BA518E7502C760CCF`
- `LICENSE`: `185AE1102A3C10B1FBD8413C37024EA5B41075D30CB124DC8E88850EF3CD6392`

The Linux archive stores `libraylib.so` and `libraylib.so.600` as symbolic links. The repository keeps only the real `libraylib.so.6.0.0` file; C~ stages it beside Linux executables as `libraylib.so`.
