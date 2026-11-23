# JPEG Repair Shop (.NET)

A .NET port of [JPEG Repair Shop](https://anderspedersen.net/jpegrepair/), a Windows tool for repairing corrupt
JPEG files by editing the image data inside the file.

The original was written in C++ with Qt. This version is C# with WPF and [SkiaSharp](https://github.com/mono/SkiaSharp).
The port was made with [Claude Code](https://claude.com/claude-code) and contains a number of bug fixes and
enhancements.

![JPEG Repair Shop with a corrupt photo open: the lower half has a colour shift, and the selected MCU is shown enlarged with its DC values and the repair tools](screenshot.png)

## What it does

A JPEG picture is stored as a long stream of small blocks called MCUs (minimum coded units). When part of a file is
lost or overwritten, every block after the damage can end up in the wrong place, or with the wrong colours. JPEG
Repair Shop shows you the blocks and lets you put them back:

- **Insert blank MCU**: shifts everything after the selected block one step right (use where data was lost).
- **Remove MCU**: shifts everything after it one step left (use on garbage blocks).
- **Fix colours**: corrects the colour/brightness shift from the selected block onwards by matching it to the row above.
- **Fix section colours**: for when the edges don't line up (garbage or misaligned rows above). Matches the colour
  histogram of the selected section (the run of good blocks around the selected one) to the good blocks just before
  it, and corrects the colours from the section's first block onwards. It asks how many rows above the section to
  compare with (2 at first; the last choice is remembered).
- **Fix all**: finds and fixes misaligned blocks and colour shifts in the whole picture, worst first. As a last step
  it checks the short stretches between damaged blocks, which a small burst of garbage often leaves with wrong colours
  or a block or two out of place: each is moved back and/or has its colours corrected, as its own undo step. This last
  step is optional ("fine-tune"): tick or untick "Continue with fine-tuning the stretches between damaged areas" in the Fix all
  window, also while it runs; if it is off, the window asks when the first part is done ("Fine-tune" or "Close"). It
  can take a long time on badly damaged pictures and may now and then make a spot worse.
- **Fill bad MCUs**: replaces bad, corrupt and blank blocks with contents made up from the picture
  around them. Filled blocks are marked in green. Best done last, after the blocks are back in place and the colours are right.
- **Adjust DC by hand**: fine-tune brightness (Y) and colour (Cb, Cr) from the selected block onwards. The buttons
  speed up when held down; Shift + mouse wheel moves 10 steps at a time.
- **Undo / redo** for every edit.
- **Borrow a header** from another picture, when the file's header (picture size, colour layout, compression
  tables) is damaged but the image data is still there. Works best with a picture from the same camera,
  taken with the same settings.
- **Save as** a new file. Everything before the image data (EXIF, colour profile, comments) is kept as it was.

### Viewing damaged files

- The decoder keeps going through damaged data instead of stopping at the first error, so as much of the picture
  as possible is shown. Blocks with invalid data are marked in red, blocks with no data at all with a red cross,
  and the problems found are listed with their position in the file.
- **Bad-MCU detection** marks blocks in orange that don't fit the picture around them: if the edges to 2 or more of
  the 4 neighbouring blocks are rugged, and at least one of them is the edge above or below, the block is probably
  garbage from damaged data or was inserted from elsewhere. Colour shifts and misalignment break only one edge per
  block, so they aren't marked (except where they start). **Fix colours** and **Fix all** leave marked blocks out
  when they work out a colour shift.
  You can override the detection for any block: mark it as bad, or as not bad (B flips it); marks stay until you
  change them. With **Show bad MCUs** on, every group of blocks that needs repair (bad, corrupt, inserted or missing)
  is outlined in orange.
- Zoom and pan freely, with an optional MCU grid. Enlarged previews show the block under the cursor and the
  selected block, together with their DC values.

### Supported files

- Baseline JPEGs, including camera and phone photos with 4:2:0, 4:2:2 or 4:4:4 colour sampling, and grayscale.
- Files with **restart markers**, including files where some markers are damaged or missing.
- Files with a **damaged header**, by borrowing the header of another picture.

Progressive JPEGs are not supported.

## Requirements

- Windows
- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Build and run

```
dotnet build JpegRepairShop.slnx
dotnet run --project JpegRepairShop
dotnet test
```

You can also pass a file to open: `JpegRepairShop.exe broken.jpg`.

A performance log pops up after **Fix all** (timings of the searches, the fine-tuning step, the slowest
stretches). It is on in every build by default; for a release, turn it off with `-p:EnablePerfLog=false`
(for example `dotnet publish JpegRepairShop -c Release -p:EnablePerfLog=false`) or set `EnablePerfLog` to
`false` in `Directory.Build.props`.

## Project layout

| Folder | Contents |
|---|---|
| `JpegRepair.Core` | JPEG parsing, decoding, rendering, re-encoding, the edit model with undo, header borrowing. No UI dependencies. |
| `JpegRepair.Core.Tests` | xUnit tests, including damaged-file scenarios. |
| `JpegRepairShop` | The WPF application. |

## Tips

- Always keep the original file. Saving writes a clean, re-encoded file, and repairs that need the original
  damaged data can't be redone from the saved copy.
- Repair the original file from the camera or memory card. A picture that was re-compressed or re-uploaded
  after it was damaged usually can't be repaired.

## License

Released under the [MIT License](LICENSE).
