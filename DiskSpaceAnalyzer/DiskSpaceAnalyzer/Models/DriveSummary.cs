using DiskSpaceAnalyzer.ViewModels;

namespace DiskSpaceAnalyzer.Models;

public sealed class DriveSummary : ViewModelBase
{
    public string RootPath { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public long TotalSize { get; init; }

    public long FreeSpace { get; init; }

    public StorageKind StorageKind { get; init; } = StorageKind.Unknown;

    public string DisplayName
    {
        get
        {
            var namePart = string.IsNullOrWhiteSpace(Name) ? RootPath : $"{RootPath} {Name}";
            return $"{namePart} - свободно {Services.FileSizeFormatter.Format(FreeSpace)} из {Services.FileSizeFormatter.Format(TotalSize)} ({StorageKindText})";
        }
    }

    public string StorageKindText => StorageKind switch
    {
        StorageKind.Hdd => "HDD",
        StorageKind.Ssd => "SSD",
        StorageKind.NvmeSsd => "NVMe SSD",
        _ => "тип неизвестен"
    };
}
