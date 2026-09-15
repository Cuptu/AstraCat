#!/usr/bin/env bash
set -euo pipefail

if [[ "${MSYSTEM:-}" != "CLANG64" ]]; then
    echo "AstraCore Windows build must run in an MSYS2 CLANG64 environment." >&2
    exit 2
fi
: "${ASTRACORE_REPO:?}"
: "${ASTRACORE_FFMPEG_SOURCE:?}"
: "${ASTRACORE_MPV_SOURCE:?}"
: "${ASTRACORE_LIBASS_SOURCE:?}"
: "${ASTRACORE_LIBPLACEBO_SOURCE:?}"
: "${ASTRACORE_X265_SOURCE:?}"
: "${ASTRACORE_HARFBUZZ_SOURCE:?}"
: "${ASTRACORE_DAV1D_SOURCE:?}"
: "${ASTRACORE_OUTPUT:?}"

export PATH="/clang64/bin:/usr/bin:$PATH"
build_root="$ASTRACORE_REPO/artifacts/astracore-build/win-x64"
prefix="$build_root/prefix"
rm -rf "$build_root" "$ASTRACORE_OUTPUT"
mkdir -p "$build_root" "$prefix" "$ASTRACORE_OUTPUT"
export PKG_CONFIG_PATH="$prefix/lib/pkgconfig:/clang64/lib/pkgconfig"

required_tools=(clang llvm-strip meson ninja cmake pkg-config patch git tar)
for tool in "${required_tools[@]}"; do
    command -v "$tool" >/dev/null || { echo "Missing CLANG64 tool: $tool" >&2; exit 3; }
done

# libass needs only HarfBuzz's core OpenType shaper. A private build avoids the
# generic GLib/PCRE2/Graphite plugin closure while retaining zlib-compressed
# font support. Windows font discovery remains owned by libass DirectWrite;
# Fontconfig is reserved for Linux RID builds and would duplicate the Windows
# system font database plus its XML/localization dependency chain.
# Keep HarfBuzz's source path short: its world-source generator passes hundreds
# of filenames in one Windows command line and otherwise exceeds CreateProcess.
harfbuzz_short_source="/tmp/astracore-harfbuzz-source-$$"
harfbuzz_short_build="/tmp/astracore-harfbuzz-build-$$"
rm -rf "$harfbuzz_short_source" "$harfbuzz_short_build"
mkdir -p "$harfbuzz_short_source"
cp -a "$ASTRACORE_HARFBUZZ_SOURCE/." "$harfbuzz_short_source/"
meson setup "$harfbuzz_short_build" "$harfbuzz_short_source" \
    --prefix "$prefix" --buildtype release --default-library shared \
    --wrap-mode nofallback --auto-features disabled \
    -Dtests=disabled -Dutilities=disabled -Ddocs=disabled -Ddoc_tests=false \
    -Dintrospection=disabled -Dglib=disabled -Dgobject=disabled \
    -Dcairo=disabled -Dchafa=disabled -Dpng=disabled -Dicu=disabled \
    -Dgraphite=disabled -Dgraphite2=disabled -Dfreetype=disabled \
    -Dfontations=disabled -Dgdi=disabled -Ddirectwrite=disabled \
    -Dcoretext=disabled -Dharfrust=disabled -Dkbts=disabled -Dwasm=disabled \
    -Draster=disabled -Dvector=disabled -Dgpu=disabled -Dgpu_demo=disabled \
    -Dsubset=disabled -Dbenchmark=disabled -Dexperimental_api=false \
    -Dragel_subproject=false -Dzlib=enabled
meson compile -C "$harfbuzz_short_build"
meson install -C "$harfbuzz_short_build"
rm -rf "$harfbuzz_short_source" "$harfbuzz_short_build"

# libass is built once as a shared dependency used by both mpv and libavfilter.
meson setup "$build_root/libass" "$ASTRACORE_LIBASS_SOURCE" \
    --prefix "$prefix" --buildtype release --default-library shared \
    -Dtest=disabled -Dcompare=disabled -Dprofile=disabled -Dfuzz=disabled \
    -Dcheckasm=disabled -Ddirectwrite=enabled -Dfontconfig=disabled
meson compile -C "$build_root/libass"
meson install -C "$build_root/libass"

# FFmpeg 9's native AV1 decoder in this profile is the D3D11VA/DXVA2
# accelerator front end. Keep a pinned dav1d shared decoder so AV1 also has a
# real software fallback on machines without compatible hardware or drivers.
meson setup "$build_root/dav1d" "$ASTRACORE_DAV1D_SOURCE" \
    --prefix "$prefix" --buildtype release --default-library shared \
    -Denable_tools=false -Denable_tests=false -Denable_examples=false
meson compile -C "$build_root/dav1d"
meson install -C "$build_root/dav1d"

