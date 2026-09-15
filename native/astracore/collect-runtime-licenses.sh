#!/usr/bin/env bash
set -euo pipefail

output="${1:?usage: collect-runtime-licenses.sh OUTPUT_DIRECTORY}"
mkdir -p "$output/LICENSES/dependencies"

source_dlls=()
for dll in "$output"/*.dll; do
    [[ -f "$dll" ]] || continue
    source_dll="/clang64/bin/$(basename "$dll")"
    [[ -f "$source_dll" ]] || continue
    source_dlls+=("$source_dll")
done

if ((${#source_dlls[@]} == 0)); then
    echo "No packaged runtime DLLs found for license collection." >&2
    exit 5
fi

mapfile -t packages < <(pacman -Qoq "${source_dlls[@]}" | sort -u)
if ((${#packages[@]} == 0)); then
    echo "No MSYS2 owners found for runtime DLLs." >&2
    exit 5
fi

# One metadata snapshot covers every DLL, including packages such as x265 that
# currently do not install a standalone license file in MSYS2.
pacman -Qi "${packages[@]}" > "$output/LICENSES/dependencies/MSYS2-PACKAGES.txt"

while IFS= read -r license_file; do
    [[ -f "$license_file" ]] || continue
    relative_license="${license_file#/clang64/share/licenses/}"
    mkdir -p "$output/LICENSES/dependencies/$(dirname "$relative_license")"
    cp -f "$license_file" "$output/LICENSES/dependencies/$relative_license"
done < <(pacman -Qlq "${packages[@]}" | sort -u | grep '^/clang64/share/licenses/' || true)
