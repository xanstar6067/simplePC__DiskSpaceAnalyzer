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
            if (!options.IgnoreCache &&
                _cache.TryRestoreSnapshot(normalized, options.SizeCalculationMode, out var cached) &&
                cached is not null)
            {
                ReportCachedResult(cached, normalized, progress);
                return cached;
            }

            using var writer = _cache.BeginSnapshot(normalized, options.SizeCalculationMode);
            var counters = new ScanCounters();
            var activeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var root = ScanEntry(
                normalized,
                fileSystemInfo: null,
                parentId: null,
                options,
                counters,
                progress,
                cancellationToken,
                reportDirectChildren: true,
                activeDirectories,
                writer);

            cancellationToken.ThrowIfCancellationRequested();
            if (!writer.Commit(root.Id) ||
                !_cache.TryRestoreSnapshot(normalized, options.SizeCalculationMode, out var result) ||
                result is null)
            {
                throw new IOException("Не удалось сохранить результаты анализа в локальный кэш.");
            }

            result.IsExpanded = true;
            return result;
        }, cancellationToken);
    }

    private ScanAggregate ScanEntry(
        string path,
        FileSystemInfo? fileSystemInfo,
        long? parentId,
        ScanOptions options,
        ScanCounters counters,
        IProgress<ScanProgressInfo>? progress,
        CancellationToken cancellationToken,
        bool reportDirectChildren,
        HashSet<string> activeDirectories,
        AnalysisCacheService.SnapshotWriter writer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        counters.CurrentPath = path;
        ReportProgress(counters, progress);

        var nodeId = writer.NextNodeId();
        try
        {
            var attributes = fileSystemInfo?.Attributes ?? File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint) &&
                (!options.IncludeSystemDirectories || !attributes.HasFlag(FileAttributes.Directory)))
            {
                var link = new ScanAggregate(
                    nodeId,
                    FileSystemItemKind.Link,
                    _classifier.Classify(path),
                    "Ссылка пропущена",
                    ModifiedAt: ReadModifiedAt(fileSystemInfo, path));
                WriteNode(writer, link, parentId, path);
                return link;
            }

            return attributes.HasFlag(FileAttributes.Directory)
                ? ScanDirectory(
                    nodeId,
                    parentId,
                    path,
                    fileSystemInfo,
                    options,
                    counters,
                    progress,
                    cancellationToken,
                    reportDirectChildren,
                    activeDirectories,
                    writer)
                : ScanFile(nodeId, parentId, path, fileSystemInfo, options, counters, writer);
        }
        catch (UnauthorizedAccessException)
        {
            counters.Directories++;
            return WriteErrorNode(
                writer,
                nodeId,
                parentId,
                path,
                "Нет доступа",
                ReadModifiedAt(fileSystemInfo, path));
        }
        catch (DirectoryNotFoundException)
        {
            return WriteErrorNode(writer, nodeId, parentId, path, "Путь не найден");
        }
        catch (FileNotFoundException)
        {
            return WriteErrorNode(writer, nodeId, parentId, path, "Файл не найден");
        }
        catch (IOException ex)
        {
            return WriteErrorNode(writer, nodeId, parentId, path, ex.Message);
        }
    }

    private ScanAggregate ScanDirectory(
        long nodeId,
        long? parentId,
        string path,
        FileSystemInfo? fileSystemInfo,
        ScanOptions options,
        ScanCounters counters,
        IProgress<ScanProgressInfo>? progress,
        CancellationToken cancellationToken,
        bool reportDirectChildren,
        HashSet<string> activeDirectories,
        AnalysisCacheService.SnapshotWriter writer)
    {
        var decision = _classifier.Evaluate(path, options.IncludeSystemDirectories, options.ExcludedPaths);
        var modifiedAt = ReadModifiedAt(fileSystemInfo, path);
        var ownSizeOnDisk = options.SizeCalculationMode == SizeCalculationMode.Exact
            ? SystemInterop.GetExactSizeOnDisk(path, 0, isDirectory: true)
            : 0;

        if (decision.ShouldSkip)
        {
            var skipped = new ScanAggregate(
                nodeId,
                FileSystemItemKind.Folder,
                RiskLevel.Skipped,
                decision.StatusText,
                SizeOnDisk: ownSizeOnDisk,
                ModifiedAt: modifiedAt);
            WriteNode(writer, skipped, parentId, path);
            return skipped;
        }

        var directoryIdentity = GetDirectoryIdentity(path, fileSystemInfo);
        if (!activeDirectories.Add(directoryIdentity))
        {
            var link = new ScanAggregate(
                nodeId,
                FileSystemItemKind.Link,
                decision.Risk,
                "Циклическая ссылка",
                SizeOnDisk: ownSizeOnDisk,
                ModifiedAt: modifiedAt);
            WriteNode(writer, link, parentId, path);
            return link;
        }

        try
        {
            return ScanDirectoryContents(
                nodeId,
                parentId,
                path,
                decision.Risk,
                modifiedAt,
                ownSizeOnDisk,
                options,
                counters,
                progress,
                cancellationToken,
                reportDirectChildren,
                activeDirectories,
                writer);
        }
        finally
        {
            activeDirectories.Remove(directoryIdentity);
        }
    }

    private ScanAggregate ScanDirectoryContents(
        long nodeId,
        long? parentId,
        string path,
        RiskLevel risk,
        DateTimeOffset? modifiedAt,
        long ownSizeOnDisk,
        ScanOptions options,
        ScanCounters counters,
        IProgress<ScanProgressInfo>? progress,
        CancellationToken cancellationToken,
        bool reportDirectChildren,
        HashSet<string> activeDirectories,
        AnalysisCacheService.SnapshotWriter writer)
    {
        counters.Directories++;
        counters.SizeOnDiskBytes += ownSizeOnDisk;

        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = GetDirectoryEntries(path, options);
        }
        catch (UnauthorizedAccessException)
        {
            return WriteErrorNode(writer, nodeId, parentId, path, "Нет доступа", modifiedAt);
        }
        catch (IOException ex)
        {
            return WriteErrorNode(writer, nodeId, parentId, path, ex.Message, modifiedAt);
        }

        var aggregate = new ScanAggregate(
            nodeId,
            FileSystemItemKind.Folder,
            risk,
            "Готово",
            SizeOnDisk: ownSizeOnDisk,
            ModifiedAt: modifiedAt);

        try
        {
            foreach (var childInfo in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var child = ScanEntry(
                    childInfo.FullName,
                    childInfo,
                    nodeId,
                    options,
                    counters,
                    progress,
                    cancellationToken,
                    reportDirectChildren: false,
                    activeDirectories,
                    writer);
                aggregate = Accumulate(aggregate, child);

                if (reportDirectChildren)
                {
                    progress?.Report(new ScanProgressInfo
                    {
                        CurrentPath = childInfo.FullName,
                        ProcessedFiles = counters.Files,
                        ProcessedDirectories = counters.Directories,
                        LogicalBytes = counters.LogicalBytes,
                        SizeOnDiskBytes = counters.SizeOnDiskBytes,
                        CompletedRootChild = ToProgressNode(childInfo.FullName, child)
                    });
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            aggregate = aggregate with
            {
                Kind = FileSystemItemKind.NoAccess,
                Risk = RiskLevel.NoAccess,
                StatusText = "Нет доступа"
            };
        }
        catch (IOException ex)
        {
            aggregate = aggregate with
            {
                Kind = FileSystemItemKind.NoAccess,
                Risk = RiskLevel.NoAccess,
                StatusText = ex.Message
            };
        }

        WriteNode(writer, aggregate, parentId, path);
        return aggregate;
    }

    private ScanAggregate ScanFile(
        long nodeId,
        long? parentId,
        string path,
        FileSystemInfo? fileSystemInfo,
        ScanOptions options,
        ScanCounters counters,
        AnalysisCacheService.SnapshotWriter writer)
    {
        var kind = FileSystemItemKind.File;
        var risk = _classifier.Classify(path);
        var status = "Готово";
        var logicalSize = 0L;
        var sizeOnDisk = 0L;
        DateTimeOffset? modifiedAt = null;

        try
        {
            var info = fileSystemInfo as FileInfo ?? new FileInfo(path);
            logicalSize = info.Length;
            sizeOnDisk = options.SizeCalculationMode switch
            {
                SizeCalculationMode.Exact => SystemInterop.GetExactSizeOnDisk(path, logicalSize),
                SizeCalculationMode.Approximate => SystemInterop.GetApproximateSizeOnDisk(path, logicalSize),
                _ => logicalSize
            };
            modifiedAt = info.LastWriteTime;
        }
        catch
        {
            kind = FileSystemItemKind.NoAccess;
            risk = RiskLevel.NoAccess;
            status = "Нет доступа к метаданным";
        }

        counters.Files++;
        counters.LogicalBytes += logicalSize;
        counters.SizeOnDiskBytes += sizeOnDisk;

        var result = new ScanAggregate(
            nodeId,
            kind,
            risk,
            status,
            logicalSize,
            sizeOnDisk,
            ModifiedAt: modifiedAt);
        WriteNode(writer, result, parentId, path);
        return result;
    }

    private static ScanAggregate WriteErrorNode(
        AnalysisCacheService.SnapshotWriter writer,
        long nodeId,
        long? parentId,
        string path,
        string status,
        DateTimeOffset? modifiedAt = null)
    {
        var result = new ScanAggregate(
            nodeId,
            FileSystemItemKind.NoAccess,
            RiskLevel.NoAccess,
            status,
            ModifiedAt: modifiedAt);
        WriteNode(writer, result, parentId, path);
        return result;
    }

    private static void WriteNode(
        AnalysisCacheService.SnapshotWriter writer,
        ScanAggregate node,
        long? parentId,
        string path)
    {
        writer.WriteNode(new AnalysisCacheService.NodeRecord(
            node.Id,
            parentId,
            path,
            GetDisplayName(path),
            node.Kind,
            node.Risk,
            node.StatusText,
            node.LogicalSize,
            node.SizeOnDisk,
            node.FileCount,
            node.DirectoryCount,
            node.ChildCount,
            node.ModifiedAt,
            string.Empty));
    }

    private static ScanAggregate Accumulate(ScanAggregate parent, ScanAggregate child)
    {
        var directFiles = child.Kind is FileSystemItemKind.File or FileSystemItemKind.Link ? 1 : 0;
        var directDirectories = child.Kind == FileSystemItemKind.Folder ? 1 : 0;
        return parent with
        {
            LogicalSize = parent.LogicalSize + child.LogicalSize,
            SizeOnDisk = parent.SizeOnDisk + child.SizeOnDisk,
            FileCount = parent.FileCount + child.FileCount + directFiles,
            DirectoryCount = parent.DirectoryCount + child.DirectoryCount + directDirectories,
            ChildCount = parent.ChildCount + 1
        };
    }

    private static ScanNode ToProgressNode(string path, ScanAggregate aggregate)
    {
        return new ScanNode
        {
            FullPath = path,
            Name = GetDisplayName(path),
            Kind = aggregate.Kind,
            Risk = aggregate.Risk,
            StatusText = aggregate.StatusText,
            LogicalSize = aggregate.LogicalSize,
            SizeOnDisk = aggregate.SizeOnDisk,
            FileCount = aggregate.FileCount,
            DirectoryCount = aggregate.DirectoryCount,
            ModifiedAt = aggregate.ModifiedAt
        };
    }

    private static DateTimeOffset? ReadModifiedAt(FileSystemInfo? fileSystemInfo, string path)
    {
        try
        {
            fileSystemInfo ??= Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path);
            return fileSystemInfo.LastWriteTime;
        }
        catch
        {
            return null;
        }
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

    private static string GetDisplayName(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return root ?? path;
        }

        return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static void ReportCachedResult(
        ScanNode cached,
        string normalized,
        IProgress<ScanProgressInfo>? progress)
    {
        progress?.Report(new ScanProgressInfo
        {
            CurrentPath = normalized,
            ProcessedFiles = cached.Kind == FileSystemItemKind.File ? 1 : cached.FileCount,
            ProcessedDirectories = cached.Kind == FileSystemItemKind.Folder ? 1 + cached.DirectoryCount : cached.DirectoryCount,
            LogicalBytes = cached.LogicalSize,
            SizeOnDiskBytes = cached.SizeOnDisk,
            Message = "Загружено из кэша"
        });
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

    private readonly record struct ScanAggregate(
        long Id,
        FileSystemItemKind Kind,
        RiskLevel Risk,
        string StatusText,
        long LogicalSize = 0,
        long SizeOnDisk = 0,
        long FileCount = 0,
        long DirectoryCount = 0,
        int ChildCount = 0,
        DateTimeOffset? ModifiedAt = null);

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
