# WebLabelMaker

A label designer that runs entirely in the browser and prints straight to Brother
P-touch tape printers over Bluetooth or a serial (COM) port. No install, no account,
no server: documents live in the browser and export as plain JSON.

Built with Blazor WebAssembly (.NET 10) and served as static files from a Cloudflare Worker.

## Features

- **Objects**: text edited in place on the label (auto or fixed width, with line breaks or fit-to-box), 23 shapes,
  dithered images, QR / Data Matrix / Aztec / PDF417 and eight linear barcodes. Pick a tool, then drag on the label.
- **Variables and macros**: `{name}` variables, a `{seq:0000}` counter that advances per printed copy, `{date}`,
  maths and formats inside the braces, and numeric fields bound to expressions such as `Title.right + 2`.
- **2D code content**: forms for URL, Wi-Fi, vCard, email, phone, SMS and location payloads.
- **Cut marks**: full and half cuts placed along the label; the print preview shows each piece.
- **Cabling layouts**: cable wrap (content repeated round the cable), flags (two faces around a fold, the second
  mirrored or designed separately) and numbered port strips with measured spacing and a printable mm ruler.
- **Tape colours**: pick the tape and print colour to preview the real result.
- **Layers**: a tree with nested groups, drag-and-drop ordering, rename, hide, lock.
- **Layout**: multi-selection, six-way align, distribute, magnetic snapping to objects,
  margins and label edges (each switchable), toggleable margin guides, rulers.
- **Fonts**: a curated set of monospace and small-size-friendly families, plus any
  Google Fonts family by name.
- **What you see is what prints**: images are dithered and barcode modules are snapped
  at the printer's resolution; the print dialog shows the exact 1-bit raster.
- **Storage**: continuous autosave, a named local library, built-in and user templates,
  JSON import/export of a single label, a template, or everything as one archive.
- **Themes**: light, dark, or follow the system.

## Printing

Printing uses the [Web Serial API](https://developer.mozilla.org/docs/Web/API/Web_Serial_API),
so it needs Chrome, Edge or Opera on desktop.

1. Pair the printer with the computer over Bluetooth (it then appears as a serial port).
2. Click **Print**, then **Connect…**, and pick the printer's port.
3. The app reads the loaded tape width and refuses to print on the wrong tape.

| Model | Status |
|---|---|
| PT-E560BT / PT-E510 | Implemented from Brother's PT-E310BT/E510/E560BT raster reference; matches a driver reported working on the E560BT |
| PT-P710BT | Implemented from Brother's raster command reference; cross-checked with open-source drivers |
| PT-P300BT | Implemented from community reverse-engineering (Brother publishes no reference) |
| PT-P910BT | Implemented from Brother's PT-P900 reference; vertical alignment may need the offset setting |
| Other 128-pin, 180 dpi P-touch | Generic profile |

None of these has been tested on hardware yet. The driver lives in
`src/LabelMaker/Services/Brother.cs`; other vendors can be added beside it.

## Develop

```
dotnet run --project src/LabelMaker
```

## Deploy (Cloudflare Workers)

The repository is set up for Cloudflare's Git integration (Workers Builds):

- Build command: `bash build.sh` (installs the .NET SDK, publishes to `dist/wwwroot`)
- Deploy command: `npx wrangler deploy`

`wrangler.jsonc` serves `dist/wwwroot` as static assets with single-page-app fallback.

## Layout

```
src/LabelMaker/
  Models/        document model (nodes, geometry, media)
  Services/      editor engine, library, barcode encoding, Brother driver
  Components/    Blazor UI
  wwwroot/js/    canvas renderer, IndexedDB, fonts, Web Serial
```
