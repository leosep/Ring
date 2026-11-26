#!/usr/bin/env bash
#
# Publica RingControl para Linux como binario autocontenido.
#
#   ./deploy/linux/build.sh            # detecta la arquitectura
#   ./deploy/linux/build.sh arm64      # fuerza linux-arm64
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
VERSION="${VERSION:-1.0.0}"
ARCH="${1:-$(uname -m)}"

case "$ARCH" in
    x86_64|amd64|x64) RID="linux-x64" ;;
    aarch64|arm64)    RID="linux-arm64" ;;
    *) echo "Arquitectura no soportada: $ARCH (usa linux-x64 o linux-arm64)" >&2; exit 1 ;;
esac

if ! command -v dotnet >/dev/null 2>&1; then
    echo "Se necesita el SDK de .NET 9 para compilar." >&2
    echo "Instálalo con: https://dot.net/v1/dotnet-install.sh --channel 9.0" >&2
    exit 1
fi

OUT="$ROOT/dist/$RID"
rm -rf "$OUT"
mkdir -p "$OUT"

echo "==> Publicando RingControl $VERSION para $RID"
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
echo "Listo: $OUT/ringcontrol"
"$OUT/ringcontrol" --version || true
