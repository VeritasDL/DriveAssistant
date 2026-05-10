# Real Image Fixture Notes

Drive Assistant keeps console-unique NAND dumps and key material out of the repository. Use these optional environment variables when you have local fixtures available:

| Variable | Purpose |
| --- | --- |
| `DRIVE_ASSISTANT_DSI_NAND_IMAGE` | Raw DSi NAND image for the DSi NAND smoke test. |
| `DRIVE_ASSISTANT_3DS_NAND_IMAGE` | Raw 3DS NAND/SYSNAND image for the NCSD smoke test. |
| `DRIVE_ASSISTANT_3DS_NAND_KEY_DIR` | Optional directory containing matching `boot9.bin`, `nand_cid.mem`, and OTP material; the test validates file shapes only unless `DRIVE_ASSISTANT_3DS_NAND_EXPECT_FAT=1` is set. |
| `DRIVE_ASSISTANT_3DS_NAND_EXPECT_FAT` | Set to `1` only when the 3DS NAND/key directory are a coherent set and should mount as decrypted FAT. |
| `DRIVE_ASSISTANT_3DS_FAT_IMAGE` | Already-decrypted/plain 3DS FAT16 image for mount, metadata, deleted-entry scan, and export coverage. |

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

## Current Limitation

Drive Assistant now derives and applies DSi No$GBA-footer NAND keys for decrypted FAT browsing. 3DS NAND FAT browsing is implemented for coherent local `boot9.bin` + OTP + NAND CID sets, but the archive.org 3DS candidates above are not both small and key-complete enough to serve as an always-on automated 3DS FAT fixture.
