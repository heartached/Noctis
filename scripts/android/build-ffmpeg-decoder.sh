#!/usr/bin/env bash
# Builds Media3's FFmpeg audio decoder extension (androidx/media
# libraries/decoder_ffmpeg, artifact media3-decoder-ffmpeg) as an AAR.
#
# Run inside WSL Ubuntu (24.04), NOT on /mnt/c (slow):
#   bash /mnt/c/Users/<you>/Downloads/Noctis/Noctis/scripts/android/build-ffmpeg-decoder.sh
#
# License: FFmpeg is configured LGPL-only (no --enable-gpl, no
# --enable-nonfree, no external libs) with ONLY the alac decoder, and linked
# statically into libffmpegJNI.so the way Media3 does it.
#
# One-time prerequisites (as root: `wsl -d Ubuntu -u root`):
#   apt-get install -y openjdk-17-jdk-headless make unzip zip git python3 pkg-config curl
# The Android SDK/NDK are installed by this script into $ANDROID_HOME if absent.
set -euo pipefail

MEDIA3_TAG="${MEDIA3_TAG:-1.11.0}"
FFMPEG_BRANCH="${FFMPEG_BRANCH:-release/6.0}"   # what decoder_ffmpeg/README.md recommends at 1.11.0
NDK_VERSION="${NDK_VERSION:-28.2.13676358}"     # r28c: 16 KB page alignment by default
CMAKE_VERSION="${CMAKE_VERSION:-3.22.1}"
ANDROID_ABI="${ANDROID_ABI:-23}"                # = Media3 1.11.0 minSdkVersion
ENABLED_DECODERS=(alac)
ABIS=(arm64-v8a x86_64)

WORK="${WORK:-$HOME/media3-ffmpeg}"
export ANDROID_HOME="${ANDROID_HOME:-$HOME/android-sdk}"
export ANDROID_SDK_ROOT="$ANDROID_HOME"
export JAVA_HOME="${JAVA_HOME:-/usr/lib/jvm/java-17-openjdk-amd64}"
OUT_DIR="${OUT_DIR:-/mnt/c/Users/okfer/Downloads/Noctis/Noctis/src/Noctis.Android/Libs}"
HOST_PLATFORM="linux-x86_64"
JOBS="$(nproc 2>/dev/null || echo 4)"

mkdir -p "$WORK"
cd "$WORK"

# ---------------------------------------------------------------- Android SDK
SDKMANAGER="$ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager"
if [[ ! -x "$SDKMANAGER" ]]; then
  mkdir -p "$ANDROID_HOME/cmdline-tools"
  curl -sSL -o /tmp/clt.zip https://dl.google.com/android/repository/commandlinetools-linux-13114758_latest.zip
  rm -rf /tmp/clt && unzip -q /tmp/clt.zip -d /tmp/clt
  mv /tmp/clt/cmdline-tools "$ANDROID_HOME/cmdline-tools/latest"
fi
yes | "$SDKMANAGER" --licenses >/dev/null 2>&1 || true
"$SDKMANAGER" --install "ndk;$NDK_VERSION" "cmake;$CMAKE_VERSION" "platforms;android-36" "build-tools;36.0.0" >/dev/null
NDK_PATH="$ANDROID_HOME/ndk/$NDK_VERSION"
TOOLCHAIN_PREFIX="$NDK_PATH/toolchains/llvm/prebuilt/$HOST_PLATFORM/bin"

# -------------------------------------------------------------------- sources
if [[ ! -d media ]]; then
  git clone --depth 1 --branch "$MEDIA3_TAG" https://github.com/androidx/media.git media
fi
MEDIA3_COMMIT="$(git -C media rev-parse HEAD)"
FFMPEG_MODULE_PATH="$WORK/media/libraries/decoder_ffmpeg/src/main"
FFMPEG_SRC="$FFMPEG_MODULE_PATH/jni/ffmpeg"
if [[ ! -d "$FFMPEG_SRC" ]]; then
  git clone --depth 1 --branch "$FFMPEG_BRANCH" https://git.ffmpeg.org/ffmpeg.git "$FFMPEG_SRC"
fi
FFMPEG_COMMIT="$(git -C "$FFMPEG_SRC" rev-parse HEAD)"
FFMPEG_RELEASE="$(cat "$FFMPEG_SRC/RELEASE")"
echo "Media3 $MEDIA3_TAG @ $MEDIA3_COMMIT"
echo "FFmpeg $FFMPEG_BRANCH @ $FFMPEG_COMMIT (RELEASE file: $FFMPEG_RELEASE)"

# ---------------------------------------------------------- FFmpeg (static)
# Same options as media3 1.11.0 decoder_ffmpeg/src/main/jni/build_ffmpeg.sh,
# restricted to the 64-bit ABIs we ship. LGPL: no --enable-gpl/--enable-nonfree.
COMMON_OPTIONS=(
  --target-os=android
  --enable-static
  --disable-shared
  --disable-doc
  --disable-programs
  --disable-everything
  --disable-avdevice
  --disable-avformat
  --disable-swscale
  --disable-postproc
  --disable-avfilter
  --disable-symver
  --enable-swresample
  --extra-ldexeflags=-pie
  --disable-v4l2-m2m
  --disable-vulkan
)
for d in "${ENABLED_DECODERS[@]}"; do COMMON_OPTIONS+=("--enable-decoder=$d"); done

