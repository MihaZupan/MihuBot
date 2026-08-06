#!/usr/bin/env bash
#
# Builds the latest MihuBot `main` branch from source and produces a
# self-contained `artifacts.tar.gz` at the path given as $1 (default:
# $MIHUBOT_HOME/State/artifacts.tar.gz).
#
# Invoked by the app itself (SelfUpdateService) when a new commit is detected on
# `main`, or by the runner loop on first boot when no build exists yet.
#
# Everything it downloads - the cloned source, the .NET SDK and the NuGet cache -
# is kept in a temporary directory that is removed on exit (including on failure
# or interruption), so nothing is left behind except the resulting tarball.

set -euo pipefail

MIHUBOT_HOME="${MIHUBOT_HOME:-/data}"
OUT_TARBALL="${1:-$MIHUBOT_HOME/State/artifacts.tar.gz}"

REPO_URL="${MIHUBOT_REPO_URL:-https://github.com/MihaZupan/MihuBot}"
BRANCH="${MIHUBOT_BRANCH:-main}"
# Exact commit to build. When set (the app sets it after verifying the signature),
# the branch tip is ignored.
COMMIT="${MIHUBOT_COMMIT:-}"
PROJECT="${MIHUBOT_PROJECT:-MihuBot}"
RID="${MIHUBOT_RID:-linux-x64}"
DOTNET_CHANNEL="${MIHUBOT_DOTNET_CHANNEL:-11.0}"
DOTNET_FALLBACK_VERSION="${MIHUBOT_DOTNET_FALLBACK_VERSION:-11.0.0-preview.7.26364.116}"

WORKDIR="$(mktemp -d)"
cleanup() {
    rm -rf "$WORKDIR"
    rm -f "$OUT_TARBALL.tmp"
}
trap cleanup EXIT
trap 'exit 1' INT TERM HUP

# Keep the NuGet/dotnet caches inside the work directory so the build doesn't
# leave anything behind in $HOME either.
export NUGET_PACKAGES="$WORKDIR/nuget"
export DOTNET_CLI_HOME="$WORKDIR/dotnet-home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

if [ -n "$COMMIT" ]; then
    echo "[build] Fetching $REPO_URL @ $COMMIT ..."
    git init -q "$WORKDIR/src"
    git -C "$WORKDIR/src" remote add origin "$REPO_URL"
    git -C "$WORKDIR/src" fetch -q --depth 1 origin "$COMMIT"
    git -C "$WORKDIR/src" checkout -q --detach FETCH_HEAD
else
    echo "[build] Cloning $REPO_URL ($BRANCH) ..."
    git clone --depth 1 --branch "$BRANCH" "$REPO_URL" "$WORKDIR/src"
fi
SHA="$(git -C "$WORKDIR/src" rev-parse HEAD)"

if [ -n "$COMMIT" ] && [ "$SHA" != "$COMMIT" ]; then
    echo "[build] Checked out $SHA but expected $COMMIT" >&2
    exit 1
fi

echo "[build] Installing the .NET SDK ..."
curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$WORKDIR/dotnet-install.sh"
chmod +x "$WORKDIR/dotnet-install.sh"

publish() {
    local dotnet="$1"
    "$dotnet" publish "$PROJECT" -c Release -r "$RID" --self-contained true \
        -p:PublishSingleFile=true -o artifacts -p:SourceRevisionId="$SHA"
}

cd "$WORKDIR/src"

# Prefer the daily build; fall back to a known-good version.
DOTNET_DIR=""
if "$WORKDIR/dotnet-install.sh" --channel "$DOTNET_CHANNEL" --quality daily --install-dir "$WORKDIR/dotnet-daily" \
    && publish "$WORKDIR/dotnet-daily/dotnet"; then
    DOTNET_DIR="$WORKDIR/dotnet-daily"
else
    echo "[build] Daily build failed; falling back to $DOTNET_FALLBACK_VERSION ..."
    rm -rf artifacts
    "$WORKDIR/dotnet-install.sh" --version "$DOTNET_FALLBACK_VERSION" --install-dir "$WORKDIR/dotnet-fallback"
    publish "$WORKDIR/dotnet-fallback/dotnet"
    DOTNET_DIR="$WORKDIR/dotnet-fallback"
fi

# Ship the regex source generator analyzer next to the app.
cp "$DOTNET_DIR"/packs/Microsoft.NETCore.App.Ref/*/analyzers/dotnet/cs/System.Text.RegularExpressions.Generator.dll artifacts/ 2>/dev/null || true

echo "[build] Packaging $OUT_TARBALL ..."
mkdir -p "$(dirname "$OUT_TARBALL")"
tar -czf "$OUT_TARBALL.tmp" artifacts
mv -f "$OUT_TARBALL.tmp" "$OUT_TARBALL"

echo "[build] Done: $OUT_TARBALL (commit $SHA)"
