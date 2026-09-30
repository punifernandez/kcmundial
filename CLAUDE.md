# KCMundial 2 – contexto para Claude

Photobooth para eventos (WPF, .NET 8, Windows). El invitado elige un marco, se saca la foto con una
Logitech Brio (apaisada), se imprime sola como figurita 5×7 cm en una DNP DP-QW410 (hoja 4×6 cortada
al medio: 3×4") y se lleva la foto
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
- `src/KCMundial.Camera/MediaCaptureCameraManager.cs` – preview ≤1080p en BGRA pedido a Media Foundation.
  Al empezar la cuenta regresiva pasa al formato más grande (4K) con el preview a media resolución, para que la
  cámara enfoque y exponga; al disparar toma 4 cuadros y se queda con el más nítido, y vuelve al preview.
  `CameraManager.cs` (DirectShow/OpenCV) es solo respaldo.
- `src/KCMundial.Processing` – `FiguritaComposer` (rotación, recorte centrado a la proporción del marco,
  marco encima, JPEG a 300 dpi) y `ExportService` (raw, máster, figurita, hoja de impresión 3×4", 20×30 con
  bandas, en paralelo; subida para el QR en segundo plano).
- Subida del QR: `https://kcmundial.puniweb.com/upload` vía Cloudflare Tunnel. Si responde 530/"error code: 1033",
  el túnel (`cloudflared`) o el servidor de subida no están corriendo: no es un bug de la app.
- `src/KCMundial.Vision` – detección de caras (YuNet ONNX + Haar), solo para indicaciones de posición.

## Decisiones tomadas (no revertir sin consultar)
- La figurita es 5×7 cm (proporción 5:7): marcos de 2500×3500, plantilla en `plantillas/`.
  NO imprimir a hoja completa 4×6 (fue un error): se imprime la figurita entera en 3×4" con corte automático,
  como la app anterior. El 20×30 lleva la figurita entera con bandas.
- Orientación de la cámara: `CameraRotation` (0 apaisada, 90/270 vertical) endereza la imagen; el preview se
  rota con un `LayoutTransform` en la vista, la foto se rota en Skia. La foto se recorta al centro con la
  proporción del marco (5:7).
- La foto final nunca sale espejada (solo el preview).
- La Sprocket/ADB ya no se usa.
- Pantalla de resultado sin tiempo: la figurita chica se imprime sola en la DNP; botones "Imprimir otra" (DNP),
  "Imprimir XL" (Epson L8050, A4: se imprime el máster 5:7 entero, ~20×28 cm) y "Tomar otra foto".

## Compilar
- En Windows: `dotnet build KCMundial.sln` / `publicar.bat`.
- En macOS/Linux (solo verifica que compile): `dotnet build KCMundial.sln -p:EnableWindowsTargeting=true`.
- El log (`kcmundial.log` junto al .exe) registra formatos de cámara elegidos, tiempos de captura y
  exportación, e impresión: revisarlo primero ante cualquier problema.
