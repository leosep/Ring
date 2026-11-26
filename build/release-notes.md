# RingControl 1.0.1

## Highlights

- **One window only.** The video is now rendered by a dedicated `HwndHost` control into a child window owned by the main WPF window. LibVLC never creates an external player window, and the LibVLCSharp overlay window that appeared in Alt+Tab is gone.
- **Embedded doorbell video.** The player waits until the native surface exists before calling `Play()`, so live view always renders inside the app.
- **Single instance.** A named mutex prevents a second RingControl window when the app is launched twice.
- **English UI.** The desktop app, CLI, installer, scripts, and messages are entirely in English.
- **Safe sign-out.** Session, devices, NVR, and recordings are shut down before the API client is disposed.
- **2FA.** The password is no longer cleared when the verification code is requested.
- **NVR settings.** Intervals are consistently clamped to 10–600 seconds and the folder supports environment variables.
- **Corrupt configuration.** An invalid `nvr.json` no longer prevents the app from starting; defaults are used.
- **Recordings shortcut.** "Open recordings" opens Explorer directly in the recordings folder.
- **Installer.** Silent installs (`/S`) exit correctly, and reinstalling or upgrading over an existing folder works.

## Downloads

- `RingControlSetup-1.0.1.exe` — Windows 10/11 x64 installer (self-contained).
- `RingControl-1.0.1-win-x64.zip` — portable version (no registry changes).
- `SHA256SUMS.txt` — checksums.
