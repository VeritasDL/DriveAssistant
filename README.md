# Drive Assistant

Drive Assistant is a Windows recovery and inspection toolkit for console drive images. It focuses on practical read-only browsing, metadata recovery, file carving, and safe export workflows for Xbox, PlayStation, and common filesystem images.

The codebase started as FATXTools, but the active application and published project name is now **Drive Assistant**.

## Features

- Open raw `.img` images and HDD Raw Copy `.imgc` compressed images.
- Browse mounted Xbox 360 FATX, original Xbox FATX, Xbox One/Series GPT/NTFS, XBFS, FAT32, exFAT, NTFS, and supported PlayStation partitions.
- Export files and folders without blocking the UI, with progress, cancellation, and disk-space preflight checks before large saves.
- Scan filesystem metadata and show recovered/deleted entries in the main file table, with cancellation for long-running metadata scans.
- Carve known file types from raw partitions with configurable scan intervals, fast/balanced/exhaustive scan profiles, cancellation, and custom signatures.
- Detect Xbox One/Series XVD/XVC containers, classify known XVD header types, and surface plaintext manifest display names when available.
- Probe readable filesystems found inside XVD/XVC containers as separate nested scan rows so outer NTFS results stay distinct from inner container findings.
- Show XVD classification and display-name details beside `.xvd` filesystem rows and in carved-file details.
- Inspect offsets, extents, clusters, timestamps, attributes, and recovery status.
- Show row-color legends for fragmentation, overwritten/unrecoverable, sparse/resident, deleted, and orphan-inode states.
- Save and reload analysis databases.

## Download

The easiest way to run Drive Assistant is to download the latest Windows x64 ZIP from [GitHub Releases](https://github.com/rain0x06/DriveAssistant/releases/latest), extract it, and run `Drive Assistant.exe`.

The portable release package is self-contained and includes the .NET runtime plus the bundled native PlayStation helper binaries under `tools\ps-hdd`.

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

Release packages are built by `.github/workflows/release.yaml` for tags like `v0.3.54`. The workflow runs tests, publishes a self-contained Windows x64 ZIP, emits a SHA256 checksum, and creates or updates the GitHub release. If `WINDOWS_CODESIGN_PFX_BASE64` and `WINDOWS_CODESIGN_PFX_PASSWORD` repository secrets are configured, the workflow signs the Windows binaries before packaging.

## File Carving Notes

- Prefer scanning the specific partition you care about rather than the whole disk.
- File carving speed depends on both the scan profile and the configured interval. Profiles do not silently change the interval you selected:
  - `Fast` skips custom signatures and nested XVD/XVC filesystem probes.
  - `Balanced` uses custom signatures and bounded nested XVD/XVC probes.
  - `Exhaustive` uses custom signatures and deeper nested XVD/XVC probes.
- Intervals still control scan step size:
  - `Sector` (`0x200`) is the default balance.
  - `Align` (`0x10`) and `Byte` (`0x1`) are more exhaustive but much slower.
  - `Page` (`0x1000`) is faster for formats known to be page-aligned, such as many Xbox One/Series containers.
- Built-in text carving is intentionally disabled to avoid noisy result sets and long scans.
- Custom carving signatures can be loaded from `custom_carvers.json`; keep custom patterns specific.
- Nested XVD/XVC probing detects readable inner filesystem headers. It does not decrypt encrypted XVD content.

## Roadmap / TODO

- Validate PS4 raw-image and ZIP-backed workflows against representative real images with known deleted-file ground truth.
- Improve PlayStation bridge packaging so native helper binaries are easier to rebuild from source.
- Add fuller inner-filesystem mounting for decrypted or plaintext XVD/XVC contents where technically practical.
- Broaden integration tests around native PS4 deleted-inode export paths.

## Documentation

- [User Guide](docs/USER_GUIDE.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)

## License

This project is licensed under the terms in [LICENSE](LICENSE).
