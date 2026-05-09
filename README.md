# Drive Assistant

Drive Assistant is a Windows disk recovery and storage inspection tool for HDD, SSD, flash, and console drive images. It is designed for read-only analysis: open an image, inspect the partition layout, browse recoverable files, run metadata scans, carve known file formats, and export data without modifying the source.

The project has a general recovery-tool foundation with a strong focus on console storage formats. The long-term goal is support for most Nintendo console drives, all PlayStation and Xbox console HDDs/SSDs where technically practical, and common PC filesystems.

## Current Capabilities

- Open raw disk images such as `.img`, `.bin`, and `.raw`.
- Open HDD Raw Copy `.imgc` compressed images.
- Browse and export from supported FATX, FAT32, exFAT, NTFS, UFS2, and console-specific partition layouts.
- Scan filesystem metadata for deleted, orphaned, or recoverable entries.
- Carve known file signatures from raw disks and partitions with configurable scan profiles.
- Inspect offsets, extents, clusters, timestamps, attributes, and recovery status.
- Save and reload analysis databases for longer recovery sessions.
- Keep recovery operations read-only against the source image.

## Supported Storage

| Family | Current support |
| --- | --- |
| General HDD/SSD images | Raw image loading, partition discovery, FAT32, exFAT, NTFS, export, metadata scanning, file carving. |
| Original Xbox | FATX partition browsing, metadata recovery, export, file carving. |
| Xbox 360 | FATX partition browsing, metadata recovery, export, XEX carving, file carving. |
| Xbox One / Xbox Series | GPT/NTFS browsing, XVD/XVC detection and classification, nested readable filesystem probing where possible. |
| PlayStation 3 | Managed Cell HDD reader for plaintext, phat ATA-CBC-swapped, slim ATA-XTS-swapped, `dev_hdd0` UFS2, `dev_hdd1` FAT, and VFLASH FAT partitions. |
| PlayStation 4 / PS4 Pro / devkit | Managed Orbis HDD image support with partition-relative XTS sectors, GPT-entry IV offsets, UFS-oriented browsing and recovery paths, PS4 package carving. |
| PlayStation 2 | APA partition table detection for PS2 HDD images, PFS/HDLoader partition classification, raw partition export, and partition-scoped carving. |
| Nintendo Wii / GameCube | Raw disc image detection, WBFS container detection, raw export, and Wii/GameCube/WBFS carving signatures. |
| Nintendo Wii U | WFS/dev storage candidate detection, raw export, WFS/FST carving signatures, and key-gated recovery planning for OTP/SEEPROM-backed storage. |
| Nintendo Switch | Raw NAND/eMMC GPT discovery, BIS partition identification, managed AES-XTS mounting for decryptable FAT32 BIS partitions, Switch package/content carving. |

## Recovery Workflows

Drive Assistant is built around practical recovery sessions rather than one-shot extraction.

- **Browse:** mount supported partitions and inspect directory trees and file tables.
- **Export:** copy files or folders out of an image with progress, cancellation, and disk-space preflight checks.
- **Metadata scan:** search filesystem structures for deleted or orphaned records.
- **File carving:** scan raw regions for known signatures when metadata is missing or damaged.
- **Nested probing:** detect readable filesystems inside supported container formats such as Xbox XVD/XVC.
- **Analysis state:** save and reopen analysis databases to continue work later.

## File Carving

Prefer scanning the specific partition you care about instead of the whole disk whenever possible.

Scan profiles:

- `Fast` skips custom signatures and nested XVD/XVC filesystem probes.
- `Balanced` uses custom signatures and bounded nested XVD/XVC probes.
- `Exhaustive` uses custom signatures and deeper nested XVD/XVC probes.

Intervals control scan step size:

- `Sector` (`0x200`) is the default balance.
- `Align` (`0x10`) and `Byte` (`0x1`) are more exhaustive but much slower.
- `Page` (`0x1000`) is faster for formats commonly aligned to pages, such as many Xbox One/Series containers.

Format-specific carving includes:

- Xbox 360 XEX variants: `XEX0`, `XEX?`, `XEX-`, `XEX%`, `XEX1`, and `XEX2`.
- PS4 packages: `CNT` package headers as `PKG`, including observed type-`1` debug packages as `DPKG`.
- PS2 storage: APA HDD partition headers.
- Nintendo Wii / Wii U: Wii/GameCube disc images, WBFS containers, Wii U WFS markers, and Wii U FST markers.
- Nintendo Switch: plaintext/decrypted `NCA2`/`NCA3`, `NSP`/`PFS0`, `XCI`, `NRO`, `NSO`, and generic `ELF`.
- Custom signatures from `custom_carvers.json`.

Raw encrypted content is not magically decrypted by carving. For encrypted console storage, mount or decrypt the relevant partition first when keys are available.

## Wii U WFS Keys

Wii U WFS storage is console-keyed. Current builds identify WFS/dev HDD candidates, accept local key material, derive the USB key from OTP plus SEEPROM, and validate/decrypt the WFS device header when the matching files are supplied. Full directory browsing still depends on valid key material from the console that formatted the drive.

