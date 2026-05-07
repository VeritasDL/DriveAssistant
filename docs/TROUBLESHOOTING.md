# Troubleshooting

## App does not launch
- Run `Drive Assistant.exe` from a terminal and check output.
- Check `log.txt` (or your configured log path) for startup exceptions.
- Make sure supported .NET Desktop runtime is installed for the release build.

## Cannot open physical disks
- Run Drive Assistant as Administrator.
- Confirm the drive is not locked by another process.

## Partitions do not appear correctly
- Use `Drive -> Search For Partitions`.
- Try `Drive -> Add Partition` manually with known offset/length.
- For raw partition images, Drive Assistant attempts raw-partition fallback automatically.

## Recovery issues
- Use `Recovery View -> Edit Cluster Chain` for fragmented files.
- Use `Carver View -> Add Manual Range` if metadata is incomplete.
- Enable file logging in Settings before long recovery sessions.
