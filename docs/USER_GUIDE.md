# Drive Assistant User Guide

## 1. Opening Data
- Use `Open Image` for `.img`, `.imgc`, or raw partition images.
- Physical disk support should be run as Administrator.

## 2. Partition Workflow
- Use `Drive -> Search For Partitions` to scan for additional FATX headers.
- Use `Drive -> Add Partition` for manual partition definitions.
- Right-click a partition tab to remove it.

## 3. Analysis
- `File Explorer`:
  - Right-click tree/list and run `Metadata Analyzer`.
  - Right-click tree/list and run `File Carver`.
- Use `Ctrl+F` in explorer/recovery views to search.

## 4. Recovery Views
- `Recovery View`:
  - Review predicted status color plus numeric score.
  - Right-click files for:
    - `Edit Cluster Chain`
    - `Open Hex Viewer`
    - manual status overrides
- `Carver View`:
  - Recover selected/all carved results.
  - Add manual recovered ranges with start/end offsets.

## 5. Settings
- `Settings`:
  - File carver interval (default: `Sector (0x200)`).
  - Metadata interval.
  - Dark/Light theme.
  - Log file path and log-to-file toggle.
  - Custom carver definition file path.

## 6. Custom Carvers
- Edit `custom_carvers.json` (or your configured path).
- Define signature name, extension, start bytes, and optional end bytes.
- Re-run file carving to apply new signatures.

## 7. Troubleshooting
- If startup fails, check `log.txt` (or your configured log path).
- For difficult images:
  - try `Drive -> Search For Partitions`
  - use `Drive -> Add Partition` with manual offsets
  - use `Recovery View` cluster chain editor for fragmented files
