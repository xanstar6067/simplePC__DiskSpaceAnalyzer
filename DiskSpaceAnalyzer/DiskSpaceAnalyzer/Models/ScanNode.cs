using System.Collections.ObjectModel;
using System.Windows.Media;
using DiskSpaceAnalyzer.Services;
using DiskSpaceAnalyzer.ViewModels;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace DiskSpaceAnalyzer.Models;

public sealed class ScanNode : ViewModelBase
{
    private long _logicalSize;
    private long _sizeOnDisk;
    private long _fileCount;
    private long _directoryCount;
    private string _statusText = "Ожидает";
    private RiskLevel _risk = RiskLevel.Safe;
    private bool _isExpanded;
    private bool _isSelected;

    public ScanNode()
    {
        Children = [];
    }

    public ObservableCollection<ScanNode> Children { get; }

    public ScanNode? Parent { get; set; }

    public string FullPath { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public FileSystemItemKind Kind { get; set; }

    public DateTimeOffset? ModifiedAt { get; set; }

    public string FileId { get; set; } = string.Empty;

    public long LogicalSize
    {
        get => _logicalSize;
        set
        {
            if (SetProperty(ref _logicalSize, value))
            {
                OnPropertyChanged(nameof(LogicalSizeText));
                OnPropertyChanged(nameof(PercentOfParentText));
            }
        }
    }

    public long SizeOnDisk
    {
        get => _sizeOnDisk;
        set
        {
            if (SetProperty(ref _sizeOnDisk, value))
            {
                OnPropertyChanged(nameof(SizeOnDiskText));
                OnPropertyChanged(nameof(PercentOfParentText));
            }
        }
    }

    public long FileCount
    {
        get => _fileCount;
        set => SetProperty(ref _fileCount, value);
    }

    public long DirectoryCount
    {
        get => _directoryCount;
        set => SetProperty(ref _directoryCount, value);
    }

    public RiskLevel Risk
    {
        get => _risk;
        set
        {
            if (SetProperty(ref _risk, value))
            {
                OnPropertyChanged(nameof(RiskText));
                OnPropertyChanged(nameof(RiskBrush));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? FullPath : Name;

    public string LogicalSizeText => FileSizeFormatter.Format(LogicalSize);

    public string SizeOnDiskText => FileSizeFormatter.Format(SizeOnDisk);

    public string ModifiedText => ModifiedAt?.ToString("g") ?? "";

    public string KindText => Kind switch
    {
        FileSystemItemKind.File => "Файл",
        FileSystemItemKind.Folder => "Папка",
        FileSystemItemKind.Link => "Ссылка",
        FileSystemItemKind.NoAccess => "Нет доступа",
        _ => "Объект"
    };

    public string RiskText => Risk switch
    {
        RiskLevel.Safe => "Безопасно",
        RiskLevel.Review => "Проверить",
        RiskLevel.System => "Системное",
        RiskLevel.Dangerous => "Опасно",
        RiskLevel.Skipped => "Пропущено",
        RiskLevel.NoAccess => "Нет доступа",
        _ => "Безопасно"
    };

    public Brush RiskBrush => Risk switch
    {
        RiskLevel.Safe => new SolidColorBrush(Color.FromRgb(32, 132, 92)),
        RiskLevel.Review => new SolidColorBrush(Color.FromRgb(166, 111, 0)),
        RiskLevel.System => new SolidColorBrush(Color.FromRgb(64, 102, 175)),
        RiskLevel.Dangerous => new SolidColorBrush(Color.FromRgb(191, 67, 67)),
        RiskLevel.Skipped => new SolidColorBrush(Color.FromRgb(111, 111, 111)),
        RiskLevel.NoAccess => new SolidColorBrush(Color.FromRgb(132, 77, 160)),
        _ => Brushes.Gray
    };

    public string PercentOfParentText
    {
        get
        {
            if (Parent?.SizeOnDisk > 0)
            {
                return $"{(double)SizeOnDisk / Parent.SizeOnDisk:P1}";
            }

            return "";
        }
    }

    public void AddChild(ScanNode child)
    {
        child.Parent = this;
        Children.Add(child);
        child.OnPropertyChanged(nameof(PercentOfParentText));
    }

    public void SortChildrenBySize()
    {
        var sorted = Children.OrderByDescending(child => child.SizeOnDisk).ThenBy(child => child.DisplayName).ToList();
        Children.Clear();
        foreach (var child in sorted)
        {
            child.Parent = this;
            Children.Add(child);
        }
    }
}
