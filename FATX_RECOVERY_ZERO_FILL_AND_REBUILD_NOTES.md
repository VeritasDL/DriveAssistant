# Drive Assistant Work Log: Recovery Zero-Fill + FATX Rebuild Spec

## Scope
- Added a new recovery-export option to zero-fill overwritten clusters when exporting recovered files.
- Reviewed current codebase support for legacy FATXTools JSON import and FATX metadata structures for full image recreation.

## Step 1: Locate Recovery Export Path
- Searched the WPF export flow and identified the recovered-file write path in `MainWindow.xaml.cs`.
- Confirmed recovered exports go through `WriteRecoveryFile(...)` and then `WriteClustersToFile(...)`.

## Step 2: Locate Collision/Overwrite Signals
- Verified overwritten/colliding clusters are already tracked in `DatabaseFile.GetCollisions()`.
- Confirmed collisions are computed by `IntegrityAnalyzer` and used in ranking (`partial cluster overwrite/collision` and `fully overwritten` states).

## Step 3: Add User Setting
- Added `AppSettings.ZeroFillOverwrittenRecoveryClusters` with default `false`.
- Added settings UI controls:
  - Label: `Recovered export: zero-fill overwritten clusters`
  - Checkbox: `ZeroFillOverwrittenRecoveryClustersCheckBox`
- Wired load/save in `SettingsWindow.xaml.cs`.

## Step 4: Apply Setting in Export Flow
- Updated recovered export call sites to pass the new setting:
  - Single recovered file export.
  - Multi-selection recovered export to directory.
- Threaded setting through recovery export helper overloads.

## Step 5: Write Zero-Fill Behavior
- Extended `WriteClustersToFile(...)` with optional `clustersToZeroFill`.
- For recovered exports with option enabled:
  - If cluster is in collision set, writes zero bytes for that cluster span (bounded by remaining file size).
  - Non-colliding clusters are written normally from source image data.
- Existing default behavior is preserved when option is disabled.

## Step 6: FATX HDD Recreation Feasibility Review
- Confirmed snapshot JSON model includes enough metadata for reconstruction skeleton:
  - Partition offsets/lengths, file metadata tree, cluster/first cluster, attributes, deleted flags.
- Confirmed FATX reader constants and layout assumptions in `FATX/FileSystem/Volume.cs`:
  - Boot header fields (`FATX`, serial, sectors-per-cluster, root first cluster).
  - FAT/table alignment (`0x1000`) and file-area offset logic.
- Identified implementation target for next phase:
  - Add CLI command to rebuild FATX image from JSON + active/deleted source folders.

## Step 7: Implement CLI FATX Rebuild Command
- Added a new CLI command: `rebuild-fatx`.
- Updated command routing and help text in `DriveAssistant.Cli/Program.cs`.
- Added full implementation in `DriveAssistant.Cli/FatxImageRebuildCommand.cs`:
  - Interactive Step 0 prompts when positional args are missing:
    - Snapshot JSON path
    - Non-deleted files folder
    - Deleted files folder
    - Output image path
  - Added partition selection (`--partition` supports index or name).
  - Added serial override (`--serial` expects hex).
  - Added native + legacy JSON snapshot loading (legacy conversion path included).

## Step 8: FATX Image Construction Logic
- Implemented devkit-style header creation at image offset `0x0` with partition table entries in sectors.
- Implemented FATX partition header creation at target partition offset:
  - `FATX` signature
  - Serial number
  - `SectorsPerCluster = 0x20`
  - `RootCluster = 0x1`
- Implemented FAT sizing/layout using existing reader assumptions:
  - FAT reserved page at `0x1000`
  - FAT size alignment to `0x1000`
  - File area offset/cluster space derived from partition length
- Implemented runtime cluster allocator to avoid overlaps and allocate root/dir/file streams.

## Step 9: Dirent + Chain Rebuild Behavior
- Added tree filtering against provided source folders:
  - Files included only when present in supplied live/deleted directories.
  - Directories included when physically present or when they contain included children.
- Added dirent writer (`0x40` entries) and stream packing by cluster.
- Added FAT chain writing:
  - Directory chains are always written for recreated directories.
  - File chains are written for active files.
  - Deleted files are recreated as deleted dirents and data write attempts are preserved when clusters are available.
- Added cluster extent parsing from snapshot range strings (`Extents`) with fallback allocation strategy.

## Step 10: Build Validation
- Built `DriveAssistant.Cli` in Release mode after changes.
- Result: build succeeded with `0 errors`, `0 warnings`.

## Step 11: Add Toggleable Rebuild Option
- Added a user-toggle for rebuild behavior:
  - `--include-deleted true|false`
  - shortcut: `--no-deleted`
- Default remains `include deleted = true` for compatibility with prior behavior.
- Updated CLI help examples to show toggle usage.
