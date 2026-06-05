using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DiskSpaceAnalyzer.Models;

namespace DiskSpaceAnalyzer.Converters;

public sealed class RiskWarningVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is ScanNode { Risk: RiskLevel.System or RiskLevel.Dangerous }
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
