using System;
using System.IO;

namespace FATXTools.DiskTypes
{
    public class CompressedImage : FATX.DriveReader
    {
        public CompressedImage(string fileName, bool exhaustiveRawPartitionSearch = true)
            : this(fileName, ImgcDecoder.DecodeToTempRawImage(fileName), exhaustiveRawPartitionSearch)
        {
        }

        private CompressedImage(string fileName, string decodedPath, bool exhaustiveRawPartitionSearch)
            : base(new FileStream(decodedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (!fileName.EndsWith(".imgc", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("CompressedImage expects an .imgc source file.");
            }

            SourcePath = fileName;
            DecodedPath = decodedPath;
            base.Initialize(exhaustiveRawPartitionSearch);
        }

        public string SourcePath { get; }

        public string DecodedPath { get; }
    }
}
