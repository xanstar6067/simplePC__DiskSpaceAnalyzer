using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
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
    private static readonly IReadOnlyList<ScanNode> EmptyChildren = Array.Empty<ScanNode>();

    private ScanNodeCollection? _children;
    private int _cachedChildCount;
    private bool _areChildrenLoaded = true;
    private long _logicalSize;
    private long _sizeOnDisk;
    private long _fileCount;
    private long _directoryCount;
    private string _statusText = "Ожидает";
    private RiskLevel _risk = RiskLevel.Safe;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isRefreshing;

    public ObservableCollection<ScanNode> Children
    {
        get
        {
            if (_children is null)
            {
                _children = CreateChildrenCollection();
            }

            return _children;
        }
    }

    public int ChildCount => _areChildrenLoaded ? _children?.Count ?? 0 : _cachedChildCount;

    internal IReadOnlyList<ScanNode> ExistingChildren => _children is null ? EmptyChildren : _children;

    internal ObservableCollection<ScanNode>? CreatedChildren => _children;

    internal bool HasUnloadedCachedChildren => IsCacheBacked && !_areChildrenLoaded && _cachedChildCount > 0;

    internal bool IsCacheBacked => !string.IsNullOrWhiteSpace(CacheDataPath);

    internal string? CacheDataPath { get; private set; }

    internal string? CacheIndexPath { get; private set; }

    internal int CacheFormatVersion { get; private set; }

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

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set => SetProperty(ref _isRefreshing, value);
    }

    public int Level
    {
        get
        {
            var level = 0;
            var parent = Parent;
            while (parent is not null)
            {
                level++;
                parent = parent.Parent;
            }

            return level;
        }
    }

    public Thickness IndentMargin => new(Level * 16, 0, 0, 0);

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
        RiskLevel.Safe => "Доступно",
        RiskLevel.Review => "Требует внимания",
        RiskLevel.System => "Системное",
        RiskLevel.Dangerous => "Ограничено",
        RiskLevel.Skipped => "Пропущено",
        RiskLevel.NoAccess => "Нет доступа",
        _ => "Доступно"
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
        if (_children is null || _children.Count < 2)
        {
            return;
        }

        var sorted = _children.OrderByDescending(child => child.SizeOnDisk).ThenBy(child => child.DisplayName).ToList();
        _children.Clear();
        foreach (var child in sorted)
        {
            child.Parent = this;
            _children.Add(child);
        }
    }

    private void ChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ChildCount));
    }

    internal void SetCachedSource(string dataPath, string? indexPath, int formatVersion, int childCount)
    {
        CacheDataPath = dataPath;
        CacheIndexPath = indexPath;
        CacheFormatVersion = formatVersion;
        _cachedChildCount = Math.Max(0, childCount);
        _areChildrenLoaded = _cachedChildCount == 0;
        OnPropertyChanged(nameof(ChildCount));
    }

    internal void SetCachedChildren(IReadOnlyList<ScanNode> children)
    {
        foreach (var child in children)
        {
            child.Parent = this;
        }

        _children ??= CreateChildrenCollection();
        _cachedChildCount = children.Count;
        _areChildrenLoaded = true;
        _children.ReplaceAll(children);
        OnPropertyChanged(nameof(ChildCount));
    }

    internal bool UnloadCachedChildren()
    {
        if (!IsCacheBacked || !_areChildrenLoaded || _cachedChildCount == 0)
        {
            return false;
        }

        _areChildrenLoaded = false;
        _children?.ReplaceAll(EmptyChildren);
        OnPropertyChanged(nameof(ChildCount));
        return true;
    }

    private ScanNodeCollection CreateChildrenCollection()
    {
        var children = new ScanNodeCollection();
        children.CollectionChanged += ChildrenChanged;
        return children;
    }

    private sealed class ScanNodeCollection : ObservableCollection<ScanNode>
    {
        public void ReplaceAll(IReadOnlyList<ScanNode> nodes)
        {
            Items.Clear();
            foreach (var node in nodes)
            {
                Items.Add(node);
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
