# Runtime image that hosts the self-updating MihuBot runner loop.
#
# MihuBot is published as a self-contained single-file linux-x64 build, so this
# image only needs the native OS dependencies plus the external tools the bot
# shells out to (ffmpeg, yt-dlp, Deno) and Discord voice native libs (opus, sodium).
# `git` is included so build-latest.sh can build the app from source when
# bootstrapping a fresh host (the .NET SDK is fetched on demand into a temp dir).
#
# Build (from the repository root):   docker build -f deploy/Dockerfile -t mihubot deploy
# See deploy/README.md for how to bootstrap the first build and run it.
FROM --platform=linux/amd64 debian:bookworm-slim

ENV DEBIAN_FRONTEND=noninteractive \
    MIHUBOT_HOME=/data \
    MIHUBOT_STORAGE_DIRECTORY=/storage

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
        libicu72 \
        libssl3 \
        libstdc++6 \
        zlib1g \
        libgcc-s1 \
        libopus0 \
        libsodium23 \
        ffmpeg \
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

COPY run.sh build-latest.sh /usr/local/bin/
# Normalise line endings (in case of a CRLF checkout) and make executable.
RUN sed -i 's/\r$//' /usr/local/bin/run.sh /usr/local/bin/build-latest.sh \
    && chmod +x /usr/local/bin/run.sh /usr/local/bin/build-latest.sh

# Persistent state / current build / incoming updates.
# /storage holds the StorageService file blobs and can be mounted separately so
# the (potentially large) file data lives on its own volume.
VOLUME ["/data", "/storage", "/youtube-archive"]

ENTRYPOINT ["/usr/local/bin/run.sh"]
