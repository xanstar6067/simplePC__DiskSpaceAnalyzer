using System.IO;
using DiskSpaceAnalyzer.Models;

namespace DiskSpaceAnalyzer.Services;

public sealed class PathRiskClassifier
{
    private readonly string _windowsRoot;
    private readonly string _systemDrive;
    private readonly string[] _safeWindowsChildren;
    private readonly string[] _safeTempRoots;
    private readonly string[] _safeModeSkippedRoots;

    public PathRiskClassifier()
    {
        _windowsRoot = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        _systemDrive = Normalize(Path.GetPathRoot(_windowsRoot) ?? "C:\\");
        _safeWindowsChildren =
        [
            Normalize(Path.Combine(_windowsRoot, "Temp")),
            Normalize(Path.Combine(_windowsRoot, "SoftwareDistribution", "Download"))
        ];

        _safeTempRoots =
        [
            Normalize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")),
            Normalize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp")),
            .._safeWindowsChildren
        ];

        _safeModeSkippedRoots =
        [
            _windowsRoot,
            Normalize(Path.Combine(_systemDrive, "ProgramData")),
            Normalize(Path.Combine(_systemDrive, "System Volume Information")),
            Normalize(Path.Combine(_systemDrive, "Recovery"))
        ];
    }

    public IReadOnlyList<string> SafeWindowsChildren => _safeWindowsChildren;

    public bool IsWindowsRoot(string path)
    {
        return string.Equals(Normalize(path), _windowsRoot, StringComparison.OrdinalIgnoreCase);
    }

    public PathDecision Evaluate(string path, bool includeSystemDirectories, IReadOnlyCollection<string> userExcludedPaths)
    {
        var normalized = Normalize(path);

        if (userExcludedPaths.Any(excluded => IsSameOrChild(normalized, Normalize(excluded))))
        {
            return new PathDecision(true, RiskLevel.Skipped, "Исключено пользователем");
        }

        if (!includeSystemDirectories)
        {
            var underAllowedWindowsChild = _safeWindowsChildren.Any(allowed => IsSameOrChild(normalized, allowed));
            var underSkippedRoot = _safeModeSkippedRoots.Any(root => IsSameOrChild(normalized, root));

            if (underSkippedRoot && !underAllowedWindowsChild && !IsWindowsRoot(normalized))
            {
                return new PathDecision(true, RiskLevel.Skipped, "Пропущено безопасным режимом");
            }
        }

        return new PathDecision(false, Classify(normalized), "Готово");
    }

    public RiskLevel Classify(string path)
    {
        var normalized = Normalize(path);

        if (_safeTempRoots.Any(root => IsSameOrChild(normalized, root)))
        {
            return normalized.StartsWith(_windowsRoot, StringComparison.OrdinalIgnoreCase) ? RiskLevel.Review : RiskLevel.Safe;
        }

        if (IsSameOrChild(normalized, Path.Combine(_windowsRoot, "System32")) ||
            IsSameOrChild(normalized, Path.Combine(_systemDrive, "System Volume Information")) ||
            IsSameOrChild(normalized, Path.Combine(_systemDrive, "Recovery")))
        {
            return RiskLevel.Dangerous;
        }

        if (IsSameOrChild(normalized, _windowsRoot) ||
            IsSameOrChild(normalized, Path.Combine(_systemDrive, "ProgramData")) ||
            IsSameOrChild(normalized, Path.Combine(_systemDrive, "Program Files")) ||
            IsSameOrChild(normalized, Path.Combine(_systemDrive, "Program Files (x86)")))
        {
            return RiskLevel.System;
        }

        return RiskLevel.Safe;
    }

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool IsSameOrChild(string path, string parent)
    {
        path = Normalize(path);
        parent = Normalize(parent);

        if (string.Equals(path, parent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var parentWithSlash = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return path.StartsWith(parentWithSlash, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record PathDecision(bool ShouldSkip, RiskLevel Risk, string StatusText);
