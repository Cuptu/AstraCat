# Third-party notices

AstraCat is distributed under `GPL-3.0-only`. The application also uses the following third-party components. Each component remains subject to its own license.

| Component | Use | License / source |
|---|---|---|
| .NET Runtime | Self-contained Windows runtime | MIT; <https://github.com/dotnet/runtime> |
| Avalonia 12.1.1 | Desktop UI framework | MIT; <https://github.com/AvaloniaUI/Avalonia> |
| Avalonia Fonts Inter | Bundled UI font resources | SIL Open Font License 1.1; <https://github.com/AvaloniaUI/Avalonia> |
| Material.Icons / Material.Icons.Avalonia 3.0.2 | Interface icons | MIT; <https://github.com/AvaloniaUtils/Material.Icons.Avalonia> |
| mpv / libmpv | Media playback and subtitle rendering | GPL-2.0-or-later; <https://github.com/mpv-player/mpv> |
| FFmpeg | Media probing, conversion and export | GPL build; <https://github.com/BtbN/FFmpeg-Builds> |

The exact libmpv archive, build version and SHA-256 are recorded in `runtime/tools/mpv/LIBMPV_SOURCE.md`. Release packages include the FFmpeg license and a manifest containing the version and hashes of the distributed FFmpeg files.

Speech-recognition model weights are not included in the source repository or Windows installer. They are downloaded separately, and their model cards and licenses apply independently.
