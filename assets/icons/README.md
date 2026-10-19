# Application icons

The application artwork is supplied as eight transparent PNGs: 16, 20, 24, 32, 48, 64, 128 and 256 pixels square. Keep these exact-size frames when updating the artwork.

`RockwellMCPServer.ico` packages those unchanged PNG bytes for Windows. The desktop executable embeds the ICO as its executable icon and as a managed resource used by the application windows. The Start Menu shortcut uses this ICO directly. The root README uses the 128-pixel PNG.

Regenerate the checked-in ICO after updating the PNGs:

```powershell
.\scripts\New-AppIcon.ps1
```

The normal `scripts/Build.ps1` also runs this step. Generation checks the expected dimensions and RGBA format, preserves the original PNG data and leaves an identical ICO untouched. No image editor or additional package is required.
