# KCMundial – Kiosk Photo Booth

Production-ready kiosk-style photo booth application for live events. Captures photos, composes "figurita" cards with a template background, and serves a mobile-friendly page via QR for download and share.

## Requirements

- **Platform:** Windows 10/11  
- **Runtime:** .NET 8  
- **UI:** WPF (MVVM), fullscreen portrait  
- **Camera:** Logitech Brio 4K or similar USB webcam  

## Solution Structure

- **KCMundial.App** – WPF UI (Views, ViewModels, Converters, Services)  
- **KCMundial.Core** – Interfaces and models  
- **KCMundial.Storage** – Paths, file naming, metadata  
- **KCMundial.Camera** – Camera enumeration, preview, high-res capture (OpenCvSharp)  
- **KCMundial.Vision** – Face detection (Haar cascade), positioning validator  
- **KCMundial.Processing** – Sticker composition (SkiaSharp), export pipeline  
- **KCMundial.ShareServer** – In-process HTTP server (ASP.NET Core Minimal API) for QR page  

## Build

1. Open `KCMundial.sln` in Visual Studio 2022 (or later) or use:

   ```bash
   dotnet restore
   dotnet build
   ```

2. Run the app:

   ```bash
   dotnet run --project src\KCMundial.App\KCMundial.App.csproj
   ```

   Or run `src\KCMundial.App\bin\Debug\net8.0-windows\KCMundial.App.exe` after building.

## Assets (required for full functionality)

At runtime the app looks for an **`assets`** folder next to the executable (same folder as the .exe, or the folder referenced in `src\KCMundial.App\assets` if you copy it to output).

Place in `assets`:

| File | Description |
|------|-------------|
| **back_300.png** | Background template for 300 DPI output. Size: **591 × 827 px**. |
| **back_600.png** | Background template for HD output. Size: **1182 × 1654 px**. |
| **haarcascade_frontalface_default.xml** | OpenCV Haar cascade for face detection. ✅ **Already included in the project** - no need to download. |

- If **back_300.png** / **back_600.png** are missing, composition still runs but no background is drawn.  
- **haarcascade_frontalface_default.xml** is included by default - face detection will work out of the box.

The `assets` folder is automatically copied to the build output (configured in `KCMundial.App.csproj`). You only need to add **back_300.png** and **back_600.png** to the `src\KCMundial.App\assets\` folder - **haarcascade_frontalface_default.xml** is already included.

## NuGet Packages

| Project | Packages |
|---------|----------|
| KCMundial.Camera | OpenCvSharp4, OpenCvSharp4.runtime.win |
| KCMundial.Vision | OpenCvSharp4, OpenCvSharp4.runtime.win |
| KCMundial.Processing | SkiaSharp |
| KCMundial.ShareServer | (FrameworkReference: Microsoft.AspNetCore.App) |
| KCMundial.App | CommunityToolkit.Mvvm, QRCoder, FrameworkReference: Microsoft.AspNetCore.App, project refs to all above |

Restore with:

```bash
dotnet restore
```

## Calibrating face size (min/max width ratio)

The positioning validator uses:

- **MinFaceWidthRatio** = 0.12 (face width ≥ 12% of frame width)  
- **MaxFaceWidthRatio** = 0.22 (face width ≤ 22% of frame width)  

These are defined in **KCMundial.Vision** → `PositioningValidator.cs` (properties `MinFaceWidthRatio` and `MaxFaceWidthRatio`). Adjust them if users are told to “Acercate”/“Alejate” too often or not enough:

- **Larger min** (e.g. 0.14) → face must be closer (larger in frame).  
- **Smaller max** (e.g. 0.20) → face must be farther (smaller in frame).  

Rebuild after changing.

## Runtime folders

The app creates these next to the executable (or under the configured install root):

- **raw/** – Raw captures (JPEG).  
- **figuritas/** – Final figuritas at 591×827 (300 DPI equivalent).  
- **figuritas_hd/** – HD figuritas at 1182×1654.  
- **assets/** – Backgrounds and cascade (see above).  

**Log file:** `kcmundial.log` in the same folder as the .exe (e.g. `c:\KCMundial\publish\kcmundial.log`). The first line in the log writes the full path. Use it to see camera enumeration timing, which camera was selected, and any errors when opening the USB camera.

## Printing

Printing is out of scope in the current build; the structure is ready to add a print service later (e.g. inject into the result/gallery flow).

## License

Use as required by your project.
