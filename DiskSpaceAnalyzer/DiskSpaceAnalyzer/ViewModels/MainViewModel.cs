using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DiskSpaceAnalyzer.Commands;
using DiskSpaceAnalyzer.Models;
using DiskSpaceAnalyzer.Services;
using DiskSpaceAnalyzer.Views;

namespace DiskSpaceAnalyzer.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly DriveInfoService _driveInfoService = new();
    private readonly PathRiskClassifier _classifier = new();
    private readonly AnalysisCacheService _cache = new();
    private readonly ShellService _shell = new();
    private readonly ObservableCollection<string> _excludedPaths = [];
    private readonly Stack<ScanNode> _navigationHistory = new();
    private readonly DispatcherTimer _driveRefreshTimer;
    private DiskScanner _scanner;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _searchCancellation;
    private INotifyCollectionChanged? _selectedChildren;
    private readonly HashSet<ScanNode> _subscribedFlatNodes = [];
    private DriveSummary? _selectedDrive;
    private ScanNode? _selectedNode;
    private ScanNode? _selectedFlatNode;
    private ScanNode? _selectedChartNode;
    private string _cacheWarningText = "";
    private string _currentPath = "";
    private string _statusSummary = "Выберите диск для анализа";
    private string _searchQuery = "";
    private bool _includeSystemDirectories;
    private bool _ignoreCache;
    private bool _analyzeSizeOnDisk;
    private bool _approximateSizeOnDisk = true;
    private SizeCalculationMode _displayedSizeCalculationMode = SizeCalculationMode.Approximate;
    private bool _isScanning;
    private bool _chartRefreshQueued;
    private bool _isRefreshingChartChildren;
    private long _processedFiles;
    private long _processedDirectories;
    private long _logicalBytes;
    private long _sizeOnDiskBytes;

    public MainViewModel()
    {
        _scanner = new DiskScanner(_classifier, _cache);

        StartScanCommand = new RelayCommand(async _ => await StartScanAsync(), _ => !IsScanning);
        CancelScanCommand = new RelayCommand(_ => CancelScan(), _ => IsScanning);
        SelectNodeCommand = new RelayCommand(parameter => NavigateToNode(parameter as ScanNode, rememberCurrent: true), parameter => parameter is ScanNode);
        NavigateToChartNodeCommand = new RelayCommand(_ => NavigateToNode(SelectedChartNode, rememberCurrent: true), _ => SelectedChartNode is not null);
        NavigateBackCommand = new RelayCommand(_ => NavigateBack(), _ => _navigationHistory.Count > 0);
        OpenInExplorerCommand = new RelayCommand(parameter => ExecuteNodeAction(parameter, _shell.OpenInExplorer), parameter => parameter is ScanNode);
        CopyPathCommand = new RelayCommand(parameter => ExecuteNodeAction(parameter, _shell.CopyPath), parameter => parameter is ScanNode);
        RefreshNodeCommand = new RelayCommand(async parameter => await RefreshNodeAsync(parameter as ScanNode), parameter => parameter is ScanNode && !IsScanning);
        ExcludeNodeCommand = new RelayCommand(parameter => ExcludeNode(parameter as ScanNode), parameter => parameter is ScanNode && !IsScanning);
        ShowDetailsCommand = new RelayCommand(parameter => ShowDetails(parameter as ScanNode), parameter => parameter is ScanNode);
        ClearCacheCommand = new RelayCommand(_ => ClearCache(), _ => !IsScanning);

        Roots.CollectionChanged += RootsChanged;
        LoadDrives();

        _driveRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _driveRefreshTimer.Tick += (_, _) => RefreshAvailableDrives(selectFirst: false);
        _driveRefreshTimer.Start();
    }

    public ObservableCollection<DriveSummary> AvailableDrives { get; } = [];

    public ObservableCollection<ScanNode> Roots { get; } = [];

    public ObservableCollection<ScanNode> FlatNodes { get; } = [];

    public ObservableCollection<ScanNode> ChartChildren { get; } = [];

    public ObservableCollection<ScanNode> SearchResults { get; } = [];

    public RelayCommand StartScanCommand { get; }

    public RelayCommand CancelScanCommand { get; }

    public RelayCommand SelectNodeCommand { get; }

    public RelayCommand NavigateToChartNodeCommand { get; }

    public RelayCommand NavigateBackCommand { get; }

    public RelayCommand OpenInExplorerCommand { get; }

    public RelayCommand CopyPathCommand { get; }

    public RelayCommand RefreshNodeCommand { get; }

    public RelayCommand ExcludeNodeCommand { get; }

    public RelayCommand ShowDetailsCommand { get; }

    public RelayCommand ClearCacheCommand { get; }

    public DriveSummary? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (SetProperty(ref _selectedDrive, value))
            {
                if (value is not null && !IsScanning)
                {
                    if (!TryShowCachedScan(value.RootPath))
                    {
                        ClearAnalysisView($"Для выбранного диска нет кэша: {value.RootPath}");
                    }
                }
                else if (value is null && !IsScanning)
                {
                    ClearAnalysisView("Выберите диск для анализа");
                }
            }
        }
    }

    public ScanNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (value is null && _isRefreshingChartChildren)
            {
                return;
            }

            if (_selectedNode == value)
            {
                return;
            }

            if (_selectedChildren is not null)
            {
                _selectedChildren.CollectionChanged -= SelectedChildrenChanged;
            }

            if (value is not null)
            {
                EnsureCachedChildrenLoaded(value);
            }

            _selectedNode = value;
            if (_selectedFlatNode != value)
            {
                _selectedFlatNode = value;
                OnPropertyChanged(nameof(SelectedFlatNode));
            }

            _selectedChildren = value?.Kind == FileSystemItemKind.Folder ? value.Children : null;
            if (_selectedChildren is not null)
            {
                _selectedChildren.CollectionChanged += SelectedChildrenChanged;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedNode));
            OnPropertyChanged(nameof(SelectedNodeSafetyText));
            QueueChartRefresh();
        }
    }

    public ScanNode? SelectedFlatNode
    {
        get => _selectedFlatNode;
        set
        {
            if (SetProperty(ref _selectedFlatNode, value))
            {
                SelectedNode = value;
            }
        }
    }

    public ScanNode? SelectedChartNode
    {
        get => _selectedChartNode;
        set
        {
            if (SetProperty(ref _selectedChartNode, value))
            {
                NavigateToChartNodeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string CurrentPath
    {
        get => _currentPath;
        set => SetProperty(ref _currentPath, value);
    }

    public string StatusSummary
    {
        get => _statusSummary;
        set => SetProperty(ref _statusSummary, value);
    }

    public string CacheWarningText
    {
        get => _cacheWarningText;
        set
        {
            if (SetProperty(ref _cacheWarningText, value))
            {
                OnPropertyChanged(nameof(HasCacheWarning));
            }
        }
    }

    public bool HasCacheWarning => !string.IsNullOrWhiteSpace(CacheWarningText);

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                RebuildSearchResults();
            }
        }
    }

    public bool IncludeSystemDirectories
    {
        get => _includeSystemDirectories;
        set => SetProperty(ref _includeSystemDirectories, value);
    }

    public bool IgnoreCache
    {
        get => _ignoreCache;
        set => SetProperty(ref _ignoreCache, value);
    }

    public bool AnalyzeSizeOnDisk
    {
        get => _analyzeSizeOnDisk;
        set
        {
            if (SetProperty(ref _analyzeSizeOnDisk, value))
            {
                if (value && _approximateSizeOnDisk)
                {
                    _approximateSizeOnDisk = false;
                    OnPropertyChanged(nameof(ApproximateSizeOnDisk));
                }

                ApplySelectedSizeModeWhenIdle();
            }
        }
    }

    public bool ApproximateSizeOnDisk
    {
        get => _approximateSizeOnDisk;
        set
        {
            if (SetProperty(ref _approximateSizeOnDisk, value))
            {
                if (value && _analyzeSizeOnDisk)
                {
                    _analyzeSizeOnDisk = false;
                    OnPropertyChanged(nameof(AnalyzeSizeOnDisk));
                }

                ApplySelectedSizeModeWhenIdle();
            }
        }
    }

    public bool DisplayUsesSizeOnDisk => _displayedSizeCalculationMode != SizeCalculationMode.Logical;

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!SetProperty(ref _isScanning, value))
            {
                return;
            }

            StartScanCommand.RaiseCanExecuteChanged();
            CancelScanCommand.RaiseCanExecuteChanged();
            RefreshNodeCommand.RaiseCanExecuteChanged();
            ExcludeNodeCommand.RaiseCanExecuteChanged();
            ClearCacheCommand.RaiseCanExecuteChanged();
        }
    }

    public long ProcessedFiles
    {
        get => _processedFiles;
        set
        {
            if (SetProperty(ref _processedFiles, value))
            {
                OnPropertyChanged(nameof(ProcessedFilesText));
            }
        }
    }

    public long ProcessedDirectories
    {
        get => _processedDirectories;
        set
        {
            if (SetProperty(ref _processedDirectories, value))
            {
                OnPropertyChanged(nameof(ProcessedDirectoriesText));
            }
        }
    }

    public long LogicalBytes
    {
        get => _logicalBytes;
        set
        {
            if (SetProperty(ref _logicalBytes, value))
            {
                OnPropertyChanged(nameof(LogicalBytesText));
            }
        }
    }

    public long SizeOnDiskBytes
    {
        get => _sizeOnDiskBytes;
        set
        {
            if (SetProperty(ref _sizeOnDiskBytes, value))
            {
                OnPropertyChanged(nameof(SizeOnDiskBytesText));
            }
        }
    }

    public bool HasSelectedNode => SelectedNode is not null;

    public string ProcessedFilesText => ProcessedFiles.ToString("N0");

    public string ProcessedDirectoriesText => ProcessedDirectories.ToString("N0");

    public string LogicalBytesText => FileSizeFormatter.Format(LogicalBytes);

    public string SizeOnDiskBytesText => DisplayUsesSizeOnDisk
        ? FileSizeFormatter.Format(SizeOnDiskBytes)
        : "Н/Д";

    private SizeCalculationMode SelectedSizeCalculationMode => AnalyzeSizeOnDisk
        ? SizeCalculationMode.Exact
        : ApproximateSizeOnDisk
            ? SizeCalculationMode.Approximate
            : SizeCalculationMode.Logical;

    private void ApplySelectedSizeModeWhenIdle()
    {
        if (IsScanning || Roots.Count == 0)
        {
            return;
        }

        StatusSummary = SelectedSizeCalculationMode == _displayedSizeCalculationMode
            ? "Показан текущий результат анализа"
            : "Режим изменен. Он будет применен при следующем запуске анализа.";
    }

    private void SetDisplayedSizeCalculationMode(SizeCalculationMode mode)
    {
        if (_displayedSizeCalculationMode == mode)
        {
            return;
        }

        _displayedSizeCalculationMode = mode;
        OnPropertyChanged(nameof(DisplayUsesSizeOnDisk));
        OnPropertyChanged(nameof(SizeOnDiskBytesText));
        QueueChartRefresh();
    }

    public string SelectedNodeSafetyText => SelectedNode?.Risk switch
    {
        RiskLevel.Dangerous => "Это системная область. Удаление файлов отсюда может повредить Windows.",
        RiskLevel.System => "Системная область: просматривайте осторожно и не удаляйте файлы без уверенности.",
        _ => ""
    };

    private void LoadDrives()
    {
        RefreshAvailableDrives(selectFirst: true);
    }

    private void RefreshAvailableDrives(bool selectFirst)
    {
        var drives = _driveInfoService.GetReadyDrives();
        var selectedRoot = SelectedDrive?.RootPath;
        var existingRoots = AvailableDrives.Select(drive => drive.RootPath).ToList();
        var newRoots = drives.Select(drive => drive.RootPath).ToList();

        if (existingRoots.Count == newRoots.Count &&
            existingRoots.Zip(newRoots).All(pair => string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        AvailableDrives.Clear();
        foreach (var drive in drives)
        {
            AvailableDrives.Add(drive);
        }

        var nextSelection = !string.IsNullOrWhiteSpace(selectedRoot)
            ? AvailableDrives.FirstOrDefault(drive => string.Equals(drive.RootPath, selectedRoot, StringComparison.OrdinalIgnoreCase))
            : null;

        if (nextSelection is not null)
        {
            _selectedDrive = nextSelection;
            OnPropertyChanged(nameof(SelectedDrive));
            return;
        }

        SelectedDrive = selectFirst ? AvailableDrives.FirstOrDefault() : null;
    }

    private bool TryShowCachedScan(
        string path,
        bool showStaleWarning = true,
        SizeCalculationMode? sizeCalculationMode = null)
    {
        var mode = sizeCalculationMode ?? SelectedSizeCalculationMode;
        if (!_cache.TryRestoreSnapshot(path, mode, out var cached) || cached is null)
        {
            return false;
        }

        SetDisplayedSizeCalculationMode(mode);
        ClearAnalysisNodes();

        cached.IsExpanded = true;
        Roots.Add(cached);
        SelectedNode = cached;
        SelectedChartNode = null;

        ProcessedFiles = cached.Kind == FileSystemItemKind.File ? 1 : cached.FileCount;
        ProcessedDirectories = cached.Kind == FileSystemItemKind.Folder ? 1 + cached.DirectoryCount : cached.DirectoryCount;
        LogicalBytes = cached.LogicalSize;
        SizeOnDiskBytes = cached.SizeOnDisk;
        CurrentPath = cached.FullPath;

        if (showStaleWarning)
        {
            ApplyCacheWarning(cached.FullPath);
        }
        else
        {
            CacheWarningText = "";
        }
        RebuildSearchResults();
        QueueChartRefresh();
        return true;
    }

    private void ClearAnalysisView(string statusSummary)
    {
        ClearAnalysisNodes();
        ResetProgress();
        CacheWarningText = "";
        StatusSummary = statusSummary;
    }

    private async Task StartScanAsync()
    {
        if (SelectedDrive is null)
        {
            StatusSummary = "Выберите диск для анализа";
            return;
        }

        if (IncludeSystemDirectories && !ConfirmFullSystemScan())
        {
            IncludeSystemDirectories = false;
            return;
        }

        IsScanning = true;
        CacheWarningText = "";
        _scanCancellation = new CancellationTokenSource();

        var options = new ScanOptions
        {
            IncludeSystemDirectories = IncludeSystemDirectories,
            IgnoreCache = IgnoreCache,
            SizeCalculationMode = SelectedSizeCalculationMode,
            ExcludedPaths = _excludedPaths.ToList()
        };
        var targetPath = SelectedDrive.RootPath;
        var preserveCurrentView = Roots.Count > 0;
        var previousFiles = ProcessedFiles;
        var previousDirectories = ProcessedDirectories;
        var previousLogicalBytes = LogicalBytes;
        var previousSizeOnDiskBytes = SizeOnDiskBytes;

        if (!preserveCurrentView)
        {
            ResetProgress();
            ClearAnalysisNodes();
            SetDisplayedSizeCalculationMode(options.SizeCalculationMode);
        }

        var shouldReleaseMemory = false;
        try
        {
            shouldReleaseMemory = await RunScanAndDisplayAsync(
                targetPath,
                options,
                _scanCancellation.Token,
                preserveCurrentView);

            StatusSummary = _scanCancellation.Token.IsCancellationRequested
                ? "Анализ отменен"
                : "Анализ завершен";
        }
        catch (OperationCanceledException)
        {
            StatusSummary = "Анализ отменен";
        }
        catch (Exception ex)
        {
            StatusSummary = $"Не удалось завершить анализ: {ex.Message}";
        }
        finally
        {
            if (_scanCancellation.Token.IsCancellationRequested && preserveCurrentView)
            {
                ProcessedFiles = previousFiles;
                ProcessedDirectories = previousDirectories;
                LogicalBytes = previousLogicalBytes;
                SizeOnDiskBytes = previousSizeOnDiskBytes;
            }

            CurrentPath = "";
            IsScanning = false;
            _scanCancellation?.Dispose();
            _scanCancellation = null;
        }

        if (shouldReleaseMemory)
        {
            await ReleaseUnusedMemoryAsync();
        }
    }

    private async Task<bool> RunScanAndDisplayAsync(
        string targetPath,
        ScanOptions options,
        CancellationToken cancellationToken,
        bool preserveCurrentView)
    {
        var root = CreatePlaceholderRoot(targetPath);
        if (!preserveCurrentView)
        {
            Roots.Add(root);
            SelectedNode = root;
        }

        StatusSummary = $"Анализ: {targetPath}";

        var progress = new Progress<ScanProgressInfo>(info => ApplyProgress(info, root));
        var result = await _scanner.ScanAsync(targetPath, options, progress, cancellationToken);

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (!cancellationToken.IsCancellationRequested &&
            TryShowCachedScan(targetPath, showStaleWarning: false, options.SizeCalculationMode))
        {
            return true;
        }

        if (preserveCurrentView)
        {
            ClearAnalysisNodes();
            Roots.Add(root);
            SelectedNode = root;
        }

        SetDisplayedSizeCalculationMode(options.SizeCalculationMode);
        ApplyRootResult(root, result);
        ApplyCacheWarningIfNeeded(result.FullPath, result.StatusText);
        RebuildSearchResults();
        return false;
    }

    private async Task RefreshNodeAsync(ScanNode? node)
    {
        if (node is null)
        {
            return;
        }

        var options = new ScanOptions
        {
            IncludeSystemDirectories = IncludeSystemDirectories,
            IgnoreCache = true,
            SizeCalculationMode = SelectedSizeCalculationMode,
            ExcludedPaths = _excludedPaths.ToList()
        };
        SetDisplayedSizeCalculationMode(options.SizeCalculationMode);

        node.IsRefreshing = true;
        IsScanning = true;
        ResetProgress();
        CacheWarningText = "";
        _scanCancellation = new CancellationTokenSource();

        var shouldReleaseMemory = false;
        try
        {
            shouldReleaseMemory = await RunRefreshAndDisplayAsync(
                node,
                options,
                _scanCancellation.Token);

            StatusSummary = _scanCancellation.Token.IsCancellationRequested
                ? "Обновление отменено"
                : $"Обновлено: {node.FullPath}";
        }
        catch (OperationCanceledException)
        {
            StatusSummary = "Обновление отменено";
        }
        catch (Exception ex)
        {
            StatusSummary = $"Не удалось обновить папку: {ex.Message}";
        }
        finally
        {
            node.IsRefreshing = false;
            CurrentPath = "";
            IsScanning = false;
            _scanCancellation?.Dispose();
            _scanCancellation = null;
        }

        if (shouldReleaseMemory)
        {
            await ReleaseUnusedMemoryAsync();
        }
    }

    private async Task<bool> RunRefreshAndDisplayAsync(
        ScanNode node,
        ScanOptions options,
        CancellationToken cancellationToken)
    {
        var progress = new Progress<ScanProgressInfo>(info => ApplyProgress(info, node));
        var result = await _scanner.ScanAsync(node.FullPath, options, progress, cancellationToken);

        if (!cancellationToken.IsCancellationRequested &&
            _cache.TryRestoreSnapshot(node.FullPath, options.SizeCalculationMode, out var cached) &&
            cached is not null)
        {
            ReplaceNode(node, cached);
            return true;
        }

        ReplaceNode(node, result);
        return false;
    }

    private void CancelScan()
    {
        _scanCancellation?.Cancel();
    }

    private void ApplyProgress(ScanProgressInfo info, ScanNode root)
    {
        if (!string.IsNullOrWhiteSpace(info.CurrentPath))
        {
            CurrentPath = info.CurrentPath;
        }

        ProcessedFiles = info.ProcessedFiles;
        ProcessedDirectories = info.ProcessedDirectories;
        LogicalBytes = info.LogicalBytes;
        SizeOnDiskBytes = info.SizeOnDiskBytes;

        if (!string.IsNullOrWhiteSpace(info.Message))
        {
            StatusSummary = info.Message;
        }

        if (info.CompletedRootChild is not null && !root.Children.Contains(info.CompletedRootChild))
        {
            root.AddChild(info.CompletedRootChild);
            root.LogicalSize += info.CompletedRootChild.LogicalSize;
            root.SizeOnDisk += info.CompletedRootChild.SizeOnDisk;
            if (SearchQuery.Length >= 2)
            {
                RebuildSearchResults();
            }
        }
    }

    private void ApplyRootResult(ScanNode root, ScanNode result)
    {
        root.FullPath = result.FullPath;
        root.Name = result.Name;
        root.Kind = result.Kind;
        root.ModifiedAt = result.ModifiedAt;
        root.FileId = result.FileId;
        root.Risk = result.Risk;
        root.StatusText = result.StatusText;
        root.LogicalSize = result.LogicalSize;
        root.SizeOnDisk = result.SizeOnDisk;
        root.FileCount = result.FileCount;
        root.DirectoryCount = result.DirectoryCount;

        if (root.ChildCount == 0)
        {
            foreach (var child in result.ExistingChildren)
            {
                root.AddChild(child);
            }
        }
        else
        {
            root.SortChildrenBySize();
        }

        root.IsExpanded = true;
        if (SelectedNode == root)
        {
            QueueChartRefresh();
        }
    }

    private void ApplyCacheWarningIfNeeded(string path, string statusText)
    {
        if (statusText.Contains("кеш", StringComparison.OrdinalIgnoreCase) ||
            statusText.Contains("кэш", StringComparison.OrdinalIgnoreCase))
        {
            ApplyCacheWarning(path);
        }
    }

    private void ApplyCacheWarning(string path)
    {
        StatusSummary = $"Загружен кэшированный скан: {path}";
        CacheWarningText = "Показан кэшированный скан. Данные могли устареть; для точного результата включите «Игнорировать кэш при сканировании» и запустите сканирование заново.";
    }

    private void ReplaceNode(ScanNode oldNode, ScanNode newNode)
    {
        var parent = oldNode.Parent;
        if (parent is null)
        {
            var index = Roots.IndexOf(oldNode);
            if (index >= 0)
            {
                Roots[index] = newNode;
            }
        }
        else
        {
            var index = parent.Children.IndexOf(oldNode);
            if (index >= 0)
            {
                newNode.Parent = parent;
                parent.Children[index] = newNode;
            }
        }

        _navigationHistory.Clear();
        NavigateBackCommand.RaiseCanExecuteChanged();
        SelectedChartNode = null;
        SelectedNode = newNode;
        RebuildSearchResults();
    }

    private void ExcludeNode(ScanNode? node)
    {
        if (node is null)
        {
            return;
        }

        if (!_excludedPaths.Contains(node.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            _excludedPaths.Add(node.FullPath);
        }

        var parent = node.Parent;
        if (parent is null)
        {
            Roots.Remove(node);
        }
        else
        {
            parent.Children.Remove(node);
        }

        _navigationHistory.Clear();
        NavigateBackCommand.RaiseCanExecuteChanged();
        SelectedChartNode = null;
        SelectedNode = parent ?? Roots.FirstOrDefault();
        StatusSummary = $"Исключено из анализа: {node.FullPath}";
        RebuildSearchResults();
    }

    private void ShowDetails(ScanNode? node)
    {
        if (node is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(node.FileId))
        {
            node.FileId = SystemInterop.GetFileId(node.FullPath);
        }

        var window = new NodeDetailsWindow
        {
            Owner = System.Windows.Application.Current.MainWindow,
            DataContext = node
        };
        window.ShowDialog();
    }

    private void ClearCache()
    {
        _cache.Clear();
        CacheWarningText = "";
        StatusSummary = "Кэш анализа очищен";
    }

    private static void ExecuteNodeAction(object? parameter, Action<ScanNode> action)
    {
        if (parameter is ScanNode node)
        {
            action(node);
        }
    }

    private void NavigateToNode(ScanNode? node, bool rememberCurrent)
    {
        if (node is null)
        {
            return;
        }

        if (rememberCurrent && SelectedNode is not null && SelectedNode != node)
        {
            _navigationHistory.Push(SelectedNode);
        }

        SelectedNode = node;
        node.IsSelected = true;
        SelectedChartNode = null;
        NavigateBackCommand.RaiseCanExecuteChanged();
    }

    private void NavigateBack()
    {
        while (_navigationHistory.Count > 0)
        {
            var node = _navigationHistory.Pop();
            if (node == SelectedNode)
            {
                continue;
            }

            SelectedNode = node;
            node.IsSelected = true;
            SelectedChartNode = null;
            break;
        }

        NavigateBackCommand.RaiseCanExecuteChanged();
    }

    private void SelectedChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        QueueChartRefresh();
    }

    private void RootsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateFlatNodeSubscriptions(e);
        RebuildFlatNodes();
    }

    private void FlatNodeChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateFlatNodeSubscriptions(e);
        if (_subscribedFlatNodes.Any(node =>
                node.IsExpanded &&
                ReferenceEquals(node.CreatedChildren, sender)))
        {
            RebuildFlatNodes();
        }
    }

    private void FlatNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScanNode.IsExpanded))
        {
            if (sender is ScanNode { IsExpanded: true } node)
            {
                EnsureCachedChildrenLoaded(node);
                AttachCreatedChildren(node);
            }
            else if (sender is ScanNode collapsedNode)
            {
                ReleaseCachedChildren(collapsedNode);
            }

            RebuildFlatNodes();
        }
    }

    private void UpdateFlatNodeSubscriptions(NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ResetFlatNodeSubscriptions();
            return;
        }

        if (e.OldItems is not null)
        {
            foreach (ScanNode node in e.OldItems)
            {
                UnsubscribeFlatNode(node);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (ScanNode node in e.NewItems)
            {
                SubscribeFlatNode(node);
            }
        }
    }

    private void ResetFlatNodeSubscriptions()
    {
        foreach (var node in _subscribedFlatNodes.ToList())
        {
            node.PropertyChanged -= FlatNodePropertyChanged;
            if (node.CreatedChildren is { } children)
            {
                children.CollectionChanged -= FlatNodeChildrenChanged;
            }
        }

        _subscribedFlatNodes.Clear();
        foreach (var root in Roots)
        {
            SubscribeFlatNode(root);
        }
    }

    private void SubscribeFlatNode(ScanNode node)
    {
        if (node.Kind != FileSystemItemKind.Folder)
        {
            return;
        }

        if (!_subscribedFlatNodes.Add(node))
        {
            return;
        }

        node.PropertyChanged += FlatNodePropertyChanged;
        AttachCreatedChildren(node);
    }

    private void UnsubscribeFlatNode(ScanNode node)
    {
        if (!_subscribedFlatNodes.Remove(node))
        {
            return;
        }

        node.PropertyChanged -= FlatNodePropertyChanged;
        if (node.CreatedChildren is not { } children)
        {
            return;
        }

        children.CollectionChanged -= FlatNodeChildrenChanged;
        foreach (var child in children)
        {
            UnsubscribeFlatNode(child);
        }
    }

    private void AttachCreatedChildren(ScanNode node)
    {
        if (node.CreatedChildren is not { } children)
        {
            return;
        }

        children.CollectionChanged -= FlatNodeChildrenChanged;
        children.CollectionChanged += FlatNodeChildrenChanged;
        foreach (var child in children)
        {
            SubscribeFlatNode(child);
        }
    }

    private void RebuildFlatNodes()
    {
        FlatNodes.Clear();
        foreach (var root in Roots)
        {
            AppendVisible(root);
        }
    }

    private void AppendVisible(ScanNode node)
    {
        FlatNodes.Add(node);
        if (!node.IsExpanded)
        {
            return;
        }

        foreach (var child in node.ExistingChildren)
        {
            AppendVisible(child);
        }
    }

    private void QueueChartRefresh()
    {
        if (_chartRefreshQueued)
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            RefreshChartChildren();
            return;
        }

        _chartRefreshQueued = true;
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _chartRefreshQueued = false;
            RefreshChartChildren();
        }));
    }

    private void ClearChartChildren()
    {
        _isRefreshingChartChildren = true;
        try
        {
            ChartChildren.Clear();
        }
        finally
        {
            _isRefreshingChartChildren = false;
        }
    }

    private void RefreshChartChildren()
    {
        List<ScanNode> children = SelectedNode is null
            ? []
            : SelectedNode.ExistingChildren
                .OrderByDescending(node => DisplayUsesSizeOnDisk ? node.SizeOnDisk : node.LogicalSize)
                .Take(20)
                .ToList();

        _isRefreshingChartChildren = true;
        try
        {
            ChartChildren.Clear();
            foreach (var child in children)
            {
                ChartChildren.Add(child);
            }
        }
        finally
        {
            _isRefreshingChartChildren = false;
        }

        if (SelectedChartNode is not null && !children.Contains(SelectedChartNode))
        {
            SelectedChartNode = null;
        }

        OnPropertyChanged(nameof(SelectedNodeSafetyText));
    }

    private async void RebuildSearchResults()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
        SearchResults.Clear();

        var query = SearchQuery.Trim();
        if (query.Length < 2)
        {
            return;
        }

        var cachedRoot = Roots.Count == 1 && Roots[0].IsCacheBacked ? Roots[0] : null;
        if (cachedRoot is not null)
        {
            var cancellation = new CancellationTokenSource();
            _searchCancellation = cancellation;

            try
            {
                await Task.Delay(150, cancellation.Token);
                var matches = await Task.Run(
                    () => _cache.SearchSnapshot(cachedRoot, query, 500, cancellation.Token),
                    cancellation.Token);

                cancellation.Token.ThrowIfCancellationRequested();
                foreach (var node in matches)
                {
                    SearchResults.Add(node);
                }
            }
            catch (OperationCanceledException)
            {
                // A newer query replaced this search.
            }
            finally
            {
                if (ReferenceEquals(_searchCancellation, cancellation))
                {
                    _searchCancellation = null;
                }

                cancellation.Dispose();
            }

            return;
        }

        foreach (var node in Roots.SelectMany(Flatten).Where(node =>
                     node.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     node.FullPath.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(500))
        {
            SearchResults.Add(node);
        }
    }

    private static IEnumerable<ScanNode> Flatten(ScanNode node)
    {
        yield return node;
        foreach (var child in node.ExistingChildren)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    private static ScanNode CreatePlaceholderRoot(string path)
    {
        var root = new ScanNode
        {
            FullPath = path,
            Name = Path.GetPathRoot(path) == path ? path : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            Kind = Directory.Exists(path) ? FileSystemItemKind.Folder : FileSystemItemKind.File,
            Risk = RiskLevel.Safe,
            StatusText = "Подготовка"
        };

        root.IsExpanded = true;
        return root;
    }

    private void ResetProgress()
    {
        ProcessedFiles = 0;
        ProcessedDirectories = 0;
        LogicalBytes = 0;
        SizeOnDiskBytes = 0;
        CurrentPath = "";
    }

    private void ClearAnalysisNodes()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
        SelectedNode = null;
        SelectedChartNode = null;
        _navigationHistory.Clear();
        NavigateBackCommand.RaiseCanExecuteChanged();
        Roots.Clear();
        SearchResults.Clear();
        ClearChartChildren();
    }

    private bool EnsureCachedChildrenLoaded(ScanNode node)
    {
        if (!node.HasUnloadedCachedChildren)
        {
            return true;
        }

        if (_cache.TryLoadChildren(node))
        {
            AttachCreatedChildren(node);
            if (node.IsExpanded)
            {
                RebuildFlatNodes();
            }

            return true;
        }

        StatusSummary = $"Не удалось загрузить папку из кеша: {node.FullPath}";
        return false;
    }

    private void ReleaseCachedChildren(ScanNode node)
    {
        if (!node.IsCacheBacked)
        {
            return;
        }

        if (SelectedNode is not null &&
            SelectedNode != node &&
            IsDescendantOf(SelectedNode, node))
        {
            SelectedNode = node;
        }

        _navigationHistory.Clear();
        NavigateBackCommand.RaiseCanExecuteChanged();
        SelectedChartNode = null;
        node.UnloadCachedChildren();
    }

    private static bool IsDescendantOf(ScanNode node, ScanNode ancestor)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task ReleaseUnusedMemoryAsync()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null)
        {
            await dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
        }

        await Task.Run(MemoryReleaseService.ReleaseUnusedMemory);
    }

    private static bool ConfirmFullSystemScan()
    {
        var result = System.Windows.MessageBox.Show(
            "Полный анализ системных каталогов может занять больше времени и показать файлы, которые не рекомендуется удалять вручную.",
            "Полный анализ",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        return result == MessageBoxResult.OK;
    }
}
