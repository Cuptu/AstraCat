#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

: "${ASTRACORE_REPO:=$REPO_ROOT}"
: "${ASTRACORE_SOURCES:=$REPO_ROOT/artifacts/astracore-sources}"
: "${ASTRACORE_FFMPEG_SOURCE:=$ASTRACORE_SOURCES/ffmpeg}"
: "${ASTRACORE_MPV_SOURCE:=$ASTRACORE_SOURCES/mpv}"
: "${ASTRACORE_LIBASS_SOURCE:=$ASTRACORE_SOURCES/libass}"
: "${ASTRACORE_LIBPLACEBO_SOURCE:=$ASTRACORE_SOURCES/libplacebo}"
: "${ASTRACORE_X265_SOURCE:=$ASTRACORE_SOURCES/x265}"
: "${ASTRACORE_HARFBUZZ_SOURCE:=$ASTRACORE_SOURCES/harfbuzz}"
: "${ASTRACORE_DAV1D_SOURCE:=$ASTRACORE_SOURCES/dav1d}"
: "${ASTRACORE_OUTPUT:=$REPO_ROOT/artifacts/astracore/linux-x64}"

build_root="$ASTRACORE_REPO/artifacts/astracore-build/linux-x64"
prefix="$build_root/prefix"
rm -rf "$build_root" "$ASTRACORE_OUTPUT"
mkdir -p "$build_root" "$prefix" "$ASTRACORE_OUTPUT"
export PKG_CONFIG_PATH="$prefix/lib/pkgconfig:$prefix/lib/x86_64-linux-gnu/pkgconfig:$prefix/share/pkgconfig:/usr/lib/x86_64-linux-gnu/pkgconfig:/usr/share/pkgconfig:/usr/lib/pkgconfig${PKG_CONFIG_PATH:+:$PKG_CONFIG_PATH}"
export LD_LIBRARY_PATH="$prefix/lib:$prefix/lib/x86_64-linux-gnu:${LD_LIBRARY_PATH:-}"


echo "==> Building minimal HarfBuzz (FreeType shaper)..."
meson setup "$build_root/harfbuzz" "$ASTRACORE_HARFBUZZ_SOURCE" \
    --prefix "$prefix" --buildtype release --default-library shared \
    --wrap-mode nofallback --auto-features disabled \
    -Dtests=disabled -Dutilities=disabled -Ddocs=disabled -Ddoc_tests=false \
    -Dintrospection=disabled -Dglib=disabled -Dgobject=disabled \
    -Dcairo=disabled -Dchafa=disabled -Dpng=disabled -Dicu=disabled \
    -Dgraphite=disabled -Dgraphite2=disabled -Dfreetype=disabled \
    -Dzlib=enabled
meson compile -C "$build_root/harfbuzz"
meson install -C "$build_root/harfbuzz"


echo "==> Building libass with Fontconfig support..."
meson setup "$build_root/libass" "$ASTRACORE_LIBASS_SOURCE" \
    --prefix "$prefix" --buildtype release --default-library shared \
    -Dtest=disabled -Dcompare=disabled -Dprofile=disabled -Dfuzz=disabled \
    -Dcheckasm=disabled -Ddirectwrite=disabled -Dfontconfig=enabled
meson compile -C "$build_root/libass"
meson install -C "$build_root/libass"

echo "==> Building dav1d AV1 software decoder..."
meson setup "$build_root/dav1d" "$ASTRACORE_DAV1D_SOURCE" \
    --prefix "$prefix" --buildtype release --default-library shared \
    -Denable_tools=false -Denable_tests=false -Denable_examples=false
meson compile -C "$build_root/dav1d"
meson install -C "$build_root/dav1d"

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
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DENABLE_SHARED=ON -DENABLE_CLI=OFF -DENABLE_LIBNUMA=OFF \
    -DHIGH_BIT_DEPTH=OFF -DENABLE_HDR10_PLUS=OFF -DSTATIC_LINK_CRT=OFF
cmake --build "$build_root/x265"
cmake --install "$build_root/x265"

echo "==> Ensuring ffnvcodec headers for NVENC..."
if ! pkg-config --exists ffnvcodec 2>/dev/null; then
    rm -rf "$build_root/nv-codec-headers"
    git clone --depth 1 https://git.videolan.org/git/ffmpeg/nv-codec-headers.git "$build_root/nv-codec-headers"
    make -C "$build_root/nv-codec-headers" install PREFIX="$prefix"
fi

