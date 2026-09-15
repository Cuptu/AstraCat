# Pinned libmpv core

- Upstream: `mpv-player/mpv`
- Distributor: `shinchiro/mpv-winbuild-cmake`
- Release: `20260828`
- Archive: `mpv-dev-x86_64-20260828-git-182fa6ca49.7z`
- Archive SHA-256: `9EFD04D351E09ECA350D01DA1B8B0C406537C037537111BA65AB43C91905635B`
- Build version: `v0.41.0-1011-g182fa6ca4`
- Client API: `2.5`
- File: `libmpv-2.dll`
- SHA-256: `82BE8EDD8E61BD7A02458EFAF648D6414E262D59E9873D516A2E107579618FE2`
- Download: `https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20260828`

Run `scripts/prepare-native-deps.ps1` to download and verify this dependency.
The release workflow repeats both archive and DLL hash checks before packaging.
AstraCat loads the library in-process and uses mpv's OpenGL Render API;
`mpv.exe` is not shipped or started by the application.

## Avalonia ANGLE EGL bridge

- File: `libEGL.dll`
- SHA-256: `22005170E92E7629012A7A524D983632383242DC684EFA80A1C2FD286A7902D8`
- Source: `native/libegl-avalonia-forwarder`
- Toolchain used for the checked binary: Zig 0.16.0, target `x86_64-windows-gnu`

This small PE forwarding DLL maps the standard `eglFoo` exports requested by
the pinned libmpv build to Avalonia's `av_libglesv2.EGL_Foo` exports. Windows
resolves the forwarders into the already loaded Avalonia ANGLE module, so mpv
and `OpenGlControlBase` observe the same EGL display, context, and thread-local
state. It contains no EGL implementation of its own.

## Experimental native D3D11 Render API build

The upstream source tree for mpv contains
an experimental `MPV_RENDER_API_TYPE_D3D11` backend. It renders to a BGRA8
D3D11 NT shared texture synchronized with a keyed mutex, allowing Avalonia's
Composition GPU interop to consume frames without a CPU readback/upload.

The validation DLL is `build-d3d11\libmpv-2.dll` (SHA-256
`B4539257BC344B19F92E0D1E3EAAC46887F8D4CA1D1E8DBB1078C1768BA1D533`). It is an MSYS2
CLANG64 development build with dynamic dependencies and is intentionally not
the pinned runtime DLL above. See `docs/MPV_D3D11VA_ANGLE_INTEROP.md`, section
14, for implementation and benchmark results.
