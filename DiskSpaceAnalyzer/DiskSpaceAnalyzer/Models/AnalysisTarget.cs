using DiskSpaceAnalyzer.ViewModels;

namespace DiskSpaceAnalyzer.Models;

public sealed class AnalysisTarget : ViewModelBase
{
    private string _path = string.Empty;
    private StorageKind _storageKind = StorageKind.Unknown;

    public string Path
    {
        get => _path;
        set
        {
            if (SetProperty(ref _path, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public StorageKind StorageKind
    {
        get => _storageKind;
        set
        {
            if (SetProperty(ref _storageKind, value))
            {
                OnPropertyChanged(nameof(StorageKindText));
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string DisplayName => $"{Path} ({StorageKindText})";

    public string StorageKindText => StorageKind switch
    {
        StorageKind.Hdd => "HDD",
        StorageKind.Ssd => "SSD",
        StorageKind.NvmeSsd => "NVMe SSD",
        _ => "тип неизвестен"
    };
}
