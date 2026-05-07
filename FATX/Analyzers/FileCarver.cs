using FATX.Analyzers.Signatures;
using FATX.Analyzers.Signatures.Blank;
using FATX.FileSystem;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace FATX.Analyzers
{
    public enum FileCarverInterval
    {
        Byte = 0x1,
        Align = 0x10,
        Sector = 0x200,
        Page = 0x1000,
        Cluster = 0x4000,
    }

    public class FileCarver
    {
        private readonly Volume _volume;
        private readonly FileCarverInterval _interval;
        private readonly long _length;
        private List<FileSignature> _carvedFiles;
        private readonly List<CustomSignatureDefinition> _customSignatures = new List<CustomSignatureDefinition>();

        public List<long> BadOffsets { get; } = new List<long>();
        public List<string> Errors { get; } = new List<string>();

        public FileCarver(Volume volume)
        {
            _volume = volume;
            _interval = FileCarverInterval.Sector;
            _length = volume.Length;
        }

        public FileCarver(Volume volume, FileCarverInterval interval, long length)
        {
            if (length == 0 || length > volume.FileAreaLength)
            {
                length = volume.Length;
            }

            _volume = volume;
            _interval = interval;
            _length = length;
        }

        public void SetCustomSignatures(IEnumerable<CustomSignatureDefinition> definitions)
        {
            _customSignatures.Clear();
            if (definitions == null)
            {
                return;
            }

            foreach (var definition in definitions)
            {
                try
                {
                    _ = definition.GetHeaderBytes();
                    _customSignatures.Add(definition);
                }
                catch (Exception ex)
                {
                    Errors.Add($"Invalid custom signature '{definition?.Name}': {ex.Message}");
                }
            }
        }

        public void LoadFromDatabase(JsonElement fileCarverList)
        {
            _carvedFiles = new List<FileSignature>();

            foreach (var file in fileCarverList.EnumerateArray())
            {
                if (!file.TryGetProperty("Offset", out var offsetElement))
                {
                    Console.WriteLine("Failed to load signature from database: Missing offset field");
                    continue;
                }

                var fileSignature = new BlankSignature(_volume, offsetElement.GetInt64());
                if (file.TryGetProperty("Name", out var nameElement))
                {
                    fileSignature.FileName = nameElement.GetString();
                }

                if (file.TryGetProperty("Size", out var sizeElement))
                {
                    fileSignature.FileSize = sizeElement.GetInt64();
                }

                _carvedFiles.Add(fileSignature);
            }
        }

        public List<FileSignature> GetCarvedFiles() => _carvedFiles;

        public Volume GetVolume() => _volume;

        public bool AddManualCarvedFile(string name, long startOffset, long endOffset)
        {
            if (startOffset < 0 || endOffset < startOffset || endOffset >= _volume.FileAreaLength)
            {
                return false;
            }

            if (_carvedFiles == null)
            {
                _carvedFiles = new List<FileSignature>();
            }

            var signature = new BlankSignature(_volume, startOffset)
            {
                FileName = string.IsNullOrWhiteSpace(name) ? $"manual_{startOffset:X}" : name,
                FileSize = (endOffset - startOffset) + 1
            };

            _carvedFiles.Add(signature);
            return true;
        }

        public List<FileSignature> Analyze(CancellationToken cancellationToken, IProgress<int> progress)
        {
            var signatureTypes = (
                from assembly in AppDomain.CurrentDomain.GetAssemblies()
                from type in assembly.GetTypes()
                where type.Namespace == "FATX.Analyzers.Signatures"
                where type.IsSubclassOf(typeof(FileSignature))
                where !type.IsAbstract
                where type != typeof(BlankSignature)
                where type != typeof(TextSignature)
                where type.GetConstructor(new[] { typeof(Volume), typeof(long) }) != null
                select type).ToList();

            _carvedFiles = new List<FileSignature>();
            BadOffsets.Clear();
            Errors.Clear();

            var interval = (long)_interval;
            var origByteOrder = _volume.GetReader().ByteOrder;
            long progressValue = 0;
            long progressUpdate = Math.Max(interval * 0x200, interval);
            long scannedBlocks = _length / interval;

            for (long offset = 0; offset < _length; offset += interval)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return _carvedFiles;
                }

                bool blockFailed = false;

                foreach (Type type in signatureTypes)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return _carvedFiles;
                    }

                    try
                    {
                        var signature = (FileSignature)Activator.CreateInstance(type, _volume, offset);
                        _volume.GetReader().ByteOrder = origByteOrder;
                        _volume.SeekFileArea(offset);

                        if (!signature.Test())
                        {
                            continue;
                        }

                        _carvedFiles.Add(signature);
                        _volume.SeekFileArea(offset);
                        signature.Parse();
                        Console.WriteLine($"Found {signature.GetType().Name} at 0x{offset:X}.");
                    }
                    catch (Exception ex)
                    {
                        blockFailed = true;
                        Errors.Add($"[{type.Name}] 0x{offset:X}: {ex.Message}");
                    }
                }

                foreach (var customDefinition in _customSignatures)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return _carvedFiles;
                    }

                    try
                    {
                        var signature = new CustomPatternSignature(_volume, offset, customDefinition);
                        _volume.GetReader().ByteOrder = origByteOrder;
                        _volume.SeekFileArea(offset);

                        if (!signature.Test())
                        {
                            continue;
                        }

                        _carvedFiles.Add(signature);
                        _volume.SeekFileArea(offset);
                        signature.Parse();
                        Console.WriteLine($"Found custom signature '{customDefinition.Name}' at 0x{offset:X}.");
                    }
                    catch (Exception ex)
                    {
                        blockFailed = true;
                        Errors.Add($"[Custom:{customDefinition.Name}] 0x{offset:X}: {ex.Message}");
                    }
                }

                if (blockFailed)
                {
                    BadOffsets.Add(offset);
                }

                progressValue += interval;
                if (progressValue % progressUpdate == 0)
                {
                    progress?.Report((int)(progressValue / interval));
                }
            }

            progress?.Report((int)scannedBlocks);
            Console.WriteLine("Complete!");
            return _carvedFiles;
        }
    }
}
