# RingControl

NVR y panel de control para cámaras y timbres **Ring**, con:

- **Aplicación de escritorio para Windows** (WPF, tema oscuro, español): estado de los dispositivos, vista en vivo, luz, sirena, timbre, detección de movimiento, grabación manual y **modo NVR**.
- **CLI + daemon headless para Linux**: grabación 24/7 o por eventos, rotación de segmentos, retención y servicio `systemd`.
- **Motor compartido** (`RingControl.Core`) que graba H.264 directamente a **MP4 fragmentado** sin ffmpeg ni dependencias nativas.

> ⚠️ **Aviso**: Ring no ofrece una API pública. RingControl usa la misma API privada que la app oficial y proyectos como `ring-client-api` / `python-ring-doorbell`. Ring puede cambiarla o bloquear el acceso en cualquier momento. Úsalo solo con tu propia cuenta.

---

## Características

| Área | Detalle |
| --- | --- |
| Autenticación | OAuth de Ring con contraseña, 2FA (SMS/TOTP) y refresh token |
| Seguridad | Solo se guarda el *refresh token*: DPAPI en Windows, AES-256-GCM + clave `0600` en Linux. Nunca la contraseña |
| Vídeo | WebRTC (SIPSorcery) → H.264 RTP → MP4 fragmentado, sin recompresión |
| NVR | Modos `events`, `continuous` y `both`, segmentos configurables, clips por evento, retención por días y cuota de disco |
| Almacenamiento | `<Vídeos>/RingControl/<cámara>_<id>/<fecha>_<tipo>.mp4` + sidecar `.json` |
| GUI | Vista en vivo, instantáneas, eventos, controles del dispositivo y grabación |
| CLI | `login`, `devices`, `status`, `snapshot`, `record`, `clips`, `nvr run/status/stop/config` |
| Instalación | Instalador de Windows (GUI + CLI + NVR) y `install.sh` con unidad `systemd` para Linux x64/arm64 |

---

## Requisitos

- **Windows 10/11 x64**. El instalador es autocontenido: no hace falta instalar .NET.
- **Linux x64 o arm64** con `systemd`. El binario es autocontenido.
- Una cuenta Ring con al menos una cámara o timbre, y acceso al teléfono para el 2FA.
- La cámara debe permitir **vista en vivo** (no estar desactivada en la app de Ring).

---

## Instalación en Windows

1. Ejecuta `RingControlSetup-1.0.0.exe`.
2. Elige la carpeta (por defecto `%LOCALAPPDATA%\Programs\RingControl`), los accesos directos y si quieres que el NVR arranque al iniciar sesión.
3. Pulsa **Instalar**. No requiere permisos de administrador.
4. Abre RingControl e inicia sesión con tu cuenta de Ring (código 2FA incluido).

Instalación silenciosa (por ejemplo, para despliegues):

```powershell
.\RingControlSetup-1.0.0.exe /S --autostart --no-launch
```

Opciones del instalador:

| Opción | Efecto |
| --- | --- |
| `/S`, `--silent` | Sin interfaz |
| `--dir <ruta>` | Carpeta de instalación |
| `--no-desktop` | Sin acceso directo en el escritorio |
| `--no-startmenu` | Sin entrada en el menú Inicio |
| `--no-path` | No añadir `ringcontrol` al PATH del usuario |
| `--autostart` | Arrancar el NVR al iniciar sesión |
| `--no-launch` | No abrir la app al terminar |
| `--uninstall` | Desinstalar |
| `--purge` | Al desinstalar, borrar también configuración y sesión |

También se genera `RingControl-1.0.0-win-x64.zip`, una versión portable que no escribe en el registro.

Desinstalación: **Configuración → Aplicaciones → RingControl**, o

```powershell
"%LOCALAPPDATA%\Programs\RingControl\RingControlSetup.exe" --uninstall
```

---

## Instalación en Linux (CLI + NVR)

### 1. Compilar el binario

Necesitas el SDK de .NET 9 solo para compilar:

```bash
./deploy/linux/build.sh            # detecta x64/arm64
# o
./deploy/linux/build.sh arm64
```

Genera `dist/linux-<arch>/ringcontrol` (autocontenido, un solo archivo).

### 2. Instalar

