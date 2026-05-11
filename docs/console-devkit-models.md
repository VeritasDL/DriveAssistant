# Console Devkit Model And Media Support

This file maps publicly documented console devkit names to the storage/image formats Drive Assistant can inspect. Devkits often do not define a new filesystem: they usually emit the same HDD, NAND, optical-disc, ROM, memory-card, or save-media images as retail hardware, with different signing/debug metadata.

## Sony PlayStation

| Platform | Public devkit/test names | Storage impact in Drive Assistant |
| --- | --- | --- |
| PlayStation / PS1 | DTL-H development/debug units, Net Yaroze | PS1 memory-card images (`.mcr`, `.mcd`, `.psx`) expose active and deleted directory save-block rows for export plus whole-card raw export/carving. PS1-style ISO9660 data tracks mount from plain `.iso` and BIN/CUE MODE1/MODE2 images, including MODE2/2352 images similar to psximager's target format; metadata scan also reports unreferenced ISO9660 directory-record candidates from devkit/data-track dumps. XA Form 2 sector extraction is still limited. |
| PlayStation 2 | DTL-T10000/DTL-T15000 TOOL, DTL-H TEST/debug stations, PS2 Linux kit media | PS2 APA/PFS HDDs are mounted and scanned. PS2 memory-card images are recognized as legacy raw media (`.ps2`) for export/carving. |
| PlayStation 3 | DECHA/DECH debug stations, DECR reference tools | Managed PS3 HDD reader covers plaintext, ATA-CBC-swapped, ATA-XTS-swapped, UFS2 `dev_hdd0`, FAT `dev_hdd1`, and VFLASH FAT partitions when appropriate keys/images are available. |
| PlayStation 4 | DUH-D development kits, DUH-T testing kits | Managed PS4/Orbis HDD support covers GPT, encrypted partition readers, UFS-oriented browsing/recovery, and PS4 package carving. |
| PlayStation 5 | DFI-D development kits, DFI-T testing kits | PS5 GPT/storage layouts are documented as related work; encrypted PS5 filesystem validation still needs lawful key material and fixtures. |

## Microsoft Xbox

| Platform | Public devkit/test names | Storage impact in Drive Assistant |
| --- | --- | --- |
| Original Xbox | XDK development/debug kits, DVT/DVT4-era hardware | FATX partitions, boot filesystem entries, XBE carving, and devkit boot files such as `devkit.ini` are already handled. |
| Xbox 360 | XDK/debug kits, sidecar-era dev units, stress kits | FATX partitions, XEX carving variants, STFS packages, and recovery scans are supported. |
| Xbox One / Xbox Series | Durango/Scarlett XDK hardware, current Xbox Development Kit hardware | GPT/NTFS browsing plus XVD/XVC detection, manifest extraction, package classification, and bounded nested filesystem probing are supported where images are readable. |

## Nintendo

