using System.Globalization;
using System.IO;
using DiskSpaceAnalyzer.Models;
using Microsoft.Data.Sqlite;

namespace DiskSpaceAnalyzer.Services;

public sealed class AnalysisCacheService
{
    private const int CurrentFormatVersion = 6;
    private const int CacheSizeKiB = 4096;

    private readonly string _cacheDirectory;

    static AnalysisCacheService()
    {
        SQLitePCL.Batteries.Init();
    }

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

    public bool TryRestoreSnapshot(string path, SizeCalculationMode sizeCalculationMode, out ScanNode? node)
    {
        node = null;
        var normalized = PathRiskClassifier.Normalize(path);
        var databasePath = GetDatabasePath(normalized, sizeCalculationMode);
        if (!File.Exists(databasePath))
        {
            return false;
        }

        try
        {
            using var connection = OpenReadOnly(databasePath);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT FormatVersion, Path, CachedAt, SizeCalculationMode,
                       RootLength, RootAttributes, RootLastWriteUtcTicks, RootFileId, RootNodeId
                FROM Snapshot
                LIMIT 1;
                """;

            using var reader = command.ExecuteReader();
            if (!reader.Read() ||
                reader.GetInt32(0) != CurrentFormatVersion ||
                !string.Equals(reader.GetString(1), normalized, StringComparison.OrdinalIgnoreCase) ||
                reader.GetInt32(3) != (int)sizeCalculationMode)
            {
                return false;
            }

            var metadata = new CachedMetadata(
                reader.GetInt64(4),
                (FileAttributes)reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetString(7));
            if (!MatchesCurrentMetadata(normalized, metadata))
            {
                return false;
            }

            var rootId = reader.GetInt64(8);
            reader.Close();
            node = ReadNode(connection, databasePath, rootId);
            if (node is null)
            {
                return false;
            }

            var cachedAt = DateTimeOffset.Parse(
                ExecuteScalarString(connection, "SELECT CachedAt FROM Snapshot LIMIT 1;"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
            node.StatusText = $"Из кэша: {cachedAt.LocalDateTime:g}";
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

        if (string.IsNullOrWhiteSpace(node.CacheDatabasePath) ||
            node.CacheNodeId <= 0 ||
            !File.Exists(node.CacheDatabasePath))
        {
            return false;
        }

        try
        {
            using var connection = OpenReadOnly(node.CacheDatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT Id, FullPath, Name, Kind, Risk, StatusText, LogicalSize, SizeOnDisk,
                       FileCount, DirectoryCount, ChildCount, ModifiedAt, FileId
                FROM Nodes
                WHERE ParentId = $parentId
                ORDER BY SizeOnDisk DESC, Name COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$parentId", node.CacheNodeId);

            var children = new List<ScanNode>(node.ChildCount);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                children.Add(ToNode(reader, node.CacheDatabasePath));
            }

            if (children.Count != node.ChildCount)
            {
                return false;
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
            string.IsNullOrWhiteSpace(cachedRoot.CacheDatabasePath) ||
            string.IsNullOrWhiteSpace(query) ||
            maxResults <= 0)
        {
            return results;
        }

        try
        {
            using var connection = OpenReadOnly(cachedRoot.CacheDatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT Id, FullPath, Name, Kind, Risk, StatusText, LogicalSize, SizeOnDisk,
                       FileCount, DirectoryCount, ChildCount, ModifiedAt, FileId
                FROM Nodes;
                """;

            using var reader = command.ExecuteReader();
            while (results.Count < maxResults && reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = reader.GetString(1);
                var name = reader.GetString(2);
                if (!name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !fullPath.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(ToNode(reader, cachedRoot.CacheDatabasePath));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A damaged cache behaves like an incomplete search.
        }

        return results;
    }

    internal SnapshotWriter BeginSnapshot(string path, SizeCalculationMode sizeCalculationMode)
    {
        return new SnapshotWriter(
            this,
            PathRiskClassifier.Normalize(path),
            sizeCalculationMode,
            GetDatabasePath(path, sizeCalculationMode));
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

    private string GetDatabasePath(string path, SizeCalculationMode sizeCalculationMode)
    {
        var normalized = PathRiskClassifier.Normalize(path);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized.ToUpperInvariant())));
        return Path.Combine(
            _cacheDirectory,
            $"analysis-cache-v{CurrentFormatVersion}-{hash}-{(int)sizeCalculationMode}.db");
    }

