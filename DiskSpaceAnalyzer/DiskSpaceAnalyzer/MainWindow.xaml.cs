using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DiskSpaceAnalyzer.Models;
using DiskSpaceAnalyzer.Services;
using DiskSpaceAnalyzer.ViewModels;

namespace DiskSpaceAnalyzer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.RegisterWindow(this);
        ThemeToggle.IsChecked = ThemeManager.IsDarkTheme;
        DataContext = new MainViewModel();
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
