# KCMundial 2 – contexto para Claude

Photobooth para eventos (WPF, .NET 8, Windows). El invitado elige un marco, se saca la foto con una
Logitech Brio montada vertical, se imprime sola en una DNP DP-QW410 (papel 4×6") y se lleva la foto
digital por QR. El segundo monitor muestra un video en loop y, después de cada foto, la figurita + QR.
Detalles de uso, configuración y archivos generados: `README.md`.

## Reglas en la PC del evento
- En la misma PC hay una app vieja en `C:\KCMundial`. **No tocarla**: no modificar, borrar ni mover nada
  ahí, y no ejecutarla (además compite por la cámara).
- Esta app se instala solo en `C:\KCMundial2` (`KCMundial2.exe`) con `publicar.bat`. Todo lo que genera
  (settings, fotos, log) queda en esa carpeta.
- Trabajar en la rama `v2`. Nunca hacer push a `main` (es la versión vieja).
- No cambiar la configuración ni los drivers de la impresora o la cámara sin preguntar.
- Preguntar antes de instalar software.

## Cómo está armado
- `src/KCMundial.App` – WPF: vistas, view models, `PhotoPrinter` (impresión DNP), `AppSettings`
  (`kcmundial.settings.json`), `ScreenHelper` (monitores), `FrameSampler` (muestra chica para detección).
- `src/KCMundial.Camera/MediaCaptureCameraManager.cs` – preview ≤1080p en BGRA pedido a Media Foundation;
  la foto cambia un instante al formato más grande (4K) y vuelve al preview. Si falla, usa el cuadro del preview.
  `CameraManager.cs` (DirectShow/OpenCV) es solo respaldo.
- `src/KCMundial.Processing` – `FiguritaComposer` (rotación, recorte centrado a la proporción del marco,
  marco encima, JPEG a 300 dpi) y `ExportService` (raw, máster, 4×6, 20×30 en paralelo; subida para el QR
  en segundo plano).
- `src/KCMundial.Vision` – detección de caras (YuNet ONNX + Haar), solo para indicaciones de posición.

## Decisiones tomadas (no revertir sin consultar)
- Marcos en proporción 2:3 (2400×3600): coincide con el papel 4×6" y con la ampliación 20×30.
  Plantilla en `plantillas/`.
- Cámara vertical: `CameraRotation` (90 o 270) endereza la imagen; el preview se rota con un
  `LayoutTransform` en la vista, la foto se rota en Skia.
- La foto final nunca sale espejada (solo el preview).
- La Sprocket/ADB ya no se usa.

## Compilar
- En Windows: `dotnet build KCMundial.sln` / `publicar.bat`.
- En macOS/Linux (solo verifica que compile): `dotnet build KCMundial.sln -p:EnableWindowsTargeting=true`.
- El log (`kcmundial.log` junto al .exe) registra formatos de cámara elegidos, tiempos de captura y
  exportación, e impresión: revisarlo primero ante cualquier problema.
