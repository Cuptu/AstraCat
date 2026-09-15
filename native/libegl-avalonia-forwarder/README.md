# Avalonia ANGLE EGL ABI bridge

The pinned upstream libmpv dynamically loads `LIBEGL.DLL` and requests standard
`eglFoo` exports. Avalonia 12.1.1 ships a monolithic `av_libglesv2.dll` whose EGL
exports use the `EGL_Foo` convention.

`libEGL.def` creates PE export forwarders from the standard names to Avalonia's
names. A forwarder is resolved by the Windows loader into the same
`av_libglesv2.dll` module that Avalonia has already loaded; this DLL does not
contain or load a second EGL implementation.

Build the checked x64 binary with Zig 0.16.0:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./native/libegl-avalonia-forwarder/build.ps1 -ZigPath C:/tools/zig/zig.exe
```

The result is written to `runtime/tools/mpv/libEGL.dll`. `AstraCat.csproj`
places it in the application root because mpv calls `LoadLibraryW` with only the
DLL basename.

This bridge keeps the pinned third-party libmpv usable. For a custom libmpv
build, prefer the corresponding loader change in
`video/out/opengl/angle_dynamic.c`: call `GetModuleHandleW` for
`av_libglesv2.dll`, resolve `eglFoo` as `EGL_Foo`, and retain the normal
`LIBEGL.DLL` fallback. The local mpv source tree contains that implementation.
