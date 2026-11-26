#!/usr/bin/env bash
#
# RingControl installer for Linux (headless CLI + NVR).
#
#   sudo ./install.sh                 # system service for the user running sudo
#   ./install.sh --user               # user service (systemd --user), no root
#   sudo ./install.sh --recordings /mnt/recordings
#
# The installer copies the binary, creates the `ringcontrol` command, prepares the
# configuration, signs in to Ring (if needed), and enables the NVR service.
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SERVICE="ringcontrol-nvr"
VERSION="${VERSION:-1.0.1}"

MODE="auto"
PREFIX=""
BIN_LINK=""
RECORDINGS=""
DO_LOGIN=1
DO_START=1

usage() {
    cat <<'EOF'
Usage: install.sh [options]

  --user                 Install for the current user (systemd --user, no root)
  --system               Force a system service installation
  --prefix <path>        Installation folder (default /opt/ringcontrol or ~/.local/opt/ringcontrol)
  --recordings <path>    Recordings folder (default ~/Videos/RingControl)
  --no-login             Do not sign in to Ring during the installation
  --no-start             Do not start the service when done
  -h, --help             Show this help

Variables: VERSION (default 1.0.1)
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --user) MODE="user" ;;
        --system) MODE="system" ;;
        --prefix) PREFIX="${2:?missing path}"; shift ;;
        --recordings) RECORDINGS="${2:?missing path}"; shift ;;
        --no-login) DO_LOGIN=0 ;;
        --no-start) DO_START=0 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage; exit 2 ;;
    esac
    shift
done

if [ "$MODE" = "auto" ]; then
    if [ "$(id -u)" -eq 0 ]; then MODE="system"; else MODE="user"; fi
fi

if [ "$MODE" = "system" ] && [ "$(id -u)" -ne 0 ]; then
    echo "To install as a system service, run: sudo $0" >&2
    echo "For a rootless installation: $0 --user" >&2
    exit 1
fi

if [ "$MODE" = "user" ] && [ "$(id -u)" -eq 0 ]; then
    echo "The --user installation must run without sudo, as the user that will run the NVR." >&2
    exit 1
fi

TARGET_USER="${SUDO_USER:-$USER}"
if [ "$MODE" = "system" ]; then
    TARGET_HOME="$(getent passwd "$TARGET_USER" | cut -d: -f6)"
    TARGET_GROUP="$(id -gn "$TARGET_USER")"
    PREFIX="${PREFIX:-/opt/ringcontrol}"
    BIN_LINK="${BIN_LINK:-/usr/local/bin/ringcontrol}"
    UNIT_DIR="/etc/systemd/system"
else
    TARGET_HOME="$HOME"
    TARGET_GROUP="$(id -gn)"
    PREFIX="${PREFIX:-$HOME/.local/opt/ringcontrol}"
    BIN_LINK="${BIN_LINK:-$HOME/.local/bin/ringcontrol}"
    UNIT_DIR="$HOME/.config/systemd/user"
fi

CONFIG_DIR="$TARGET_HOME/.config/ringcontrol"
RECORDINGS="${RECORDINGS:-$TARGET_HOME/Videos/RingControl}"

case "$(uname -m)" in
    x86_64|amd64|x64) RID="linux-x64" ;;
    aarch64|arm64)    RID="linux-arm64" ;;
    *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

PAYLOAD="$ROOT/dist/$RID/ringcontrol"

if [ ! -f "$PAYLOAD" ]; then
    echo "==> No published binary found; building for $RID"
    "$ROOT/deploy/linux/build.sh" "$(uname -m)"
fi

if [ ! -f "$PAYLOAD" ]; then
    echo "Binary not found: $PAYLOAD" >&2
    exit 1
fi

run_as_user() {
    if [ "$MODE" = "system" ]; then
        sudo -u "$TARGET_USER" -H "$@"
    else
        "$@"
    fi
}

echo "==> Installing RingControl $VERSION into $PREFIX"
install -d -m 755 "$PREFIX"
install -m 755 "$PAYLOAD" "$PREFIX/ringcontrol"

