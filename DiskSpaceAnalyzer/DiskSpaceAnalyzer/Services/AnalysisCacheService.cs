using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiskSpaceAnalyzer.Models;

namespace DiskSpaceAnalyzer.Services;

public sealed class AnalysisCacheService
{
    private const int CurrentFormatVersion = 2;
    private const int StreamBufferSize = 64 * 1024;

    private readonly string _cacheDirectory;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AnalysisCacheService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _cacheDirectory = Path.Combine(appData, "simplePC DiskSpaceAnalyzer");
        Directory.CreateDirectory(_cacheDirectory);
    }

    public bool TryRestoreSnapshot(string path, bool analyzeSizeOnDisk, out ScanNode? node)
    {
        node = null;

        var normalized = PathRiskClassifier.Normalize(path);
        var cachePath = GetCachePath(normalized);
        if (!File.Exists(cachePath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                cachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                StreamBufferSize,
                FileOptions.SequentialScan);
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                StreamBufferSize);

            var headerLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(headerLine))
            {
                return false;
            }

            var header = JsonSerializer.Deserialize<CachedSnapshotHeader>(headerLine, _jsonOptions);
            if (header is null ||
                header.FormatVersion != CurrentFormatVersion ||
                !string.Equals(header.Path, normalized, StringComparison.OrdinalIgnoreCase) ||
                header.AnalyzeSizeOnDisk.GetValueOrDefault(true) != analyzeSizeOnDisk ||
                !MatchesCurrentMetadata(header.Path, header.Metadata))
            {
                return false;
            }

            node = ReadNodeTree(reader);
            if (node is null)
            {
                return false;
            }

            node.StatusText = $"Из кеша: {header.CachedAt.LocalDateTime:g}";
            return true;
        }
        catch
        {
            node = null;
            return false;
        }
    }

    public void StoreSnapshot(ScanNode node, bool analyzeSizeOnDisk)
    {
        var normalized = PathRiskClassifier.Normalize(node.FullPath);
        var header = new CachedSnapshotHeader
        {
            FormatVersion = CurrentFormatVersion,
            Path = normalized,
            CachedAt = DateTimeOffset.UtcNow,
            AnalyzeSizeOnDisk = analyzeSizeOnDisk,
            Metadata = ReadMetadata(node.FullPath)
        };

        Save(GetCachePath(normalized), header, node);
    }

    public void Clear()
    {
        try
        {
            foreach (var cachePath in Directory.EnumerateFiles(_cacheDirectory, "analysis-cache*"))
            {
                File.Delete(cachePath);
            }
        }
        catch
        {
            // Cache failures should not affect the app.
        }
    }

    private void Save(string cachePath, CachedSnapshotHeader header, ScanNode root)
    {
        var temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       StreamBufferSize,
                       FileOptions.SequentialScan))
            {
                WriteJsonLine(stream, header);
                WriteNodeTree(stream, root, depth: 0);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            // Cache failures should not affect scanning.
        }
    }

    private void WriteNodeTree(Stream stream, ScanNode node, int depth)
    {
        var record = new CachedNodeRecord
        {
            Depth = depth,
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
            FileId = node.FileId
        };

        WriteJsonLine(stream, record);
        foreach (var child in node.ExistingChildren)
        {
            WriteNodeTree(stream, child, depth + 1);
        }
    }

    private void WriteJsonLine<T>(Stream stream, T value)
    {
        JsonSerializer.Serialize(stream, value, _jsonOptions);
        stream.WriteByte((byte)'\n');
    }

    private ScanNode? ReadNodeTree(StreamReader reader)
    {
        ScanNode? root = null;
        var ancestors = new List<ScanNode>();

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var record = JsonSerializer.Deserialize<CachedNodeRecord>(line, _jsonOptions);
            if (record is null ||
                record.Depth < 0 ||
                (root is null && record.Depth != 0) ||
                (root is not null && record.Depth == 0) ||
                record.Depth > ancestors.Count)
            {
                return null;
            }

            var current = ToNode(record);
            if (record.Depth == 0)
            {
                root = current;
            }
            else
            {
                ancestors[record.Depth - 1].AddChild(current);
            }

            if (ancestors.Count == record.Depth)
            {
                ancestors.Add(current);
            }
            else
            {
                ancestors[record.Depth] = current;
                if (ancestors.Count > record.Depth + 1)
                {
                    ancestors.RemoveRange(record.Depth + 1, ancestors.Count - record.Depth - 1);
                }
            }
        }

        return root;
    }

    private string GetCachePath(string path)
    {
        var normalizedCacheKey = PathRiskClassifier.Normalize(path).ToUpperInvariant();
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCacheKey));
        var hash = Convert.ToHexString(hashBytes);
        return Path.Combine(_cacheDirectory, $"analysis-cache-v{CurrentFormatVersion}-{hash}.jsonl");
    }

    private static bool MatchesCurrentMetadata(string path, CachedMetadata metadata)
    {
        var current = ReadMetadata(path);
        if (!current.Exists || current.Attributes != metadata.Attributes)
        {
            return false;
        }

        if (IsDriveRoot(path))
        {
            return string.IsNullOrWhiteSpace(metadata.FileId) || current.FileId == metadata.FileId;
        }

        return current.Length == metadata.Length &&
               current.LastWriteUtc == metadata.LastWriteUtc &&
               (string.IsNullOrWhiteSpace(metadata.FileId) || current.FileId == metadata.FileId);
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

    private static ScanNode ToNode(CachedNodeRecord cached)
    {
        var node = new ScanNode
        {
            FullPath = cached.FullPath,
            Name = cached.Name,
            Kind = cached.Kind,
            ModifiedAt = cached.ModifiedAt,
            FileId = cached.FileId
        };

        node.Risk = cached.Risk;
        node.StatusText = cached.StatusText;
        node.LogicalSize = cached.LogicalSize;
        node.SizeOnDisk = cached.SizeOnDisk;
        node.FileCount = cached.FileCount;
        node.DirectoryCount = cached.DirectoryCount;
        return node;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A stale temporary cache file is harmless.
        }
    }

    private sealed class CachedSnapshotHeader
    {
        public int FormatVersion { get; set; }

        public string Path { get; set; } = string.Empty;

        public DateTimeOffset CachedAt { get; set; }

        public bool? AnalyzeSizeOnDisk { get; set; }

        public CachedMetadata Metadata { get; set; } = new();
    }

    private sealed class CachedMetadata
    {
        public bool Exists { get; set; }

        public long Length { get; set; }

        public DateTime LastWriteUtc { get; set; }

        [JsonConverter(typeof(JsonStringEnumConverter<FileAttributes>))]
        public FileAttributes Attributes { get; set; }

        public string FileId { get; set; } = string.Empty;
    }

    private sealed class CachedNodeRecord
    {
        public int Depth { get; set; }

        public string FullPath { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        [JsonConverter(typeof(JsonStringEnumConverter<FileSystemItemKind>))]
        public FileSystemItemKind Kind { get; set; }

        [JsonConverter(typeof(JsonStringEnumConverter<RiskLevel>))]
        public RiskLevel Risk { get; set; }

        public string StatusText { get; set; } = string.Empty;

        public long LogicalSize { get; set; }

        public long SizeOnDisk { get; set; }

        public long FileCount { get; set; }

        public long DirectoryCount { get; set; }

        public DateTimeOffset? ModifiedAt { get; set; }

        public string FileId { get; set; } = string.Empty;
    }
}
