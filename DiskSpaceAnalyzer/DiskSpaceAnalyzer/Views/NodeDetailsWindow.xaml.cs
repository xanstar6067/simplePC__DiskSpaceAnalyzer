using System.Windows;
using DiskSpaceAnalyzer.Services;

namespace DiskSpaceAnalyzer.Views;

public partial class NodeDetailsWindow : Window
{
    public NodeDetailsWindow()
    {
        InitializeComponent();
        ThemeManager.RegisterWindow(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