# mpv requires libplacebo even when AstraCat uses the legacy OpenGL Render
# API. Build the core library without Vulkan/gpu-next shader backends so the
# Windows loader does not drag shaderc, SPIR-V Cross, Vulkan and libdovi into
# every preview process.
meson setup "$build_root/libplacebo" "$ASTRACORE_LIBPLACEBO_SOURCE" \
    --prefix "$prefix" --buildtype release --default-library shared \
    -Dvulkan=disabled -Dopengl=disabled -Dd3d11=disabled \
    -Dglslang=disabled -Dshaderc=disabled -Dlcms=disabled \
    -Ddovi=disabled -Dlibdovi=disabled -Dunwind=disabled -Dxxhash=disabled \
    -Ddemos=false -Dtests=false -Dbench=false -Dfuzz=false
meson compile -C "$build_root/libplacebo"
meson install -C "$build_root/libplacebo"

# AstraCat's software HEVC export is always yuv420p/Main. A pinned 8-bit-only
# x265 keeps the fallback encoder while avoiding the generic 8/10/12-bit
# multilib DLL from the rolling toolchain repository.
cmake -S "$ASTRACORE_X265_SOURCE/source" -B "$build_root/x265" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$prefix" \
    -DENABLE_SHARED=ON -DENABLE_CLI=OFF -DENABLE_LIBNUMA=OFF \
    -DHIGH_BIT_DEPTH=OFF -DENABLE_HDR10_PLUS=OFF -DSTATIC_LINK_CRT=OFF
cmake --build "$build_root/x265"
cmake --install "$build_root/x265"

mkdir -p "$build_root/ffmpeg"
pushd "$build_root/ffmpeg" >/dev/null
"$ASTRACORE_FFMPEG_SOURCE/configure" \
    --prefix="$prefix" \
    --cc=clang --cxx=clang++ \
    --enable-shared --disable-static --disable-debug --disable-doc --disable-ffplay \
    --enable-gpl --enable-version3 --enable-schannel --disable-vaapi --disable-vulkan --disable-d3d12va \
    --disable-everything --disable-avdevice --enable-network \
    --enable-libass --disable-fontconfig --enable-libfreetype --enable-libfribidi --enable-libharfbuzz --enable-libdav1d \
    --enable-libx264 --enable-libx265 --enable-libsvtav1 \
    --enable-ffnvcodec --enable-nvenc --enable-amf --enable-libvpl \
    --enable-protocol=file,pipe,http,https,httpproxy,tcp,tls,crypto,data \
    --enable-demuxer=mov,matroska,avi,flv,mpegts,wav,ogg,flac,aac,mp3,image2,ass,srt,webvtt,hls,asf \
    --enable-muxer=mp4,mov,matroska,webm,wav,pcm_s16le,adts,ass,srt,webvtt,image2 \
    --enable-decoder=h264,hevc,av1,libdav1d,vp8,vp9,mpeg2video,mpeg4,mjpeg,png,aac,mp3,flac,opus,vorbis,alac,wmav1,wmav2,wmapro,pcm_s16le,pcm_s24le,pcm_s32le,pcm_f32le,ass,ssa,srt,subrip,webvtt,movtext \
    --enable-encoder=libx264,libx265,libsvtav1,aac,pcm_s16le,png,ass,ssa,srt,subrip,webvtt,movtext,h264_nvenc,hevc_nvenc,av1_nvenc,h264_qsv,hevc_qsv,av1_qsv,h264_amf,hevc_amf,av1_amf \
    --enable-parser=h264,hevc,av1,vp8,vp9,mpeg4video,mpegvideo,aac,mpegaudio,flac,opus,vorbis \
    --enable-filter=aformat,aresample,asetpts,atrim,anull,atempo,volume,format,fps,null,scale,setpts,subtitles,trim \
    --enable-bsf=aac_adtstoasc,av1_frame_merge,av1_metadata,h264_mp4toannexb,hevc_mp4toannexb,vp9_superframe \
    --enable-hwaccel=h264_d3d11va,h264_d3d11va2,hevc_d3d11va,hevc_d3d11va2,av1_d3d11va,av1_d3d11va2,vp9_d3d11va,vp9_d3d11va2,mpeg2_d3d11va,mpeg2_d3d11va2,h264_dxva2,hevc_dxva2,av1_dxva2,vp9_dxva2,mpeg2_dxva2 \
    --disable-libbluray --disable-libdvdnav --disable-libdvdread \
    --disable-lv2 --disable-frei0r --disable-librist --disable-libsrt --disable-libssh --disable-libzmq
make -j"$(nproc)"
make install
popd >/dev/null

# Apply the audited ANGLE compatibility patch to an isolated source copy so
# the pinned checkout remains pristine and its commit hash stays meaningful.
mpv_build_source="$build_root/mpv-source"
mkdir -p "$mpv_build_source"
git -C "$ASTRACORE_MPV_SOURCE" archive HEAD | tar -x -C "$mpv_build_source"
patch -d "$mpv_build_source" -p1 < \
    "$ASTRACORE_REPO/native/astracore/patches/mpv-angle-device-query.patch"

