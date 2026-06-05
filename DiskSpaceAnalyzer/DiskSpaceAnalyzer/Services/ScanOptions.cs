namespace DiskSpaceAnalyzer.Services;

public sealed class ScanOptions
{
    public bool IncludeSystemDirectories { get; init; }

    public bool IgnoreCache { get; init; }

    public IReadOnlyCollection<string> ExcludedPaths { get; init; } = [];
}
