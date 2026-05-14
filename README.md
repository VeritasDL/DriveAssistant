# Drive Assistant

Drive Assistant is a read-only storage recovery and inspection tool for disk images and console media.

It is built for practical recovery sessions: open an image, inspect partitions, browse files, run recovery scans, and export what you can recover.

## Why This Project

- Designed for real recovery workflows, not one-click demos.
- Works across common PC disk images and many console storage formats.
- Keeps source images untouched during analysis and export.

## What You Can Do

- Open raw images (`.img`, `.bin`, `.raw`) and `.imgc`.
- Browse supported filesystems and partitions.
- Run metadata-based recovery scans.
- Run signature-based file carving.
- Export selected files with progress feedback.
- Save and reload analysis sessions.

## Platform Apps

- `DriveAssistant.Wpf`: Windows desktop app (WPF).
- `DriveAssistant.Avalonia`: Linux desktop app (Avalonia).
- `DriveAssistant.Cli`: cross-platform terminal app.

## Quick Start

1. Download the latest release: [GitHub Releases](https://github.com/rain0x06/DriveAssistant/releases/latest)
2. Extract it.
3. Launch the app for your platform:
   - Windows: `Drive Assistant.exe`
   - Linux desktop: `Drive Assistant`
   - CLI: `drive-assistant --help`

## Documentation

Technical and detailed reference material lives in `docs/`.

- [User Guide](docs/USER_GUIDE.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)
- [Console/Devkit Model Coverage](docs/console-devkit-models.md)
- [Real Image Fixtures](docs/real-image-fixtures.md)
- [Example Key Files](docs/example-key-files/README.md)

## Repository

- `DriveAssistant.Wpf` - Windows desktop UI
- `DriveAssistant.Avalonia` - Linux desktop UI
- `DriveAssistant.Cli` - terminal workflow
- `FATX` - core FATX/recovery library
- `docs` - guides and technical documentation

## License

This project is licensed under [LICENSE](LICENSE).
