# Example Key Files

These files show the key-file formats Drive Assistant accepts.

The values in this directory are placeholders. Replace every placeholder value with keys dumped from the console that produced the image you are opening. Do not publish real console keys.

## Nintendo Switch

Use `Switch-bis_keys.example.txt` for Nintendo Switch raw NAND/eMMC images. The app needs the BIS AES-XTS `crypt` and `tweak` pair for each encrypted BIS partition you want to mount.

- BIS key 0: `PRODINFO` and `PRODINFOF`
- BIS key 1: `SAFE`
- BIS key 2: `SYSTEM`
- BIS key 3: `USER`
