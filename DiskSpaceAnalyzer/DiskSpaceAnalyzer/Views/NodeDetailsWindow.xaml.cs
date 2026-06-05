using System.Windows;

namespace DiskSpaceAnalyzer.Views;

public partial class NodeDetailsWindow : Window
{
    public NodeDetailsWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
