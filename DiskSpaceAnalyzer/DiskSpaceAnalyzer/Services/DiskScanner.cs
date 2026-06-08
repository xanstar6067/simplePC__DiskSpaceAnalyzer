using System.Diagnostics;
using System.IO;
using DiskSpaceAnalyzer.Models;

namespace DiskSpaceAnalyzer.Services;

public sealed class DiskScanner
{
    private readonly PathRiskClassifier _classifier;
    private readonly AnalysisCacheService _cache;

    public DiskScanner(PathRiskClassifier classifier, AnalysisCacheService cache)
    {
        _classifier = classifier;
        _cache = cache;
    }

    public Task<ScanNode> ScanAsync(
        string path,
        ScanOptions options,
        IProgress<ScanProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var normalized = PathRiskClassifier.Normalize(path);
            if (!options.IgnoreCache && _cache.TryRestoreSnapshot(normalized, options.AnalyzeSizeOnDisk, out var cached) && cached is not null)
            {
                AddCachedCounters(cached, out var files, out var directories, out var logical, out var onDisk);
                progress?.Report(new ScanProgressInfo
                {
                    CurrentPath = normalized,
                    ProcessedFiles = files,
                    ProcessedDirectories = directories,
                    LogicalBytes = logical,
                    SizeOnDiskBytes = onDisk,
                    Message = "Загружено из кеша"
                });
                return cached;
            }

            using var cacheWriter = _cache.BeginSnapshot(normalized, options.AnalyzeSizeOnDisk);
            var counters = new ScanCounters();
            var activeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var root = ScanEntry(
                normalized,
                fileSystemInfo: null,
                options,
                counters,
                progress,
                cancellationToken,
                isRootChild: true,
                activeDirectories,
                cacheWriter);
            root.IsExpanded = true;

            if (!cancellationToken.IsCancellationRequested && root.Risk is not RiskLevel.Skipped and not RiskLevel.NoAccess)
            {
                cacheWriter?.Commit(root);
            }

            return root;
        });
    }

    private ScanNode ScanEntry(
        string path,
        FileSystemInfo? fileSystemInfo,
        ScanOptions options,
        ScanCounters counters,
        IProgress<ScanProgressInfo>? progress,
        CancellationToken cancellationToken,
        bool isRootChild,
        HashSet<string> activeDirectories,
        AnalysisCacheService.SnapshotWriter? cacheWriter)
    {
        counters.CurrentPath = path;
        ReportProgress(counters, progress);

        if (cancellationToken.IsCancellationRequested)
        {
            return CreateCanceledNode(path);
        }

        try
        {
            var attributes = fileSystemInfo?.Attributes ?? File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint) &&
                (!options.IncludeSystemDirectories || !attributes.HasFlag(FileAttributes.Directory)))
            {
                var linkNode = CreateBaseNode(path, FileSystemItemKind.Link, _classifier.Classify(path), "Ссылка пропущена");
                FillMetadata(linkNode, fileSystemInfo);
                return linkNode;
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                return ScanDirectory(path, fileSystemInfo, options, counters, progress, cancellationToken, isRootChild, activeDirectories, cacheWriter);
            }

            return ScanFile(path, fileSystemInfo, options, counters);
        }
        catch (UnauthorizedAccessException)
        {
            counters.Directories++;
            return CreateBaseNode(path, FileSystemItemKind.NoAccess, RiskLevel.NoAccess, "Нет доступа");
        }
        catch (DirectoryNotFoundException)
        {
            return CreateBaseNode(path, FileSystemItemKind.NoAccess, RiskLevel.NoAccess, "Путь не найден");
        }
        catch (FileNotFoundException)
        {
            return CreateBaseNode(path, FileSystemItemKind.NoAccess, RiskLevel.NoAccess, "Файл не найден");
        }
        catch (IOException ex)
        {
            return CreateBaseNode(path, FileSystemItemKind.NoAccess, RiskLevel.NoAccess, ex.Message);
        }
    }

    private ScanNode ScanDirectory(
        string path,
        FileSystemInfo? fileSystemInfo,
        ScanOptions options,
        ScanCounters counters,
        IProgress<ScanProgressInfo>? progress,
        CancellationToken cancellationToken,
        bool isRootChild,
        HashSet<string> activeDirectories,
        AnalysisCacheService.SnapshotWriter? cacheWriter)
    {
        var decision = _classifier.Evaluate(path, options.IncludeSystemDirectories, options.ExcludedPaths);
        var node = CreateBaseNode(path, FileSystemItemKind.Folder, decision.Risk, decision.StatusText);
        FillMetadata(node, fileSystemInfo);

        if (decision.ShouldSkip)
        {
            node.Risk = RiskLevel.Skipped;
            node.StatusText = decision.StatusText;
            return node;
        }

        var directoryIdentity = GetDirectoryIdentity(path, fileSystemInfo);
        if (!activeDirectories.Add(directoryIdentity))
        {
            node.Kind = FileSystemItemKind.Link;
            node.StatusText = "Циклическая ссылка";
            return node;
        }

        try
        {
            return ScanDirectoryContents(node, path, options, counters, progress, cancellationToken, isRootChild, activeDirectories, cacheWriter);
        }
        finally
        {
            activeDirectories.Remove(directoryIdentity);
        }
    }

    private ScanNode ScanDirectoryContents(
        ScanNode node,
        string path,
        ScanOptions options,
        ScanCounters counters,
        IProgress<ScanProgressInfo>? progress,
        CancellationToken cancellationToken,
        bool isRootChild,
        HashSet<string> activeDirectories,
        AnalysisCacheService.SnapshotWriter? cacheWriter)
    {
        counters.Directories++;
        node.StatusText = options.IncludeSystemDirectories || !_classifier.IsWindowsRoot(path)
            ? "Сканируется"
            : "Частичный безопасный анализ";

        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = GetDirectoryEntries(path, options);
        }
        catch (UnauthorizedAccessException)
        {
            cacheWriter?.WriteDirectory(node);
            node.Kind = FileSystemItemKind.NoAccess;
            node.Risk = RiskLevel.NoAccess;
            node.StatusText = "Нет доступа";
            return node;
        }
        catch (IOException ex)
        {
            node.Kind = FileSystemItemKind.NoAccess;
            node.Risk = RiskLevel.NoAccess;
            node.StatusText = ex.Message;
            cacheWriter?.WriteDirectory(node);
            return node;
        }

        try
        {
            foreach (var childInfo in entries)
            {
                var childPath = childInfo.FullName;
                if (cancellationToken.IsCancellationRequested)
                {
                    node.StatusText = "Отменено";
                    break;
                }

                var child = ScanEntry(childPath, childInfo, options, counters, progress, cancellationToken, isRootChild: false, activeDirectories, cacheWriter);
                node.AddChild(child);
                Accumulate(node, child);

                if (isRootChild)
                {
                    progress?.Report(new ScanProgressInfo
                    {
                        CurrentPath = childPath,
                        ProcessedFiles = counters.Files,
                        ProcessedDirectories = counters.Directories,
                        LogicalBytes = counters.LogicalBytes,
                        SizeOnDiskBytes = counters.SizeOnDiskBytes,
                        CompletedRootChild = child
                    });
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            node.Kind = FileSystemItemKind.NoAccess;
            node.Risk = RiskLevel.NoAccess;
            cacheWriter?.WriteDirectory(node);
            node.StatusText = "Нет доступа";
            return node;
        }
        catch (IOException ex)
        {
            node.Kind = FileSystemItemKind.NoAccess;
            node.Risk = RiskLevel.NoAccess;
            node.StatusText = ex.Message;
            cacheWriter?.WriteDirectory(node);
            return node;
        }

        node.SortChildrenBySize();
        if (!cancellationToken.IsCancellationRequested)
        {
            cacheWriter?.WriteDirectory(node);
        }
        if (!cancellationToken.IsCancellationRequested)
        {
            node.StatusText = "Готово";
        }

        return node;
    }

    private static string GetDirectoryIdentity(string path, FileSystemInfo? fileSystemInfo)
    {
        try
        {
            var info = fileSystemInfo as DirectoryInfo ?? new DirectoryInfo(path);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            return PathRiskClassifier.Normalize(target?.FullName ?? info.FullName);
        }
        catch
        {
            return PathRiskClassifier.Normalize(path);
        }
    }

    private IEnumerable<FileSystemInfo> GetDirectoryEntries(string path, ScanOptions options)
    {
        if (!options.IncludeSystemDirectories && _classifier.IsWindowsRoot(path))
        {
            return _classifier.SafeWindowsChildren
                .Where(Directory.Exists)
                .Select(childPath => (FileSystemInfo)new DirectoryInfo(childPath));
        }

        return new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        });
    }

    private ScanNode ScanFile(string path, FileSystemInfo? fileSystemInfo, ScanOptions options, ScanCounters counters)
    {
        var node = CreateBaseNode(path, FileSystemItemKind.File, _classifier.Classify(path), "Готово");
        FillMetadata(node, fileSystemInfo);

        try
        {
            var info = fileSystemInfo as FileInfo ?? new FileInfo(path);
            node.LogicalSize = info.Length;
            node.SizeOnDisk = options.AnalyzeSizeOnDisk
                ? SystemInterop.GetSizeOnDisk(path, node.LogicalSize)
                : node.LogicalSize;
            node.ModifiedAt = info.LastWriteTime;
        }
        catch
        {
            node.Kind = FileSystemItemKind.NoAccess;
            node.Risk = RiskLevel.NoAccess;
            node.StatusText = "Нет доступа к метаданным";
        }

        counters.Files++;
        counters.LogicalBytes += node.LogicalSize;
        counters.SizeOnDiskBytes += node.SizeOnDisk;
        return node;
    }

    private static ScanNode CreateBaseNode(string path, FileSystemItemKind kind, RiskLevel risk, string status)
    {
        return new ScanNode
        {
            FullPath = path,
            Name = GetDisplayName(path),
            Kind = kind,
            Risk = risk,
            StatusText = status
        };
    }

    private static ScanNode CreateCanceledNode(string path)
    {
        return CreateBaseNode(
            path,
            Directory.Exists(path) ? FileSystemItemKind.Folder : FileSystemItemKind.File,
            RiskLevel.Skipped,
            "Отменено");
    }

    private static void FillMetadata(ScanNode node, FileSystemInfo? fileSystemInfo)
    {
        try
        {
            fileSystemInfo ??= Directory.Exists(node.FullPath)
                ? new DirectoryInfo(node.FullPath)
                : new FileInfo(node.FullPath);
            node.ModifiedAt = fileSystemInfo.LastWriteTime;
        }
        catch
        {
            node.ModifiedAt = null;
        }
    }

    private static void Accumulate(ScanNode parent, ScanNode child)
    {
        parent.LogicalSize += child.LogicalSize;
        parent.SizeOnDisk += child.SizeOnDisk;

        if (child.Kind == FileSystemItemKind.File || child.Kind == FileSystemItemKind.Link)
        {
            parent.FileCount++;
        }
        else if (child.Kind == FileSystemItemKind.Folder)
        {
            parent.DirectoryCount++;
        }

        parent.FileCount += child.FileCount;
        parent.DirectoryCount += child.DirectoryCount;
    }

    private static void AddCachedCounters(
        ScanNode node,
        out long files,
        out long directories,
        out long logical,
        out long onDisk)
    {
        files = node.Kind == FileSystemItemKind.File ? 1 : node.FileCount;
        directories = node.Kind == FileSystemItemKind.Folder ? 1 + node.DirectoryCount : node.DirectoryCount;
        logical = node.LogicalSize;
        onDisk = node.SizeOnDisk;
    }

    private static string GetDisplayName(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return root ?? path;
        }

        return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static void ReportProgress(ScanCounters counters, IProgress<ScanProgressInfo>? progress)
    {
        if (progress is null || counters.Stopwatch.ElapsedMilliseconds - counters.LastReportMs < 120)
        {
            return;
        }

        counters.LastReportMs = counters.Stopwatch.ElapsedMilliseconds;
        progress.Report(new ScanProgressInfo
        {
            CurrentPath = counters.CurrentPath,
            ProcessedFiles = counters.Files,
            ProcessedDirectories = counters.Directories,
            LogicalBytes = counters.LogicalBytes,
            SizeOnDiskBytes = counters.SizeOnDiskBytes
        });
    }

    private sealed class ScanCounters
    {
        public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();

        public long LastReportMs { get; set; } = -1000;

        public string CurrentPath { get; set; } = string.Empty;

        public long Files { get; set; }

        public long Directories { get; set; }

        public long LogicalBytes { get; set; }

        public long SizeOnDiskBytes { get; set; }
    }
}