LOG_DIR="$WORK/logs"
mkdir -p "$LOG_DIR"
: > "$LOG_DIR/configure-lines.txt"

cd "$FFMPEG_SRC"
for abi in "${ABIS[@]}"; do
  case "$abi" in
    arm64-v8a) ARCH_OPTS=(--arch=aarch64 --cpu=armv8-a --cross-prefix="$TOOLCHAIN_PREFIX/aarch64-linux-android${ANDROID_ABI}-") ;;
    x86_64)    ARCH_OPTS=(--arch=x86_64 --cpu=x86-64 --cross-prefix="$TOOLCHAIN_PREFIX/x86_64-linux-android${ANDROID_ABI}-" --disable-asm) ;;
    *) echo "unsupported ABI $abi"; exit 1 ;;
  esac
  ARGS=(
    --libdir="android-libs/$abi"
    "${ARCH_OPTS[@]}"
    --nm="$TOOLCHAIN_PREFIX/llvm-nm"
    --ar="$TOOLCHAIN_PREFIX/llvm-ar"
    --ranlib="$TOOLCHAIN_PREFIX/llvm-ranlib"
    --strip="$TOOLCHAIN_PREFIX/llvm-strip"
    "${COMMON_OPTIONS[@]}"
  )
  echo "[$abi] ./configure ${ARGS[*]}" | tee -a "$LOG_DIR/configure-lines.txt"
  make distclean >/dev/null 2>&1 || true
  ./configure "${ARGS[@]}" > "$LOG_DIR/configure-$abi.log"

  # License / decoder guards (ffmpegHasDecoder relies on the decoder list).
  grep -q '^License: LGPL version 2.1 or later' "$LOG_DIR/configure-$abi.log" \
    || { echo "FFmpeg license is not LGPL-2.1-or-later"; exit 1; }
  grep -q '^#define CONFIG_GPL 0' config.h || { echo "CONFIG_GPL not 0"; exit 1; }
  grep -q '^#define CONFIG_NONFREE 0' config.h || { echo "CONFIG_NONFREE not 0"; exit 1; }
  # FFmpeg 6.0 keeps component switches in config_components.h.
  ENABLED="$(grep -E '^#define CONFIG_[A-Z0-9_]+_DECODER 1' config_components.h | sed -E 's/#define CONFIG_([A-Z0-9_]+)_DECODER 1/\1/' | tr 'A-Z' 'a-z' | tr '\n' ' ' || true)"
  echo "[$abi] enabled decoders: $ENABLED"
  [[ "$ENABLED" == "alac " ]] || { echo "unexpected decoder set: $ENABLED"; exit 1; }
  cp config.h "$LOG_DIR/config-$abi.h"
  cp config_components.h "$LOG_DIR/config_components-$abi.h"

  make -j"$JOBS" > "$LOG_DIR/make-$abi.log"
  make install-libs >> "$LOG_DIR/make-$abi.log"
done
# `make clean` (not distclean): keeps the generated libavutil/avconfig.h the JNI
# wrapper includes (same as Media3's build_ffmpeg.sh).
make clean >/dev/null

# -------------------------------------------------------------- Gradle / AAR
cd "$WORK/media"
echo "sdk.dir=$ANDROID_HOME" > local.properties
# Pin the NDK AGP uses for the JNI wrapper to the same r28 as FFmpeg (16 KB pages),
# and build only the ABIs we compiled FFmpeg for.
cat > "$WORK/ndk-init.gradle" <<GRADLE
allprojects {
  pluginManager.withPlugin("com.android.library") {
    android.ndkVersion = "$NDK_VERSION"
    android.defaultConfig.ndk.abiFilters.clear()
    android.defaultConfig.ndk.abiFilters.addAll(["arm64-v8a", "x86_64"])
  }
}
GRADLE
./gradlew --no-daemon -I "$WORK/ndk-init.gradle" :lib-decoder-ffmpeg:assembleRelease 2>&1 | tee "$LOG_DIR/gradle.log"

AAR="$(ls libraries/decoder_ffmpeg/buildout/outputs/aar/*release*.aar 2>/dev/null || ls libraries/decoder_ffmpeg/build/outputs/aar/*release*.aar)"
echo "AAR: $AAR"
mkdir -p "$OUT_DIR"
cp "$AAR" "$OUT_DIR/media3-decoder-ffmpeg-$MEDIA3_TAG.aar"
echo "Copied to $OUT_DIR/media3-decoder-ffmpeg-$MEDIA3_TAG.aar"

# ------------------------------------------------------------------- checks
CHECK="$WORK/aar-check"
rm -rf "$CHECK" && mkdir -p "$CHECK"
unzip -q "$AAR" -d "$CHECK"
unzip -l "$AAR"
for abi in "${ABIS[@]}"; do
  so="$CHECK/jni/$abi/libffmpegJNI.so"
  echo "== $abi $(stat -c %s "$so") bytes"
  "$TOOLCHAIN_PREFIX/llvm-readelf" -lW "$so" | grep -E 'LOAD'
  "$TOOLCHAIN_PREFIX/llvm-nm" -D --defined-only "$so" | grep -c ' T Java_androidx_media3_decoder_ffmpeg_' || true
done
unzip -l "$CHECK/classes.jar" | grep 'androidx/media3/decoder/ffmpeg/FfmpegAudioRenderer.class'