Accepted key paths:

- A folder containing `otp.bin` and `seeprom.bin`.
- `otp.bin` directly, with `seeprom.bin` in the same folder when opening USB/dev HDD images.

`otp.bin` is normally 1024 bytes. `seeprom.bin` is normally 512 bytes. Do not publish real console OTP or SEEPROM dumps; keep them local, mirroring the Switch key-file approach.

Some preserved Wii U devkit HDD dumps are ZIP64 local-header archives without a normal ZIP central directory. Drive Assistant will not silently expand a hundreds-of-GB raw image unless the temp drive has enough free space; if it cannot extract the raw `.img`, it reports the archive as a ZIP-wrapped candidate instead of pretending the compressed wrapper is browseable WFS data.

## Nintendo Switch Keys

Switch NAND/eMMC images can be opened without keys for GPT partition discovery. Encrypted BIS partitions need a text key file to mount.

Public placeholder examples are available in [`docs/example-key-files`](docs/example-key-files). Replace every placeholder with keys dumped from the same console that produced the NAND/eMMC image. Do not publish real console keys.

Accepted forms:

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

or prod.keys-style names:

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

Partition mapping:

- BIS key 0: `PRODINFO` / `PRODINFOF`
- BIS key 1: `SAFE`
- BIS key 2: `SYSTEM`
- BIS key 3: `USER`

Each value must be exactly 32 hexadecimal characters. If a FAT32 BIS partition does not mount, re-check the matching crypt/tweak pair; one wrong character is enough to produce random decrypted data instead of a valid boot sector.

## Roadmap

Drive Assistant is moving toward a broader public recovery tool with deep console support.

Planned direction:

- Stronger general HDD/SSD workflows: more partition layouts, damaged filesystem handling, richer export validation, and better reporting.
- Nintendo storage support across DS, 3DS, Wii, Wii U, and Switch.
- PlayStation storage support across PS1/PS2 memory/storage media where applicable, PS3, PS4, PS5, and future variants where technically practical.
- Xbox storage support across original Xbox, Xbox 360, Xbox One, Xbox Series, and future variants where technically practical.
- More file-carving signatures for console packages, executables, save data, media, and common user files.
- Better public documentation, fixtures, and reproducible validation images.

## Download

Download the latest Windows x64 ZIP from [GitHub Releases](https://github.com/rain0x06/DriveAssistant/releases/latest), extract it, and run `Drive Assistant.exe`.

The portable release package is self-contained and includes the .NET runtime.

## Requirements

- Windows 10/11.
- .NET 8 SDK for building from source.
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

## Repository Layout

| Path | Purpose |
| --- | --- |
| `DriveAssistant.Wpf` | Active WPF desktop application, published as `Drive Assistant.exe`. |
| `DriveAssistant.Wpf.Tests` | Tests for image readers and WPF-supporting scanners. |
| `DriveAssistant.Shared` | Shared support files used by the WPF app. |
| `FATX` | Core FATX reader, metadata scanner, and legacy FATX signature carver library. |
| `FATX.Tests` | Tests for the core FATX library. |
| `docs` | User, troubleshooting, and example-key documentation. |

## Documentation

- [User Guide](docs/USER_GUIDE.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)
- [Example Key Files](docs/example-key-files)

## Sources And References

- [FATXTools](https://github.com/aerosoul94/FATXTools/) for the initial FATX-focused codebase.
- [PS-HDD-Tools](https://github.com/aerosoul94/PS-HDD-Tools) for PlayStation helper tooling lineage and storage-behavior reference material.
- [DiscUtils.Ntfs 0.16.13](https://www.nuget.org/packages/DiscUtils.Ntfs) for NTFS parsing used by the Xbox GPT/NTFS reader.
- [UEFI Specification, GUID Partition Table layout](https://uefi.org/specs/UEFI/2.10/) for GPT structure and partition-table parsing behavior.
- [Microsoft exFAT file system specification](https://learn.microsoft.com/windows/win32/fileio/exfat-specification) for exFAT layout reference.
- [FreeBSD UFS dinode definitions](https://github.com/freebsd/freebsd-src/blob/main/sys/ufs/ufs/dinode.h), [directory entry definitions](https://github.com/freebsd/freebsd-src/blob/main/sys/ufs/ufs/dir.h), and [FFS superblock definitions](https://github.com/freebsd/freebsd-src/blob/main/sys/ufs/ffs/fs.h) for UFS2 metadata parsing and recovery heuristics.
- [PSDevWiki PS4 PKG files](https://www.psdevwiki.com/ps4/PKG_files), [PS4 partitions](https://www.psdevwiki.com/ps4/Partitions), [PS5 partitions](https://www.psdevwiki.com/ps5/Partitions), [PS5 filesystem](https://www.psdevwiki.com/ps5/Filesystem), and [PS5 kernel](https://www.psdevwiki.com/ps5/Kernel) for PlayStation package and storage-layout references.
- [.NET application publishing documentation](https://learn.microsoft.com/dotnet/core/deploying/) for the self-contained Windows release package workflow.

## License

This project is licensed under the terms in [LICENSE](LICENSE).
