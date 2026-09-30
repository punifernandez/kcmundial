# KCMundial – Photobooth

Photobooth para eventos: el invitado elige un marco, se saca la foto, se imprime sola como **figurita 5×7 cm en la
DNP DP-QW410** (hoja 4×6 cortada al medio por la impresora) y se lleva la foto digital escaneando un QR. En el segundo monitor corre un video en loop y, después de cada foto,
se muestra la figurita con el QR.

## Requisitos

- Windows 10/11, .NET 8
- Cámara **Logitech Brio 4K** (apaisada; también funciona montada vertical, ver `CameraRotation`)
- Impresora **DNP DP-QW410** con su driver instalado y papel 4×6" (imprime en 3×4" con corte automático)
- Impresora **Epson EcoTank L8050** con papel fotográfico A4, para el botón "Imprimir XL". El tipo de papel y
  la calidad se toman de sus *Preferencias de impresión* en Windows: dejarlas en papel fotográfico / alta calidad.
- Pantalla táctil vertical (principal) + monitor/TV (secundario, opcional)

## Compilar y correr

```bash
dotnet build KCMundial.sln -c Release
dotnet run --project src\KCMundial.App\KCMundial.App.csproj -c Release
```

### Instalar en la PC del evento

**KCMundial 2 es una instalación aparte:** se llama `KCMundial2.exe`, va en `C:\KCMundial2` y guarda todo
(configuración, fotos, log) en esa carpeta. La app vieja no se toca.

Con el repo clonado en la PC, doble clic en **`publicar.bat`** (o desde una terminal):

```bash
publicar.bat
```

Después copiá el video a `C:\KCMundial2\assets\promo.mp4`.

No abras las dos apps a la vez: la cámara la usa una sola. Si la vieja está abierta, la nueva muestra el preview pero no puede sacar la foto en 4K.

(También compila desde macOS/Linux para verificar: `dotnet build KCMundial.sln -p:EnableWindowsTargeting=true`.)

## Qué genera cada foto

Todo queda en carpetas junto al `.exe`, con el mismo nombre de archivo (fecha_hora_código):

| Carpeta | Tamaño | Para qué |
|---|---|---|
| `raw/` | resolución completa de la cámara (Brio: 4096×2160; vertical: 2160×4096) | original, sin marco |
| `figuritas_hd/` | lado largo 3600 px (con marcos 5:7: 2571×3600) | máster con marco |
| `figuritas/` | 1182×1654 (5×7 cm a 600 dpi) | se muestra en pantalla y se comparte por QR |
| `impresion/` | 900×1200 (3×4" a 300 dpi) | hoja para la DNP: la figurita entera, centrada, con margen blanco |
| `ampliaciones_20x30/` | 2362×3543 (20×30 cm a 300 dpi) | la figurita entera a 20×28 cm con bandas del color del fondo |

Todos los JPEG van marcados a 300 dpi.

## Configuración (`kcmundial.settings.json`)

Se crea junto al `.exe` la primera vez que se abre la app. Cambiá los valores y reiniciá la app:

| Opción | Por defecto | Qué hace |
|---|---|---|
| `CameraRotation` | `0` | Grados (horario) para enderezar la imagen. Brio apaisada: `0`. Montada vertical: `90` (o `270` si sale cabeza abajo). |
| `MirrorPreview` | `true` | Preview en espejo (la foto final nunca sale espejada). |
| `HighResCapture` | `true` | Foto en la resolución máxima de la cámara: pasa a 4K al empezar la cuenta regresiva (para que enfoque) y se queda con el cuadro más nítido. Si falla, usa el cuadro del preview. |
| `CountdownSeconds` | `3` | Cuenta regresiva. |
| `AutoPrint` | `true` | Imprimir apenas se saca la foto. |
| `PrinterName` | `""` | Nombre (o parte) de la impresora. Vacío = la primera que contenga "QW410". |
| `PrintCopies` | `1` | Copias por foto. |
| `PrintPaperName` | `""` | Nombre (o parte) del papel del driver, tal como aparece en el log ("driver paper sizes"). Tiene prioridad sobre el tamaño. |
| `PrintPageWidthInches` / `PrintPageHeightInches` | `3` / `4` | Si no hay `PrintPaperName`: se elige el papel del driver más parecido a este tamaño. |
| `PrintMarginMm` | `2` | Margen blanco alrededor de la figurita en la hoja. |
| `XlPrinterName` | `"L8050"` | Impresora del botón "Imprimir XL" (nombre o parte). |
| `XlPaperName` | `""` | Papel de la XL por nombre (ver "driver paper sizes" en el log). Vacío = el más parecido al tamaño. |
| `XlPageWidthInches` / `XlPageHeightInches` | `8.27` / `11.69` | Tamaño de hoja XL (A4). |
| `XlMarginMm` | `3` | Margen de seguridad en la XL (la impresión sin bordes recorta los costados). |
| `XlCopies` | `1` | Copias por cada "Imprimir XL". |
| `UploadEnabled` | `true` | Subir la foto al servidor para el QR. Si no hay internet, el QR apunta a la red local. |

## Marcos

`assets/Fondo_1.png`, `Fondo_2.png`, `Fondo_3.png`: PNG con la ventana de la foto **transparente**, en proporción
**5:7** (la figurita). Recomendado **2500×3500**. Plantilla e instrucciones para Canva:
[`plantillas/`](plantillas/COMO_HACER_LOS_MARCOS.md). La app toma la proporción del marco.

## Segundo monitor

Poné el video en `assets/promo.mp4` (también sirven `idle.mp4`, `video.mp4` o `.wmv`/`.mov`).
Corre en loop, sin sonido, y se pausa mientras se muestra una foto.

## Operador

- Botón de engranaje (arriba a la derecha): elegir cámara, galería, cerrar la app.
- Galería: ver, reimprimir y borrar fotos.
- Log: `kcmundial.log` junto al `.exe` (cámara, formatos elegidos, tiempos de captura, impresión).

## Estructura

- **KCMundial.App** – WPF (vistas, view models, impresión, configuración)
- **KCMundial.Camera** – Cámara con MediaCapture (preview liviano + foto a resolución máxima); DirectShow de respaldo
- **KCMundial.Processing** – Composición con SkiaSharp y exportación de los cuatro archivos
- **KCMundial.Vision** – Detección de caras (YuNet ONNX + Haar) para las indicaciones de posición
- **KCMundial.Storage** / **KCMundial.Core** – Rutas, nombres, metadata, interfaces
- **KCMundial.ShareServer** – Servidor HTTP local para el QR sin internet
