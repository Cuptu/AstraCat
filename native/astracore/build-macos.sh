#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

ARCH="$(uname -m)"
RID_DEFAULT="osx-$ARCH"
if [[ "$ARCH" == "arm64" ]]; then
    RID_DEFAULT="osx-arm64"
elif [[ "$ARCH" == "x86_64" ]]; then
    RID_DEFAULT="osx-x64"
fi

: "${ASTRACORE_REPO:=$REPO_ROOT}"
: "${ASTRACORE_SOURCES:=$REPO_ROOT/artifacts/astracore-sources}"
: "${ASTRACORE_FFMPEG_SOURCE:=$ASTRACORE_SOURCES/ffmpeg}"
: "${ASTRACORE_MPV_SOURCE:=$ASTRACORE_SOURCES/mpv}"
: "${ASTRACORE_LIBASS_SOURCE:=$ASTRACORE_SOURCES/libass}"
: "${ASTRACORE_LIBPLACEBO_SOURCE:=$ASTRACORE_SOURCES/libplacebo}"
: "${ASTRACORE_X265_SOURCE:=$ASTRACORE_SOURCES/x265}"
: "${ASTRACORE_HARFBUZZ_SOURCE:=$ASTRACORE_SOURCES/harfbuzz}"
: "${ASTRACORE_DAV1D_SOURCE:=$ASTRACORE_SOURCES/dav1d}"
: "${ASTRACORE_OUTPUT:=$REPO_ROOT/artifacts/astracore/$RID_DEFAULT}"

