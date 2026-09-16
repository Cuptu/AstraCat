# AstraCore native runtime

AstraCore is a thin, stable C ABI in front of pinned FFmpeg libraries. It does not expose FFmpeg structs to .NET. libmpv, the FFmpeg command-line tools, and AstraCore.Native all resolve the same shared `av*` libraries.

Standalone upstream repository: **[Cuptu/AstraCore](https://github.com/Cuptu/AstraCore)**.

## ABI Evolution

- **ABI v1**:
  - `ac_probe_utf8`: Container and stream metadata extraction without frame decoding.
- **ABI v2**:
  - `ac_check_encoder`: Hardware and software encoder probe (e.g. `h264_nvenc`, `hevc_nvenc`, `av1_nvenc`, `libx264`).
  - `ac_extract_waveform_peaks_utf8`: Direct in-memory audio decode, resample, and peak computation into caller-provided float buffer without spawning sub-processes.
  - `ac_extract_audio_wav_utf8`: Direct 16kHz PCM WAV extraction for ASR pipelines.
- **ABI v3**:
  - `ac_cancel_callback`: Cooperative cancellation via `int (*ac_cancel_callback)(void *opaque)`.
  - `ac_probe_cancel_utf8`, `ac_extract_waveform_peaks_cancel_utf8`, `ac_extract_audio_wav_cancel_utf8`: Cancellation-aware variants hooked into FFmpeg `AVIOInterruptCB` and decoding packet/frame loops.
  - Memory safety: Stricter buffer reallocation invariants (`av_realloc` failure checks prior to capacity updates), ensuring zero memory leaks and bounds integrity.
  - Transparent error diagnostics: Detailed libav error reporting via `error_buffer`.
- **ABI v4**:
  - `ac_change_audio_speed_cancel_utf8` / `ac_change_audio_speed_utf8`: Pitch-preserving audio tempo adjustment (0.25x - 4.0x) via `atempo` filter graph.
  - `ac_trim_media_cancel_utf8` / `ac_trim_media_utf8`: Lossless stream copy trimming with automatic PTS/DTS timestamp rebasing.
  - `ac_extract_audio_stream_utf8`: Direct audio stream extraction without transcoding.
  - Multiplatform: Windows (`win-x64`), Linux (`linux-x64`), macOS (`osx-arm64`).

## Runtime Packages & RPATH

Runtime packages are RID-specific and described by `astracore-runtime.json`.
- Windows: Uses private DLL search directory or side-by-side deployment with `AstraCore.Native.dll`.
- macOS: Uses `@loader_path`/`@rpath`.
- Linux: Uses `$ORIGIN` RUNPATH.
- GPU drivers and hardware acceleration runtimes (CUDA, NVENC, VAAPI, VideoToolbox) are supplied by the host operating system.

