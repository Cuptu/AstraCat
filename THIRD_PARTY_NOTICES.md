# Third-party notices

AstraCat is distributed under `GPL-3.0-only`. The application also uses the following third-party components. Each component remains subject to its own license.

| Component | Use | License / source |
|---|---|---|
| .NET Runtime | Self-contained Windows runtime | MIT; <https://github.com/dotnet/runtime> |
| Avalonia 12.1.1 | Desktop UI framework | MIT; <https://github.com/AvaloniaUI/Avalonia> |
| Avalonia Fonts Inter | Bundled UI font resources | SIL Open Font License 1.1; <https://github.com/AvaloniaUI/Avalonia> |
| Material.Icons / Material.Icons.Avalonia 3.0.2 | Interface icons | MIT; <https://github.com/AvaloniaUtils/Material.Icons.Avalonia> |
| mpv / libmpv | Media playback and subtitle rendering | GPL-2.0-or-later; <https://github.com/mpv-player/mpv> |
| FFmpeg | Shared decoding, probing, conversion and export runtime | GPL build; <https://ffmpeg.org/> |
| libass | ASS/SSA subtitle rendering through mpv and FFmpeg | ISC; <https://github.com/libass/libass> |
| HarfBuzz | Complex OpenType subtitle shaping | MIT; <https://github.com/harfbuzz/harfbuzz> |
| libplacebo | mpv video rendering support library | LGPL-2.1-or-later; <https://code.videolan.org/videolan/libplacebo> |
| x264 / x265 / SVT-AV1 | Software video encoding | GPL-2.0-or-later / GPL-2.0-only / BSD-3-Clause; component upstream projects |
| sherpa-onnx | Offline speech recognition and VAD native inference engine | Apache-2.0; <https://github.com/k2-fsa/sherpa-onnx> |
| ONNX Runtime | High-performance cross-platform ML inference engine | MIT; <https://github.com/microsoft/onnxruntime> |
| Google ANGLE | OpenGL ES to Direct3D translation / EGL forwarder | BSD-3-Clause; <https://chromium.googlesource.com/angle/angle> |
| SkiaSharp / HarfBuzzSharp | 2D vector graphics and text rendering | MIT; <https://github.com/mono/SkiaSharp> |

The exact source commits, versions, build configuration, feature contract and
SHA-256 values are recorded in `astracore-runtime.json`. Release packages carry
the corresponding license payloads under the AstraCore `LICENSES` directory.

Speech-recognition model weights are not included in the source repository or Windows installer. They are downloaded separately, and their model cards and licenses apply independently.