```bash
# Servicio del sistema (recomendado), ejecutado como tu usuario:
sudo ./deploy/linux/install.sh

# Servicio de usuario, sin root:
./deploy/linux/install.sh --user

# Carpeta de grabaciones personalizada:
sudo ./deploy/linux/install.sh --recordings /mnt/grabaciones
```

El instalador:

- copia el binario a `/opt/ringcontrol` (o `~/.local/opt/ringcontrol`),
- crea el comando `ringcontrol`,
- prepara `~/.config/ringcontrol/nvr.json`,
- te pide iniciar sesión en Ring si no hay sesión guardada,
- instala y activa `ringcontrol-nvr.service` con `Restart=always`.

### 3. Comprobar

```bash
ringcontrol --version
ringcontrol nvr status
sudo systemctl status ringcontrol-nvr
sudo journalctl -u ringcontrol-nvr -f
```

Si cambias la configuración:

```bash
nano ~/.config/ringcontrol/nvr.json
sudo systemctl restart ringcontrol-nvr
```

### 4. Desinstalar

```bash
sudo ./deploy/linux/uninstall.sh             # conserva grabaciones y sesión
sudo ./deploy/linux/uninstall.sh --purge     # borra también configuración/sesión
```

---

## Uso de la CLI

```text
ringcontrol login                 Inicia sesión (contraseña + 2FA)
ringcontrol logout                Borra la sesión guardada
ringcontrol devices               Lista cámaras (--json)
ringcontrol status                Estado de la cuenta y del NVR
ringcontrol snapshot [cámara]     Guarda un JPEG (--out archivo.jpg)
ringcontrol record [cámara]       Graba un clip (--seconds 30, --root carpeta)
ringcontrol clips                 Lista grabaciones (--prune, --json)
ringcontrol nvr run               Arranca el NVR en primer plano
ringcontrol nvr status            Estado del NVR en ejecución (--json)
ringcontrol nvr stop              Detiene el NVR
ringcontrol nvr config --init     Crea la configuración
```

`[cámara]` acepta el ID o parte del nombre. Si solo hay una cámara se usa automáticamente.

Variables de entorno útiles: `RING_EMAIL`, `RING_PASSWORD`, `RING_2FA_CODE` (para automatizar el login) y `RINGCONTROL_DEBUG=1` (traza de errores).

Ejemplo de grabación de prueba sin esperar a un evento:

```bash
ringcontrol nvr run --once --seconds 10
```

---

## Configuración del NVR

Archivo: `~/.config/ringcontrol/nvr.json` (Linux) o `%APPDATA%\RingControl\nvr.json` (Windows). Lo comparten la GUI y la CLI.

```json
{
  "RootFolder": "/home/usuario/Videos/RingControl",
  "Mode": "Events",
  "SegmentMinutes": 5,
  "EventClipSeconds": 45,
  "EventPollSeconds": 20,
  "RetentionDays": 7,
  "MaxStorageBytes": 21474836480,
  "RecordOnDing": true,
  "RecordOnMotion": true,
  "RecordOnDemand": false,
  "DeviceIds": [],
  "MaxConcurrentStreams": 2,
  "RecordAudio": false
}
```

| Campo | Descripción |
| --- | --- |
| `Mode` | `Events` (solo eventos), `Continuous` (24/7) o `Both` (24/7; todo queda grabado) |
| `SegmentMinutes` | Duración de cada segmento continuo (1–60) |
| `EventClipSeconds` | Duración de un clip por evento (10–600) |
| `EventPollSeconds` | Frecuencia de consulta de eventos (10–600) |
| `RetentionDays` | Días de retención; `0` desactiva el límite por fecha |
| `MaxStorageBytes` | Cuota de disco; se borran los clips más antiguos al superarla. `0` = sin límite |
| `RecordOnDing` / `RecordOnMotion` / `RecordOnDemand` | Qué eventos inician grabación |
| `DeviceIds` | IDs a grabar; vacío = todas |
| `MaxConcurrentStreams` | Sesiones simultáneas (Ring limita las vistas en vivo) |

Los cambios se aplican al reiniciar el servicio (`systemctl restart ringcontrol-nvr`) o al reiniciar el NVR desde la GUI.

