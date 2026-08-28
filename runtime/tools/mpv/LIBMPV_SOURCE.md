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
