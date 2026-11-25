# JPEG Repair Shop (.NET)

A .NET port of [JPEG Repair Shop](https://anderspedersen.net/jpegrepair/), a Windows tool for repairing corrupt
JPEG files by editing the image data inside the file.

The original was written in C++ with Qt. This version is C# with WPF and [SkiaSharp](https://github.com/mono/SkiaSharp).
The port was made with [Claude Code](https://claude.com/claude-code) and contains a number of bug fixes and
enhancements.

![JPEG Repair Shop with a corrupt photo open: the lower half has a colour shift, and the selected MCU is shown enlarged with its DC values and the repair tools](screenshot.png)

## What it does

A JPEG picture is stored as a long stream of small blocks called MCUs (minimum coded units). When part of a file is
lost or overwritten, every block after the damage can end up in the wrong place, or with the wrong colours, even
though the blocks themselves are intact. JPEG Repair Shop shows you the blocks and puts them back:

See the **[user guide](docs/USER_GUIDE.md)** for how JPEG damage works and how to repair a picture step by step.

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

## License

Released under the [MIT License](LICENSE).
