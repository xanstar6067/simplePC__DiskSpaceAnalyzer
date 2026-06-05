using DiskSpaceAnalyzer.Models;

namespace DiskSpaceAnalyzer.Services;

public sealed class ScanProgressInfo
{
    public string CurrentPath { get; init; } = string.Empty;

    public long ProcessedFiles { get; init; }

    public long ProcessedDirectories { get; init; }

    public long LogicalBytes { get; init; }

    public long SizeOnDiskBytes { get; init; }

    public ScanNode? CompletedRootChild { get; init; }

    public string Message { get; init; } = string.Empty;
}
