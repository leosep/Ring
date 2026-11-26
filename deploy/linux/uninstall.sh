#!/usr/bin/env bash
#
# Desinstala RingControl en Linux. Conserva las grabaciones salvo --purge-recordings.
#
#   sudo ./uninstall.sh
#   ./uninstall.sh --user
#   sudo ./uninstall.sh --purge          # borra también configuración y sesión
#
set -euo pipefail

SERVICE="ringcontrol-nvr"
MODE="auto"
PREFIX=""
PURGE=0
PURGE_RECORDINGS=0

usage() {
    cat <<'EOF'
Uso: uninstall.sh [opciones]

  --user                Desinstala la instalación de usuario (systemd --user)
  --system              Fuerza la desinstalación del servicio del sistema
  --prefix <ruta>       Carpeta de instalación usada al instalar
  --purge               Elimina también la configuración y la sesión de Ring
  --purge-recordings    Elimina también las grabaciones (¡irreversible!)
  -h, --help            Muestra esta ayuda
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --user) MODE="user" ;;
        --system) MODE="system" ;;
        --prefix) PREFIX="${2:?falta la ruta}"; shift ;;
        --purge) PURGE=1 ;;
        --purge-recordings) PURGE_RECORDINGS=1 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Opción desconocida: $1" >&2; usage; exit 2 ;;
    esac
    shift
done

if [ "$MODE" = "auto" ]; then
    if [ "$(id -u)" -eq 0 ]; then MODE="system"; else MODE="user"; fi
fi

if [ "$MODE" = "system" ] && [ "$(id -u)" -ne 0 ]; then
    echo "Ejecuta con sudo para desinstalar el servicio del sistema." >&2
    exit 1
fi

TARGET_USER="${SUDO_USER:-$USER}"
if [ "$MODE" = "system" ]; then
    TARGET_HOME="$(getent passwd "$TARGET_USER" | cut -d: -f6)"
    PREFIX="${PREFIX:-/opt/ringcontrol}"
    BIN_LINK="/usr/local/bin/ringcontrol"
    UNIT_DIR="/etc/systemd/system"
else
    TARGET_HOME="$HOME"
    PREFIX="${PREFIX:-$HOME/.local/opt/ringcontrol}"
    BIN_LINK="$HOME/.local/bin/ringcontrol"
    UNIT_DIR="$HOME/.config/systemd/user"
fi

CONFIG_DIR="$TARGET_HOME/.config/ringcontrol"
RECORDINGS="$TARGET_HOME/Videos/RingControl"

echo "==> Deteniendo el servicio"
if [ "$MODE" = "system" ]; then
    systemctl stop "$SERVICE" 2>/dev/null || true
    systemctl disable "$SERVICE" 2>/dev/null || true
    rm -f "$UNIT_DIR/$SERVICE.service"
    systemctl daemon-reload 2>/dev/null || true
else
    systemctl --user stop "$SERVICE" 2>/dev/null || true
    systemctl --user disable "$SERVICE" 2>/dev/null || true
    rm -f "$UNIT_DIR/$SERVICE.service"
    systemctl --user daemon-reload 2>/dev/null || true
fi

echo "==> Eliminando archivos"
rm -rf "$PREFIX"
if [ -L "$BIN_LINK" ] || [ -f "$BIN_LINK" ]; then
    rm -f "$BIN_LINK"
fi

if [ "$PURGE" = "1" ]; then
    echo "==> Eliminando configuración y sesión"
    rm -rf "$CONFIG_DIR"
fi

if [ "$PURGE_RECORDINGS" = "1" ]; then
    echo "==> Eliminando grabaciones en $RECORDINGS"
    rm -rf "$RECORDINGS"
else
    echo "==> Las grabaciones se conservan en $RECORDINGS"
fi

echo ""
echo "RingControl desinstalado."
