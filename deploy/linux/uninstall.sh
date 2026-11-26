#!/usr/bin/env bash
#
# Uninstalls RingControl on Linux. Recordings are kept unless --purge-recordings.
#
#   sudo ./uninstall.sh
#   ./uninstall.sh --user
#   sudo ./uninstall.sh --purge          # also deletes the configuration and session
#
set -euo pipefail

SERVICE="ringcontrol-nvr"
MODE="auto"
PREFIX=""
PURGE=0
PURGE_RECORDINGS=0

usage() {
    cat <<'EOF'
Usage: uninstall.sh [options]

  --user                Uninstall the user installation (systemd --user)
  --system              Force removal of the system service
  --prefix <path>       Installation folder used when installing
  --purge               Also delete the configuration and the Ring session
  --purge-recordings    Also delete the recordings (irreversible!)
  -h, --help            Show this help
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --user) MODE="user" ;;
        --system) MODE="system" ;;
        --prefix) PREFIX="${2:?missing path}"; shift ;;
        --purge) PURGE=1 ;;
        --purge-recordings) PURGE_RECORDINGS=1 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage; exit 2 ;;
    esac
    shift
done

if [ "$MODE" = "auto" ]; then
    if [ "$(id -u)" -eq 0 ]; then MODE="system"; else MODE="user"; fi
fi

if [ "$MODE" = "system" ] && [ "$(id -u)" -ne 0 ]; then
    echo "Run with sudo to uninstall the system service." >&2
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

echo "==> Stopping the service"
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

echo "==> Removing files"
rm -rf "$PREFIX"
if [ -L "$BIN_LINK" ] || [ -f "$BIN_LINK" ]; then
    rm -f "$BIN_LINK"
fi

if [ "$PURGE" = "1" ]; then
    echo "==> Removing the configuration and session"
    rm -rf "$CONFIG_DIR"
fi

if [ "$PURGE_RECORDINGS" = "1" ]; then
    echo "==> Removing recordings in $RECORDINGS"
    rm -rf "$RECORDINGS"
else
    echo "==> Recordings are kept in $RECORDINGS"
fi

echo ""
echo "RingControl uninstalled."
