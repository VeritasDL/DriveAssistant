using FATX.FileSystem;

namespace FATXTools.Wpf;

public sealed class PartitionModel
{
    public PartitionModel(Volume volume, string status)
    {
        FatxVolume = volume;
        Status = status;
    }

    public PartitionModel(XboxNtfsVolume volume, string status)
    {
        NtfsVolume = volume;
        Status = status;
    }

    public PartitionModel(PlayStationVolume volume, string status)
    {
        PlayStationVolume = volume;
        Status = status;
    }

    public PartitionModel(GenericFileSystemVolume volume, string status)
    {
        GenericVolume = volume;
        Status = status;
    }

    public Volume? FatxVolume { get; }

    public XboxNtfsVolume? NtfsVolume { get; }

    public PlayStationVolume? PlayStationVolume { get; }

    public GenericFileSystemVolume? GenericVolume { get; }

    public bool IsMounted => FatxVolume?.Mounted == true || NtfsVolume != null || PlayStationVolume?.IsLoaded == true || GenericVolume != null;

    public string Name => FatxVolume?.Name ?? NtfsVolume?.Name ?? PlayStationVolume?.Name ?? GenericVolume?.Name ?? "Partition";

    public string Status { get; }

    public long Offset => FatxVolume?.Offset ?? NtfsVolume?.Offset ?? PlayStationVolume?.Offset ?? GenericVolume?.Offset ?? 0;

    public long Length => FatxVolume?.Length ?? NtfsVolume?.Length ?? PlayStationVolume?.Length ?? GenericVolume?.Length ?? 0;
}