build_root="$ASTRACORE_REPO/artifacts/astracore-build/$RID_DEFAULT"
prefix="$build_root/prefix"
rm -rf "$build_root" "$ASTRACORE_OUTPUT"
brew_prefix="$(brew --prefix 2>/dev/null || echo /opt/homebrew)"
export PKG_CONFIG_PATH="$prefix/lib/pkgconfig:$brew_prefix/lib/pkgconfig:$brew_prefix/share/pkgconfig${PKG_CONFIG_PATH:+:$PKG_CONFIG_PATH}"
for d in "$brew_prefix"/opt/*/lib/pkgconfig; do
    [[ -d "$d" ]] && PKG_CONFIG_PATH="$d:$PKG_CONFIG_PATH"
done
export PKG_CONFIG_PATH
export CPATH="$prefix/include:$brew_prefix/include:$brew_prefix/opt/vulkan-headers/include:${CPATH:-}"
export LIBRARY_PATH="$prefix/lib:$brew_prefix/lib:${LIBRARY_PATH:-}"
export DYLD_LIBRARY_PATH="$prefix/lib:${DYLD_LIBRARY_PATH:-}"


echo "==> Building minimal HarfBuzz (CoreText shaper)..."
meson setup "$build_root/harfbuzz" "$ASTRACORE_HARFBUZZ_SOURCE" \
    --prefix "$prefix" --buildtype release --default-library shared \
    --wrap-mode nofallback --auto-features disabled \
    -Dtests=disabled -Dutilities=disabled -Ddocs=disabled -Ddoc_tests=false \
    -Dintrospection=disabled -Dglib=disabled -Dgobject=disabled \
    -Dcairo=disabled -Dchafa=disabled -Dpng=disabled -Dicu=disabled \
    -Dgraphite=disabled -Dgraphite2=disabled -Dfreetype=disabled \
    -Dcoretext=enabled -Dzlib=enabled
meson compile -C "$build_root/harfbuzz"
meson install -C "$build_root/harfbuzz"


echo "==> Building libass with CoreText support (no Fontconfig)..."
meson setup "$build_root/libass" "$ASTRACORE_LIBASS_SOURCE" \
    --prefix "$prefix" --buildtype release --default-library shared \
    -Dtest=disabled -Dcompare=disabled -Dprofile=disabled -Dfuzz=disabled \
    -Dcheckasm=disabled -Ddirectwrite=disabled -Dfontconfig=disabled -Dcoretext=enabled
meson compile -C "$build_root/libass"
meson install -C "$build_root/libass"

echo "==> Building dav1d AV1 software decoder..."
meson setup "$build_root/dav1d" "$ASTRACORE_DAV1D_SOURCE" \
    --prefix "$prefix" --buildtype release --default-library shared \
    -Denable_tools=false -Denable_tests=false -Denable_examples=false
meson compile -C "$build_root/dav1d"
meson install -C "$build_root/dav1d"

echo "==> Ensuring Jinja2 for libplacebo shader compilation..."
brew_python="$(brew --prefix 2>/dev/null || echo /opt/homebrew)/bin/python3"
if [[ -x "$brew_python" ]]; then
    "$brew_python" -m pip install --break-system-packages jinja2 2>/dev/null || true
fi
python3 -m pip install --break-system-packages jinja2 2>/dev/null || true

echo "==> Building libplacebo minimal..."
meson setup "$build_root/libplacebo" "$ASTRACORE_LIBPLACEBO_SOURCE" \
    --prefix "$prefix" --buildtype release --default-library shared \
    -Dvulkan=disabled -Dopengl=disabled -Dd3d11=disabled \
    -Dglslang=disabled -Dshaderc=disabled -Dlcms=disabled \
    -Ddovi=disabled -Dlibdovi=disabled -Dunwind=disabled -Dxxhash=disabled \
    -Ddemos=false -Dtests=false -Dbench=false -Dfuzz=false
meson compile -C "$build_root/libplacebo"
meson install -C "$build_root/libplacebo"

echo "==> Building x265 8-bit shared..."
cmake -S "$ASTRACORE_X265_SOURCE/source" -B "$build_root/x265" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$prefix" \
    -DENABLE_SHARED=ON -DENABLE_CLI=OFF -DENABLE_LIBNUMA=OFF \
    -DHIGH_BIT_DEPTH=OFF -DENABLE_HDR10_PLUS=OFF -DSTATIC_LINK_CRT=OFF
cmake --build "$build_root/x265"
cmake --install "$build_root/x265"

echo "==> Configuring and building FFmpeg 9.0.1 (shared, macOS VideoToolbox profile)..."
mkdir -p "$build_root/ffmpeg"
pushd "$build_root/ffmpeg" >/dev/null
"$ASTRACORE_FFMPEG_SOURCE/configure" \
    --prefix="$prefix" \
    --enable-shared --disable-static --disable-debug --disable-doc --disable-ffplay \
    --enable-gpl --enable-version3 --disable-vulkan --enable-securetransport \
    --disable-everything --disable-avdevice --enable-network \
    --enable-libass --enable-libfreetype --enable-libfribidi --enable-libharfbuzz --enable-libdav1d \
    --enable-libx264 --enable-libx265 --enable-libsvtav1 \
    --enable-videotoolbox --enable-audiotoolbox \
    --enable-protocol=file,pipe,http,https,httpproxy,tcp,tls,crypto,data \
    --enable-demuxer=mov,matroska,avi,flv,mpegts,wav,ogg,flac,aac,mp3,image2,ass,srt,webvtt,hls,asf \
    --enable-muxer=mp4,mov,matroska,webm,wav,pcm_s16le,adts,ass,srt,webvtt,image2 \
    --enable-decoder=h264,hevc,av1,libdav1d,vp8,vp9,mpeg2video,mpeg4,mjpeg,png,aac,mp3,flac,opus,vorbis,alac,pcm_s16le,pcm_s24le,pcm_s32le,pcm_f32le,ass,ssa,srt,subrip,webvtt,movtext \
    --enable-encoder=libx264,libx265,libsvtav1,aac,pcm_s16le,png,ass,ssa,srt,subrip,webvtt,movtext,h264_videotoolbox,hevc_videotoolbox \
    --enable-parser=h264,hevc,av1,vp8,vp9,mpeg4video,mpegvideo,aac,mpegaudio,flac,opus,vorbis \
    --enable-filter=aformat,aresample,asetpts,atrim,anull,atempo,volume,format,fps,null,scale,setpts,subtitles,trim \
    --enable-bsf=aac_adtstoasc,av1_frame_merge,av1_metadata,h264_mp4toannexb,hevc_mp4toannexb,vp9_superframe \
    --enable-hwaccel=h264_videotoolbox,hevc_videotoolbox,av1_videotoolbox,vp9_videotoolbox,mpeg2_videotoolbox \
    --disable-libbluray --disable-libdvdnav --disable-libdvdread \
    --disable-lv2 --disable-frei0r --disable-librist --disable-libsrt --disable-libssh --disable-libzmq || {
        echo "=== FFmpeg configure failed. config.log tail: ==="
        tail -n 120 ffbuild/config.log
        exit 1
    }
make -j"$(sysctl -n hw.ncpu)"
make install
popd >/dev/null

echo "==> Building libmpv 0.41.0+..."
meson setup "$build_root/mpv" "$ASTRACORE_MPV_SOURCE" \
    --prefix "$prefix" --buildtype release --default-library shared --auto-features disabled \
    -Dcplayer=false -Dlibmpv=true -Dbuild-date=false -Dtests=false \
    -Dlua=disabled -Djavascript=disabled -Dcplugins=disabled \
    -Dlibavdevice=disabled -Dplain-gl=enabled -Dgl=enabled \
    -Dvulkan=disabled -Dcoreaudio=enabled \
    -Dswift-build=disabled \
    -Diconv=enabled -Djpeg=disabled -Dlcms2=enabled -Dzlib=enabled
meson compile -C "$build_root/mpv"


meson install -C "$build_root/mpv"

echo "==> Building AstraCore.Native C ABI v4..."
cmake -S "$ASTRACORE_REPO" -B "$build_root/native" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release -DCMAKE_PREFIX_PATH="$prefix" -DCMAKE_INSTALL_PREFIX="$prefix"
cmake --build "$build_root/native"
DYLD_LIBRARY_PATH="$prefix/lib:${DYLD_LIBRARY_PATH:-}" \
    ctest --test-dir "$build_root/native" --output-on-failure
cmake --install "$build_root/native"

echo "==> Assembling standalone macOS runtime package in $ASTRACORE_OUTPUT..."
cp -P "$prefix/bin/ffmpeg" "$prefix/bin/ffprobe" "$ASTRACORE_OUTPUT/"
cp -P "$prefix/lib"/lib*.dylib "$ASTRACORE_OUTPUT/" 2>/dev/null || true

echo "==> Relocating Mach-O install names with install_name_tool..."
for dylib in "$ASTRACORE_OUTPUT"/lib*.dylib; do
    [[ -f "$dylib" && ! -L "$dylib" ]] || continue
    name="$(basename "$dylib")"
    install_name_tool -id "@rpath/$name" "$dylib" 2>/dev/null || true
done

for bin in "$ASTRACORE_OUTPUT"/*; do
    [[ -f "$bin" && ! -L "$bin" ]] || continue
    # Add @loader_path to RPATH
    install_name_tool -add_rpath "@loader_path" "$bin" 2>/dev/null || true
    # Fix references to prefix libraries
    for dylib in "$ASTRACORE_OUTPUT"/lib*.dylib; do
        [[ -f "$dylib" && ! -L "$dylib" ]] || continue
        name="$(basename "$dylib")"
        install_name_tool -change "$prefix/lib/$name" "@rpath/$name" "$bin" 2>/dev/null || true
    done
done

echo "==> Resolving and bundling external non-system dependencies (Homebrew/local)..."
changed=1
while [[ $changed -eq 1 ]]; do
    changed=0
    for bin in "$ASTRACORE_OUTPUT"/*; do
        [[ -f "$bin" && ! -L "$bin" ]] || continue
        for dep in $(otool -L "$bin" 2>/dev/null | awk '{print $1}' | grep -E '^/(opt/homebrew|usr/local)' || true); do
            dep_name="$(basename "$dep")"
            if [[ ! -f "$ASTRACORE_OUTPUT/$dep_name" ]]; then
                echo "Bundling non-system dependency: $dep"
                cp -L "$dep" "$ASTRACORE_OUTPUT/$dep_name"
                chmod 755 "$ASTRACORE_OUTPUT/$dep_name"
                install_name_tool -id "@rpath/$dep_name" "$ASTRACORE_OUTPUT/$dep_name" 2>/dev/null || true
                install_name_tool -add_rpath "@loader_path" "$ASTRACORE_OUTPUT/$dep_name" 2>/dev/null || true
                changed=1
            fi
            install_name_tool -change "$dep" "@rpath/$dep_name" "$bin" 2>/dev/null || true
        done
    done
done

echo "==> Stripping binaries..."
for bin in "$ASTRACORE_OUTPUT"/*; do
    if [[ -f "$bin" && ! -L "$bin" ]]; then
        strip -S "$bin" 2>/dev/null || true
    fi
done

mkdir -p "$ASTRACORE_OUTPUT/LICENSES"
cp -f "$ASTRACORE_REPO/LICENSE" "$ASTRACORE_OUTPUT/LICENSES/AstraCore-GPL-3.0.txt"
cp -f "$ASTRACORE_MPV_SOURCE/LICENSE.GPL" "$ASTRACORE_OUTPUT/LICENSES/mpv-GPL.txt"
cp -f "$ASTRACORE_FFMPEG_SOURCE/COPYING.GPLv3" "$ASTRACORE_OUTPUT/LICENSES/FFmpeg-GPLv3.txt"
cp -f "$ASTRACORE_LIBASS_SOURCE/COPYING" "$ASTRACORE_OUTPUT/LICENSES/libass-ISC.txt"
cp -f "$ASTRACORE_LIBPLACEBO_SOURCE/LICENSE" "$ASTRACORE_OUTPUT/LICENSES/libplacebo-LGPL-2.1.txt"
cp -f "$ASTRACORE_X265_SOURCE/COPYING" "$ASTRACORE_OUTPUT/LICENSES/x265-GPL-2.0.txt"
cp -f "$ASTRACORE_HARFBUZZ_SOURCE/COPYING" "$ASTRACORE_OUTPUT/LICENSES/HarfBuzz-MIT.txt"
cp -f "$ASTRACORE_DAV1D_SOURCE/COPYING" "$ASTRACORE_OUTPUT/LICENSES/dav1d-BSD-2-Clause.txt"

echo "==> macOS build completed successfully in $ASTRACORE_OUTPUT"