# Force mpv to resolve libav* and libass from the private prefix. Only libmpv
# is built; the standalone player, scripting engines, optical media, and input
# devices are outside AstraCat's feature contract.
meson setup "$build_root/mpv" "$mpv_build_source" \
    --prefix "$prefix" --buildtype release --default-library shared --auto-features disabled \
    -Dcplayer=false -Dlibmpv=true -Dbuild-date=false -Dtests=false \
    -Dlua=disabled -Djavascript=disabled -Dcplugins=disabled \
    -Dcdda=disabled -Ddvdnav=disabled -Dlibbluray=disabled -Ddvbin=disabled \
    -Dlibavdevice=disabled -Dplain-gl=enabled -Dgl=enabled -Dgl-win32=enabled \
    -Degl=disabled -Degl-angle=enabled -Degl-angle-lib=disabled -Degl-angle-win32=enabled \
    -Dd3d-hwaccel=enabled -Dwin32-threads=enabled -Dwasapi=enabled \
    -Diconv=enabled -Djpeg=disabled -Dlcms2=enabled -Dzlib=enabled
meson compile -C "$build_root/mpv"
meson install -C "$build_root/mpv"

cmake -S "$ASTRACORE_REPO/native/astracore" -B "$build_root/native" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release -DCMAKE_PREFIX_PATH="$prefix" -DCMAKE_INSTALL_PREFIX="$prefix"
cmake --build "$build_root/native"
PATH="$prefix/bin:$PATH" ctest --test-dir "$build_root/native" --output-on-failure
cmake --install "$build_root/native"

export PATH="$prefix/bin:/clang64/bin:/usr/bin:$PATH"
roots=("$prefix/bin/libmpv-2.dll" "$prefix/bin/ffmpeg.exe" "$prefix/bin/ffprobe.exe" "$prefix/bin/AstraCore.Native.dll")
for root in "${roots[@]}"; do
    [[ -f "$root" ]] || { echo "Missing build output: $root" >&2; exit 4; }
    cp -f "$root" "$ASTRACORE_OUTPUT/"
done

# Copy only PE dependencies reachable from the four public binaries. `ldd`
# reports loader candidates and can retain orphan DLLs after a private library
# replaces a toolchain library with the same SONAME, so traverse direct COFF
# imports instead.
queue=("${roots[@]}")
declare -A seen=()
while ((${#queue[@]})); do
    binary="${queue[0]}"
    queue=("${queue[@]:1}")
    while IFS= read -r dependency; do
        [[ -n "$dependency" ]] || continue
        key="${dependency,,}"
        [[ -n "${seen[$key]:-}" ]] && continue
        source=""
        for directory in "$prefix/bin" /clang64/bin; do
            if [[ -f "$directory/$dependency" ]]; then
                source="$directory/$dependency"
                break
            fi
        done
        [[ -n "$source" ]] || continue
        seen[$key]=1
        cp -f "$source" "$ASTRACORE_OUTPUT/$dependency"
        queue+=("$source")
    done < <(llvm-readobj --coff-imports "$binary" | awk '/^[[:space:]]+Name: / { print $2 }')
done

# Remove COFF debug/symbol tables from locally built PE images. Export tables,
# unwind data and runtime resources are retained by --strip-unneeded.
while IFS= read -r -d '' image; do
    llvm-strip --strip-unneeded "$image"
done < <(find "$ASTRACORE_OUTPUT" -maxdepth 1 -type f \( -iname '*.dll' -o -iname '*.exe' \) -print0)

mkdir -p "$ASTRACORE_OUTPUT/LICENSES"
cp -f "$ASTRACORE_REPO/LICENSE" "$ASTRACORE_OUTPUT/LICENSES/AstraCat-GPL-3.0.txt"
cp -f "$ASTRACORE_MPV_SOURCE/LICENSE.GPL" "$ASTRACORE_OUTPUT/LICENSES/mpv-GPL.txt"
cp -f "$ASTRACORE_FFMPEG_SOURCE/COPYING.GPLv3" "$ASTRACORE_OUTPUT/LICENSES/FFmpeg-GPLv3.txt"
cp -f "$ASTRACORE_LIBASS_SOURCE/COPYING" "$ASTRACORE_OUTPUT/LICENSES/libass-ISC.txt"
cp -f "$ASTRACORE_LIBPLACEBO_SOURCE/LICENSE" "$ASTRACORE_OUTPUT/LICENSES/libplacebo-LGPL-2.1.txt"
cp -f "$ASTRACORE_X265_SOURCE/COPYING" "$ASTRACORE_OUTPUT/LICENSES/x265-GPL-2.0.txt"
cp -f "$ASTRACORE_HARFBUZZ_SOURCE/COPYING" "$ASTRACORE_OUTPUT/LICENSES/HarfBuzz-MIT.txt"
cp -f "$ASTRACORE_DAV1D_SOURCE/COPYING" "$ASTRACORE_OUTPUT/LICENSES/dav1d-BSD-2-Clause.txt"

bash "$ASTRACORE_REPO/native/astracore/collect-runtime-licenses.sh" "$ASTRACORE_OUTPUT"
