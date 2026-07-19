#!/usr/bin/env bash
#
# Self-updating runner loop for MihuBot.
#
# MihuBot polls GitHub for new commits on `main` and, on detecting one, invokes
# build-latest.sh itself to produce a fresh `artifacts.tar.gz` at `next_update/`
# (relative to its working directory), then exits. This script launches the app,
# and once it exits, applies any pending update and relaunches - forever.
#
# Everything lives under $MIHUBOT_HOME (a persistent volume):
#   $MIHUBOT_HOME/artifacts/    - the current build (replaced on every update)
#   $MIHUBOT_HOME/State/        - persistent app state (databases, logs, ...)
#   $MIHUBOT_HOME/next_update/  - incoming update tarball
#
# For the first boot on a new host there is no build yet (the deploy endpoint is
# served by the app itself). The runner bootstraps by building the latest main
# branch in-container (see build-latest.sh), unless an initial build is supplied
# manually as $MIHUBOT_HOME/State/artifacts.tar.gz.
#
# The app resolves `State/` and `next_update/` relative to its working directory
# (via System.IO / Environment.CurrentDirectory), so we run it from
# $MIHUBOT_HOME and pin ASP.NET's content root to `artifacts/` (which drives
# wwwroot / appsettings) - keeping data and the replaceable build separate
# without any symlinks.

set -uo pipefail

MIHUBOT_HOME="${MIHUBOT_HOME:-/data}"
EXECUTABLE="${MIHUBOT_EXECUTABLE:-MihuBot}"
# Helper script (next to this one) that builds the latest main branch when no
# build is available yet.
BUILD_SCRIPT="${MIHUBOT_BUILD_SCRIPT:-$(dirname "$(readlink -f "$0")")/build-latest.sh}"

APP_DIR="$MIHUBOT_HOME/artifacts"
STATE_DIR="$MIHUBOT_HOME/State"
UPDATE_DIR="$MIHUBOT_HOME/next_update"
UPDATE_TARBALL="$UPDATE_DIR/artifacts.tar.gz"
# Optional initial build supplied by whoever runs the image, placed in the State
# directory. Used as-is if present; otherwise produced by BUILD_SCRIPT.
SEED_TARBALL="$STATE_DIR/artifacts.tar.gz"

mkdir -p "$MIHUBOT_HOME" "$STATE_DIR" "$UPDATE_DIR"

extract_build() {
    # The tarball produced by CI (`tar -czf artifacts.tar.gz artifacts`) contains
    # a top-level `artifacts/` directory, so extract it into $MIHUBOT_HOME.
    rm -rf "$APP_DIR"
    if tar -xzf "$1" -C "$MIHUBOT_HOME"; then
        chmod +x "$APP_DIR/$EXECUTABLE" 2>/dev/null || true
        return 0
    fi
    return 1
}

apply_pending_update() {
    if [ -f "$UPDATE_TARBALL" ]; then
        echo "[runner] Applying pending update ..."
        if extract_build "$UPDATE_TARBALL"; then
            rm -f "$UPDATE_TARBALL"
            echo "[runner] Update applied."
        else
            echo "[runner] Failed to extract update; leaving tarball in place for retry."
        fi
        return 0
    fi

    # Already have a runnable build; nothing to do until the next deployment.
    [ -x "$APP_DIR/$EXECUTABLE" ] && return 0

    # First boot / recovery: build the latest main branch, unless an initial
    # build was supplied manually in the State directory.
    if [ ! -f "$SEED_TARBALL" ]; then
        echo "[runner] No build present; building latest main via $BUILD_SCRIPT ..."
        if ! "$BUILD_SCRIPT" "$SEED_TARBALL"; then
            echo "[runner] Build failed; will retry."
            rm -f "$SEED_TARBALL"
            return 0
        fi
    fi

    echo "[runner] Applying build from $SEED_TARBALL ..."
    extract_build "$SEED_TARBALL" \
        && echo "[runner] Build applied." \
        || { echo "[runner] Failed to extract $SEED_TARBALL; removing it."; rm -f "$SEED_TARBALL"; }
}

while true; do
    apply_pending_update

    if [ ! -x "$APP_DIR/$EXECUTABLE" ]; then
        echo "[runner] No build available yet; retrying in 30s ..."
        sleep 30
        continue
    fi

    # Optional secrets file (e.g. an Azure service principal for running outside
    # of Azure) kept on the volume so it survives updates. It is loaded relative
    # to the content root (artifacts/, wiped on update), so copy it in.
    if [ -f "$MIHUBOT_HOME/credentials.json" ]; then
        cp -f "$MIHUBOT_HOME/credentials.json" "$APP_DIR/credentials.json"
    fi

    # Run from $MIHUBOT_HOME so the app's relative State/ and next_update/ dirs
    # (resolved against the working directory) land on the persistent volume,
    # while pinning ASP.NET's content root to the build so wwwroot / appsettings
    # still resolve. This avoids having to symlink those dirs into artifacts/.
    echo "[runner] Starting $EXECUTABLE ..."
    ( cd "$MIHUBOT_HOME" && ASPNETCORE_CONTENTROOT="$APP_DIR" exec "$APP_DIR/$EXECUTABLE" )
    status=$?
    echo "[runner] $EXECUTABLE exited with status $status."

    # If the app exited without a pending update it likely crashed; back off a
    # little to avoid a hot restart loop.
    if [ ! -f "$UPDATE_TARBALL" ]; then
        echo "[runner] No pending update; restarting in 3s ..."
        sleep 3
    fi
done
