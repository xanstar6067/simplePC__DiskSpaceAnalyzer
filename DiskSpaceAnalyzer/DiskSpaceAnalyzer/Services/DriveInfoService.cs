using System.Diagnostics;
using System.IO;
using DiskSpaceAnalyzer.Models;

namespace DiskSpaceAnalyzer.Services;

public sealed class DriveInfoService
{
    public IReadOnlyList<DriveSummary> GetReadyDrives()
    {
        return DriveInfo.GetDrives()
            .Where(drive => drive.IsReady)
            .Select(drive => new DriveSummary
            {
                RootPath = drive.RootDirectory.FullName,
                Name = drive.VolumeLabel,
                TotalSize = drive.TotalSize,
                FreeSpace = drive.AvailableFreeSpace,
                StorageKind = DetectStorageKind(drive.RootDirectory.FullName)
            })
            .OrderBy(drive => drive.RootPath)
            .ToList();
    }

    public StorageKind DetectStorageKind(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root))
        {
            return StorageKind.Unknown;
        }

        var driveLetter = char.ToUpperInvariant(root[0]);
        if (!char.IsLetter(driveLetter))
        {
            return StorageKind.Unknown;
        }

        try
        {
            var command =
                "$ErrorActionPreference='Stop';" +
                $"$p=Get-Partition -DriveLetter '{driveLetter}' | Get-Disk | Get-PhysicalDisk | Select-Object -First 1 MediaType,BusType;" +
                "if($p){ Write-Output \"$($p.MediaType)|$($p.BusType)\" }";

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                return StorageKind.Unknown;
            }

            if (!process.WaitForExit(1200))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // Ignore process cleanup errors.
                }

                return StorageKind.Unknown;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            if (string.IsNullOrWhiteSpace(output))
            {
                return StorageKind.Unknown;
            }

            var parts = output.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var mediaType = parts.ElementAtOrDefault(0) ?? "";
            var busType = parts.ElementAtOrDefault(1) ?? "";

            if (busType.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
            {
                return StorageKind.NvmeSsd;
            }

            if (mediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase))
            {
                return StorageKind.Ssd;
            }

            if (mediaType.Contains("HDD", StringComparison.OrdinalIgnoreCase))
            {
                return StorageKind.Hdd;
            }
        }
        catch
        {
            // Windows storage cmdlets are not always available. Unknown is acceptable.
        }

        return StorageKind.Unknown;
    }
}