    private static SqliteConnection OpenReadOnly(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA query_only=ON; PRAGMA cache_size=-{CacheSizeKiB}; PRAGMA temp_store=FILE;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static ScanNode? ReadNode(SqliteConnection connection, string databasePath, long nodeId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, FullPath, Name, Kind, Risk, StatusText, LogicalSize, SizeOnDisk,
                   FileCount, DirectoryCount, ChildCount, ModifiedAt, FileId
            FROM Nodes
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", nodeId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ToNode(reader, databasePath) : null;
    }

    private static ScanNode ToNode(SqliteDataReader reader, string databasePath)
    {
        var node = new ScanNode
        {
            FullPath = reader.GetString(1),
            Name = reader.GetString(2),
            Kind = (FileSystemItemKind)reader.GetInt32(3),
            Risk = (RiskLevel)reader.GetInt32(4),
            StatusText = reader.GetString(5),
            LogicalSize = reader.GetInt64(6),
            SizeOnDisk = reader.GetInt64(7),
            FileCount = reader.GetInt64(8),
            DirectoryCount = reader.GetInt64(9),
            ModifiedAt = reader.IsDBNull(11)
                ? null
                : DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            FileId = reader.GetString(12)
        };
        node.SetCachedSource(databasePath, reader.GetInt64(0), reader.GetInt32(10));
        return node;
    }

    private static string ExecuteScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
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
               current.LastWriteUtcTicks == metadata.LastWriteUtcTicks &&
               (string.IsNullOrWhiteSpace(metadata.FileId) || current.FileId == metadata.FileId);
    }

    private static bool IsDriveRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrWhiteSpace(root) &&
               string.Equals(
                   PathRiskClassifier.Normalize(path),
                   PathRiskClassifier.Normalize(root),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static CachedMetadata ReadMetadata(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            return new CachedMetadata(
                isDirectory ? 0 : ((FileInfo)info).Length,
                attributes,
                info.LastWriteTimeUtc.Ticks,
                SystemInterop.GetFileId(path),
                Exists: true);
        }
        catch
        {
            return new CachedMetadata(0, 0, 0, string.Empty, Exists: false);
        }
    }