echo "==> Creating the ringcontrol command"
install -d -m 755 "$(dirname "$BIN_LINK")"
ln -sf "$PREFIX/ringcontrol" "$BIN_LINK"

echo "==> Preparing folders"
install -d -m 700 -o "$TARGET_USER" -g "$TARGET_GROUP" "$CONFIG_DIR" 2>/dev/null || install -d -m 700 "$CONFIG_DIR"
install -d -m 755 -o "$TARGET_USER" -g "$TARGET_GROUP" "$RECORDINGS" 2>/dev/null || install -d -m 755 "$RECORDINGS"

if [ "$DO_LOGIN" = "1" ] && [ ! -f "$CONFIG_DIR/session.dat" ]; then
    if [ -t 0 ]; then
        echo ""
        echo "==> Signing in to Ring (only the encrypted refresh token is stored)"
        run_as_user "$PREFIX/ringcontrol" login || {
            echo ""
            echo "Sign-in failed. Run it later:" >&2
            echo "  ringcontrol login" >&2
        }
    else
        echo "==> No interactive terminal: sign in later with 'ringcontrol login'"
    fi
fi

echo "==> Creating the NVR configuration"
run_as_user "$PREFIX/ringcontrol" nvr config --init >/dev/null 2>&1 || true

echo "==> Installing the systemd service"
install -d -m 755 "$UNIT_DIR"

ROOT_ARG=""
if [ -n "${RECORDINGS:-}" ]; then
    ROOT_ARG=" --root $RECORDINGS"
fi

sed \
    -e "s|@PREFIX@|$PREFIX|g" \
    -e "s|@USER@|$TARGET_USER|g" \
    -e "s|@GROUP@|$TARGET_GROUP|g" \
    -e "s|@HOME@|$TARGET_HOME|g" \
    -e "s|@CONFIG_DIR@|$CONFIG_DIR|g" \
    -e "s|@RECORDINGS@|$RECORDINGS|g" \
    -e "s|@ROOT_ARG@|$ROOT_ARG|g" \
    "$ROOT/deploy/linux/ringcontrol-nvr.service" > "$UNIT_DIR/$SERVICE.service"

if [ "$MODE" = "user" ]; then
    # A user service cannot harden ProtectHome over its own home directory.
    sed -i \
        -e "/^User=/d" \
        -e "/^Group=/d" \
        -e "/^ProtectHome=/d" \
        "$UNIT_DIR/$SERVICE.service"
fi

if [ "$MODE" = "system" ]; then
    systemctl daemon-reload
    if [ "$DO_START" = "1" ]; then
        systemctl enable --now "$SERVICE"
    else
        systemctl enable "$SERVICE"
    fi
else
    systemctl --user daemon-reload
    if [ "$DO_START" = "1" ]; then
        systemctl --user enable --now "$SERVICE"
    else
        systemctl --user enable "$SERVICE"
    fi
    loginctl enable-linger "$USER" >/dev/null 2>&1 || \
        echo "  (run 'sudo loginctl enable-linger $USER' so the NVR starts without an open session)"
fi

echo ""
echo "RingControl installed."
echo "  Binary:       $PREFIX/ringcontrol"
echo "  Command:      $BIN_LINK"
echo "  Configuration: $CONFIG_DIR/nvr.json"
echo "  Recordings:   $RECORDINGS"
echo "  Service:      $SERVICE"
echo ""

if [ "$MODE" = "system" ]; then
    echo "  Status:  sudo systemctl status $SERVICE"
    echo "  Logs:    sudo journalctl -u $SERVICE -f"
    echo "  Stop:    sudo systemctl stop $SERVICE"
else
    echo "  Status:  systemctl --user status $SERVICE"
    echo "  Logs:    journalctl --user -u $SERVICE -f"
    echo "  Stop:    systemctl --user stop $SERVICE"
fi

echo "  NVR status: ringcontrol nvr status"
echo ""
