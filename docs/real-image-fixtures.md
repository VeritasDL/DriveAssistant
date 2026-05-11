# Real Image Fixture Notes

Drive Assistant keeps console-unique NAND dumps and key material out of the repository. Use these optional environment variables when you have local fixtures available:

| Variable | Purpose |
| --- | --- |
| `DRIVE_ASSISTANT_DSI_NAND_IMAGE` | Raw DSi NAND image for the DSi NAND smoke test. |
| `DRIVE_ASSISTANT_3DS_NAND_IMAGE` | Raw 3DS NAND/SYSNAND image for the NCSD smoke test. |
| `DRIVE_ASSISTANT_3DS_NAND_KEY_DIR` | Optional directory containing matching `boot9.bin`, `nand_cid.mem`, and OTP material; the test validates file shapes only unless `DRIVE_ASSISTANT_3DS_NAND_EXPECT_FAT=1` is set. |
| `DRIVE_ASSISTANT_3DS_NAND_EXPECT_FAT` | Set to `1` only when the 3DS NAND/key directory are a coherent set and should mount as decrypted FAT. |
| `DRIVE_ASSISTANT_3DS_FAT_IMAGE` | Already-decrypted/plain 3DS FAT16 image for mount, metadata, deleted-entry scan, and export coverage. |
| `DRIVE_ASSISTANT_PUBLIC_PSP_CSO` | Optional public/homebrew PSP `.cso` fixture for CSO decompression and UMD ISO9660 smoke testing. |
| `DRIVE_ASSISTANT_PSP_NAND_IMAGE` | Optional local PSP NAND image for manual mapped/physical FAT12 flash recovery checks. Keep console-specific NAND dumps local. |
| `DRIVE_ASSISTANT_VITA_PLAINTEXT_NAND_IMAGE` | Optional local Vita NAND/eMMC image that has already been decrypted into a plaintext master-block partition map. |

## Archive.org Candidates Checked

- `https://archive.org/details/dsiprototype2352008sdk6291`
  - Downloaded `nand.bin`.
  - Expected SHA-1: `98f057617cae635b27a91a5885530eaf8832a748`.
  - Size: `251,658,304` bytes, a 240 MiB DSi NAND with a 64-byte No$GBA footer.
  - Current coverage: decrypts the No$GBA-footer NAND locally, opens FAT partitions, and runs deleted/raw scan smoke paths.

- `https://archive.org/details/3DS-Panda-NAND-0-16-24`
  - Downloaded `EJF10002007.zip`.
  - Expected SHA-1: `6015507f747e28e2f438a37ed78ac99a14efa6eb`.
  - Contains `010101_EJF10002007_sysnand_00.bin`, `nand_cid.mem`, `otp.mem`, and `otp_dec.mem`.
  - Current coverage: opens the extracted SYSNAND as Nintendo 3DS NCSD raw partitions and validates the key-material file sizes locally. It does not include a matching `boot9.bin`, so it is not a complete 3DS FAT browsing fixture by itself.

- `https://archive.org/details/lovure-n-3ds-xl-backup`
  - Contains a large New 3DS XL NAND plus boot9 and OTP files.
  - Useful for future decrypting tests, but its published key material did not produce a verified OTP decrypt in the local pyctr cross-check, so it is documented as a manual/research candidate rather than a CI-style fixture.

- `https://archive.org/details/nand-dsi-usa-1-matte-blue`
  - Contains many DSi NAND images.
  - Not used for keyed testing because the key-material format is not clearly documented in the item metadata.

- `https://archive.org/details/fnaf-special-delibery-psp-homebrew`
  - Contains `fnaf-delivery-ar-lite.cso`.
  - Current coverage: optional public/homebrew PSP CSO smoke fixture. The test downloads or points to a local copy through `DRIVE_ASSISTANT_PUBLIC_PSP_CSO` and verifies native CSO decompression plus PSP UMD ISO9660 browsing.

- `https://archive.org/details/itchio_homebrew_crawl_psp`
  - Contains `Sony PSP.zip`.
  - Useful as a public PSP homebrew package/file-layout research candidate. Not committed to the repository.

- archive.org advanced searches for `psp nand dump`, `psp flash0 dump`, `ps vita nand dump`, `vita pfs key`, `psvpfstools key`, and `ps vita pfs dump`
  - Result: no small coherent public PSP NAND or PS Vita NAND/PFS fixture was found with matching, redistributable key material.
  - PSP results such as `time-machine-0.1-full` and `sxt-firmware-bfm-for-developer-beta-one` are firmware/homebrew packages, not NAND recovery fixtures with console-specific keys.
  - Vita PFS-key results pointed at archived GitHub/tool snapshots, not paired encrypted content plus rights material suitable for checked-in tests.

## Current Limitation

Drive Assistant now derives and applies DSi No$GBA-footer NAND keys for decrypted FAT browsing. 3DS NAND FAT browsing is implemented for coherent local `boot9.bin` + OTP + NAND CID sets, but the archive.org 3DS candidates above are not both small and key-complete enough to serve as an always-on automated 3DS FAT fixture. PSP CSO/UMD, PSP NAND FAT12 flash recovery, and PS Vita VPK/package browsing are keyless. Plaintext/decrypted Vita NAND/eMMC partition maps mount FAT16/exFAT partitions. Encrypted Vita PFS, CMA backup, and externally dumped encrypted Vita NAND/eMMC content still require matching keys or prior decryption before filesystem browsing.