    private static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup is best-effort.
        }
    }

    private readonly record struct CachedMetadata(
        long Length,
        FileAttributes Attributes,
        long LastWriteUtcTicks,
        string FileId,
        bool Exists = true);

    internal readonly record struct NodeRecord(
        long Id,
        long? ParentId,
        string FullPath,
        string Name,
        FileSystemItemKind Kind,
        RiskLevel Risk,
        string StatusText,
        long LogicalSize,
        long SizeOnDisk,
        long FileCount,
        long DirectoryCount,
        int ChildCount,
        DateTimeOffset? ModifiedAt,
        string FileId);

    internal sealed class SnapshotWriter : IDisposable
    {
        private readonly AnalysisCacheService _owner;
        private readonly string _normalizedPath;
        private readonly SizeCalculationMode _sizeCalculationMode;
        private readonly string _databasePath;
        private readonly string _temporaryPath;
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private readonly SqliteCommand _insertNode;
        private long _nextNodeId;
        private bool _completed;

        internal SnapshotWriter(
            AnalysisCacheService owner,
            string normalizedPath,
            SizeCalculationMode sizeCalculationMode,
            string databasePath)
        {
            _owner = owner;
            _normalizedPath = normalizedPath;
            _sizeCalculationMode = sizeCalculationMode;
            _databasePath = databasePath;
            _temporaryPath = $"{databasePath}.{Guid.NewGuid():N}.writing";

            _connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _temporaryPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
            _connection.Open();

            using (var setup = _connection.CreateCommand())
            {
                setup.CommandText =
                    $"""
                    PRAGMA journal_mode=DELETE;
                    PRAGMA synchronous=NORMAL;
                    PRAGMA temp_store=FILE;
                    PRAGMA cache_size=-{CacheSizeKiB};
                    PRAGMA foreign_keys=OFF;

                    CREATE TABLE Nodes (
                        Id INTEGER PRIMARY KEY,
                        ParentId INTEGER NULL,
                        FullPath TEXT NOT NULL,
                        Name TEXT NOT NULL,
                        Kind INTEGER NOT NULL,
                        Risk INTEGER NOT NULL,
                        StatusText TEXT NOT NULL,
                        LogicalSize INTEGER NOT NULL,
                        SizeOnDisk INTEGER NOT NULL,
                        FileCount INTEGER NOT NULL,
                        DirectoryCount INTEGER NOT NULL,
                        ChildCount INTEGER NOT NULL,
                        ModifiedAt TEXT NULL,
                        FileId TEXT NOT NULL
                    );

                    CREATE TABLE Snapshot (
                        FormatVersion INTEGER NOT NULL,
                        Path TEXT NOT NULL,
                        CachedAt TEXT NOT NULL,
                        SizeCalculationMode INTEGER NOT NULL,
                        RootLength INTEGER NOT NULL,
                        RootAttributes INTEGER NOT NULL,
                        RootLastWriteUtcTicks INTEGER NOT NULL,
                        RootFileId TEXT NOT NULL,
                        RootNodeId INTEGER NOT NULL
                    );
                    """;
                setup.ExecuteNonQuery();
            }

            _transaction = _connection.BeginTransaction();
            _insertNode = _connection.CreateCommand();
            _insertNode.Transaction = _transaction;
            _insertNode.CommandText =
                """
                INSERT INTO Nodes (
                    Id, ParentId, FullPath, Name, Kind, Risk, StatusText, LogicalSize,
                    SizeOnDisk, FileCount, DirectoryCount, ChildCount, ModifiedAt, FileId)
                VALUES (
                    $id, $parentId, $fullPath, $name, $kind, $risk, $statusText, $logicalSize,
                    $sizeOnDisk, $fileCount, $directoryCount, $childCount, $modifiedAt, $fileId);
                """;
            foreach (var parameterName in new[]
                     {
                         "$id", "$parentId", "$fullPath", "$name", "$kind", "$risk", "$statusText",
                         "$logicalSize", "$sizeOnDisk", "$fileCount", "$directoryCount", "$childCount",
                         "$modifiedAt", "$fileId"
                     })
            {
                _insertNode.Parameters.Add(new SqliteParameter(parameterName, null));
            }
            _insertNode.Prepare();
        }

        public long NextNodeId()
        {
            return ++_nextNodeId;
        }

        public void WriteNode(NodeRecord node)
        {
            Set("$id", node.Id);
            Set("$parentId", node.ParentId);
            Set("$fullPath", node.FullPath);
            Set("$name", node.Name);
            Set("$kind", (int)node.Kind);
            Set("$risk", (int)node.Risk);
            Set("$statusText", node.StatusText);
            Set("$logicalSize", node.LogicalSize);
            Set("$sizeOnDisk", node.SizeOnDisk);
            Set("$fileCount", node.FileCount);
            Set("$directoryCount", node.DirectoryCount);
            Set("$childCount", node.ChildCount);
            Set("$modifiedAt", node.ModifiedAt?.ToString("O", CultureInfo.InvariantCulture));
            Set("$fileId", node.FileId);
            _insertNode.ExecuteNonQuery();
        }

        public bool Commit(long rootNodeId)
        {
            if (_completed)
            {
                return false;
            }

            try
            {
                var metadata = ReadMetadata(_normalizedPath);
                if (!metadata.Exists)
                {
                    return false;
                }

                using (var snapshot = _connection.CreateCommand())
                {
                    snapshot.Transaction = _transaction;
                    snapshot.CommandText =
                        """
                        INSERT INTO Snapshot (
                            FormatVersion, Path, CachedAt, SizeCalculationMode, RootLength,
                            RootAttributes, RootLastWriteUtcTicks, RootFileId, RootNodeId)
                        VALUES (
                            $formatVersion, $path, $cachedAt, $sizeCalculationMode, $rootLength,
                            $rootAttributes, $rootLastWriteUtcTicks, $rootFileId, $rootNodeId);
                        """;
                    snapshot.Parameters.AddWithValue("$formatVersion", CurrentFormatVersion);
                    snapshot.Parameters.AddWithValue("$path", _normalizedPath);
                    snapshot.Parameters.AddWithValue("$cachedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    snapshot.Parameters.AddWithValue("$sizeCalculationMode", (int)_sizeCalculationMode);
                    snapshot.Parameters.AddWithValue("$rootLength", metadata.Length);
                    snapshot.Parameters.AddWithValue("$rootAttributes", (long)metadata.Attributes);
                    snapshot.Parameters.AddWithValue("$rootLastWriteUtcTicks", metadata.LastWriteUtcTicks);
                    snapshot.Parameters.AddWithValue("$rootFileId", metadata.FileId);
                    snapshot.Parameters.AddWithValue("$rootNodeId", rootNodeId);
                    snapshot.ExecuteNonQuery();
                }

                using (var indexes = _connection.CreateCommand())
                {
                    indexes.Transaction = _transaction;
                    indexes.CommandText =
                        """
                        CREATE INDEX IX_Nodes_Parent_Size_Name
                            ON Nodes (ParentId, SizeOnDisk DESC, Name COLLATE NOCASE);
                        CREATE INDEX IX_Nodes_FullPath
                            ON Nodes (FullPath COLLATE NOCASE);
                        """;
                    indexes.ExecuteNonQuery();
                }

                _transaction.Commit();
                _insertNode.Dispose();
                _transaction.Dispose();
                _connection.Close();
                _connection.Dispose();

                File.Move(_temporaryPath, _databasePath, overwrite: true);
                _completed = true;
                _owner.DeleteLegacyCacheFiles(_databasePath);
                return true;
            }
            catch (Exception ex)
            {
                throw new IOException("Не удалось опубликовать SQLite-кэш анализа.", ex);
            }
        }

        public void Dispose()
        {
            if (!_completed)
            {
                try
                {
                    _transaction.Rollback();
                }
                catch
                {
                    // The transaction may already be closed after a failed commit.
                }

                _insertNode.Dispose();
                _transaction.Dispose();
                _connection.Dispose();
                TryDelete(_temporaryPath);
            }
        }

        private void Set(string parameterName, object? value)
        {
            _insertNode.Parameters[parameterName].Value = value ?? DBNull.Value;
        }
    }

    private void DeleteLegacyCacheFiles(string currentDatabasePath)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(_cacheDirectory, "analysis-cache*"))
            {
                if (!string.Equals(path, currentDatabasePath, StringComparison.OrdinalIgnoreCase) &&
                    (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".writing", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".backup", StringComparison.OrdinalIgnoreCase)))
                {
                    TryDelete(path);
                }
            }
        }
        catch
        {
            // Legacy cache cleanup is optional.
        }
    }
}
