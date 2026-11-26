# AudioPilot Icon

The icon is original artwork created for AudioPilot. `audiopilot.svg` is the editable vector master;
`audiopilot.png` is the 1024-pixel export. `audiopilot.ico` contains transparent 16, 20, 24, 32, 40, 48, 64,
96, 128, and 256-pixel frames for Windows shell, tray, and high-DPI use. `audiopilot-small.svg` uses a heavier
speaker silhouette, compass cutout, and sound arc for the 16 and 20-pixel frames.

The initial concept used the built-in image-generation tool with this prompt:

> Create an original Windows application icon for AudioPilot, a desktop audio switching, mixer and automation app.
> Unite sound and navigation in one compact geometric mark: a compass needle integrated with an open circular
> sound contour, using purposeful negative space and a subtle P relationship. Use deep ink navy and one
> turquoise accent, flat vector aesthetics, balanced proportions, and clean silhouettes. Avoid literal speakers,
> headphones, switching arrows, equalizer bars, organic squiggles, gradients, and decorative noise.

The concept was redrawn as a clean vector to remove raster edge artifacts and provide a scalable source.
The final design replaces the abstract circular contour with a recognizable speaker silhouette. A compass
needle is inset into the speaker cone, with one sound arc outside it. The speaker remains recognizable when
the finer navigation detail is reduced at tray size.

To regenerate the PNG and multi-size ICO with ImageMagick:

```powershell
./scripts/update-app-icon.ps1
```
