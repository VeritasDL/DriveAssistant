# Drive Assistant

Drive Assistant is a Windows recovery and inspection toolkit for console drive images. It focuses on practical read-only browsing, metadata recovery, file carving, and safe export workflows for Xbox, PlayStation, and common filesystem images.

The codebase started as FATXTools, but the active application and published project name is now **Drive Assistant**.

## Features

- Open raw `.img` images and HDD Raw Copy `.imgc` compressed images.
- Browse mounted Xbox 360 FATX, original Xbox FATX, Xbox One/Series GPT/NTFS, XBFS, FAT32, exFAT, NTFS, and supported PlayStation partitions.
- Export files and folders without blocking the UI, with progress and cancellation for long exports.
- Scan filesystem metadata and show recovered/deleted entries in the main file table.
- Carve known file types from raw partitions with configurable scan intervals and custom signatures.
- Detect Xbox One/Series XVD containers, classify known XVD header types, and surface plaintext manifest display names when available.
- Inspect offsets, extents, clusters, timestamps, attributes, and recovery status.
- Save and reload analysis databases.

## Repository Layout

| Path | Purpose |
| --- | --- |
| `DriveAssistant.Wpf` | Active WPF desktop application, published as `Drive Assistant.exe`. |
| `DriveAssistant.Wpf.Tests` | Tests for image readers and WPF-supporting scanners. |
| `DriveAssistant.Shared` | Shared support files used by the WPF app. |
| `FATX` | Core FATX reader, metadata scanner, and legacy FATX signature carver library. |
| `FATX.Tests` | Tests for the core FATX library. |
| `docs` | User and troubleshooting documentation. |

## Requirements

- Windows 10/11.
- .NET 8 SDK.
- Visual Studio 2022 is optional but recommended for UI work.

Check your SDK:

```powershell
dotnet --info
```

## Build

From the repository root:

```powershell
dotnet restore DriveAssistant.sln
dotnet build DriveAssistant.sln -c Release
```

The WPF app build output is:

```text
DriveAssistant.Wpf\bin\Release\net8.0-windows\
```

Run the app from the build output:

```powershell
& ".\DriveAssistant.Wpf\bin\Release\net8.0-windows\Drive Assistant.exe"
```

## Test

Run the full test suite:

```powershell
dotnet test DriveAssistant.sln -c Release
```

Run only WPF support tests:

```powershell
dotnet test DriveAssistant.Wpf.Tests\DriveAssistant.Wpf.Tests.csproj -c Release
```

## Publish a Portable Build

Framework-dependent publish:

```powershell
dotnet publish DriveAssistant.Wpf\DriveAssistant.Wpf.csproj -c Release -o .\publish\DriveAssistant
```

Self-contained Windows x64 publish:

```powershell
dotnet publish DriveAssistant.Wpf\DriveAssistant.Wpf.csproj -c Release -r win-x64 --self-contained true -o .\publish\DriveAssistant-win-x64
```

## File Carving Notes

- Prefer scanning the specific partition you care about rather than the whole disk.
- File carving speed depends heavily on the configured interval:
  - `Sector` (`0x200`) is the default balance.
  - `Align` (`0x10`) and `Byte` (`0x1`) are more exhaustive but much slower.
  - `Page` (`0x1000`) is faster for formats known to be page-aligned, such as many Xbox One/Series containers.
- Built-in text carving is intentionally disabled to avoid noisy result sets and long scans.
- Custom carving signatures can be loaded from `custom_carvers.json`; keep custom patterns specific.

## Roadmap / TODO

- Add PS4 and PS5 workflows for partition discovery, filesystem browsing, and safe export where legally and technically practical.
- Improve PlayStation bridge packaging so native helper binaries are easier to rebuild from source.
- Add XVD-aware nested scanning that keeps outer NTFS results separate from filesystems found inside XVD/XVC containers.
- Show XVD classification and plaintext display names directly beside `.xvd` filesystem rows.
- Add a scan profile selector for exhaustive, balanced, and fast carving presets without silently changing the selected interval.
- Add resumable/cancelable metadata scans and file carver scans.
- Add safer disk-space estimation before large exports.
- Expand automated tests with larger synthetic Xbox GPT/NTFS and FATX carving fixtures.
- Add release packaging and signed artifact workflow.

## Documentation

- [User Guide](docs/USER_GUIDE.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)

## License

This project is licensed under the terms in [LICENSE](LICENSE).
