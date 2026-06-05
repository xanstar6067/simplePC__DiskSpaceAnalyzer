namespace DiskSpaceAnalyzer.Services;

public sealed class ScanOptions
{
    public bool IncludeSystemDirectories { get; init; }

    public bool IgnoreCache { get; init; }

    public bool AnalyzeSizeOnDisk { get; init; } = true;

    public IReadOnlyCollection<string> ExcludedPaths { get; init; } = [];
}
