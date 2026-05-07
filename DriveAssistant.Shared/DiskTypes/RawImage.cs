using System.IO;

namespace FATXTools.DiskTypes
{
    public class RawImage : FATX.DriveReader
    {
        // TODO: replace with FileStream to be able to use "using"
        public RawImage(string fileName, bool exhaustiveRawPartitionSearch = true)
            : base(new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            SourcePath = fileName;
            base.Initialize(exhaustiveRawPartitionSearch);
        }

        public string SourcePath { get; }
    }
}
