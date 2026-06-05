using System.Diagnostics;
using System.IO;
using System.Windows;
using DiskSpaceAnalyzer.Models;
using WinForms = System.Windows.Forms;

namespace DiskSpaceAnalyzer.Services;

public sealed class ShellService
{
    public string? PickFolder()
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Выберите папку для анализа",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        return dialog.ShowDialog() == WinForms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    public void OpenInExplorer(ScanNode node)
    {
        if (Directory.Exists(node.FullPath))
        {
            StartExplorer($"\"{node.FullPath}\"");
            return;
        }

        if (File.Exists(node.FullPath))
        {
            StartExplorer($"/select,\"{node.FullPath}\"");
            return;
        }

        OpenLocation(node);
    }

    public void OpenLocation(ScanNode node)
    {
        var location = Directory.Exists(node.FullPath)
            ? node.FullPath
            : Path.GetDirectoryName(node.FullPath);

        if (!string.IsNullOrWhiteSpace(location) && Directory.Exists(location))
        {
            StartExplorer($"\"{location}\"");
        }
    }

    public void CopyPath(ScanNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.FullPath))
        {
            System.Windows.Clipboard.SetText(node.FullPath);
        }
    }

    private static void StartExplorer(string arguments)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = arguments,
            UseShellExecute = true
        });
    }
}
