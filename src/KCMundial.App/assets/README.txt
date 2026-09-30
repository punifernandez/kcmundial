Place the following files in this folder (or in the "assets" folder next to KCMundial.App.exe at runtime):

1. back_300.png  - Background template for 300 DPI figurita (591 x 827 pixels)
2. back_600.png  - Background template for HD figurita (1182 x 1654 pixels)

Note: haarcascade_frontalface_default.xml is already included in the project.

Without these files:
- The app will start but face detection will not work (no positioning guide) if haarcascade is missing.
- Figurita composition will skip the background if back_300/back_600 are missing.
