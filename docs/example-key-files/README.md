# Example Key Files

These files show the key-file formats Drive Assistant accepts.

The values in this directory are placeholders. Replace every placeholder value with keys dumped from the console that produced the image you are opening. Do not publish real console keys.

## Nintendo Switch

Use `Switch-bis_keys.example.txt` for Nintendo Switch raw NAND/eMMC images. The app needs the BIS AES-XTS `crypt` and `tweak` pair for each encrypted BIS partition you want to mount.

- BIS key 0: `PRODINFO` and `PRODINFOF`
- BIS key 1: `SAFE`
- BIS key 2: `SYSTEM`
- BIS key 3: `USER`

## Wii

Use `Wii-keys-folder.example.txt` as a manifest for a folder or `.tar.gz`/`.tgz` archive containing `common-key`, `sd-key`, `sd-iv`, and `md5-blanker`. For Wii NAND file export, use the matching console's BootMii `keys.bin`; the shared key archive alone is not enough to decrypt per-console NAND file clusters.

## Wii U

Use `WiiU-keys-folder.example.txt` as the accepted folder layout. `otp.bin` is required for MLC WFS images. `seeprom.bin` is also needed for USB/dev HDD key derivation.

## Nintendo DS / DSi / 3DS

Use `DSi-3DS-keys.example.txt` as a checklist for console-specific key material. Plain DS ROMs and already-decrypted CTR/DSi FAT16 images do not need keys. Encrypted NAND dumps need keys from the same console before metadata scanning and deleted-file recovery can operate on the decrypted FAT filesystem.

## PlayStation

Use `PlayStation-keys.example.txt` for the accepted local key-file shapes for PS3/PS4-style encrypted images. Placeholder values are intentionally fake.
