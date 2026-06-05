namespace DiskSpaceAnalyzer.Services;

public static class FileSizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "0 B";
        }

        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < Units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{bytes} {Units[unitIndex]}" : $"{value:0.##} {Units[unitIndex]}";
    }
}