| Platform | Public devkit/test names | Storage impact in Drive Assistant |
| --- | --- | --- |
| Nintendo 64 | Partner-N64, IS-Viewer 64, SN64, Monegi Smart Pack | N64 ROM images (`.z64`, `.n64`, `.v64`, `.rom`) expose normalized header metadata and header/raw exports across the common byte orders handled by N64 dump converters. Common save-media extensions (`.sra`, `.eep`, `.fla`, `.mpk`) are classified for raw export/carving. |
| Game Boy / Game Boy Color / Game Boy Advance | IS-CGB, IS-AGB-EMU, PARTNER-AGB, ProDG/GBA workflows | GB/GBC ROMs expose cartridge title, mapper, ROM size, and RAM size metadata. GBA ROMs expose header metadata and common save marker detection (`SRAM_V`, `FLASH_V`, `EEPROM_V`). `.sav` files remain raw export/carving. |
| Nintendo DS / DSi | IS-NITRO-EMULATOR, IS-NITRO-DEBUGGER, IS-TWL/NTR family tools | NDS NitroFS ROM browsing/export is supported; DSi NAND layouts and plaintext/decrypted FAT16 images are supported according to available key/material state. |
| Nintendo 3DS | PARTNER-CTR Debugger/Capture, Panda/test units | NCSD partition mapping, NCCH section mapping, plaintext/decrypted CTR FAT16 browsing/export, keyed NAND FAT browsing when matching material is supplied, and raw export/carving are supported. |
| GameCube | Dolphin/NPDP-GDEV, NR Reader, DDH, SN-TDEV | GameCube/Dolphin disc images mount the disc FST for file browsing/export when the FST is present; native GameCube RVZ decompression mounts the same FST path from compressed Dolphin images; `.bca` and CleanRip dump-info sidecars are exposed for export. |
| Wii | NDEV, RVT-R, RVT-H, Revolution SDK hardware | Wii NAND with BootMii keys, Wii optical images, WBFS containers, and RVT-H disc banks are supported. |
| Wii U | CAT-DEV, CAT-R, Cafe SDK hardware | Wii U MLC WFS with matching `otp.bin`, WFS deleted metadata candidates, CAT-DEV/CAT-SES ZIP-wrapped HDD handling, WUX/WFS/FST carving, and raw export are supported. |
| Nintendo Switch | SDEV, EDEV, ADEV, NX-era hardware | Switch NAND/eMMC GPT discovery, BIS partition identification, AES-XTS FAT32 BIS mounting with keys, NSP/PFS0 entry expansion, CNMT summaries, NCA section spans, and Switch package/content carving are supported. |

## Sega

| Platform | Public devkit/test names | Storage impact in Drive Assistant |
| --- | --- | --- |
| Dreamcast | Katana / HKT-01 Dev.Box, GD-Writer HKT-0400, GD-R workflow | Dreamcast Katana `IP.BIN` boot sectors are carved. `.gdi` descriptors expose track rows and export sidecar track files when present, and GDI/CUE/ISO data tracks mount as ISO9660 when the filesystem is present. `.cim`, `.hex` Katana flash partitions, `.cdi`, `.vmu`, `.vms`, and `.dci` are recognized for raw export/carving; DiscJuggler CDI and VMU filesystem parsing still need deeper work. |
| Saturn / earlier Sega | Sophia/CartDev-era Saturn tools and earlier cartridge/CD dev systems | Sega Saturn system-area/IP.BIN headers are detected for raw export/carving. Full ISO9660/session browsing for Saturn cue/bin sets and earlier Sega cartridge/CD systems still needs dedicated parsers. |

## Practical Scope

Drive Assistant prioritizes media that can be recovered without proprietary SDKs:

- HDD, NAND, eMMC, USB, memory-card, ROM, disc, and save images.
- Publicly understood filesystem/container layouts.
- Key-gated encrypted images when the user supplies their own console-derived keys.

It does not emulate devkit hardware, run SDK tools, bypass signing, or include proprietary keys.

## Public Research References

- [Sega Retro: Dreamcast Dev.Box](https://www.segaretro.org/Dreamcast_Dev.Box)
- [Retro Reversing: Sega Dreamcast Katana Development Kit Hardware](https://www.retroreversing.com/Sega-Dreamcast-Katana-Development-Kit)
- [PS2 Developer Wiki: PlayStation 2 Tool](https://www.psdevwiki.com/ps2/PlayStation_2_Tool)
- [PS3 Developer Wiki](https://www.psdevwiki.com/ps3/)
- [PS4 Developer Wiki: DUH-T1000xA series](https://www.psdevwiki.com/ps4/DUH-T1000xA_series)
- [xboxdevwiki: Development Kits](https://xboxdevwiki.net/Development_Kits)
- [WiiBrew: NDEV](https://wiibrew.org/wiki/NDEV)
- [Retro Reversing: Nintendo 3DS Development Kit Hardware](https://www.retroreversing.com/nintendo-3ds-development-kit)
- [Nintendo DS Development Kit Hardware](https://frds.github.io/nintendo-ds-development-kit/)
- [Nintendo 64 accessories, development kit overview](https://en.wikipedia.org/wiki/Nintendo_64_accessories)
