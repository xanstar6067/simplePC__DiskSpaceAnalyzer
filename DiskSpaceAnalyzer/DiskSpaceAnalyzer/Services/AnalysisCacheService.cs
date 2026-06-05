using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiskSpaceAnalyzer.Models;

namespace DiskSpaceAnalyzer.Services;

public sealed class AnalysisCacheService
{
    private const int MaxCachedNodesPerSnapshot = 50000;
    private readonly string _cacheDirectory;
    private readonly string _legacyCachePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };
    private readonly Dictionary<string, Dictionary<string, CachedSnapshot>> _snapshotsByFile = new(StringComparer.OrdinalIgnoreCase);

    public AnalysisCacheService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _cacheDirectory = Path.Combine(appData, "simplePC DiskSpaceAnalyzer");
        Directory.CreateDirectory(_cacheDirectory);
        _legacyCachePath = Path.Combine(_cacheDirectory, "analysis-cache.json");
    }

    public bool TryRestoreSnapshot(string path, bool analyzeSizeOnDisk, out ScanNode? node)
    {
        node = null;

        var normalized = PathRiskClassifier.Normalize(path);
        var cachePath = GetCachePath(normalized);
        var snapshots = EnsureLoaded(cachePath);
        if (!snapshots.TryGetValue(normalized, out var snapshot))
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
        var normalized = PathRiskClassifier.Normalize(node.FullPath);
        var cachePath = GetCachePath(normalized);
        var snapshots = EnsureLoaded(cachePath);
        if (CountNodes(node) > MaxCachedNodesPerSnapshot)
        {
            if (snapshots.Remove(normalized))
            {
                Save(cachePath, snapshots);
            }

            return;
        }

        var snapshot = new CachedSnapshot
        {
            Path = normalized,
            CachedAt = DateTimeOffset.UtcNow,
            AnalyzeSizeOnDisk = analyzeSizeOnDisk,
            Node = FromNode(node),
            Metadata = ReadMetadata(node.FullPath)
        };

        snapshots[snapshot.Path] = snapshot;
        Save(cachePath, snapshots);
    }

    public void Clear()
    {
        _snapshotsByFile.Clear();

        try
        {
            foreach (var cachePath in Directory.EnumerateFiles(_cacheDirectory, "analysis-cache*.json"))
            {
                File.Delete(cachePath);
            }
        }
        catch
        {
            // Cache failures should not affect the app.
        }
    }

    private Dictionary<string, CachedSnapshot> EnsureLoaded(string cachePath)
    {
        if (_snapshotsByFile.TryGetValue(cachePath, out var loaded))
        {
            return loaded;
        }

        var snapshots = new Dictionary<string, CachedSnapshot>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(cachePath))
        {
            try
            {
                var text = File.ReadAllText(cachePath);
                snapshots = JsonSerializer.Deserialize<Dictionary<string, CachedSnapshot>>(text, _jsonOptions) ?? new(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                snapshots = new(StringComparer.OrdinalIgnoreCase);
            }
        }

        ImportLegacySnapshots(cachePath, snapshots);
        _snapshotsByFile[cachePath] = snapshots;
        return snapshots;
    }

    private void ImportLegacySnapshots(string cachePath, Dictionary<string, CachedSnapshot> snapshots)
    {
        if (!File.Exists(_legacyCachePath) || string.Equals(cachePath, _legacyCachePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var text = File.ReadAllText(_legacyCachePath);
            var legacy = JsonSerializer.Deserialize<Dictionary<string, CachedSnapshot>>(text, _jsonOptions);
            if (legacy is null)
            {
                return;
            }

            var changed = false;
            foreach (var item in legacy.Where(item => string.Equals(GetCachePath(item.Key), cachePath, StringComparison.OrdinalIgnoreCase)))
            {
                snapshots.TryAdd(item.Key, item.Value);
                changed = true;
            }

            if (changed)
            {
                Save(cachePath, snapshots);
            }
        }
        catch
        {
            // Cache migration failures should not affect scanning.
        }
    }

    private void Save(string cachePath, Dictionary<string, CachedSnapshot> snapshots)
    {
        try
        {
            var text = JsonSerializer.Serialize(snapshots, _jsonOptions);
            File.WriteAllText(cachePath, text);
        }
        catch
        {
            // Cache failures should not affect scanning.
        }
    }

    private string GetCachePath(string path)
    {
        var normalized = PathRiskClassifier.Normalize(path);
        var root = Path.GetPathRoot(normalized);
        var cacheKey = string.IsNullOrWhiteSpace(root) ? normalized : root;

        if (cacheKey.Length >= 2 && cacheKey[1] == ':')
        {
            return Path.Combine(_cacheDirectory, $"analysis-cache-{char.ToUpperInvariant(cacheKey[0])}.json");
        }

        var safeName = string.Join("_", cacheKey.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "paths";
        }

        return Path.Combine(_cacheDirectory, $"analysis-cache-{safeName}.json");
    }

    private static bool MatchesCurrentMetadata(CachedSnapshot snapshot)
    {
        var current = ReadMetadata(snapshot.Path);
        if (!current.Exists || current.Attributes != snapshot.Metadata.Attributes)
        {
            return false;
        }

        if (IsDriveRoot(snapshot.Path))
        {
            return string.IsNullOrWhiteSpace(snapshot.Metadata.FileId) || current.FileId == snapshot.Metadata.FileId;
        }

        return current.Length == snapshot.Metadata.Length &&
               current.LastWriteUtc == snapshot.Metadata.LastWriteUtc &&
               (string.IsNullOrWhiteSpace(snapshot.Metadata.FileId) || current.FileId == snapshot.Metadata.FileId);
    }

    private static bool IsDriveRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrWhiteSpace(root) &&
               string.Equals(PathRiskClassifier.Normalize(path), PathRiskClassifier.Normalize(root), StringComparison.OrdinalIgnoreCase);
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
