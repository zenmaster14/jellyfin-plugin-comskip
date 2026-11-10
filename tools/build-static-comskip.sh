#!/bin/sh
# Builds a fully static comskip (musl + a decode-only FFmpeg) that runs on any Linux,
# including the unmodified official Jellyfin container. Run inside alpine:
#   docker run --rm -v "$PWD:/work" -w /work alpine:3.21 sh tools/build-static-comskip.sh
# Output: out/comskip
set -eux

FFMPEG_REF="${FFMPEG_REF:-n7.1.2}"
COMSKIP_REF="${COMSKIP_REF:-master}"
PREFIX=/opt/static
JOBS="$(nproc)"

apk add --no-cache build-base autoconf automake libtool pkgconf git nasm curl file linux-headers

mkdir -p /build "$PREFIX" out
cd /build

# argtable2 (command-line parsing used by comskip)
curl -fsSL -o argtable2.tar.gz https://prdownloads.sourceforge.net/argtable/argtable2-13.tar.gz \
  || curl -fsSL -o argtable2.tar.gz https://deb.debian.org/debian/pool/main/a/argtable2/argtable2_13.orig.tar.gz
mkdir argtable2 && tar xzf argtable2.tar.gz -C argtable2 --strip-components=1
# argtable2's 2011-era config.sub predates musl; use automake's current copies.
cp /usr/share/automake-*/config.sub /usr/share/automake-*/config.guess argtable2/
(cd argtable2 && ./configure --prefix="$PREFIX" --enable-static --disable-shared \
    && make -j"$JOBS" && make install)

# FFmpeg: only what comskip needs to decode broadcast and common library formats.
git clone --depth 1 --branch "$FFMPEG_REF" https://github.com/FFmpeg/FFmpeg.git ffmpeg
(cd ffmpeg && ./configure --prefix="$PREFIX" --pkg-config-flags=--static \
    --enable-static --disable-shared --enable-pic \
    --disable-programs --disable-doc --disable-network --disable-autodetect \
    --disable-avdevice --disable-avfilter --disable-swresample \
    --disable-everything \
    --enable-protocol=file,pipe \
    --enable-demuxer=mpegts,mpegtsraw,mpegps,mpegvideo,matroska,mov,avi,h264,hevc,ac3,eac3,aac,wtv,asf,flv \
    --enable-parser=mpegvideo,h264,hevc,mpeg4video,vc1,ac3,aac,aac_latm,mpegaudio,dvbsub,dvd_nav \
    --enable-decoder=mpeg1video,mpeg2video,h264,hevc,mpeg4,vc1,wmv3,av1,vp9,ac3,eac3,aac,aac_latm,mp1,mp2,mp3,pcm_s16le,pcm_s16be,pcm_bluray,pcm_dvd,dvbsub,dvdsub,ccaption \
    --enable-bsf=h264_mp4toannexb,hevc_mp4toannexb \
    && make -j"$JOBS" && make install)

# comskip itself, statically linked.
git clone --depth 1 --branch "$COMSKIP_REF" https://github.com/erikkaashoek/Comskip.git comskip
(cd comskip && ./autogen.sh \
  && PKG_CONFIG_PATH="$PREFIX/lib/pkgconfig" ./configure --enable-static --disable-gui \
  && make -j"$JOBS" comskip)

cd /work
cp /build/comskip/comskip out/comskip
strip out/comskip
file out/comskip
file out/comskip | grep -q "statically linked"
out/comskip --help 2>&1 | head -n 3 || true
