# AstraCore media runtime

AstraCore is AstraCat's RID-specific native media bundle. It is also maintained as an independent upstream repository at **[Cuptu/AstraCore](https://github.com/Cuptu/AstraCore)** with its own multiplatform CI/CD and release cycles. It shares one FFmpeg ABI and one libass instance between libmpv, the FFmpeg tools, and the thin `AstraCore.Native` C ABI (ABI v4). The legacy prebuilt runtime remains a development fallback until the new bundle passes the size, dependency, software, and GPU release gates.


## Subtitle boundary

Video subtitle preview does not call libass directly. C# writes an ASS file and
uses the public libmpv Client API commands `sub-add`, `sub-reload`, and
`sub-remove`; mpv invokes libass internally and renders through its OpenGL
Render API. Audio-only preview is an Avalonia text overlay. FFmpeg is involved
only in subtitle format conversion and export-time subtitle burning.

Consequently, subtitle preview must remain owned by libmpv. SRT/ASS sidecar
conversion can move to managed code, while export-time burning remains a
libavfilter/libass responsibility.

## Source and build contract

Pinned source commits are prepared under ignored `artifacts/` storage:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-astracore-sources.ps1
```

The Windows build requires MSYS2 CLANG64 and the dependencies listed in
`.github/workflows/astracore.yml`:

```powershell
$env:ASTRACAT_MSYS2_BASH = 'C:\msys64\usr\bin\bash.exe'
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-astracore.ps1
```

The build produces `artifacts/astracore/win-x64`, generates
`astracore-runtime.json`, verifies every hash and required FFmpeg capability,
rejects a statically bloated libmpv, inspects native imports, and enforces a
200 MiB runtime limit. To package it:

```powershell
./package-release.ps1 -Version 0.1.2-DEV -AstraCoreDir artifacts/astracore/win-x64
```

Formal packages contain only `runtime/tools/astracore/<RID>`. The environment
variable `ASTRACAT_MEDIA_RUNTIME` selects an explicit development runtime;
system FFmpeg lookup is disabled unless `ASTRACAT_ALLOW_SYSTEM_MEDIA_TOOLS=1`.

The pinned Windows profile builds an 8-bit-only x265 and a libplacebo core
without Vulkan, shaderc, SPIR-V Cross, libdovi or VAAPI. Screenshots use the
shared FFmpeg PNG encoder, so mpv does not pull in a second JPEG library.
HarfBuzz is built with its core OpenType shaper but without GLib, PCRE2 or
Graphite plugin dependencies. The Windows libass build keeps DirectWrite font
discovery and fallback but omits Linux-only Fontconfig, Expat and gettext;
Linux RID builds retain Fontconfig. Locally built PE images are stripped, and
the packaging step
walks direct COFF imports instead of copying loader candidates. The verified
2026-08-31 build is 37.58 MiB (56 manifest files); the 200 MiB value is only
the release rejection ceiling, not an expected package size.

## Windows preview verification

Avalonia's bundled ANGLE exposes the `eglQueryDeviceAttribEXT` and
`eglQueryDeviceStringEXT` entry points used by mpv, but its extension string
does not advertise `EGL_EXT_device_query`. Upstream mpv rejects `d3d11-egl`
from that string check before probing the working entry points. The pinned mpv
source is therefore built from an isolated copy with the audited
`native/astracore/patches/mpv-angle-device-query.patch`. The patch removes only
the extension-token gate; the function-pointer and returned-device checks stay
in place. Its presence is recorded in the runtime manifest and enforced by the
runtime validator.

On an NVIDIA RTX 4060, the formal build completed three-cycle Render API smoke
tests for H.264, HEVC 10-bit, AV1 and VP9 with `hwdec-current=d3d11va` and zero
decoder/display drops. Forced `d3d11va-copy` and `hwdec=no` tests also completed
three cycles with zero drops. The final DirectWrite-only runtime's sixty-second
runs measured 59.44 FPS and 25.42% of one CPU core for 1080p60 H.264, and
29.79 FPS and 20.47% for 4K30 HEVC 10-bit. Both used direct D3D11VA with zero
drops and zero measured A/V-sync error. Intel and AMD real-machine results
remain mandatory before a production release; encoder-name enumeration is not
accepted as hardware proof.

## Native ABI roadmap

ABI version 1 exposes size-versioned `ac_media_info` and `ac_probe_utf8`.
ABI version 2 expands the memory C ABI to eliminate routine FFmpeg subprocess calls across the pipeline:
1. `ac_check_encoder`: Direct in-process probing of hardware/software encoders via `avcodec_find_encoder_by_name` and `avcodec_open2`, eliminating 10+ FFmpeg CLI processes and temp disk I/O on startup.
2. `ac_extract_waveform_peaks_utf8`: Direct in-memory audio decoding and resampling via `libavformat`, `libavcodec`, and `libswresample` with CPU-cache bucket peak reduction directly into C# arrays, bypassing standard output piping.
3. `ac_extract_audio_wav_utf8`: In-process extraction of 16 kHz mono 16-bit PCM WAV for ASR engines (Qwen / Parakeet).
4. Subtitle conversion (SRT <-> ASS) handled in-memory in managed code (`TryConvertSubtitleFormatInMemory`).

Full export rendering (`ExportCoreAsync`) remains delegated to `ffmpeg.exe` for complex multi-stream filter graphs until an isolated worker pipeline is introduced.

## Platform contract

| RID | Render path | Hardware decode | Hardware encode |
| --- | --- | --- | --- |
| `win-x64` | Avalonia ANGLE + libmpv OpenGL | D3D11VA, copy, software | NVENC, QSV, AMF |
| `osx-arm64` / `osx-x64` | native OpenGL Render API | VideoToolbox, copy, software | VideoToolbox |
| `linux-x64` / `linux-arm64` | X11/XWayland OpenGL | VAAPI, copy, software; optional NVDEC | VAAPI; optional NVENC/QSV |

Windows registers the private DLL directory before loading libmpv. macOS
packages must use `@loader_path`/`@rpath` and sign nested dylibs before the app.
Linux packages must use `$ORIGIN` RUNPATH and must not bundle GPU drivers.
Hardware acceleration is accepted only when `hwdec-current` and drop counters
are verified on real vendor hardware; `auto-safe` alone is not proof.

The exact Windows test matrix, artifact hashes, performance measurements and
remaining vendor gate are recorded in
[`ASTRACORE_VERIFICATION.md`](ASTRACORE_VERIFICATION.md).
