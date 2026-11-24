# TZe cassette identification

How a P-touch printer knows which tape is loaded, what it reports to WebLabelMaker,
and how to work out the hole pattern for a given colour so a third-party cassette can
be made to report correctly.

**Status of this document: the hole-to-colour table is not filled in.** No published
source documents it (see [Sources](#sources)), and inventing one would send you
printing shims that do not work. What follows is what is established, the codes the
printer reports, and a procedure to map the patterns on your own printer. The
worksheet at the end is where the real table goes once measured.

## What is established

- **The cassette is identified mechanically.** The printer has spring-loaded sense
  pins (switches). Each pin either drops into a hole in the cassette or is pushed in
  by plastic where there is no hole. The printer reads the combination.
- **There are several groups of holes on a TZe cassette:**
  - a group on the **bottom, in a corner**: the original TZ scheme, kept so TZe
    cassettes work in older TZ printers;
  - a group on the **bottom, in the middle**: added with TZe;
  - holes on the **side**, entered by pins that the cover lever pushes out when the
    lid closes.
- **Printers do not all read every hole.** A small printer (PT-1090, PT-D210) has
  fewer pins than a wide one (PT-P700 has more, for 24 mm tape). A cassette can carry
  information a given printer never looks at.
- **On an older three-switch printer (PT-H100 service manual), a closed hole means
  switch ON**, and the three switches together give tape width and cassette type
  (laminated, non-laminated, fabric). That manual says nothing about colour. Treat
  the polarity as likely but unconfirmed for the E560BT.
- **Which group carries colour is not documented.** The older corner group predates
  colour reporting, so the middle and side groups are the likely candidates. This is
  an inference, not a measurement.

## What the printer reports

After a status request the printer answers with 32 bytes. WebLabelMaker reads:

| Byte | Meaning |
|---|---|
| 10 | Tape width in mm (`0` = no cassette, `4` = 3.5 mm) |
| 11 | Tape type |
| 24 | Tape colour code |
| 25 | Text colour code |

The printer panel (top right) shows bytes 24 and 25 as hex, which is what you need
while testing.

### Tape colour codes (byte 24)

| Code | Tape | Code | Tape |
|---|---|---|---|
| `01` | White | `30` | Blue (D) |
| `02` | Other | `31` | Red (D) |
| `03` | Clear | `40` | Fluorescent orange |
| `04` | Red | `41` | Fluorescent yellow |
| `05` | Blue | `50` | Berry pink (S) |
| `06` | Yellow | `51` | Light gray (S) |
| `07` | Green | `52` | Lime green (S) |
| `08` | Black | `60` | Yellow (fabric) |
| `09` | Clear (white text) | `61` | Pink (fabric) |
| `20` | Matte white | `62` | Blue (fabric) |
| `21` | Matte clear | `70` | White heat-shrink tube |
| `22` | Matte silver | `90` | White flexible ID |
| `23` | Satin gold | `91` | Yellow flexible ID |
| `24` | Satin silver | `F0` | Cleaning |
| | | `F1` | Stencil |
| | | `FF` | Incompatible |

### Text colour codes (byte 25)

| Code | Text | Code | Text |
|---|---|---|---|
| `01` | White | `0A` | Gold |
| `02` | Other | `62` | Blue (fabric) |
| `04` | Red | `F0` | Cleaning |
| `05` | Blue | `F1` | Stencil |
| `08` | Black | `FF` | Incompatible |

These tables were written from memory of Brother's raster command reference for the
PT-P700 / E550W / P710BT families. Your PT-E560BT does report colour, so the bytes
are live, but **check the codes against cassettes you own** before trusting a row:
load a genuine cassette, read the two bytes in the printer panel, and compare.

A no-brand cassette that always reads `01` / `08` (white tape, black text) has the
hole pattern of a white/black cassette, whatever tape is inside.

## Mapping the hole pattern yourself

You need the printer, WebLabelMaker connected to it, a few **genuine** cassettes of
different colours in the **same width**, and opaque adhesive tape (Kapton or
electrical tape) to block holes.

1. **Number the holes.** Photograph the underside and side of one cassette. Mark
   every position where any of your cassettes has a hole, including positions that
   are solid plastic on this one. Number them and keep the photo as the key.
2. **Record each genuine cassette.** For each one, write down which numbered
   positions are open, then load it and note bytes 10, 24 and 25 from the printer
   panel (press Refresh). One row per cassette in the worksheet below.
3. **Find the colour positions by difference.** Two cassettes of the same width and
   type that differ only in colour differ only in colour holes. Positions that never
   change across your set are width, type or unused.
4. **Confirm by blocking.** Take one cassette, cover one open hole with tape, reload,
   Refresh, and note the bytes. Uncover it and repeat for the next hole. A hole whose
   blocking changes byte 24 belongs to tape colour; byte 25, text colour; byte 10,
   width. A hole that changes nothing is not read by this printer.
5. **Check the polarity.** Blocking a hole can only take you from "open" to
   "closed". To go the other way you would have to drill, so confirm the pattern for
   a target colour by blocking holes on a cassette that has more holes open than the
   target, not fewer.
6. **Design the shim from the measured table.** A shim can close holes (fill them);
   it cannot open ones that are not there. If the no-brand cassette has fewer open
   holes than the colour you want needs, the shim alone will not do it and the
   cassette shell must be drilled at the right positions instead.

Precautions:

- Do not force the lid. If a blocked hole stops the cassette seating, the tape is
  too thick; use thinner tape.
- Width and type holes also set the print energy and the pins used. Leave them
  exactly as on a genuine cassette of the tape actually inside, or prints will be
  misplaced or too light or dark.
- A pattern the printer does not recognise reports `FF` (incompatible) and the
  printer may refuse to print.

## Worksheet

Hole positions as numbered in your photo. `O` = open hole, `X` = solid.

| Cassette (part no.) | Width | Tape / text | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | Byte 10 | Byte 24 | Byte 25 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| | | | | | | | | | | | | | | | |
| | | | | | | | | | | | | | | | |
| | | | | | | | | | | | | | | | |
| | | | | | | | | | | | | | | | |

Blocking test, one cassette:

| Hole blocked | Byte 10 | Byte 24 | Byte 25 | Conclusion |
|---|---|---|---|---|
| none | | | | baseline |
| 1 | | | | |
| 2 | | | | |
| 3 | | | | |

Once a few rows are filled in, the colour table can be written here properly and the
app's colour names checked against it.

## Sources

- [TZe carts have side pin holes](https://ptouchman.blogspot.com/2017/02/tze-carts-have-side-pin-holes-on.html):
  hole groups and their positions; no encoding table.
- [Cheapnified Brother TZ label cartridge](https://softsolder.com/2020/04/19/cheapnified-brother-tz-label-cartridge/):
  sense pins on a PT-1090; a commenter says they also carry width and print energy; no table.
- [Brother PT-H100 service manual, "Identification of tape cassette type"](https://www.manualslib.com/manual/1307233/Brother-Pt-H100.html?page=23):
  three switches, closed hole = switch on, width and cassette type only.
- A Hackaday project on the PT-P300BT cassette holes exists but could not be read
  when this was written; it is not reflected here.
