using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiskSpaceAnalyzer.Models;

namespace DiskSpaceAnalyzer.Services;

public sealed class AnalysisCacheService
{
    private const int MaxCachedNodesPerSnapshot = 50000;
    private readonly string _cachePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };
    private Dictionary<string, CachedSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public AnalysisCacheService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var cacheDirectory = Path.Combine(appData, "simplePC DiskSpaceAnalyzer");
        Directory.CreateDirectory(cacheDirectory);
        _cachePath = Path.Combine(cacheDirectory, "analysis-cache.json");
    }

    public bool TryRestoreSnapshot(string path, bool analyzeSizeOnDisk, out ScanNode? node)
    {
        EnsureLoaded();
        node = null;

        var normalized = PathRiskClassifier.Normalize(path);
        if (!_snapshots.TryGetValue(normalized, out var snapshot))
        {
            return false;
        }

        if (snapshot.AnalyzeSizeOnDisk.GetValueOrDefault(true) != analyzeSizeOnDisk ||
            !MatchesCurrentMetadata(snapshot))
        {
            return false;
        }

        node = ToNode(snapshot.Node, null);
        node.StatusText = $"Из кеша: {snapshot.CachedAt.LocalDateTime:g}";
        return true;
    }

    public void StoreSnapshot(ScanNode node, bool analyzeSizeOnDisk)
    {
        EnsureLoaded();

        if (CountNodes(node) > MaxCachedNodesPerSnapshot)
        {
            return;
        }

        var snapshot = new CachedSnapshot
        {
            Path = PathRiskClassifier.Normalize(node.FullPath),
            CachedAt = DateTimeOffset.UtcNow,
            AnalyzeSizeOnDisk = analyzeSizeOnDisk,
            Node = FromNode(node),
            Metadata = ReadMetadata(node.FullPath)
        };

        _snapshots[snapshot.Path] = snapshot;
        Save();
    }

    public void Clear()
    {
        EnsureLoaded();
        _snapshots.Clear();

        try
        {
            if (File.Exists(_cachePath))
            {
                File.Delete(_cachePath);
            }
        }
        catch
        {
            Save();
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (!File.Exists(_cachePath))
        {
            return;
        }

        try
        {
            var text = File.ReadAllText(_cachePath);
            _snapshots = JsonSerializer.Deserialize<Dictionary<string, CachedSnapshot>>(text, _jsonOptions) ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _snapshots = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        try
        {
            var text = JsonSerializer.Serialize(_snapshots, _jsonOptions);
            File.WriteAllText(_cachePath, text);
        }
        catch
        {
            // Cache failures should not affect scanning.
        }
    }

    private static bool MatchesCurrentMetadata(CachedSnapshot snapshot)
    {
        var current = ReadMetadata(snapshot.Path);
        return current.Exists &&
               current.Length == snapshot.Metadata.Length &&
               current.LastWriteUtc == snapshot.Metadata.LastWriteUtc &&
               current.Attributes == snapshot.Metadata.Attributes &&
               (string.IsNullOrWhiteSpace(snapshot.Metadata.FileId) || current.FileId == snapshot.Metadata.FileId);
    }

    private static CachedMetadata ReadMetadata(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            var length = isDirectory ? 0 : ((FileInfo)info).Length;
            return new CachedMetadata
            {
                Exists = true,
                Length = length,
                Attributes = attributes,
                LastWriteUtc = info.LastWriteTimeUtc,
                FileId = SystemInterop.GetFileId(path)
            };
        }
        catch
        {
            return new CachedMetadata { Exists = false };
        }
    }

    private static int CountNodes(ScanNode node)
    {
        var count = 1;
        foreach (var child in node.Children)
        {
            count += CountNodes(child);
            if (count > MaxCachedNodesPerSnapshot)
            {
                return count;
            }
        }

        return count;
    }

    private static CachedScanNode FromNode(ScanNode node)
    {
        return new CachedScanNode
        {
            FullPath = node.FullPath,
            Name = node.Name,
            Kind = node.Kind,
            Risk = node.Risk,
            StatusText = node.StatusText,
            LogicalSize = node.LogicalSize,
            SizeOnDisk = node.SizeOnDisk,
            FileCount = node.FileCount,
            DirectoryCount = node.DirectoryCount,
            ModifiedAt = node.ModifiedAt,
            FileId = node.FileId,
            Children = node.Children.Select(FromNode).ToList()
        };
    }

    private static ScanNode ToNode(CachedScanNode cached, ScanNode? parent)
    {
        var node = new ScanNode
        {
            FullPath = cached.FullPath,
            Name = cached.Name,
            Kind = cached.Kind,
            ModifiedAt = cached.ModifiedAt,
            FileId = cached.FileId,
            Parent = parent
        };

        node.Risk = cached.Risk;
        node.StatusText = cached.StatusText;
        node.LogicalSize = cached.LogicalSize;
        node.SizeOnDisk = cached.SizeOnDisk;
        node.FileCount = cached.FileCount;
        node.DirectoryCount = cached.DirectoryCount;

        foreach (var child in cached.Children)
        {
            node.AddChild(ToNode(child, node));
        }

        return node;
    }

    private sealed class CachedSnapshot
    {
        public string Path { get; set; } = string.Empty;

        public DateTimeOffset CachedAt { get; set; }

        public bool? AnalyzeSizeOnDisk { get; set; }

        public CachedMetadata Metadata { get; set; } = new();

        public CachedScanNode Node { get; set; } = new();
    }

    private sealed class CachedMetadata
    {
        public bool Exists { get; set; }

        public long Length { get; set; }

        public DateTime LastWriteUtc { get; set; }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public FileAttributes Attributes { get; set; }

        public string FileId { get; set; } = string.Empty;
    }

    private sealed class CachedScanNode
    {
        public string FullPath { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public FileSystemItemKind Kind { get; set; }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public RiskLevel Risk { get; set; }

        public string StatusText { get; set; } = string.Empty;

        public long LogicalSize { get; set; }

        public long SizeOnDisk { get; set; }

        public long FileCount { get; set; }

        public long DirectoryCount { get; set; }

        public DateTimeOffset? ModifiedAt { get; set; }

        public string FileId { get; set; } = string.Empty;

        public List<CachedScanNode> Children { get; set; } = [];
    }
}
