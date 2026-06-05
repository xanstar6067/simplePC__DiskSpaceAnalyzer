using System.Windows;
using DiskSpaceAnalyzer.Models;
using DiskSpaceAnalyzer.ViewModels;

namespace DiskSpaceAnalyzer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    private void ResultsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.SelectedNode = e.NewValue as ScanNode;
        }
    }
}