### Carpeta de grabaciones

```text
Videos/RingControl/
└── Front Door_88118055/
    ├── 2026-11-25_21-46-04_continuous.mp4
    ├── 2026-11-25_21-46-04_continuous.json
    ├── 2026-11-25_21-52-10_ding.mp4
    └── 2026-11-25_21-52-10_ding.json
```

Cada `.json` contiene dispositivo, inicio/fin, duración, tamaño, resolución, FPS, tipo de disparo y si el clip se cerró correctamente.

Los MP4 son **fragmentados** (fMP4): se pueden reproducir mientras se graban y son tolerantes a cortes. VLC, navegadores, ffmpeg y la mayoría de reproductores los abren directamente. Para editarlos puede ser necesario remuxarlos:

```bash
ffmpeg -i clip.mp4 -c copy clip-remux.mp4
```

---

## Seguridad

- **Nunca se guarda la contraseña.** Solo el refresh token que Ring rota en cada uso.
- **Windows**: el token se cifra con DPAPI (`CurrentUser`), ligado a tu cuenta de Windows.
- **Linux**: AES-256-GCM con una clave aleatoria en `~/.config/ringcontrol/session.key` con permisos `0600`. La carpeta de configuración es `0700`.
- El servicio `systemd` aplica `NoNewPrivileges`, `ProtectSystem=strict`, `ProtectHome=read-only` y solo permite escribir en la configuración y en la carpeta de grabaciones.
- Las grabaciones quedan en tu disco; no se suben a ningún servicio.

---

## Limitaciones conocidas

- **API no oficial**: puede cambiar sin aviso. Si Ring modifica el flujo de autenticación o los endpoints, habrá que actualizar la app.
- **Clips en la nube**: algunos planes/cuentas no exponen URLs de reproducción (`url: null`). RingControl no depende de ellos: graba en local.
- **Instantáneas**: Ring bloquea las instantáneas si la detección de movimiento está desactivada en la cámara.
- **Audio**: la vista en vivo y las grabaciones son solo vídeo (Ring requiere un flujo BUNDLE que SIPSorcery no negocia bien).
- **Vistas simultáneas**: Ring limita cuántas sesiones en vivo permite; por eso `MaxConcurrentStreams` es 2 por defecto.
- **2FA**: el código es de un solo uso. Si el login falla, pide uno nuevo.
- Una misma cuenta no debería usar el GUI y el daemon Linux con el **mismo** refresh token a la vez. Inicia sesión por separado en cada equipo.

---

## Compilar desde el código

```bash
dotnet build RingControl.sln -c Release
```

Windows (instalador + portable):

```powershell
.\build\publish-windows.ps1 -Version 1.0.0
```

Linux:

```bash
./deploy/linux/build.sh
```

### Estructura

```text
src/RingControl.Core/    API, auth, WebRTC, RTP, MP4, motor NVR (net9.0, multiplataforma)
src/RingControl.App/     GUI WPF (net9.0-windows)
src/RingControl.Cli/     CLI y daemon NVR (net9.0)
src/RingControl.Setup/   Instalador de Windows (WPF, payload embebido)
deploy/linux/            build.sh, install.sh, uninstall.sh, unidad systemd
build/                   publish-windows.ps1
```

---

## Solución de problemas

| Síntoma | Solución |
| --- | --- |
| `No hay ninguna sesión de Ring guardada` | `ringcontrol login` |
| `La sesión guardada ha caducado` | Vuelve a iniciar sesión; Ring rotó el token |
| `La cámara no envió vídeo a tiempo` | Comprueba que la vista en vivo funciona en la app oficial y que la cámara está en línea |
| El NVR no graba eventos | Revisa `Mode`, `RecordOnDing`/`RecordOnMotion` y `ringcontrol nvr status` |
| Disco lleno | Baja `RetentionDays` o `MaxStorageBytes`; se borran los clips más antiguos |
| El servicio no arranca | `journalctl -u ringcontrol-nvr -n 100 --no-pager` |
| Ring devuelve 429 | Ring ha limitado las solicitudes; espera unos minutos |

Registros: `~/.local/state/ringcontrol/logs/nvr.log` (Linux) y `%LOCALAPPDATA%\RingControl\logs\` (Windows).
