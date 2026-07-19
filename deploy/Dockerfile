# Runtime image that hosts the self-updating MihuBot runner loop.
#
# MihuBot is published as a self-contained single-file linux-x64 build, so this
# image only needs the native OS dependencies plus the external tools the bot
# shells out to (ffmpeg, yt-dlp) and Discord voice native libs (opus, sodium).
# `git` is included so build-latest.sh can build the app from source when
# bootstrapping a fresh host (the .NET SDK is fetched on demand into a temp dir).
#
# Build (from the repository root):   docker build -f deploy/Dockerfile -t mihubot deploy
# See deploy/README.md for how to bootstrap the first build and run it.
FROM --platform=linux/amd64 debian:bookworm-slim

ENV DEBIAN_FRONTEND=noninteractive \
    MIHUBOT_HOME=/data

RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        git \
        tar \
        gzip \
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
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

COPY run.sh build-latest.sh /usr/local/bin/
# Normalise line endings (in case of a CRLF checkout) and make executable.
RUN sed -i 's/\r$//' /usr/local/bin/run.sh /usr/local/bin/build-latest.sh \
    && chmod +x /usr/local/bin/run.sh /usr/local/bin/build-latest.sh

# Persistent state / current build / incoming updates (certs live under State/).
VOLUME ["/data"]

EXPOSE 80 443

ENTRYPOINT ["/usr/local/bin/run.sh"]
