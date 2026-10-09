# Runtime image that hosts the self-updating MihuBot runner loop.
#
# MihuBot is published as a self-contained single-file linux-x64 build, so this
# image only needs the native OS dependencies plus the external tools the bot
# shells out to (ffmpeg, yt-dlp, Deno) and Discord voice native libs (opus, sodium, DAVE).
# `git` is included so build-latest.sh can build the app from source when
# bootstrapping a fresh host (the .NET SDK is fetched on demand into a temp dir).
#
# Build (from the repository root):   docker build -f deploy/Dockerfile -t mihubot deploy
# See deploy/README.md for how to bootstrap the first build and run it.
# Official libdave binaries require newer glibc/libstdc++ than Bookworm provides.
FROM --platform=linux/amd64 debian:trixie-slim

ARG LIBDAVE_VERSION=1.2.1

ENV DEBIAN_FRONTEND=noninteractive \
    MIHUBOT_HOME=/data \
    MIHUBOT_STORAGE_DIRECTORY=/storage

# Discord.Net imports unversioned Opus/sodium names; runtime packages need aliases.
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        git \
        python3 \
        python3-venv \
        python3-pip \
        tar \
        gzip \
        unzip \
        libicu76 \
        libssl3t64 \
        libstdc++6 \
        zlib1g \
        libgcc-s1 \
        libopus0 \
        libsodium23 \
        ffmpeg \
    && ln -s /usr/lib/x86_64-linux-gnu/libopus.so.0 /usr/local/lib/libopus.so \
    && ln -s /usr/lib/x86_64-linux-gnu/libsodium.so.23 /usr/local/lib/libsodium.so \
    && curl -fsSL "https://github.com/discord/libdave/releases/download/v${LIBDAVE_VERSION}/cpp/libdave-Linux-X64-boringssl.zip" \
        -o /tmp/libdave.zip \
    && unzip -q /tmp/libdave.zip -d /tmp/libdave \
    && install -m 644 /tmp/libdave/lib/libdave.so /usr/local/lib/libdave.so \
    && mkdir -p /usr/local/share/licenses/libdave \
    && cp /tmp/libdave/licenses/* /usr/local/share/licenses/libdave/ \
    && rm -rf /tmp/libdave /tmp/libdave.zip \
    && ldconfig \
    && curl -fsSL https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux \
        -o /usr/local/bin/yt-dlp \
    && chmod +x /usr/local/bin/yt-dlp \
    && curl -fsSL https://github.com/denoland/deno/releases/latest/download/deno-x86_64-unknown-linux-gnu.zip \
        -o /tmp/deno.zip \
    && unzip /tmp/deno.zip -d /usr/local/bin \
    && chmod +x /usr/local/bin/deno \
    && rm /tmp/deno.zip \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# Fail the image build if a voice library or its native dependencies cannot load.
RUN python3 -c 'import ctypes; ctypes.CDLL("libopus.so").opus_get_version_string(); assert ctypes.CDLL("libsodium.so").sodium_init() >= 0; assert ctypes.CDLL("libdave.so").daveMaxSupportedProtocolVersion() > 0'

COPY run.sh build-latest.sh /usr/local/bin/
# Normalise line endings (in case of a CRLF checkout) and make executable.
RUN sed -i 's/\r$//' /usr/local/bin/run.sh /usr/local/bin/build-latest.sh \
    && chmod +x /usr/local/bin/run.sh /usr/local/bin/build-latest.sh

# Persistent state / current build / incoming updates.
# /storage holds the StorageService file blobs and can be mounted separately so
# the (potentially large) file data lives on its own volume.
VOLUME ["/data", "/storage", "/youtube-archive"]

ENTRYPOINT ["/usr/local/bin/run.sh"]
