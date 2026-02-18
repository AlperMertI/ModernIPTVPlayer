#!/bin/bash
set -e

# Ensure we are in the right directory
cd "$(dirname "$0")"

# Setup PATH for MSYS2 Clang64
export PATH="/clang64/bin:/usr/bin:$PATH"

# Prevent Git from hanging
export GIT_TERMINAL_PROMPT=0
export GIT_ASKPASS=echo
export SSH_ASKPASS=echo

# 1. Install dependencies - including glslang which shaderc needs when static
message() { echo -e "\n>>> $1"; }
message "Installing system dependencies..."
pacman -S --noconfirm --needed \
    mingw-w64-clang-x86_64-toolchain \
    mingw-w64-clang-x86_64-meson \
    mingw-w64-clang-x86_64-ninja \
    mingw-w64-clang-x86_64-pkgconf \
    mingw-w64-clang-x86_64-cmake \
    mingw-w64-clang-x86_64-nasm \
    mingw-w64-clang-x86_64-shaderc \
    mingw-w64-clang-x86_64-spirv-cross \
    mingw-w64-clang-x86_64-glslang \
    mingw-w64-clang-x86_64-spirv-headers \
    mingw-w64-clang-x86_64-spirv-tools \
    mingw-w64-clang-x86_64-libiconv \
    mingw-w64-clang-x86_64-fontconfig \
    mingw-w64-clang-x86_64-expat \
    mingw-w64-clang-x86_64-libffi \
    mingw-w64-clang-x86_64-icu \
    mingw-w64-clang-x86_64-gperf \
    mingw-w64-clang-x86_64-vulkan-loader \
    mingw-w64-clang-x86_64-vulkan-headers \
    git

# 2. Cleanup broken wrap redirects (chicken-and-egg issues)
message "Cleaning up broken wraps..."
rm -f subprojects/expat.wrap subprojects/libffi.wrap subprojects/icu.wrap subprojects/gperf.wrap

# 3. Setup wraps (incremental skip if build exists and is valid)
if [ ! -f "build/build.ninja" ]; then
    message "Build directory missing or invalid. Initializing..."
    # If build directory exists but is invalid, Meson setup build might fail. 
    # We remove it to ensure a clean state if no build.ninja is present.
    rm -rf build 
    mkdir -p build
    mkdir -p subprojects
    # Use WrapDB for core libs
    meson wrap install zlib || true
    meson wrap install libpng || true
    meson wrap install freetype2 || true
    meson wrap install fribidi || true
    meson wrap install harfbuzz || true

    # Custom wraps
    cat <<EOF > subprojects/ffmpeg.wrap
[wrap-git]
url = https://gitlab.freedesktop.org/gstreamer/meson-ports/ffmpeg.git
revision = meson-8.0
depth = 1
clone-recursive = true
[provide]
libavcodec = libavcodec_dep
libavdevice = libavdevice_dep
libavfilter = libavfilter_dep
libavformat = libavformat_dep
libavutil = libavutil_dep
libswresample = libswresample_dep
libswscale = libswscale_dep
EOF

    cat <<EOF > subprojects/libass.wrap
[wrap-git]
url = https://github.com/libass/libass.git
revision = master
depth = 1
[provide]
libass = libass_dep
EOF

    cat <<EOF > subprojects/libplacebo.wrap
[wrap-git]
url = https://code.videolan.org/videolan/libplacebo.git
revision = master
depth = 1
clone-recursive = true
[provide]
libplacebo = libplacebo_dep
EOF

    message "Configuring Meson..."
    # MSYS2'de static shaderc linklemesi için eksik olan bağımlılıklar manuel olarak eklenmeli
    EXTRA_LIBS="-lshaderc_combined -lglslang -lMachineIndependent -lOSDependent -lGenericCodeGen -lSPIRV -lSPIRV-Tools-opt -lSPIRV-Tools -lpthread"

    # forcefallback her şeyi wrap ile aramaya zorladığı için sistem paketlerini görmezden gelebilir.
    # Bunun yerine sadece gerekli olanlar için fallback zorluyoruz.
    meson setup build \
        --wrap-mode=default \
        --force-fallback-for=zlib,libpng,freetype2,fribidi,harfbuzz,ffmpeg,libass,libplacebo \
        -Ddefault_library=static \
        -Dprefer_static=true \
        -Dc_link_args="$EXTRA_LIBS" \
        -Dcpp_link_args="$EXTRA_LIBS" \
        -Dlibmpv=true \
        -Dgpl=true \
        -Dd3d11=enabled \
        -Dvulkan=enabled \
        -Dlua=disabled \
        -Djavascript=disabled \
        -Dshaderc=enabled \
        -Dspirv-cross=enabled \
        -Dlcms2=disabled \
        -Diconv=disabled \
        -Dtests=false
fi

# 3. Final build step
message "Compiling libmpv..."
# If it fails again, we might need to inject link arguments
meson compile -C build mpv:shared_library

echo "-------------------------------------------------------"
echo "Build complete! Your self-contained DLL should be in build/"
find build -name "*.dll"
echo "-------------------------------------------------------"
