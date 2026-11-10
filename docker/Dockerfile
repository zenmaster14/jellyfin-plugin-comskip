# Official Jellyfin image + comskip, compiled from source.
# (Debian 13 "trixie", which the official image is based on, no longer packages comskip.)
ARG JELLYFIN_IMAGE=jellyfin/jellyfin:latest
FROM ${JELLYFIN_IMAGE}

ARG COMSKIP_REPO=https://github.com/erikkaashoek/Comskip
ARG COMSKIP_REF=master

RUN set -eux; \
    export DEBIAN_FRONTEND=noninteractive; \
    buildDeps="ca-certificates git build-essential autoconf automake libtool pkg-config \
        libargtable2-dev libavformat-dev libavcodec-dev libavutil-dev libswscale-dev"; \
    apt-get update; \
    apt-get install -y --no-install-recommends $buildDeps; \
    git clone --depth 1 --branch "$COMSKIP_REF" "$COMSKIP_REPO" /tmp/comskip; \
    cd /tmp/comskip; \
    ./autogen.sh; \
    ./configure; \
    make -j"$(nproc)"; \
    install -m 0755 comskip /usr/local/bin/comskip; \
    cd /; rm -rf /tmp/comskip; \
    # Keep only the shared libraries comskip actually links against, then drop the toolchain.
    for lib in $(ldd /usr/local/bin/comskip | awk '/=> \//{print $3}'); do \
        dpkg -S "$(basename "$lib")" 2>/dev/null | cut -d: -f1; \
    done | sort -u | xargs -r apt-mark manual; \
    apt-get purge -y --auto-remove $buildDeps; \
    rm -rf /var/lib/apt/lists/*; \
    if ldd /usr/local/bin/comskip | grep -q "not found"; then ldd /usr/local/bin/comskip; exit 1; fi; \
    comskip --help 2>&1 | head -n 3 || true
