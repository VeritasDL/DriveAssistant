# Drive Assistant

Drive Assistant is a Windows recovery and inspection toolkit for console drive images. It focuses on practical read-only browsing, metadata recovery, file carving, and safe export workflows for Xbox, PlayStation, and common filesystem images.

## Features

- Open raw `.img` images and HDD Raw Copy `.imgc` compressed images.
- Browse mounted Xbox 360 FATX, original Xbox FATX, Xbox One/Series GPT/NTFS, XBFS, FAT32, exFAT, NTFS, and supported PlayStation partitions.
- Open managed PS3 Cell HDD images and PS4/PS4 Pro/devkit Orbis HDD images. PS3 supports plaintext, phat ATA-CBC-swapped, slim ATA-XTS-swapped, dev_hdd0 UFS2, dev_hdd1 FAT, and VFLASH FAT partitions; PS4/Pro supports partition-relative XTS sectors and GPT-entry IV offsets.
- Open Nintendo Switch raw NAND/eMMC images, parse the GPT/BIS partition layout, and mount decryptable FAT32 BIS partitions such as SAFE when a biskeydump/prod.keys-style key file is supplied.
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

The portable release package is self-contained and includes the .NET runtime.

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
- Xbox 360 XEX carving recognizes known executable magic variants: `XEX0`, `XEX?`, `XEX-`, `XEX%`, `XEX1`, and `XEX2`.
- PS4 package carving recognizes `CNT` package headers as `PKG`, and classifies observed type-`1` debug packages as `DPKG`.
- Nintendo Switch carving recognizes plaintext/decrypted `NCA2`/`NCA3` content archive headers, `NSP`/`PFS0` packages, `XCI` game-card images, `NRO`, `NSO`, and generic `ELF` executables. Raw encrypted NCA headers remain encrypted by design; run carving against a decrypted/mounted BIS partition when possible.

## Nintendo Switch Key Notes

- Switch NAND/eMMC images can be opened without keys for GPT partition discovery, but encrypted BIS partitions need a text key file to mount.
- Accepted formats are biskeydump-style lines such as `BIS KEY 1 (crypt): 0123...` / `BIS KEY 1 (tweak): 4567...`, or prod.keys-style names such as `bis_key_01_crypt = 0123...`.
- Each crypt/tweak value must be exactly 32 hexadecimal characters.
- A complete biskeydump-style file can be as small as:

```text
BIS KEY 0 (crypt): 00112233445566778899AABBCCDDEEFF
BIS KEY 0 (tweak): 00112233445566778899AABBCCDDEEFF
BIS KEY 1 (crypt): 00112233445566778899AABBCCDDEEFF
BIS KEY 1 (tweak): 00112233445566778899AABBCCDDEEFF
BIS KEY 2 (crypt): 00112233445566778899AABBCCDDEEFF
BIS KEY 2 (tweak): 00112233445566778899AABBCCDDEEFF
BIS KEY 3 (crypt): 00112233445566778899AABBCCDDEEFF
BIS KEY 3 (tweak): 00112233445566778899AABBCCDDEEFF
```

- The same file can use prod.keys-style names:

```text
bis_key_00_crypt = 00112233445566778899AABBCCDDEEFF
bis_key_00_tweak = 00112233445566778899AABBCCDDEEFF
bis_key_01_crypt = 00112233445566778899AABBCCDDEEFF
bis_key_01_tweak = 00112233445566778899AABBCCDDEEFF
bis_key_02_crypt = 00112233445566778899AABBCCDDEEFF
bis_key_02_tweak = 00112233445566778899AABBCCDDEEFF
bis_key_03_crypt = 00112233445566778899AABBCCDDEEFF
bis_key_03_tweak = 00112233445566778899AABBCCDDEEFF
```

- Required key pairs by partition:
  - `BIS KEY 0 (crypt)` and `BIS KEY 0 (tweak)` for `PRODINFO` / `PRODINFOF`.
  - `BIS KEY 1 (crypt)` and `BIS KEY 1 (tweak)` for `SAFE`.
  - `BIS KEY 2 (crypt)` and `BIS KEY 2 (tweak)` for `SYSTEM`.
  - `BIS KEY 3 (crypt)` and `BIS KEY 3 (tweak)` for `USER`.
- If a FAT32 BIS partition does not mount, re-check the matching crypt/tweak pair against the source photo or dump. A single transcribed character error is enough to produce random decrypted data instead of a FAT32 boot sector.

## Roadmap / TODO

- Validate PS4 raw-image and ZIP-backed workflows against representative real images with known deleted-file ground truth.
- Continue PS4 deleted-file validation with images that contain known nonzero deleted UFS file data. The current managed PS4 reader loads the tested devkit raw image, exports active files, decrypts partitions, and finds name-only deleted dirent candidates, but that image does not prove full deleted-file data recovery.
- Research PS5 support by adding `ssd0.*` partition discovery, validating tEXFAT/exFAT and UFS partition readers, and identifying what PS5 `bfs` user storage requires.
- Add fuller inner-filesystem mounting for decrypted or plaintext XVD/XVC contents where technically practical.
- Broaden automated tests around managed PS4 deleted-inode export paths using fixtures with known recoverable deleted content.

## Sources / References

- [FATXTools](https://github.com/aerosoul94/FATXTools/) for the initial codebase.
- [PS-HDD-Tools](https://github.com/aerosoul94/PS-HDD-Tools) for PlayStation helper tooling lineage and storage-behavior reference material.
- [DiscUtils.Ntfs 0.16.13](https://www.nuget.org/packages/DiscUtils.Ntfs) for NTFS parsing used by the Xbox GPT/NTFS reader.
- [UEFI Specification, GUID Partition Table layout](https://uefi.org/specs/UEFI/2.10/) for GPT structure and partition-table parsing behavior.
- [Microsoft exFAT file system specification](https://learn.microsoft.com/windows/win32/fileio/exfat-specification) for exFAT layout reference.
- [FreeBSD UFS dinode definitions](https://github.com/freebsd/freebsd-src/blob/main/sys/ufs/ufs/dinode.h), [directory entry definitions](https://github.com/freebsd/freebsd-src/blob/main/sys/ufs/ufs/dir.h), and [FFS superblock definitions](https://github.com/freebsd/freebsd-src/blob/main/sys/ufs/ffs/fs.h) for UFS2 metadata parsing and recovery heuristics.
- [PSDevWiki PS4 PKG files](https://www.psdevwiki.com/ps4/PKG_files), [PS4 partitions](https://www.psdevwiki.com/ps4/Partitions), [PS5 partitions](https://www.psdevwiki.com/ps5/Partitions), [PS5 filesystem](https://www.psdevwiki.com/ps5/Filesystem), and [PS5 kernel](https://www.psdevwiki.com/ps5/Kernel) for PlayStation package and storage-layout references.
- [.NET application publishing documentation](https://learn.microsoft.com/dotnet/core/deploying/) for the self-contained Windows release package workflow.

## Documentation

- [User Guide](docs/USER_GUIDE.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)

## License

This project is licensed under the terms in [LICENSE](LICENSE).
