#!/usr/bin/env bash
#
# Publishes RingControl for Linux as a self-contained binary.
#
#   ./deploy/linux/build.sh            # detects the architecture
#   ./deploy/linux/build.sh arm64      # forces linux-arm64
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
VERSION="${VERSION:-1.0.1}"
ARCH="${1:-$(uname -m)}"

case "$ARCH" in
    x86_64|amd64|x64) RID="linux-x64" ;;
    aarch64|arm64)    RID="linux-arm64" ;;
    *) echo "Unsupported architecture: $ARCH (use linux-x64 or linux-arm64)" >&2; exit 1 ;;
esac

if ! command -v dotnet >/dev/null 2>&1; then
    echo "The .NET 9 SDK is required to build." >&2
    echo "Install it with: https://dot.net/v1/dotnet-install.sh --channel 9.0" >&2
    exit 1
fi

OUT="$ROOT/dist/$RID"
rm -rf "$OUT"
mkdir -p "$OUT"

echo "==> Publishing RingControl $VERSION for $RID"
dotnet publish "$ROOT/src/RingControl.Cli/RingControl.Cli.csproj" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:Version="$VERSION" \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -o "$OUT"

chmod +x "$OUT/ringcontrol"

echo ""
echo "Done: $OUT/ringcontrol"
"$OUT/ringcontrol" --version || true
