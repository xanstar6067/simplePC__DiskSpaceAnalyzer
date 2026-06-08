namespace DiskSpaceAnalyzer.Models;

public enum FileSystemItemKind
{
    File,
    Folder,
    Link,
    NoAccess
}

public enum RiskLevel
{
    Safe,
    Review,
    System,
    Dangerous,
    Skipped,
    NoAccess
}

public enum StorageKind
{
    Hdd,
    Ssd,
    NvmeSsd,
    Unknown
}

public enum SizeCalculationMode
{
    Logical,
    Exact,
    Approximate
}
