using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiskSpaceAnalyzer.Models;

namespace DiskSpaceAnalyzer.Services;

public sealed class AnalysisCacheService
{
    private const int CurrentFormatVersion = 3;
    private const int StreamBufferSize = 64 * 1024;
    private const int HashLength = 32;
    private const int IndexHeaderSize = 16;
    private const int IndexRecordSize = HashLength + sizeof(long) + sizeof(int);

    private static readonly byte[] IndexMagic = Encoding.ASCII.GetBytes("DSAIDX3\0");

    private readonly string _cacheDirectory;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public AnalysisCacheService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "simplePC DiskSpaceAnalyzer"))
    {
    }

    internal AnalysisCacheService(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
        Directory.CreateDirectory(_cacheDirectory);
    }

    public bool TryRestoreSnapshot(string path, bool analyzeSizeOnDisk, out ScanNode? node)
    {
        node = null;

        var normalized = PathRiskClassifier.Normalize(path);
        var manifestPath = GetManifestPath(normalized);
        var manifest = TryReadManifest(manifestPath);
        if (manifest is null ||
            manifest.FormatVersion != CurrentFormatVersion ||
            !string.Equals(manifest.Path, normalized, StringComparison.OrdinalIgnoreCase) ||
            manifest.AnalyzeSizeOnDisk.GetValueOrDefault(true) != analyzeSizeOnDisk ||
            !MatchesCurrentMetadata(manifest.Path, manifest.Metadata))
        {
            return false;
        }

        var dataPath = ResolveCacheFile(manifest.DataFileName);
        var indexPath = ResolveCacheFile(manifest.IndexFileName);
        if (dataPath is null || indexPath is null || !File.Exists(dataPath) || !File.Exists(indexPath))
        {
            return false;
        }

        try
        {
            node = ToNode(manifest.Root, dataPath, indexPath);
            if (node.HasUnloadedCachedChildren && !TryLoadChildren(node))
            {
                node = null;
                return false;
            }

            node.StatusText = $"Из кеша: {manifest.CachedAt.LocalDateTime:g}";
            return true;
        }
        catch
        {
            node = null;
            return false;
        }
    }

    public bool TryLoadChildren(ScanNode node)
    {
        if (!node.HasUnloadedCachedChildren)
        {
            return true;
        }

        if (node.CacheFormatVersion != CurrentFormatVersion ||
            string.IsNullOrWhiteSpace(node.CacheDataPath) ||
            string.IsNullOrWhiteSpace(node.CacheIndexPath) ||
            !TryFindDirectory(node.CacheIndexPath, node.FullPath, out var entry))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                node.CacheDataPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                StreamBufferSize,
                FileOptions.RandomAccess);
            stream.Seek(entry.Offset, SeekOrigin.Begin);

            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                StreamBufferSize,
                leaveOpen: false);

            var children = new List<ScanNode>(entry.ChildCount);
            for (var i = 0; i < entry.ChildCount; i++)
            {
                var line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line))
                {
                    return false;
                }

                var record = JsonSerializer.Deserialize<CachedNodeRecord>(line, _jsonOptions);
                if (record is null)
                {
                    return false;
                }

                children.Add(ToNode(record, node.CacheDataPath, node.CacheIndexPath));
            }

            node.SetCachedChildren(children);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<ScanNode> SearchSnapshot(
        ScanNode cachedRoot,
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var results = new List<ScanNode>(Math.Min(maxResults, 500));
        if (!cachedRoot.IsCacheBacked ||
            string.IsNullOrWhiteSpace(cachedRoot.CacheDataPath) ||
            string.IsNullOrWhiteSpace(cachedRoot.CacheIndexPath) ||
            string.IsNullOrWhiteSpace(query) ||
            maxResults <= 0)
        {
            return results;
        }

        if (MatchesQuery(cachedRoot.Name, cachedRoot.FullPath, query))
        {
            results.Add(cachedRoot);
        }

        try
        {
            using var stream = new FileStream(
                cachedRoot.CacheDataPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                StreamBufferSize,
                FileOptions.SequentialScan);
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                StreamBufferSize);

            var canUseRawPrefilter = query.IndexOfAny(['\\', '"', '\r', '\n', '\t']) < 0;
            while (results.Count < maxResults && reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (canUseRawPrefilter && !line.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var record = JsonSerializer.Deserialize<CachedNodeRecord>(line, _jsonOptions);
                if (record is null || !MatchesQuery(record.Name, record.FullPath, query))
                {
                    continue;
                }

                results.Add(ToNode(record, cachedRoot.CacheDataPath, cachedRoot.CacheIndexPath));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A damaged cache should behave like an incomplete search, not crash the app.
        }

        return results;
    }

    public void StoreSnapshot(ScanNode node, bool analyzeSizeOnDisk)
    {
        var normalized = PathRiskClassifier.Normalize(node.FullPath);
        var cacheHash = GetCacheHash(normalized);
        var generation = Guid.NewGuid().ToString("N");
        var dataFileName = $"analysis-cache-v{CurrentFormatVersion}-{cacheHash}-{generation}.nodes.jsonl";
        var indexFileName = $"analysis-cache-v{CurrentFormatVersion}-{cacheHash}-{generation}.index.bin";
        var dataPath = Path.Combine(_cacheDirectory, dataFileName);
        var indexPath = Path.Combine(_cacheDirectory, indexFileName);
        var manifestPath = GetManifestPath(normalized);
        var temporaryDataPath = $"{dataPath}.tmp";
        var temporaryIndexPath = $"{indexPath}.tmp";
        var temporaryManifestPath = $"{manifestPath}.{generation}.tmp";
        var previousManifest = TryReadManifest(manifestPath);

        try
        {
            var indexEntries = WriteDataFile(temporaryDataPath, node);
            WriteIndexFile(temporaryIndexPath, indexEntries);

            File.Move(temporaryDataPath, dataPath);
            File.Move(temporaryIndexPath, indexPath);

            var manifest = new CachedSnapshotManifest
            {
                FormatVersion = CurrentFormatVersion,
                Path = normalized,
                CachedAt = DateTimeOffset.UtcNow,
                AnalyzeSizeOnDisk = analyzeSizeOnDisk,
                Metadata = ReadMetadata(node.FullPath),
                DataFileName = dataFileName,
                IndexFileName = indexFileName,
                Root = FromNode(node)
            };

            using (var stream = new FileStream(
                       temporaryManifestPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       StreamBufferSize,
                       FileOptions.SequentialScan))
            {
                JsonSerializer.Serialize(stream, manifest, _jsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryManifestPath, manifestPath, overwrite: true);
            DeleteManifestFiles(previousManifest, dataFileName, indexFileName);
        }
        catch
        {
            TryDelete(temporaryDataPath);
            TryDelete(temporaryIndexPath);
            TryDelete(temporaryManifestPath);
            TryDelete(dataPath);
            TryDelete(indexPath);
            // Cache failures should not affect scanning.
        }
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

    private List<DirectoryIndexEntry> WriteDataFile(string path, ScanNode root)
    {
        var indexEntries = new List<DirectoryIndexEntry>();
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            StreamBufferSize,
            FileOptions.SequentialScan);

        WriteDirectoryBlocks(stream, root, indexEntries);
        stream.Flush(flushToDisk: true);
        return indexEntries;
    }

    private void WriteDirectoryBlocks(
        Stream stream,
        ScanNode directory,
        List<DirectoryIndexEntry> indexEntries)
    {
        if (directory.Kind != FileSystemItemKind.Folder || directory.ExistingChildren.Count == 0)
        {
            return;
        }

        var offset = stream.Position;
        foreach (var child in directory.ExistingChildren)
        {
            WriteJsonLine(stream, FromNode(child));
        }

        indexEntries.Add(new DirectoryIndexEntry(
            HashPath(directory.FullPath),
            offset,
            directory.ExistingChildren.Count));

        foreach (var child in directory.ExistingChildren)
        {
            WriteDirectoryBlocks(stream, child, indexEntries);
        }
    }

    private void WriteIndexFile(string path, List<DirectoryIndexEntry> entries)
    {
        entries.Sort(static (left, right) => CompareHashes(left.Hash, right.Hash));

        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            StreamBufferSize,
            FileOptions.SequentialScan);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(IndexMagic);
        writer.Write(CurrentFormatVersion);
        writer.Write(entries.Count);
        foreach (var entry in entries)
        {
            writer.Write(entry.Hash);
            writer.Write(entry.Offset);
            writer.Write(entry.ChildCount);
        }

        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static bool TryFindDirectory(string indexPath, string directoryPath, out DirectoryIndexEntry entry)
    {
        entry = default;

        try
        {
            using var stream = new FileStream(
                indexPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                StreamBufferSize,
                FileOptions.RandomAccess);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            if (!reader.ReadBytes(IndexMagic.Length).SequenceEqual(IndexMagic) ||
                reader.ReadInt32() != CurrentFormatVersion)
            {
                return false;
            }

            var entryCount = reader.ReadInt32();
            var expectedLength = IndexHeaderSize + (long)entryCount * IndexRecordSize;
            if (entryCount < 0 || stream.Length != expectedLength)
            {
                return false;
            }

            var targetHash = HashPath(directoryPath);
            var low = 0;
            var high = entryCount - 1;
            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                stream.Seek(IndexHeaderSize + (long)middle * IndexRecordSize, SeekOrigin.Begin);
                var currentHash = reader.ReadBytes(HashLength);
                if (currentHash.Length != HashLength)
                {
                    return false;
                }

                var comparison = CompareHashes(currentHash, targetHash);
                if (comparison == 0)
                {
                    var offset = reader.ReadInt64();
                    var childCount = reader.ReadInt32();
                    if (offset < 0 || childCount < 0)
                    {
                        return false;
                    }

                    entry = new DirectoryIndexEntry(currentHash, offset, childCount);
                    return true;
                }

                if (comparison < 0)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private void WriteJsonLine<T>(Stream stream, T value)
    {
        JsonSerializer.Serialize(stream, value, _jsonOptions);
        stream.WriteByte((byte)'\n');
    }

    private CachedSnapshotManifest? TryReadManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                StreamBufferSize,
                FileOptions.SequentialScan);
            return JsonSerializer.Deserialize<CachedSnapshotManifest>(stream, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void DeleteManifestFiles(
        CachedSnapshotManifest? manifest,
        string currentDataFileName,
        string currentIndexFileName)
    {
        if (manifest is null)
        {
            return;
        }

        if (!string.Equals(manifest.DataFileName, currentDataFileName, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(ResolveCacheFile(manifest.DataFileName));
        }

        if (!string.Equals(manifest.IndexFileName, currentIndexFileName, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(ResolveCacheFile(manifest.IndexFileName));
        }
    }

    private string? ResolveCacheFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            return null;
        }

        return Path.Combine(_cacheDirectory, fileName);
    }

    private string GetManifestPath(string path)
    {
        return Path.Combine(
            _cacheDirectory,
            $"analysis-cache-v{CurrentFormatVersion}-{GetCacheHash(path)}.manifest.json");
    }

    private static string GetCacheHash(string path)
    {
        return Convert.ToHexString(HashPath(path));
    }

    private static byte[] HashPath(string path)
    {
        var normalized = PathRiskClassifier.Normalize(path).ToUpperInvariant();
        return SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
    }

    private static int CompareHashes(byte[] left, byte[] right)
    {
        return left.AsSpan().SequenceCompareTo(right);
    }

    private static bool MatchesQuery(string name, string fullPath, string query)
    {
        return name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               fullPath.Contains(query, StringComparison.OrdinalIgnoreCase);
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

    private static CachedNodeRecord FromNode(ScanNode node)
    {
        return new CachedNodeRecord
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
            ChildCount = node.ExistingChildren.Count,
            ModifiedAt = node.ModifiedAt,
            FileId = node.FileId
        };
    }

    private static ScanNode ToNode(CachedNodeRecord cached, string dataPath, string indexPath)
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
        node.SetCachedSource(dataPath, indexPath, CurrentFormatVersion, cached.ChildCount);
        return node;
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Stale cache files are harmless and can be removed by Clear().
        }
    }

    private readonly record struct DirectoryIndexEntry(byte[] Hash, long Offset, int ChildCount);

    private sealed class CachedSnapshotManifest
    {
        public int FormatVersion { get; set; }

        public string Path { get; set; } = string.Empty;

        public DateTimeOffset CachedAt { get; set; }

        public bool? AnalyzeSizeOnDisk { get; set; }

        public CachedMetadata Metadata { get; set; } = new();

        public string DataFileName { get; set; } = string.Empty;

        public string IndexFileName { get; set; } = string.Empty;

        public CachedNodeRecord Root { get; set; } = new();
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

        public int ChildCount { get; set; }

        public DateTimeOffset? ModifiedAt { get; set; }

        public string FileId { get; set; } = string.Empty;
    }
}
