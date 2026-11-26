#!/usr/bin/env bash
#
# Instalador de RingControl para Linux (CLI + NVR headless).
#
#   sudo ./install.sh                 # servicio del sistema para el usuario que ejecuta sudo
#   ./install.sh --user               # servicio de usuario (systemd --user), sin root
#   sudo ./install.sh --recordings /mnt/grabaciones
#
# El instalador copia el binario, crea el comando `ringcontrol`, prepara la
# configuración, inicia sesión en Ring (si hace falta) y activa el servicio NVR.
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SERVICE="ringcontrol-nvr"
VERSION="${VERSION:-1.0.0}"

MODE="auto"
PREFIX=""
BIN_LINK=""
RECORDINGS=""
DO_LOGIN=1
DO_START=1

usage() {
    cat <<'EOF'
Uso: install.sh [opciones]

  --user                 Instala para el usuario actual (systemd --user, sin root)
  --system               Fuerza la instalación como servicio del sistema
  --prefix <ruta>        Carpeta de instalación (por defecto /opt/ringcontrol o ~/.local/opt/ringcontrol)
  --recordings <ruta>    Carpeta de grabaciones (por defecto ~/Videos/RingControl)
  --no-login             No iniciar sesión en Ring durante la instalación
  --no-start             No arrancar el servicio al terminar
  -h, --help             Muestra esta ayuda

Variables: VERSION (por defecto 1.0.0)
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --user) MODE="user" ;;
        --system) MODE="system" ;;
        --prefix) PREFIX="${2:?falta la ruta}"; shift ;;
        --recordings) RECORDINGS="${2:?falta la ruta}"; shift ;;
        --no-login) DO_LOGIN=0 ;;
        --no-start) DO_START=0 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Opción desconocida: $1" >&2; usage; exit 2 ;;
    esac
    shift
done

if [ "$MODE" = "auto" ]; then
    if [ "$(id -u)" -eq 0 ]; then MODE="system"; else MODE="user"; fi
fi

if [ "$MODE" = "system" ] && [ "$(id -u)" -ne 0 ]; then
    echo "Para instalar como servicio del sistema ejecuta: sudo $0" >&2
    echo "Para una instalación sin root: $0 --user" >&2
    exit 1
fi

if [ "$MODE" = "user" ] && [ "$(id -u)" -eq 0 ]; then
    echo "La instalación --user debe ejecutarse sin sudo, con el usuario que usará el NVR." >&2
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
    *) echo "Arquitectura no soportada: $(uname -m)" >&2; exit 1 ;;
esac

PAYLOAD="$ROOT/dist/$RID/ringcontrol"

if [ ! -f "$PAYLOAD" ]; then
    echo "==> No hay binario publicado; compilando para $RID"
    "$ROOT/deploy/linux/build.sh" "$(uname -m)"
fi

if [ ! -f "$PAYLOAD" ]; then
    echo "No se encontró el binario: $PAYLOAD" >&2
    exit 1
fi

run_as_user() {
    if [ "$MODE" = "system" ]; then
        sudo -u "$TARGET_USER" -H "$@"
    else
        "$@"
    fi
}

echo "==> Instalando RingControl $VERSION en $PREFIX"
install -d -m 755 "$PREFIX"
install -m 755 "$PAYLOAD" "$PREFIX/ringcontrol"

echo "==> Creando el comando ringcontrol"
install -d -m 755 "$(dirname "$BIN_LINK")"
ln -sf "$PREFIX/ringcontrol" "$BIN_LINK"

echo "==> Preparando carpetas"
install -d -m 700 -o "$TARGET_USER" -g "$TARGET_GROUP" "$CONFIG_DIR" 2>/dev/null || install -d -m 700 "$CONFIG_DIR"
install -d -m 755 -o "$TARGET_USER" -g "$TARGET_GROUP" "$RECORDINGS" 2>/dev/null || install -d -m 755 "$RECORDINGS"

if [ "$DO_LOGIN" = "1" ] && [ ! -f "$CONFIG_DIR/session.dat" ]; then
    if [ -t 0 ]; then
        echo ""
        echo "==> Iniciando sesión en Ring (se guarda solo el refresh token cifrado)"
        run_as_user "$PREFIX/ringcontrol" login || {
            echo ""
            echo "No se pudo iniciar sesión. Ejecuta más tarde:" >&2
            echo "  ringcontrol login" >&2
        }
    else
        echo "==> Sin terminal interactiva: inicia sesión luego con 'ringcontrol login'"
    fi
fi

echo "==> Creando la configuración del NVR"
run_as_user "$PREFIX/ringcontrol" nvr config --init >/dev/null 2>&1 || true

echo "==> Instalando el servicio systemd"
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
    # Un servicio de usuario no puede endurecer ProtectHome sobre su propio hogar.
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
        echo "  (ejecuta 'sudo loginctl enable-linger $USER' para que el NVR arranque sin sesión abierta)"
fi

echo ""
echo "RingControl instalado."
echo "  Binario:      $PREFIX/ringcontrol"
echo "  Comando:      $BIN_LINK"
echo "  Configuración: $CONFIG_DIR/nvr.json"
echo "  Grabaciones:  $RECORDINGS"
echo "  Servicio:     $SERVICE"
echo ""

if [ "$MODE" = "system" ]; then
    echo "  Estado:  sudo systemctl status $SERVICE"
    echo "  Logs:    sudo journalctl -u $SERVICE -f"
    echo "  Parar:   sudo systemctl stop $SERVICE"
else
    echo "  Estado:  systemctl --user status $SERVICE"
    echo "  Logs:    journalctl --user -u $SERVICE -f"
    echo "  Parar:   systemctl --user stop $SERVICE"
fi

echo "  Estado NVR: ringcontrol nvr status"
echo ""
