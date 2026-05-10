# Real Image Fixture Notes

Drive Assistant keeps console-unique NAND dumps and key material out of the repository. Use these optional environment variables when you have local fixtures available:

| Variable | Purpose |
| --- | --- |
| `DRIVE_ASSISTANT_DSI_NAND_IMAGE` | Raw DSi NAND image for the DSi NAND smoke test. |
| `DRIVE_ASSISTANT_3DS_NAND_IMAGE` | Raw 3DS NAND/SYSNAND image for the NCSD smoke test. |
| `DRIVE_ASSISTANT_3DS_NAND_KEY_DIR` | Optional directory containing matching `nand_cid.mem`, `otp.mem`, and `otp_dec.mem`; the test validates file shapes only and does not store or print values. |
| `DRIVE_ASSISTANT_3DS_FAT_IMAGE` | Already-decrypted/plain 3DS FAT16 image for mount, metadata, deleted-entry scan, and export coverage. |

## Archive.org Candidates Checked

- `https://archive.org/details/dsiprototype2352008sdk6291`
  - Downloaded `nand.bin`.
  - Expected SHA-1: `98f057617cae635b27a91a5885530eaf8832a748`.
  - Size: `251,658,304` bytes, a 240 MiB DSi NAND with a 64-byte No$GBA footer.
  - Current coverage: opens as Nintendo DSi NAND raw regions, deleted/raw scan smoke path runs.

- `https://archive.org/details/3DS-Panda-NAND-0-16-24`
  - Downloaded `EJF10002007.zip`.
  - Expected SHA-1: `6015507f747e28e2f438a37ed78ac99a14efa6eb`.
  - Contains `010101_EJF10002007_sysnand_00.bin`, `nand_cid.mem`, `otp.mem`, and `otp_dec.mem`.
  - Current coverage: opens the extracted SYSNAND as Nintendo 3DS NCSD raw partitions and validates the matching key-material file sizes locally.

- `https://archive.org/details/lovure-n-3ds-xl-backup`
  - Contains a large New 3DS XL NAND plus boot9 and OTP files.
  - Useful for future decrypting tests, but too large for the normal local smoke loop.

- `https://archive.org/details/nand-dsi-usa-1-matte-blue`
  - Contains many DSi NAND images.
  - Not used for keyed testing because the key-material format is not clearly documented in the item metadata.

## Current Limitation

Drive Assistant does not yet derive or apply 3DS/DSi NAND AES keys from OTP/CID/footer material. Encrypted 3DS and DSi NAND images currently open for raw export and carving. Full filesystem browsing still requires an already-decrypted FAT image or a future managed decrypting volume implementation.
