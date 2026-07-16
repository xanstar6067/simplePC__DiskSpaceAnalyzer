using System.Windows;
using DiskSpaceAnalyzer.Services;

namespace DiskSpaceAnalyzer;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeManager.Initialize();
        base.OnStartup(e);
    }
}
