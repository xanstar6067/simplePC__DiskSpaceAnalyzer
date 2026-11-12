using System.Windows;
using DiskSpaceAnalyzer.Services;
using DiskSpaceAnalyzer.ViewModels;

namespace DiskSpaceAnalyzer.Views;

public partial class McpConnectionWindow : Window
{
    public McpConnectionWindow(McpConnectionViewModel viewModel)
    {
        InitializeComponent();
        ThemeManager.RegisterWindow(this);
        DataContext = viewModel;
    }
}
