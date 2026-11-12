using System.Windows;
using System.ComponentModel;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DiskSpaceAnalyzer.Models;
using DiskSpaceAnalyzer.Services;
using DiskSpaceAnalyzer.ViewModels;
using DiskSpaceAnalyzer.Views;

namespace DiskSpaceAnalyzer;

public partial class MainWindow : Window
{
    private readonly McpConnectionViewModel _mcp;
    private bool _shutdownComplete;

    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.RegisterWindow(this);
        ThemeToggle.IsChecked = ThemeManager.IsDarkTheme;
        var viewModel = new MainViewModel();
        var queries = new AnalysisQueryService(
            token => Dispatcher.InvokeAsync(viewModel.CaptureAnalysisContext, System.Windows.Threading.DispatcherPriority.Background, token).Task,
            (path, token) => Dispatcher.InvokeAsync(() =>
            {
                var revealed = viewModel.RevealAnalysisNode(path);
                if (revealed && viewModel.SelectedFlatNode is not null) ResultsTree.ScrollIntoView(viewModel.SelectedFlatNode);
                return revealed;
            }, System.Windows.Threading.DispatcherPriority.Background, token).Task);
        _mcp = new McpConnectionViewModel(new AnalysisMcpServer(queries));
        viewModel.Mcp = _mcp;
        DataContext = viewModel;
        Loaded += async (_, _) => await _mcp.InitializeAsync();
        Closing += MainWindow_Closing;
    }

    private void AiConnection_Click(object sender, RoutedEventArgs e)
    {
        new McpConnectionWindow(_mcp) { Owner = this }.ShowDialog();
    }

    private bool _closing;

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        try { await _mcp.DisposeAsync(); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception); }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.SetDarkTheme(ThemeToggle.IsChecked == true);
    }

    private void ExpanderToggle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ToggleButton { DataContext: ScanNode node } && node.ChildCount > 0)
        {
            node.IsExpanded = !node.IsExpanded;
            e.Handled = true;
        }
    }

    private void ResultsTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left &&
            ResultsTree.SelectedItem is ScanNode { ChildCount: > 0 } node)
        {
            node.IsExpanded = !node.IsExpanded;
            e.Handled = true;
        }
    }

    private void ChartItems_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (DataContext is MainViewModel viewModel && viewModel.NavigateToChartNodeCommand.CanExecute(null))
        {
            viewModel.NavigateToChartNodeCommand.Execute(null);
        }
    }
}
