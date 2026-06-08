using System.Windows;
using System.Windows.Input;
using DiskSpaceAnalyzer.ViewModels;

namespace DiskSpaceAnalyzer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
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