echo "==> Configuring and building FFmpeg 9.0.1 (shared, Linux VA-API/NVENC profile)..."
mkdir -p "$build_root/ffmpeg"
pushd "$build_root/ffmpeg" >/dev/null
"$ASTRACORE_FFMPEG_SOURCE/configure" \
    --prefix="$prefix" \
    --enable-shared --disable-static --disable-debug --disable-doc --disable-ffplay \
    --enable-gpl --enable-version3 --disable-vulkan \
    --disable-everything --disable-avdevice --enable-network \
    --enable-libass --enable-libfontconfig --enable-libfreetype --enable-libfribidi --enable-libharfbuzz --enable-libdav1d \
    --enable-libx264 --enable-libx265 --enable-libsvtav1 \
    --enable-vaapi --enable-vdpau --enable-ffnvcodec --enable-nvenc \
    --enable-protocol=file,pipe,http,https,httpproxy,tcp,tls,crypto,data \
    --enable-demuxer=mov,matroska,avi,flv,mpegts,wav,ogg,flac,aac,mp3,image2,ass,srt,webvtt,hls,asf \
    --enable-muxer=mp4,mov,matroska,webm,wav,pcm_s16le,adts,ass,srt,webvtt,image2 \
    --enable-decoder=h264,hevc,av1,libdav1d,vp8,vp9,mpeg2video,mpeg4,mjpeg,png,aac,mp3,flac,opus,vorbis,alac,pcm_s16le,pcm_s24le,pcm_s32le,pcm_f32le,ass,ssa,srt,subrip,webvtt,movtext \
    --enable-encoder=libx264,libx265,libsvtav1,aac,pcm_s16le,png,ass,ssa,srt,subrip,webvtt,movtext,h264_nvenc,hevc_nvenc,av1_nvenc,h264_vaapi,hevc_vaapi \
    --enable-parser=h264,hevc,av1,vp8,vp9,mpeg4video,mpegvideo,aac,mpegaudio,flac,opus,vorbis \
    --enable-filter=aformat,aresample,asetpts,atrim,anull,atempo,volume,format,fps,null,scale,setpts,subtitles,trim \
    --enable-bsf=aac_adtstoasc,av1_frame_merge,av1_metadata,h264_mp4toannexb,hevc_mp4toannexb,vp9_superframe \

    --enable-hwaccel=h264_vaapi,hevc_vaapi,av1_vaapi,vp9_vaapi,mpeg2_vaapi,h264_vdpau,hevc_vdpau,vp9_vdpau,mpeg2_vdpau \
    --disable-libbluray --disable-libdvdnav --disable-libdvdread \
    --disable-lv2 --disable-frei0r --disable-librist --disable-libsrt --disable-libssh --disable-libzmq \
    --extra-cflags="-fPIC"
make -j"$(nproc)"
make install
popd >/dev/null

echo "==> Building libmpv 0.41.0+..."
meson setup "$build_root/mpv" "$ASTRACORE_MPV_SOURCE" \
    --prefix "$prefix" --buildtype release --default-library shared --auto-features disabled \
    -Dcplayer=false -Dlibmpv=true -Dbuild-date=false -Dtests=false \
    -Dlua=disabled -Djavascript=disabled -Dcplugins=disabled \
    -Dcdda=disabled -Ddvdnav=disabled -Dlibbluray=disabled -Ddvbin=disabled \
    -Dlibavdevice=disabled -Dplain-gl=enabled -Dgl=enabled -Degl=enabled \
    -Dvulkan=disabled \
    -Dvaapi=enabled -Dvdpau=enabled \
    -Dalsa=enabled -Dpulse=enabled \
    -Diconv=enabled -Djpeg=disabled -Dlcms2=enabled -Dzlib=enabled
meson compile -C "$build_root/mpv"

meson install -C "$build_root/mpv"

echo "==> Building AstraCore.Native C ABI v4..."
cmake -S "$ASTRACORE_REPO" -B "$build_root/native" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release -DCMAKE_PREFIX_PATH="$prefix" -DCMAKE_INSTALL_PREFIX="$prefix"
cmake --build "$build_root/native"
LD_LIBRARY_PATH="$prefix/lib:$prefix/lib64:$prefix/lib/x86_64-linux-gnu:${LD_LIBRARY_PATH:-}" \
    ctest --test-dir "$build_root/native" --output-on-failure
cmake --install "$build_root/native"

echo "==> Assembling standalone Linux runtime package in $ASTRACORE_OUTPUT..."
cp -P "$prefix/bin/ffmpeg" "$prefix/bin/ffprobe" "$ASTRACORE_OUTPUT/"
for libdir in "$prefix/lib" "$prefix/lib64" "$prefix/lib/x86_64-linux-gnu"; do
    if [[ -d "$libdir" ]]; then
        cp -P "$libdir"/lib*.so* "$ASTRACORE_OUTPUT/" 2>/dev/null || true
    fi
done

for libname in libx264 libSvtAv1Enc libfreetype libfribidi libfontconfig liblcms2 libunibreak; do
    for f in /usr/lib/x86_64-linux-gnu/${libname}.so* /usr/lib/${libname}.so*; do
        if [[ -f "$f" || -L "$f" ]]; then
            cp -P "$f" "$ASTRACORE_OUTPUT/" 2>/dev/null || true
        fi
    done
done

if command -v patchelf >/dev/null; then
    echo "==> Setting \$ORIGIN RUNPATH with patchelf..."
    for binary in "$ASTRACORE_OUTPUT"/*; do
        if [[ -f "$binary" && ! -L "$binary" && -x "$binary" ]]; then
            patchelf --set-rpath '$ORIGIN' "$binary" 2>/dev/null || true
        fi
    done
fi

echo "==> Stripping binaries..."
for binary in "$ASTRACORE_OUTPUT"/*; do
    if [[ -f "$binary" && ! -L "$binary" && -x "$binary" ]]; then
        strip --strip-unneeded "$binary" 2>/dev/null || true
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

echo "==> Linux x64 build completed successfully in $ASTRACORE_OUTPUT"
