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

        if (SystemInterop.TryDetectStorageKind(root, out var storageKind))
        {
            return storageKind;
        }

        try
        {
            var command =
                "$ErrorActionPreference='Stop';" +
                $"$disk=Get-Partition -DriveLetter '{driveLetter}' | Get-Disk | Select-Object -First 1;" +
                "$physical=$null;" +
                "if($disk){" +
                "  $physical=Get-PhysicalDisk | Where-Object { $_.DeviceId -eq [string]$disk.Number -or $_.FriendlyName -eq $disk.FriendlyName } | Select-Object -First 1;" +
                "}" +
                "$media=if($physical){$physical.MediaType}else{''};" +
                "$bus=if($disk){$disk.BusType}else{if($physical){$physical.BusType}else{''}};" +
                "$model=(($disk.FriendlyName,$physical.FriendlyName) -join ' ');" +
                "Write-Output \"$media|$bus|$model\"";

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

            var parts = output.Split('|', StringSplitOptions.TrimEntries);
            var mediaType = parts.ElementAtOrDefault(0) ?? "";
            var busType = parts.ElementAtOrDefault(1) ?? "";
            var model = parts.ElementAtOrDefault(2) ?? "";

            if (busType.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ||
                model.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
            {
                return StorageKind.NvmeSsd;
            }

            if (mediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
                model.Contains("SSD", StringComparison.OrdinalIgnoreCase))
            {
                return StorageKind.Ssd;
            }

            if (mediaType.Contains("HDD", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Contains("Hard disk", StringComparison.OrdinalIgnoreCase))
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
