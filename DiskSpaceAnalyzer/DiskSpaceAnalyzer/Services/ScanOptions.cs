using DiskSpaceAnalyzer.Models;

namespace DiskSpaceAnalyzer.Services;

public sealed class ScanOptions
{
    public bool IncludeSystemDirectories { get; init; }

    public bool IgnoreCache { get; init; }

    public SizeCalculationMode SizeCalculationMode { get; init; } = SizeCalculationMode.Approximate;

    public IReadOnlyCollection<string> ExcludedPaths { get; init; } = [];
}
